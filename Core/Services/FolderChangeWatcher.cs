// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Watches one folder for changes and raises a single, debounced
    /// <see cref="Changed"/> event once activity settles — never a raw
    /// per-file-system-event stream, which callers would otherwise have
    /// to coalesce themselves (and a bulk copy/move can raise thousands
    /// of raw events in a few hundred milliseconds).
    ///
    /// Deliberately UI-framework-agnostic: this class knows nothing
    /// about a WPF Dispatcher. <see cref="Changed"/> fires on whatever
    /// thread the internal debounce timer happens to run on (a
    /// ThreadPool thread) — callers that need to touch UI-bound state in
    /// response are responsible for marshaling onto their own UI thread
    /// first, exactly as every other background-originated notification
    /// in this app's ViewModel layer already does. Keeping that
    /// responsibility at the ViewModel layer (which already owns an
    /// IUiDispatcher) rather than baking a dispatcher dependency into
    /// this Core.Services primitive keeps it independently usable and
    /// testable outside a UI context, matching this codebase's existing
    /// layering (see DriveEnumerationService's own remarks on the same
    /// principle).
    ///
    /// Every failure mode is absorbed here, never thrown past
    /// construction: a watched path that's a network share which is
    /// unreachable, becomes unreachable, doesn't support change
    /// notifications at all (some NAS/exotic filesystems), or simply
    /// vanishes out from under the watch, all result in a watcher that
    /// silently does nothing further — never an exception the caller
    /// has to handle, never a crash. Live updates are a pure
    /// enhancement on top of a folder that already works without them;
    /// losing the enhancement is always an acceptable outcome, an
    /// unhandled exception from this class never is.
    ///
    /// Network-path watcher creation itself runs under
    /// BlockingOperationRunner on a dedicated thread with a hard
    /// timeout, for the same reason directory enumeration does
    /// elsewhere in this app: constructing a FileSystemWatcher and
    /// setting EnableRaisingEvents against an unresponsive UNC path can
    /// block the calling thread on the OS's own network timeout, and a
    /// plain CancellationToken cannot interrupt that once it's blocked
    /// inside the call.
    /// </summary>
    public sealed class FolderChangeWatcher : IDisposable
    {
        private const int DefaultNetworkCreateTimeoutMs = 4000;
        private const int MinDebounceMs = 50;
        private const int MaxDebounceMs = 30000;

        private readonly bool _directoriesOnly;
        private readonly int _debounceMs;
        private readonly ILogger _logger;
        private readonly Timer _debounceTimer;
        private readonly int _maxWaitMs;
        private readonly object _burstGate = new object();
        private long _burstStartTicks;
        private FileSystemWatcher _watcher;
        private volatile bool _disposed;

        /// <summary>Raised, debounced, after one or more raw filesystem events settle. See class remarks on threading.</summary>
        public event EventHandler Changed;

        public string WatchedPath { get; }

        /// <summary>True only once a real, live FileSystemWatcher was successfully created and started for WatchedPath.</summary>
        public bool IsActive => _watcher != null && !_disposed;

        private FolderChangeWatcher(string path, bool directoriesOnly, int debounceMs, ILogger logger)
        {
            WatchedPath = path;
            _directoriesOnly = directoriesOnly;
            _debounceMs = Clamp(debounceMs, MinDebounceMs, MaxDebounceMs);
            _maxWaitMs = Math.Max(1500, _debounceMs * 3);
            _logger = logger;
            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Creates and starts watching <paramref name="path"/>. Always
        /// returns a non-null instance, even on total failure — check
        /// <see cref="IsActive"/> if the caller cares whether watching
        /// actually started (most callers don't need to: a failed watch
        /// just never raises <see cref="Changed"/>, which is already
        /// the correct behavior to fall back to).
        /// </summary>
        public static async Task<FolderChangeWatcher> CreateAsync(
            string path,
            bool directoriesOnly,
            int debounceMs,
            int networkCreateTimeoutMs = DefaultNetworkCreateTimeoutMs,
            CancellationToken cancellationToken = default,
            ILogger logger = null)
        {
            var instance = new FolderChangeWatcher(path, directoriesOnly, debounceMs, (logger ?? Log.Logger).ForContext<FolderChangeWatcher>());

            if (string.IsNullOrEmpty(path))
            {
                return instance;
            }

            try
            {
                if (PathClassifier.IsNetworkPath(path))
                {
                    await BlockingOperationRunner.RunAsync(
                        _ =>
                        {
                            instance.StartWatcherCore();
                            return true;
                        },
                        networkCreateTimeoutMs <= 0 ? DefaultNetworkCreateTimeoutMs : networkCreateTimeoutMs,
                        cancellationToken,
                        threadName: "ByteExplorer.WatcherCreate").ConfigureAwait(false);
                }
                else
                {
                    instance.StartWatcherCore();
                }
            }
            catch (OperationCanceledException)
            {
                // Genuine cancellation (the caller's own navigation was
                // superseded while this was starting) — propagate as-is
                // so the caller's own supersede handling applies,
                // exactly as it would for any other cancellable step in
                // this app.
                throw;
            }
            catch (TimeoutException ex)
            {
                instance._logger.Debug(ex, "Live folder watching for {Path} timed out starting — continuing without live updates for this folder", path);
            }
            catch (Exception ex)
            {
                // Any other failure — path vanished, access denied, an
                // exotic filesystem/NAS that rejects change
                // notifications, the drive was ejected mid-creation, …
                instance._logger.Debug(ex, "Live folder watching unavailable for {Path} — continuing without live updates for this folder", path);
            }

            return instance;
        }

        private void StartWatcherCore()
        {
            FileSystemWatcher watcher = null;
            try
            {
                watcher = new FileSystemWatcher(WatchedPath)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = _directoriesOnly
                        ? NotifyFilters.DirectoryName
                        : NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,

                    // The default (8 KB) overflows under any real burst
                    // of activity — a large copy/extract can silently
                    // drop events past that in well under a second. 64
                    // KB is the practical, broadly-recommended ceiling
                    // for FileSystemWatcher without other native-level
                    // changes; values much beyond this aren't reliably
                    // honored by the underlying OS buffer anyway.
                    InternalBufferSize = 65536
                };

                watcher.Created += OnRawEvent;
                watcher.Deleted += OnRawEvent;
                watcher.Renamed += OnRawEvent;
                if (!_directoriesOnly)
                {
                    // The tree (directoriesOnly = true) only ever
                    // displays subfolders, never file contents changing
                    // — skipping Changed there avoids debounce churn
                    // from files being written to inside a watched
                    // folder that the tree wouldn't render differently
                    // for anyway.
                    watcher.Changed += OnRawEvent;
                }
                watcher.Error += OnWatcherError;

                watcher.EnableRaisingEvents = true;

                // On a network path this runs on a helper thread that can finish after CreateAsync gave up
                // (timeout) and the caller disposed this instance: don't attach a live watcher to a dead one.
                // Returning here leaves `watcher` set, so the finally below disposes it.
                if (_disposed)
                {
                    return;
                }

                _watcher = watcher;
                watcher = null; // ownership transferred — don't dispose it below
            }
            finally
            {
                // A failure anywhere after the FileSystemWatcher was
                // constructed but before ownership transferred to
                // _watcher above would otherwise leak its native handle.
                watcher?.Dispose();
            }
        }

        private void OnRawEvent(object sender, FileSystemEventArgs e)
        {
            ArmDebounce();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Most commonly an internal buffer overflow (too many
            // changes too fast to keep up with, even at 64 KB) or the
            // watched path becoming unreachable mid-watch. Either way,
            // the correct response is identical to any other change: a
            // full refresh is inherently self-correcting, unlike trying
            // to reason about exactly which individual events were lost.
            _logger.Debug(e.GetException(), "Watcher error for {Path} — treating as a change", WatchedPath);
            ArmDebounce();
        }

        private void ArmDebounce()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Timer.Change is safe to call repeatedly and
                // concurrently — each call simply reschedules the one
                // pending firing further out, which is exactly the
                // coalescing behavior debouncing a burst of rapid raw
                // events requires: the timer only ever actually fires
                // once activity has been quiet for _debounceMs.
                // A pure trailing debounce never fires while a file is being written continuously (a download,
                // a growing log), and fires every _debounceMs when the writes are spaced slightly wider. Capping
                // the wait from the first event of a burst bounds both: at most one refresh per _maxWaitMs.
                lock (_burstGate)
                {
                    long now = Environment.TickCount64;
                    if (_burstStartTicks == 0)
                    {
                        _burstStartTicks = now;
                    }

                    long remaining = _maxWaitMs - (now - _burstStartTicks);
                    long due = Math.Max(0, Math.Min(_debounceMs, remaining));
                    _debounceTimer.Change(due, Timeout.Infinite);
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently with a raw event arriving —
                // harmless, there's nothing left to notify.
            }
        }

        private void OnDebounceElapsed(object state)
        {
            lock (_burstGate)
            {
                _burstStartTicks = 0;
            }

            if (_disposed)
            {
                return;
            }

            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // A Timer callback has no caller to observe a fault —
                // a misbehaving subscriber must never be able to bring
                // down the process from here.
                _logger.Warning(ex, "Unhandled exception in a folder-change subscriber for {Path}", WatchedPath);
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnRawEvent;
                    _watcher.Deleted -= OnRawEvent;
                    _watcher.Renamed -= OnRawEvent;
                    _watcher.Changed -= OnRawEvent;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error disposing the filesystem watcher for {Path}", WatchedPath);
            }
            finally
            {
                _watcher = null;
            }

            try
            {
                _debounceTimer.Dispose();
            }
            catch
            {
                // Best-effort — the process is either fine or exiting
                // either way.
            }
        }
    }
}
