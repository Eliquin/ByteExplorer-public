// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Builds a DirectoryIndex by draining NativeFileEnumerator into a
    /// List, sorting it once (folders first, then name — matching
    /// Explorer's default), and wrapping the whole run in per-failure-
    /// mode error handling so any problem produces a clear, user-facing
    /// message instead of an unhandled exception.
    ///
    /// A small LRU+TTL cache sits transparently in front of the actual
    /// enumeration: revisiting a folder within the TTL window (default
    /// 5s) returns the cached index immediately, with zero disk access —
    /// this is what makes back/forward navigation between recently
    /// visited folders feel instant. The cache is intentionally an
    /// implementation detail here rather than a separate injected
    /// service: callers ask for an index and get one, correctly, and
    /// never need to know or care whether this particular call actually
    /// touched disk. The cache is thread-safe, which matters here: a
    /// network listing that ultimately finishes after this method has
    /// already returned a timeout to its caller still writes its result
    /// into the cache when it completes, so the very next visit to that
    /// folder can come back instantly instead of waiting again.
    ///
    /// Network paths get a REAL bounded timeout on the underlying
    /// enumeration, enforced by <see cref="BlockingOperationRunner"/> on
    /// a dedicated thread rather than the shared ThreadPool — a plain
    /// CancellationToken cannot interrupt NativeFileEnumerator's first
    /// FindFirstFileEx call against an unresponsive server, since that
    /// call blocks before any cancellation-checking code has a chance to
    /// run. Local paths never get a timeout at all — a local enumeration
    /// finishing slowly is a performance question, not a hang risk, and
    /// was never observed to actually hang. Every real (non-cached) run
    /// is timed and logged so slow folders are visible in the logs, not
    /// just felt in the UI.
    /// </summary>
    public sealed class DirectoryIndexService : IDirectoryIndexService
    {
        private readonly ILogger _logger;
        private readonly ISettingsService _settingsService;
        private readonly DirectoryIndexCache _cache = new DirectoryIndexCache();

        public DirectoryIndexService(ISettingsService settingsService, ILogger logger = null)
        {
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<DirectoryIndexService>();
        }

        public Task<DirectoryIndex> BuildIndexAsync(string path, int networkTimeoutMs, bool bypassCache = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Task.FromResult(DirectoryIndex.Empty(path));
            }

            FileListSettings fileListSettings = _settingsService?.Current?.FileList;
            int cacheCapacity = fileListSettings?.DirectoryIndexCacheCapacity ?? 8;
            int cacheTtlMs = fileListSettings?.DirectoryIndexCacheTtlMs ?? 5000;

            // On a phone: listed by its provider on a dedicated thread, never cached (the phone changes on its own), with a
            // generous timeout: a phone lists a big camera folder slowly, and a locked one doesn't answer at all.
            if (Platform.Phones.Phones.IsPhonePath(path))
            {
                return ListPhoneFolderAsync(path, cancellationToken);
            }

            // Inside a ZIP file: listed from the archive's own (cached) directory, never through the folder cache.
            if (ArchiveBrowser.TryResolve(path, out string archivePath, out string innerPath))
            {
                return Task.Run(() => ArchiveBrowser.List(archivePath, innerPath, path), cancellationToken);
            }

            string cacheKey = NormalizeCacheKey(path);

            if (!bypassCache && _cache.TryGet(cacheKey, cacheTtlMs, out DirectoryIndex cached))
            {
                _logger.Debug("Serving {Path} from index cache ({Count} entries)", path, cached.Count);
                return Task.FromResult(cached);
            }

            if (PathClassifier.IsNetworkPath(path))
            {
                return BuildIndexOverNetworkAsync(path, networkTimeoutMs, cacheKey, cacheCapacity, cacheTtlMs, cancellationToken);
            }

            return Task.Run(() => BuildIndexBlocking(path, cancellationToken, cacheKey, cacheCapacity, cacheTtlMs, isNetwork: false), cancellationToken);
        }

        /// <summary>How long a phone folder may take to list before the list says it timed out.</summary>
        internal const int PhoneListTimeoutMs = 120_000;

        private async Task<DirectoryIndex> ListPhoneFolderAsync(string path, CancellationToken cancellationToken)
        {
            Platform.Phones.IPhoneProvider phone = Platform.Phones.Phones.For(path);
            if (phone == null)
            {
                return DirectoryIndex.Error(path, "Phones can't be reached on this computer.");
            }

            try
            {
                DirectoryIndex index = await BlockingOperationRunner.RunAsync(
                    token => phone.List(path, token), PhoneListTimeoutMs, cancellationToken, threadName: "ByteExplorer.PhoneList").ConfigureAwait(false);
                // Default order (folders first, natural names) like every other listing, so the view needn't special-case it.
                return index.HasError
                    ? index
                    : new DirectoryIndex(index.ParentPath, DirectoryEntrySorter.Sort(index.Entries, EntryOrder.Name, ascending: true), false, null) { IsSortedByName = true };
            }
            catch (TimeoutException)
            {
                return DirectoryIndex.Error(path, "The phone didn't answer in time. " + Platform.Phones.PhoneErrors.LockedHint);
            }
        }

        private async Task<DirectoryIndex> BuildIndexOverNetworkAsync(string path, int networkTimeoutMs, string cacheKey, int cacheCapacity, int cacheTtlMs, CancellationToken cancellationToken)
        {
            try
            {
                return await BlockingOperationRunner.RunAsync(
                    token => BuildIndexBlocking(path, token, cacheKey, cacheCapacity, cacheTtlMs, isNetwork: true),
                    networkTimeoutMs,
                    cancellationToken,
                    threadName: "ByteExplorer.NetworkEnum").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warning(
                    "Listing {Path} exceeded the {TimeoutMs}ms network timeout — the underlying request is still running in the background and, if it eventually succeeds, will warm the cache for the next visit",
                    path, networkTimeoutMs);
                return DirectoryIndex.Error(path, "Timed out reading this location.");
            }
        }

        /// <summary>
        /// The actual enumeration. Runs either on a plain ThreadPool
        /// thread (local paths, via Task.Run) or on a dedicated thread
        /// under a hard deadline (network paths, via
        /// BlockingOperationRunner) — this method itself doesn't know or
        /// care which; it just drains the enumerator and, on success,
        /// writes the result into the cache. For a network path whose
        /// caller has already moved on after a timeout, this still runs
        /// to completion on its abandoned dedicated thread and still
        /// populates the cache — a deliberate, low-cost way to make a
        /// slow-to-respond share feel instant on the next visit instead
        /// of just as slow every time.
        /// </summary>
        private DirectoryIndex BuildIndexBlocking(string path, CancellationToken cancellationToken, string cacheKey, int cacheCapacity, int cacheTtlMs, bool isNetwork)
        {
            var stopwatch = Stopwatch.StartNew();
            var entries = new List<DirectoryIndexEntry>();

            try
            {
                foreach (DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(path, cancellationToken))
                {
                    entries.Add(entry);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // classified by the caller (genuine cancel vs. hard timeout)
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Warning("Access denied listing {Path}", path);
                return DirectoryIndex.Error(path, "Access denied.");
            }
            catch (DirectoryNotFoundException)
            {
                _logger.Warning("Directory not found: {Path}", path);
                return DirectoryIndex.Error(path, "This folder no longer exists.");
            }
            catch (IOException ex)
            {
                _logger.Warning(ex, "I/O error listing {Path}", path);
                return DirectoryIndex.Error(path, "Couldn't read this location.");
            }
            catch (Win32Exception ex)
            {
                _logger.Warning(ex, "Win32 error listing {Path}", path);
                return DirectoryIndex.Error(path, "Couldn't read this location.");
            }

            DirectoryIndexEntry[] sorted = DirectoryEntrySorter.Sort(entries, EntryOrder.Name, ascending: true);
            stopwatch.Stop();

            _logger.Information(
                "Indexed {Count} entries in {Path} in {ElapsedMs:N2}ms{NetworkSuffix}",
                entries.Count, path, stopwatch.Elapsed.TotalMilliseconds, isNetwork ? " (network)" : string.Empty);

            var index = new DirectoryIndex(path, sorted, false, null) { IsSortedByName = true };

            // Only successful results are cached — an error (access
            // denied, timeout, missing folder) might be transient, so
            // the next attempt should always retry for real rather than
            // repeating a cached failure.
            _cache.Set(cacheKey, index, cacheCapacity, cacheTtlMs);

            return index;
        }

        public void InvalidateCache(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            _cache.Remove(NormalizeCacheKey(path));
        }

        private static string NormalizeCacheKey(string path)
        {
            try
            {
                return PathUtil.Normalize(path);
            }
            catch (Exception)
            {
                // A path that can't even be normalized will fail
                // enumeration anyway and surface its own error — just
                // fall back to using it as-is for cache-key purposes.
                return path;
            }
        }
    }
}
