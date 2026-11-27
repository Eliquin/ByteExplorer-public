// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>What the content indexer reads, and what it leaves out.</summary>
    public sealed class ContentIndexerOptions
    {
        /// <summary>Files larger than this are not read. Default: 20 MB.</summary>
        public long MaxFileSize { get; set; } = 20L * 1024 * 1024;

        /// <summary>Folders whose files are never read (system and program folders, the app's own data).</summary>
        public IReadOnlyList<string> ExcludedFolders { get; set; } = Array.Empty<string>();

        /// <summary>Folder names whose files are never read wherever they are (dependency caches).</summary>
        public IReadOnlyList<string> ExcludedFolderNames { get; set; } = DefaultExcludedFolderNames;

        /// <summary>
        /// The share of time the worker may spend working: after each file it rests so that work stays at this share
        /// (0.5: as long as the file took). Lower is politer and slower. Default: 0.5.
        /// </summary>
        public double DutyCycle { get; set; } = 0.5;

        /// <summary>Delay before the first sync after start (the app settles first).</summary>
        public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Third-party code and caches: large, many files, never what a person searches the contents of.</summary>
        public static readonly IReadOnlyList<string> DefaultExcludedFolderNames = new[]
        {
            "node_modules", "bower_components", "__pycache__", "site-packages", ".git", ".svn", ".hg", ".venv", "venv"
        };
    }

    /// <summary>
    /// Keeps the <see cref="ContentIndex"/> in step with the name index, on one background thread:
    /// <list type="bullet">
    /// <item><b>Sync</b>: one pass over the name index (no crawl of its own) picks the files to read: not folders,
    /// links, hidden or deleted entries, of a readable type (<see cref="ContentExtractor"/>), not empty, at most
    /// <see cref="ContentIndexerOptions.MaxFileSize"/>, outside excluded and system folders and dependency caches.
    /// Files whose size or time changed since they were read are queued, newest first (what was just worked on is
    /// searchable soonest); indexed files no longer listed are removed (unless the name index is partial). A pass
    /// costs a few hundred milliseconds per million entries; reading only changed files is what makes updates cheap.</item>
    /// <item><b>When</b>: <see cref="StartDelay"/> after start, then a few seconds after the name index changes (a build,
    /// a live update from the watchers or the app's own operations), coalesced so a burst of changes is one pass.</item>
    /// <item><b>Polite</b>: lowest thread priority (and background I/O priority on Windows); after each file it rests as
    /// long as the file took (<see cref="ContentIndexerOptions.DutyCycle"/>); it waits while a search runs and while the
    /// name index is being built (<see cref="Paused"/>); every step is cancellable.</item>
    /// <item><b>Commits</b>: when the memory buffer is full, every 30 seconds while reading, and when the queue is empty.</item>
    /// </list>
    /// </summary>
    internal sealed class ContentIndexer : IDisposable
    {
        /// <summary>The most text read from one file (chars); the rest of a huge log is not indexed.</summary>
        public const long MaxCharsPerFile = 8L * 1024 * 1024;

        /// <summary>The most distinct terms kept from one file.</summary>
        public const int MaxTermsPerFile = 200_000;

        private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxCoalesce = TimeSpan.FromMinutes(2);

        private readonly ContentIndex _index;
        private readonly Func<IndexData> _names;
        private readonly Func<bool> _isBusy;
        private readonly ContentIndexerOptions _options;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _thread;
        private DateTime _syncDueUtc = DateTime.MaxValue;
        private DateTime _firstRequestUtc = DateTime.MaxValue;
        private DateTime _notBeforeUtc = DateTime.MinValue;
        private volatile bool _paused;
        private volatile int _remaining;
        private volatile int _readThisPass;
        private volatile string _status = "Waiting to start.";
        private volatile bool _idle;
        private bool _disposed;

        public ContentIndexer(ContentIndex index, Func<IndexData> names, Func<bool> isBusy, ContentIndexerOptions options)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _isBusy = isBusy ?? (() => false);
            _options = options ?? new ContentIndexerOptions();
        }

        /// <summary>Raised (on the worker thread) when <see cref="Status"/> changes; at most a few times a second.</summary>
        public event EventHandler StatusChanged;

        public ContentIndex Index => _index;

        /// <summary>A line for the settings: files indexed and progress.</summary>
        public string Status => _status;

        /// <summary>Files queued to read in the current pass.</summary>
        public int Remaining => _remaining;

        /// <summary>No pass is running and nothing is queued.</summary>
        public bool IsIdle => _idle;

        /// <summary>Holds the worker between files (while the name index is built).</summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                _signal.Set();
            }
        }

        /// <summary>Starts the worker: loads the saved index, then syncs after <see cref="ContentIndexerOptions.StartDelay"/>.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null || _disposed)
                {
                    return;
                }

                _notBeforeUtc = DateTime.UtcNow + _options.StartDelay;
                _syncDueUtc = _notBeforeUtc;
                _thread = new Thread(Run)
                {
                    Name = "Content indexer",
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest
                };
                _thread.Start();
            }
        }

        /// <summary>Asks for a sync <paramref name="delay"/> from now; requests in a burst are coalesced (at most two minutes).</summary>
        public void RequestSync(TimeSpan delay)
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                if (_firstRequestUtc == DateTime.MaxValue)
                {
                    _firstRequestUtc = now;
                }

                DateTime due = now + delay;
                DateTime latest = _firstRequestUtc + MaxCoalesce;
                due = due < latest ? due : latest;
                _syncDueUtc = due > _notBeforeUtc ? due : _notBeforeUtc; // never before the start delay
            }

            _signal.Set();
        }

        /// <summary>Runs one sync and reads every queued file on the calling thread, without throttling (tests).</summary>
        internal void RunOnce(CancellationToken cancellationToken = default)
        {
            List<WorkItem> work = Sync(cancellationToken);
            foreach (WorkItem item in work)
            {
                ReadFile(item, cancellationToken);
            }

            _index.Commit(cancellationToken);
        }

        public void Dispose()
        {
            Thread thread;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                thread = _thread;
            }

            _cts.Cancel();
            _signal.Set();
            if (thread != null && !thread.Join(TimeSpan.FromSeconds(5)))
            {
                Serilog.Log.Logger.Warning("The content indexer did not stop within 5 seconds");
            }

            try
            {
                if (thread == null || !thread.IsAlive)
                {
                    _index.Commit(); // what was read since the last commit
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Saving the content index at exit failed");
            }

            _signal.Dispose();
            _cts.Dispose();
        }

        // ------------------------------------------------------------------ worker

        private void Run()
        {
            CancellationToken token = _cts.Token;
            LowerIoPriority();
            try
            {
                SetStatus("Loading the content index…");
                _index.Load();
                _idle = true;
                SetStatus(DescribeIdle());
                while (!token.IsCancellationRequested)
                {
                    TimeSpan wait;
                    lock (_gate)
                    {
                        wait = _syncDueUtc == DateTime.MaxValue ? Timeout.InfiniteTimeSpan : _syncDueUtc - DateTime.UtcNow;
                    }

                    if (wait > TimeSpan.Zero || wait == Timeout.InfiniteTimeSpan)
                    {
                        _signal.WaitOne(wait == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Min(wait.TotalMilliseconds, int.MaxValue - 1));
                        continue;
                    }

                    lock (_gate)
                    {
                        _syncDueUtc = DateTime.MaxValue;
                        _firstRequestUtc = DateTime.MaxValue;
                    }

                    if (!WaitWhilePaused(token))
                    {
                        break;
                    }

                    RunPass(token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "The content indexer stopped");
                SetStatus("Content indexing stopped after an error; the details are in the log.");
            }
        }

        private void RunPass(CancellationToken token)
        {
            _idle = false;
            SetStatus("Looking for changed files…");
            List<WorkItem> work = Sync(token);
            _remaining = work.Count;
            _readThisPass = 0;
            var commitClock = Stopwatch.StartNew();
            var statusClock = Stopwatch.StartNew();
            for (int i = 0; i < work.Count; i++)
            {
                if (!WaitWhilePaused(token))
                {
                    return;
                }

                while (_isBusy() && !token.IsCancellationRequested)
                {
                    token.WaitHandle.WaitOne(100); // a search is running: it gets the machine
                }

                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                ReadFile(work[i], token);
                _readThisPass++;
                _remaining = work.Count - i - 1;
                if (_index.ShouldCommit || commitClock.Elapsed > CommitInterval)
                {
                    TryCommit(token);
                    commitClock.Restart();
                }

                if (statusClock.ElapsedMilliseconds > 500)
                {
                    SetStatus($"Reading file contents: {_index.DocumentCount + _index.PendingCount:N0} files indexed, {_remaining:N0} to go.");
                    statusClock.Restart();
                }

                Rest(clock.Elapsed, token);
                if (SyncIsDue())
                {
                    break; // the name index changed meanwhile: list again (files already read are skipped)
                }
            }

            TryCommit(token);
            _remaining = 0;
            _idle = !SyncIsDue();
            SetStatus(DescribeIdle());
        }

        private bool SyncIsDue()
        {
            lock (_gate)
            {
                return _syncDueUtc <= DateTime.UtcNow;
            }
        }

        private void TryCommit(CancellationToken token)
        {
            try
            {
                _index.Commit(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Serilog.Log.Logger.Warning(ex, "Saving the content index failed; it is retried at the next commit");
            }
        }

        /// <summary>Rests so that work stays at the duty cycle (and at least a moment between files).</summary>
        private void Rest(TimeSpan worked, CancellationToken token)
        {
            double duty = Math.Clamp(_options.DutyCycle, 0.05, 1);
            double rest = worked.TotalMilliseconds * (1 - duty) / duty;
            int milliseconds = (int)Math.Clamp(rest, duty >= 1 ? 0 : 1, 2000);
            if (milliseconds > 0)
            {
                token.WaitHandle.WaitOne(milliseconds);
            }
        }

        private bool WaitWhilePaused(CancellationToken token)
        {
            while (_paused && !token.IsCancellationRequested)
            {
                _signal.WaitOne(1000);
            }

            return !token.IsCancellationRequested;
        }

        private string DescribeIdle()
        {
            int count = _index.DocumentCount;
            return count == 0
                ? "No file contents indexed yet."
                : $"{count:N0} files' contents indexed ({FormatBytes(_index.SizeOnDisk)} on disk). Up to date.";
        }

        private void SetStatus(string status)
        {
            if (status == _status)
            {
                return;
            }

            _status = status;
            try
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Debug(ex, "A content indexer status handler failed");
            }
        }

        private static string FormatBytes(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";

        // ------------------------------------------------------------------ sync

        private readonly record struct WorkItem(string Path, long Size, long Ticks);

        /// <summary>The files to read (changed or new), newest first; removes indexed files the name index no longer lists.</summary>
        private List<WorkItem> Sync(CancellationToken token)
        {
            var work = new List<WorkItem>();
            IndexData data = _names();
            if (data == null || data.Count == 0)
            {
                return work;
            }

            byte[][] excludedNames = _options.ExcludedFolderNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Encoding.UTF8.GetBytes(SearchQuery.Fold(n.Trim())))
                .ToArray();
            string[] excludedFolders = _options.ExcludedFolders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => PathUtil.WithTrailingSeparator(PathUtil.TrimTrailingSeparator(f)))
                .ToArray();
            var seen = new HashSet<string>(PathUtil.Comparer);
            var excludedParents = new Dictionary<int, bool>();
            const byte Skipped = (byte)(EntryFlags.Directory | EntryFlags.Deleted | EntryFlags.Hidden | EntryFlags.Link);
            long maxSize = _options.MaxFileSize;
            for (int c = 0; c < data.Chunks.Length; c++)
            {
                token.ThrowIfCancellationRequested();
                IndexChunk chunk = data.Chunks[c];
                int idBase = c << IndexChunk.Shift;
                for (int slot = 0; slot < chunk.Count; slot++)
                {
                    if ((chunk.Flags[slot] & Skipped) != 0)
                    {
                        continue;
                    }

                    long size = chunk.Size[slot];
                    if (size <= 0 || size > maxSize || ContentExtractor.FormatOfUtf8(chunk.FoldedName(slot)) == ContentFormat.None)
                    {
                        continue;
                    }

                    int id = idBase + slot;
                    if (data.HasDeletedDirectories && !data.IsAlive(id))
                    {
                        continue;
                    }

                    if (excludedNames.Length > 0 && InExcludedFolder(data, chunk.Parent[slot], excludedNames, excludedParents))
                    {
                        continue;
                    }

                    string path = data.GetFullPath(id);
                    if (IsExcluded(path, excludedFolders))
                    {
                        continue;
                    }

                    seen.Add(path);
                    long ticks = chunk.Ticks[slot];
                    if (!_index.IsCurrent(path, size, ticks))
                    {
                        work.Add(new WorkItem(path, size, ticks));
                    }
                }
            }

            if (!data.IsPartial)
            {
                foreach (string path in _index.DocumentPaths())
                {
                    if (!seen.Contains(path))
                    {
                        _index.RemoveDocument(path);
                    }
                }
            }

            work.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            return work;
        }

        /// <summary>Whether a folder (or one above it) has an excluded name; answers are kept per folder for the pass.</summary>
        private static bool InExcludedFolder(IndexData data, int folder, byte[][] names, Dictionary<int, bool> known)
        {
            if (folder < 0)
            {
                return false;
            }

            if (known.TryGetValue(folder, out bool excluded))
            {
                return excluded;
            }

            excluded = false;
            ReadOnlySpan<byte> name = data.FoldedName(folder);
            foreach (byte[] candidate in names)
            {
                if (name.SequenceEqual(candidate))
                {
                    excluded = true;
                    break;
                }
            }

            excluded = excluded || InExcludedFolder(data, data.ParentOf(folder), names, known);
            known[folder] = excluded;
            return excluded;
        }

        private static bool IsExcluded(string path, string[] folders)
        {
            foreach (string folder in folders)
            {
                if (path.StartsWith(folder, PathUtil.Comparison))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ reading

        private void ReadFile(WorkItem item, CancellationToken token)
        {
            var terms = new TermSet(MaxTermsPerFile);
            var sink = new TokenizingSink(terms, MaxCharsPerFile);
            try
            {
                ContentExtractor.Extract(item.Path, sink, token);
                sink.Finish();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                // Not readable: recorded without terms, so it is not tried again until it changes.
            }
            catch (IOException ex)
            {
                // Locked, or gone since the sync: tried again at the next pass.
                Serilog.Log.Logger.Debug(ex, "Content indexing skipped {Path}", item.Path);
                return;
            }
            catch (Exception ex)
            {
                // A document the extractors could not cope with: recorded without its text.
                Serilog.Log.Logger.Debug(ex, "Content extraction failed for {Path}", item.Path);
            }

            _index.AddDocument(item.Path, item.Size, item.Ticks, terms.Terms);
        }

        /// <summary>The distinct terms of one file, without a string for each repeated occurrence.</summary>
        private sealed class TermSet : ITokenReceiver
        {
            private readonly HashSet<string> _terms = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
            private readonly int _max;

            public TermSet(int max)
            {
                _max = max;
                _lookup = _terms.GetAlternateLookup<ReadOnlySpan<char>>();
            }

            public IEnumerable<string> Terms => _terms;

            public bool OnToken(ReadOnlySpan<char> term, long start, long end)
            {
                if (!_lookup.Contains(term))
                {
                    if (_terms.Count >= _max)
                    {
                        return true; // keep reading: repeated words cost nothing, new ones are dropped
                    }

                    _lookup.Add(term);
                }

                return true;
            }
        }

        // ------------------------------------------------------------------ priority

        private static void LowerIoPriority()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                // THREAD_MODE_BACKGROUND_BEGIN: lower I/O and memory priority as well as CPU, for this thread only.
                SetThreadPriority(GetCurrentThread(), 0x00010000);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
    }
}
