// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Analytics;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Local-only usage analytics backing the Home dashboard. Every
    /// design decision here follows from one constraint: tracking calls
    /// happen on the UI thread, on ordinary interaction paths (opening
    /// a folder, running a search), so they must be effectively free —
    /// no disk I/O, no meaningful lock contention, no possibility of
    /// throwing out to the caller.
    ///
    /// Write path: every Track* call mutates in-memory state under a
    /// short lock, then arms a debounce Timer (same coalescing pattern
    /// FolderChangeWatcher and the drive-refresh timer already use
    /// elsewhere in this app) — a burst of rapid activity produces one
    /// disk write once things go quiet, not one per event. The actual
    /// JSON serialization happens while still holding the lock (CPU-only,
    /// fast even for the largest bounded data this can hold), but the
    /// file write itself happens after releasing it, so Track* callers
    /// are never blocked on disk I/O.
    ///
    /// Crash resilience: a session's end time (LastAliveUtc) is
    /// refreshed on every periodic save AND by a separate heartbeat
    /// timer that ticks even during a fully idle session, so an
    /// ungraceful shutdown (crash, forced close, Windows logoff) still
    /// leaves a reasonably accurate session record rather than one that
    /// looks like it never ended or is missing outright. A clean
    /// shutdown (EndSession, called from Dispose) flushes synchronously
    /// regardless of the debounce window.
    ///
    /// Read path: GetSummary computes the entire dashboard snapshot in
    /// one pass under one lock acquisition — the dashboard is expected
    /// to call this once per visit, not poll it.
    /// </summary>
    public sealed class UserAnalyticsService : IUserAnalyticsService
    {
        private const int DefaultSaveDebounceMs = 5000;
        private const int HeartbeatIntervalMs = 60000;
        private const int MaxFileTypesTracked = 1000;

        private static readonly JsonSerializerOptions SerializeOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;
        private readonly string _filePath;
        private readonly object _gate = new object();
        private readonly Timer _saveTimer;
        private readonly Timer _heartbeatTimer;

        private UserAnalyticsData _data;
        private AppSessionRecord _currentSession;
        private bool _dirty;
        private bool _initialLoadComplete;

        /// <summary>Renames and moves reported before the saved data finished loading, replayed onto it.</summary>
        private List<(string From, string To)> _movesBeforeLoad;
        private volatile bool _disposed;

        public UserAnalyticsService(ISettingsService settingsService, ILogger logger = null)
            : this(ByteExplorer.Core.AppPaths.Combine("analytics.json"), settingsService, logger)
        {
        }

        /// <summary>Internal constructor taking an explicit path — exists so tests (or a future diagnostics tool) can point this at a throwaway file instead of the real one.</summary>
        internal UserAnalyticsService(string filePath, ISettingsService settingsService, ILogger logger = null)
        {
            _filePath = filePath;
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<UserAnalyticsService>();

            // Instantly usable, matching ISettingsService.Current's own
            // contract — this constructor runs synchronously during
            // MainWindow's own construction, before the window is even
            // shown, so any blocking disk read here is pure added
            // startup latency for a feature that has nothing to do with
            // getting the window on screen. The real file, which can
            // grow to a meaningful size over months of use, loads on a
            // background thread instead — see CompleteInitialLoad for
            // how anything tracked in the brief window before that
            // finishes (StartSession, called immediately after this
            // constructor returns, reliably races it) is preserved
            // rather than silently discarded when the load completes.
            _data = new UserAnalyticsData();

            _saveTimer = new Timer(OnSaveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _heartbeatTimer = new Timer(OnHeartbeatElapsed, null, HeartbeatIntervalMs, HeartbeatIntervalMs);

            var ignored = Task.Run(() => CompleteInitialLoad(LoadFromDisk()));
        }

        /// <summary>
        /// Swaps the placeholder empty UserAnalyticsData the constructor
        /// handed out for the real, disk-loaded one. If a session was
        /// already started against the placeholder (StartSession runs
        /// immediately after construction and will typically win this
        /// race, since disk I/O is slower than a field assignment),
        /// that session record is carried over into the loaded data
        /// rather than lost — it's the same object reference either
        /// way, so _currentSession itself never needs to change, only
        /// which list it lives in. Anything else tracked in that same
        /// narrow window (vanishingly unlikely — this runs in
        /// low-single-digit milliseconds for the bounded, capped file
        /// this service ever writes) is not specially preserved; losing
        /// a handful of events for a dashboard that isn't correctness-
        /// critical is an acceptable trade for never blocking startup.
        /// </summary>
        private void CompleteInitialLoad(UserAnalyticsData loaded)
        {
            lock (_gate)
            {
                if (_initialLoadComplete)
                {
                    return;
                }

                if (_currentSession != null)
                {
                    loaded.Sessions.Add(_currentSession);
                }

                // Deliberate fix: only the session used to survive; the folder visits, file opens, searches and
                // operations tracked while the file loaded (the first navigation of every start) were dropped.
                MergeTrackedBeforeLoad(loaded, _data);
                _data = loaded;
                _initialLoadComplete = true;

                if (_movesBeforeLoad != null)
                {
                    foreach ((string from, string to) in _movesBeforeLoad)
                    {
                        MovePathCore(from, to);
                    }

                    _movesBeforeLoad = null;
                }
            }
        }

        /// <summary>Adds what was tracked in memory before the saved data loaded (<paramref name="early"/>) to it.</summary>
        private static void MergeTrackedBeforeLoad(UserAnalyticsData loaded, UserAnalyticsData early)
        {
            if (early == null || ReferenceEquals(early, loaded))
            {
                return;
            }

            foreach (KeyValuePair<string, FolderUsageStat> pair in early.Folders)
            {
                if (loaded.Folders.TryGetValue(pair.Key, out FolderUsageStat known))
                {
                    known.VisitCount += pair.Value.VisitCount;
                    known.LastVisitedUtc = known.LastVisitedUtc > pair.Value.LastVisitedUtc ? known.LastVisitedUtc : pair.Value.LastVisitedUtc;
                }
                else
                {
                    loaded.Folders[pair.Key] = pair.Value;
                }
            }

            foreach (KeyValuePair<string, FileTypeUsageStat> pair in early.FileTypes)
            {
                if (loaded.FileTypes.TryGetValue(pair.Key, out FileTypeUsageStat known))
                {
                    known.OpenCount += pair.Value.OpenCount;
                    known.LastOpenedUtc = known.LastOpenedUtc > pair.Value.LastOpenedUtc ? known.LastOpenedUtc : pair.Value.LastOpenedUtc;
                }
                else
                {
                    loaded.FileTypes[pair.Key] = pair.Value;
                }
            }

            foreach (KeyValuePair<string, int> pair in early.ViewModeUsage)
            {
                loaded.ViewModeUsage[pair.Key] = (loaded.ViewModeUsage.TryGetValue(pair.Key, out int count) ? count : 0) + pair.Value;
            }

            // The logs are oldest first; what was tracked early is newer than anything saved.
            loaded.RecentSearches.AddRange(early.RecentSearches);
            loaded.RecentFileOpens.AddRange(early.RecentFileOpens);
            loaded.RecentOperations.AddRange(early.RecentOperations);

            for (int i = 0; i < 24; i++)
            {
                loaded.HourlyActivityCounts[i] += early.HourlyActivityCounts[i];
            }

            for (int i = 0; i < 7; i++)
            {
                loaded.DayOfWeekActivityCounts[i] += early.DayOfWeekActivityCounts[i];
            }

            loaded.TotalFolderVisits += early.TotalFolderVisits;
            loaded.TotalFilesOpened += early.TotalFilesOpened;
            loaded.TotalSearches += early.TotalSearches;
            loaded.TotalFileOperationsSucceeded += early.TotalFileOperationsSucceeded;
            loaded.TotalFileOperationsFailed += early.TotalFileOperationsFailed;
            loaded.TotalFoldersCreated += early.TotalFoldersCreated;
            loaded.TotalItemsRenamed += early.TotalItemsRenamed;
            loaded.TotalNetworkOperations += early.TotalNetworkOperations;
            loaded.TotalNetworkTimeouts += early.TotalNetworkTimeouts;
        }

        public bool IsEnabled => _settingsService?.Current?.Analytics?.EnableUsageAnalytics ?? true;

        #region Load / Save

        private UserAnalyticsData LoadFromDisk()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    UserAnalyticsData loaded = JsonSerializer.Deserialize<UserAnalyticsData>(json);
                    if (loaded != null)
                    {
                        NormalizeAfterLoad(loaded);
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load analytics.json — starting with fresh, empty analytics data");
            }

            return new UserAnalyticsData();
        }

        /// <summary>
        /// A hand-edited or partially-corrupt analytics.json (or one
        /// from an older SchemaVersion) must never leave any collection
        /// null — every Track* method assumes these are always usable.
        ///
        /// Every dictionary is also explicitly rebuilt with
        /// OrdinalIgnoreCase here rather than just null-coalesced:
        /// System.Text.Json's deserializer constructs a plain
        /// Dictionary&lt;string,T&gt; using its default (case-sensitive)
        /// comparer, discarding whatever comparer the property's field
        /// initializer originally used. Left unfixed, this would
        /// silently turn Windows' case-insensitive path matching into
        /// case-sensitive matching for every dictionary the moment it's
        /// loaded back from disk — "C:\Users" and "c:\users" would
        /// start being tracked as two different folders.
        /// </summary>
        private static void NormalizeAfterLoad(UserAnalyticsData data)
        {
            data.Folders = Rebuild(data.Folders, PathUtil.Comparer);
            // Extensions and view mode names are not paths: case-insensitive on every OS (they were rebuilt with the path
            // comparer, so on Linux ".JPG" and ".jpg" counted together until a restart and separately after it).
            data.FileTypes = Rebuild(data.FileTypes, StringComparer.OrdinalIgnoreCase);
            data.ViewModeUsage = Rebuild(data.ViewModeUsage, StringComparer.OrdinalIgnoreCase);

            data.RecentSearches = data.RecentSearches ?? new List<SearchLogEntry>();
            data.RecentFileOpens = data.RecentFileOpens ?? new List<RecentFileOpenEntry>();
            data.RecentOperations = data.RecentOperations ?? new List<FileOperationLogEntry>();
            data.Sessions = data.Sessions ?? new List<AppSessionRecord>();
            data.HourlyActivityCounts = data.HourlyActivityCounts != null && data.HourlyActivityCounts.Length == 24
                ? data.HourlyActivityCounts
                : new int[24];
            data.DayOfWeekActivityCounts = data.DayOfWeekActivityCounts != null && data.DayOfWeekActivityCounts.Length == 7
                ? data.DayOfWeekActivityCounts
                : new int[7];

            if (data.FirstUseUtc == default)
            {
                data.FirstUseUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Rebuilds a dictionary with the given comparer regardless of what comparer it was deserialized with — safe to
        /// call on a null source. Last-write-wins coalesces duplicates the comparer considers equal.
        /// </summary>
        private static Dictionary<string, T> Rebuild<T>(Dictionary<string, T> source, IEqualityComparer<string> comparer)
        {
            var result = new Dictionary<string, T>(comparer);
            if (source != null)
            {
                foreach (KeyValuePair<string, T> pair in source)
                {
                    if (pair.Key != null)
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }

            return result;
        }

        private void MarkDirtyAndScheduleSave()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                _dirty = true;
            }

            int debounceMs = _settingsService?.Current?.Analytics?.SaveDebounceMs ?? DefaultSaveDebounceMs;
            if (debounceMs < 200)
            {
                debounceMs = 200;
            }

            try
            {
                // Reschedules the one pending save further out on every
                // call — a burst of rapid tracking calls collapses into
                // a single write once activity goes quiet, the same
                // coalescing pattern FolderChangeWatcher's debounce uses.
                _saveTimer.Change(debounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently with a tracked event — harmless.
            }
        }

        private void OnSaveTimerElapsed(object state)
        {
            FlushToDisk();
        }

        private void OnHeartbeatElapsed(object state)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                bool hasActiveSession;
                lock (_gate)
                {
                    hasActiveSession = _currentSession != null;
                    if (hasActiveSession)
                    {
                        _currentSession.LastAliveUtc = DateTime.UtcNow;
                        _dirty = true;
                    }
                }

                if (hasActiveSession)
                {
                    MarkDirtyAndScheduleSave();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Analytics heartbeat failed");
            }
        }

        /// <summary>
        /// Serializes under the lock (fast, CPU-only, bounded by the
        /// caps in AnalyticsSettings) then writes to disk after
        /// releasing it, so this never holds up a concurrent Track*
        /// call for the duration of the actual file I/O.
        ///
        /// Never writes before CompleteInitialLoad has run. Without
        /// this guard, a save racing ahead of the background initial
        /// load — the app opened and closed again within the
        /// single-digit-millisecond window before that load finishes —
        /// would serialize the constructor's near-empty placeholder
        /// data and overwrite analytics.json, silently destroying
        /// months of real history to persist one just-started session.
        /// The only cost of waiting is that such an ultra-brief run
        /// doesn't get its own session recorded; that's a far better
        /// trade than any risk of clobbering everything already on disk.
        /// </summary>
        private void FlushToDisk()
        {
            if (_disposed)
            {
                return;
            }

            string json;
            lock (_gate)
            {
                if (!_initialLoadComplete)
                {
                    return;
                }

                if (_currentSession != null)
                {
                    _currentSession.LastAliveUtc = DateTime.UtcNow;
                }

                if (!_dirty)
                {
                    return;
                }

                _dirty = false;

                try
                {
                    json = JsonSerializer.Serialize(_data, SerializeOptions);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to serialize analytics data — skipping this save");
                    return;
                }
            }

            try
            {
                string directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to write analytics.json — will retry on the next tracked event");

                // Re-mark dirty so a future save attempts this again —
                // a transient failure (file briefly locked by an AV
                // scan, momentary disk pressure) shouldn't mean this
                // data is lost outright.
                lock (_gate)
                {
                    _dirty = true;
                }
            }
        }

        #endregion

        #region Session tracking

        public void StartSession()
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    var session = new AppSessionRecord
                    {
                        StartUtc = DateTime.UtcNow,
                        LastAliveUtc = DateTime.UtcNow,
                        FoldersVisited = 0
                    };

                    _data.Sessions.Add(session);
                    _currentSession = session;

                    TrimSessionsIfNeeded();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to start an analytics session");
            }
        }

        public void EndSession()
        {
            if (!IsEnabled && _currentSession == null)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_currentSession != null)
                    {
                        _currentSession.LastAliveUtc = DateTime.UtcNow;
                        _currentSession = null;
                        _dirty = true;
                    }
                }

                // Deliberately synchronous, bypassing the debounce — this
                // runs during app shutdown; anything tracked in the last
                // few seconds (up to SaveDebounceMs) would otherwise be
                // lost outright rather than merely delayed.
                FlushToDisk();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to end the analytics session cleanly");
            }
        }

        private void TrimSessionsIfNeeded()
        {
            int max = _settingsService?.Current?.Analytics?.MaxSessionHistory ?? 120;
            if (max < 10)
            {
                max = 10;
            }

            while (_data.Sessions.Count > max)
            {
                _data.Sessions.RemoveAt(0);
            }
        }

        #endregion

        #region Track*

        public void TrackFolderVisited(string path)
        {
            // Inside a password-protected item: its names stay out of the statistics kept on disk.
            if (!IsEnabled || string.IsNullOrEmpty(path) || Security.ProtectedFormat.IsProtectedPath(path))
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_data.Folders.TryGetValue(path, out FolderUsageStat stat))
                    {
                        stat.VisitCount++;
                        stat.LastVisitedUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        _data.Folders[path] = new FolderUsageStat
                        {
                            Path = path,
                            VisitCount = 1,
                            FirstVisitedUtc = DateTime.UtcNow,
                            LastVisitedUtc = DateTime.UtcNow
                        };

                        TrimFoldersIfNeeded();
                    }

                    _data.TotalFolderVisits++;
                    if (_currentSession != null)
                    {
                        _currentSession.FoldersVisited++;
                    }

                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track folder visit for {Path}", path);
            }
        }

        public void TrackFileOpened(string fullPath)
        {
            if (!IsEnabled || Security.ProtectedFormat.IsProtectedPath(fullPath))
            {
                return;
            }

            string extension;
            string name;
            try
            {
                extension = Path.GetExtension(fullPath);
                name = Path.GetFileName(fullPath);
            }
            catch (Exception ex)
            {
                // Path.GetExtension/GetFileName can throw for a path
                // containing characters Path considers invalid — an
                // edge case for real Windows filenames, but not
                // impossible for something that arrived via an unusual
                // network share. Falls back to skipping only the
                // per-file details, not the whole tracking call.
                _logger.Debug(ex, "Failed to parse path for file-open tracking: {Path}", fullPath);
                extension = null;
                name = fullPath;
            }

            string key = string.IsNullOrWhiteSpace(extension) ? "(none)" : extension.Trim().ToLowerInvariant();

            try
            {
                lock (_gate)
                {
                    if (_data.FileTypes.TryGetValue(key, out FileTypeUsageStat stat))
                    {
                        stat.OpenCount++;
                        stat.LastOpenedUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        _data.FileTypes[key] = new FileTypeUsageStat
                        {
                            Extension = key,
                            OpenCount = 1,
                            LastOpenedUtc = DateTime.UtcNow
                        };

                        TrimFileTypesIfNeeded();
                    }

                    if (!string.IsNullOrWhiteSpace(fullPath))
                    {
                        _data.RecentFileOpens.Add(new RecentFileOpenEntry
                        {
                            FullPath = fullPath,
                            Name = string.IsNullOrEmpty(name) ? fullPath : name,
                            TimestampUtc = DateTime.UtcNow
                        });

                        TrimListIfNeeded(_data.RecentFileOpens);
                    }

                    _data.TotalFilesOpened++;
                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track file open for {Path}", fullPath);
            }
        }

        public void TrackSearchPerformed(string query, int resultCount)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _data.RecentSearches.Add(new SearchLogEntry
                    {
                        Query = query,
                        ResultCount = Math.Max(0, resultCount),
                        TimestampUtc = DateTime.UtcNow
                    });

                    TrimListIfNeeded(_data.RecentSearches);

                    _data.TotalSearches++;
                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track search");
            }
        }

        public void TrackFileOperation(string operationType, int successCount, int failureCount, bool wasCancelled)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _data.RecentOperations.Add(new FileOperationLogEntry
                    {
                        OperationType = string.IsNullOrEmpty(operationType) ? "Unknown" : operationType,
                        SuccessCount = Math.Max(0, successCount),
                        FailureCount = Math.Max(0, failureCount),
                        WasCancelled = wasCancelled,
                        TimestampUtc = DateTime.UtcNow
                    });

                    TrimListIfNeeded(_data.RecentOperations);

                    _data.TotalFileOperationsSucceeded += Math.Max(0, successCount);
                    _data.TotalFileOperationsFailed += Math.Max(0, failureCount);
                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track file operation {OperationType}", operationType);
            }
        }

        public void TrackFolderCreated()
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _data.TotalFoldersCreated++;
                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track folder creation");
            }
        }

        /// <summary>
        /// An item was renamed or moved (or an undo/redo put it back): the frequent and recent folders and the recent files
        /// at or under the old path follow it, so Home doesn't offer entries that lead nowhere. Recorded even when tracking
        /// is off: it only corrects what is already stored, never adds anything.
        /// </summary>
        public void TrackPathMoved(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath) || PathUtil.AreSame(fromPath, toPath) && string.Equals(fromPath, toPath, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                bool changed;
                lock (_gate)
                {
                    if (!_initialLoadComplete)
                    {
                        (_movesBeforeLoad ??= new List<(string, string)>()).Add((fromPath, toPath));
                    }

                    changed = MovePathCore(fromPath, toPath);
                    if (changed)
                    {
                        _dirty = true;
                    }
                }

                if (changed)
                {
                    MarkDirtyAndScheduleSave();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to follow a moved path in the analytics");
            }
        }

        /// <summary>Re-points the stored folders and recent files at or under <paramref name="fromPath"/>. Call under _gate.</summary>
        private bool MovePathCore(string fromPath, string toPath)
        {
            bool changed = false;
            string from = PathUtil.TrimTrailingSeparator(fromPath);
            string to = PathUtil.TrimTrailingSeparator(toPath);

            List<KeyValuePair<string, FolderUsageStat>> moved = null;
            foreach (KeyValuePair<string, FolderUsageStat> pair in _data.Folders)
            {
                if (PathUtil.IsSameOrInside(pair.Key, from))
                {
                    (moved ??= new List<KeyValuePair<string, FolderUsageStat>>()).Add(pair);
                }
            }

            if (moved != null)
            {
                foreach (KeyValuePair<string, FolderUsageStat> pair in moved)
                {
                    _data.Folders.Remove(pair.Key);
                }

                foreach (KeyValuePair<string, FolderUsageStat> pair in moved)
                {
                    string newPath = to + pair.Key.Substring(from.Length);
                    pair.Value.Path = newPath;
                    if (_data.Folders.TryGetValue(newPath, out FolderUsageStat existing))
                    {
                        // Moved onto a folder that has its own history: one entry with both.
                        existing.VisitCount += pair.Value.VisitCount;
                        existing.FirstVisitedUtc = existing.FirstVisitedUtc < pair.Value.FirstVisitedUtc ? existing.FirstVisitedUtc : pair.Value.FirstVisitedUtc;
                        existing.LastVisitedUtc = existing.LastVisitedUtc > pair.Value.LastVisitedUtc ? existing.LastVisitedUtc : pair.Value.LastVisitedUtc;
                    }
                    else
                    {
                        _data.Folders[newPath] = pair.Value;
                    }
                }

                changed = true;
            }

            foreach (RecentFileOpenEntry entry in _data.RecentFileOpens)
            {
                if (!string.IsNullOrEmpty(entry.FullPath) && PathUtil.IsSameOrInside(entry.FullPath, from))
                {
                    bool isTheItem = entry.FullPath.Length == from.Length;
                    entry.FullPath = to + entry.FullPath.Substring(from.Length);
                    if (isTheItem)
                    {
                        entry.Name = PathUtil.GetName(entry.FullPath);
                    }

                    changed = true;
                }
            }

            return changed;
        }

        public void TrackItemRenamed(bool success)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (success)
                    {
                        _data.TotalItemsRenamed++;
                    }

                    BumpActivityCounters();
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track rename");
            }
        }

        public void TrackViewModeChanged(string viewModeName)
        {
            if (!IsEnabled || string.IsNullOrEmpty(viewModeName))
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _data.ViewModeUsage[viewModeName] = _data.ViewModeUsage.TryGetValue(viewModeName, out int count)
                        ? count + 1
                        : 1;
                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track view mode change to {ViewMode}", viewModeName);
            }
        }

        public void TrackNetworkOperation(bool timedOut)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _data.TotalNetworkOperations++;
                    if (timedOut)
                    {
                        _data.TotalNetworkTimeouts++;
                    }

                    _dirty = true;
                }

                MarkDirtyAndScheduleSave();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to track network operation");
            }
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private void BumpActivityCounters()
        {
            DateTime nowLocal = DateTime.Now;

            if (_data.HourlyActivityCounts == null || _data.HourlyActivityCounts.Length != 24)
            {
                _data.HourlyActivityCounts = new int[24];
            }

            if (_data.DayOfWeekActivityCounts == null || _data.DayOfWeekActivityCounts.Length != 7)
            {
                _data.DayOfWeekActivityCounts = new int[7];
            }

            _data.HourlyActivityCounts[nowLocal.Hour]++;
            _data.DayOfWeekActivityCounts[(int)nowLocal.DayOfWeek]++;
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private void TrimFoldersIfNeeded()
        {
            int max = _settingsService?.Current?.Analytics?.MaxTrackedFolders ?? 500;
            if (max < 10)
            {
                max = 10;
            }

            if (_data.Folders.Count <= max)
            {
                return;
            }

            double halfLifeDays = _settingsService?.Current?.Analytics?.FavoriteFolderHalfLifeDays ?? 14.0;
            DateTime now = DateTime.UtcNow;

            // Evict the lowest-frecency entries first, not oldest-added
            // or oldest-visited — this keeps whatever's actually most
            // relevant (a mix of frequency and recency) even under
            // sustained heavy, varied use, rather than discarding a
            // folder visited 200 times in favor of one visited once
            // yesterday just because it happens to sort "newer."
            List<string> toRemove = _data.Folders.Values
                .OrderBy(stat => FrecencyScore(stat, now, halfLifeDays))
                .Take(_data.Folders.Count - max)
                .Select(stat => stat.Path)
                .ToList();

            foreach (string path in toRemove)
            {
                _data.Folders.Remove(path);
            }
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private void TrimFileTypesIfNeeded()
        {
            if (_data.FileTypes.Count <= MaxFileTypesTracked)
            {
                return;
            }

            List<string> toRemove = _data.FileTypes.Values
                .OrderBy(f => f.OpenCount)
                .ThenBy(f => f.LastOpenedUtc)
                .Take(_data.FileTypes.Count - MaxFileTypesTracked)
                .Select(f => f.Extension)
                .ToList();

            foreach (string key in toRemove)
            {
                _data.FileTypes.Remove(key);
            }
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private void TrimListIfNeeded<T>(List<T> list)
        {
            int max = _settingsService?.Current?.Analytics?.MaxLogEntries ?? 200;
            if (max < 20)
            {
                max = 20;
            }

            while (list.Count > max)
            {
                list.RemoveAt(0);
            }
        }

        /// <summary>
        /// Frequency weighted by an exponential recency decay — a
        /// well-established technique (the same idea behind browser
        /// address-bar ranking and Windows' own Quick Access), not a
        /// raw "most visits wins" count: a folder visited 50 times two
        /// months ago and never since will eventually rank below one
        /// visited 10 times this week, but not the instant a fresher
        /// folder shows up once — the halving curve is gradual.
        /// </summary>
        private static double FrecencyScore(FolderUsageStat stat, DateTime nowUtc, double halfLifeDays)
        {
            double daysSinceLastVisit = Math.Max(0.0, (nowUtc - stat.LastVisitedUtc).TotalDays);
            double decay = halfLifeDays > 0.0 ? Math.Pow(0.5, daysSinceLastVisit / halfLifeDays) : 1.0;
            return stat.VisitCount * decay;
        }

        #endregion

        #region GetSummary

        public AnalyticsSummary GetSummary(int topFolderCount = 8, int topFileTypeCount = 8, int recentSearchCount = 10, int recentFileOpenCount = 200, int recentlyVisitedFolderCount = 20)
        {
            lock (_gate)
            {
                try
                {
                    return BuildSummary(topFolderCount, topFileTypeCount, recentSearchCount, recentFileOpenCount, recentlyVisitedFolderCount);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to compute the analytics summary — returning an empty one");
                    return BuildEmptySummary();
                }
            }
        }

        /// <summary>
        /// Lightweight alternative to GetSummary, specifically for the
        /// left sidebar's "Frequent folders" section (the same concept
        /// as Windows 11 Explorer's own Quick Access frequent-folders
        /// row) — computes only the frecency-ranked folder list, not
        /// the full dashboard snapshot (file-type breakdown, hourly
        /// activity, recent searches/file-opens, session stats), none
        /// of which the sidebar needs. This matters because of how
        /// often each is called, not just what each returns: GetSummary
        /// is meant to be called once, when the Home dashboard opens;
        /// this is called after every single successful folder
        /// navigation, so it deliberately does the minimum work that
        /// still holds the shared lock correctly.
        /// </summary>
        public IReadOnlyList<FavoriteFolderInsight> GetTopFolders(int count = 3)
        {
            lock (_gate)
            {
                try
                {
                    double halfLifeDays = _settingsService?.Current?.Analytics?.FavoriteFolderHalfLifeDays ?? 14.0;
                    return ComputeFavoriteFolders(count, DateTime.UtcNow, halfLifeDays);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to compute top folders — returning an empty list");
                    return Array.Empty<FavoriteFolderInsight>();
                }
            }
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private List<FavoriteFolderInsight> ComputeFavoriteFolders(int count, DateTime now, double halfLifeDays)
        {
            return _data.Folders.Values
                .Select(stat => new
                {
                    Stat = stat,
                    Score = FrecencyScore(stat, now, halfLifeDays)
                })
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(0, count))
                .Select(x => new FavoriteFolderInsight(
                    x.Stat.Path,
                    DisplayNameForPath(x.Stat.Path),
                    x.Stat.VisitCount,
                    x.Stat.LastVisitedUtc,
                    x.Score))
                .ToList();
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private AnalyticsSummary BuildSummary(int topFolderCount, int topFileTypeCount, int recentSearchCount, int recentFileOpenCount, int recentlyVisitedFolderCount)
        {
            DateTime now = DateTime.UtcNow;
            double halfLifeDays = _settingsService?.Current?.Analytics?.FavoriteFolderHalfLifeDays ?? 14.0;

            List<FavoriteFolderInsight> favorites = ComputeFavoriteFolders(topFolderCount, now, halfLifeDays);

            // Same underlying folder stats as "favorites" above, sorted
            // by plain recency instead of frecency — "folders I visited
            // most recently" is a genuinely different question from
            // "folders I visit often and recently," and the Home
            // dashboard's new "Latest folders opened" section needs the
            // former specifically. Score is still computed (via the
            // same FrecencyScore used for favorites) purely for
            // consistency/diagnostics, not because this list is ordered
            // by it.
            List<FavoriteFolderInsight> recentlyVisitedFolders = _data.Folders.Values
                .Select(stat => new
                {
                    Stat = stat,
                    Score = FrecencyScore(stat, now, halfLifeDays)
                })
                .OrderByDescending(x => x.Stat.LastVisitedUtc)
                .Take(Math.Max(0, recentlyVisitedFolderCount))
                .Select(x => new FavoriteFolderInsight(
                    x.Stat.Path,
                    DisplayNameForPath(x.Stat.Path),
                    x.Stat.VisitCount,
                    x.Stat.LastVisitedUtc,
                    x.Score))
                .ToList();

            int maxFileTypeCount = _data.FileTypes.Count > 0 ? _data.FileTypes.Values.Max(f => f.OpenCount) : 0;
            List<FileTypeInsight> fileTypes = _data.FileTypes.Values
                .OrderByDescending(f => f.OpenCount)
                .Take(Math.Max(0, topFileTypeCount))
                .Select(f => new FileTypeInsight(
                    f.Extension,
                    f.OpenCount,
                    maxFileTypeCount > 0 ? (double)f.OpenCount / maxFileTypeCount * 100.0 : 0.0))
                .ToList();

            int maxHourly = _data.HourlyActivityCounts.Length > 0 ? _data.HourlyActivityCounts.Max() : 0;
            var hourly = new List<HourlyActivityInsight>(24);
            for (int hour = 0; hour < 24; hour++)
            {
                int count = hour < _data.HourlyActivityCounts.Length ? _data.HourlyActivityCounts[hour] : 0;
                hourly.Add(new HourlyActivityInsight(
                    hour,
                    count,
                    maxHourly > 0 ? (double)count / maxHourly * 100.0 : 0.0));
            }

            List<RecentSearchInsight> recentSearches = _data.RecentSearches
                .OrderByDescending(s => s.TimestampUtc)
                .Take(Math.Max(0, recentSearchCount))
                .Select(s => new RecentSearchInsight(s.Query, s.ResultCount, s.TimestampUtc))
                .ToList();

            // Deliberately NOT split by category (video/music/document)
            // here — that's a presentation concern belonging to
            // AnalyticsDashboardViewModel (which already references
            // FileCategoryClassifier), not this service. Splitting by
            // category and THEN taking the top N of each would also be
            // wrong done here: this returns the top N most recent
            // opens overall, unfiltered, so the caller can filter by
            // category first and take 20 of each from within that
            // larger pool — otherwise a folder full of recently-opened
            // documents could crowd out an occasional video from ever
            // appearing, even though it really was opened recently.
            List<RecentFileInsight> recentFileOpens = _data.RecentFileOpens
                .OrderByDescending(f => f.TimestampUtc)
                .Take(Math.Max(0, recentFileOpenCount))
                .Select(f => new RecentFileInsight(f.FullPath, f.Name, f.TimestampUtc))
                .ToList();

            SessionInsights sessionInsights = ComputeSessionInsights(now);

            long totalNetworkOps = _data.TotalNetworkOperations;
            long totalTimeouts = _data.TotalNetworkTimeouts;
            double reliabilityPercent = totalNetworkOps > 0
                ? (1.0 - (double)totalTimeouts / totalNetworkOps) * 100.0
                : 100.0;

            long totalOps = _data.TotalFileOperationsSucceeded + _data.TotalFileOperationsFailed;
            double operationSuccessRate = totalOps > 0
                ? (double)_data.TotalFileOperationsSucceeded / totalOps * 100.0
                : 100.0;

            int daysSinceFirstUse = Math.Max(0, (int)(now - _data.FirstUseUtc).TotalDays);

            return new AnalyticsSummary(
                isEnabled: IsEnabled,
                firstUseUtc: _data.FirstUseUtc,
                daysSinceFirstUse: daysSinceFirstUse,
                totalFolderVisits: _data.TotalFolderVisits,
                uniqueFoldersVisited: _data.Folders.Count,
                totalFilesOpened: _data.TotalFilesOpened,
                totalSearches: _data.TotalSearches,
                totalFileOperationsSucceeded: _data.TotalFileOperationsSucceeded,
                totalFileOperationsFailed: _data.TotalFileOperationsFailed,
                operationSuccessRatePercent: operationSuccessRate,
                totalFoldersCreated: _data.TotalFoldersCreated,
                totalItemsRenamed: _data.TotalItemsRenamed,
                networkReliabilityPercent: reliabilityPercent,
                totalNetworkOperations: totalNetworkOps,
                totalNetworkTimeouts: totalTimeouts,
                mostActiveHour: sessionInsights.MostActiveHour,
                mostActiveDayOfWeek: sessionInsights.MostActiveDayOfWeek,
                averageSessionLength: sessionInsights.AverageSessionLength,
                currentStreakDays: sessionInsights.CurrentStreakDays,
                longestStreakDays: sessionInsights.LongestStreakDays,
                totalUsageTime: sessionInsights.TotalUsageTime,
                totalSessions: _data.Sessions.Count,
                favoriteFolders: favorites,
                fileTypeBreakdown: fileTypes,
                hourlyActivity: hourly,
                recentSearches: recentSearches,
                recentFileOpens: recentFileOpens,
                recentlyVisitedFolders: recentlyVisitedFolders);
        }

        private static AnalyticsSummary BuildEmptySummary()
        {
            return new AnalyticsSummary(
                isEnabled: false,
                firstUseUtc: DateTime.UtcNow,
                daysSinceFirstUse: 0,
                totalFolderVisits: 0,
                uniqueFoldersVisited: 0,
                totalFilesOpened: 0,
                totalSearches: 0,
                totalFileOperationsSucceeded: 0,
                totalFileOperationsFailed: 0,
                operationSuccessRatePercent: 100.0,
                totalFoldersCreated: 0,
                totalItemsRenamed: 0,
                networkReliabilityPercent: 100.0,
                totalNetworkOperations: 0,
                totalNetworkTimeouts: 0,
                mostActiveHour: 0,
                mostActiveDayOfWeek: "Unknown",
                averageSessionLength: TimeSpan.Zero,
                currentStreakDays: 0,
                longestStreakDays: 0,
                totalUsageTime: TimeSpan.Zero,
                totalSessions: 0,
                favoriteFolders: Array.Empty<FavoriteFolderInsight>(),
                fileTypeBreakdown: Array.Empty<FileTypeInsight>(),
                hourlyActivity: Array.Empty<HourlyActivityInsight>(),
                recentSearches: Array.Empty<RecentSearchInsight>(),
                recentFileOpens: Array.Empty<RecentFileInsight>(),
                recentlyVisitedFolders: Array.Empty<FavoriteFolderInsight>());
        }

        private static string DisplayNameForPath(string path)
        {
            try
            {
                return PathUtil.GetName(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private sealed class SessionInsights
        {
            public int MostActiveHour;
            public string MostActiveDayOfWeek = "Unknown";
            public TimeSpan AverageSessionLength;
            public int CurrentStreakDays;
            public int LongestStreakDays;
            public TimeSpan TotalUsageTime;
        }

        /// <summary>Must be called only while already holding _gate.</summary>
        private SessionInsights ComputeSessionInsights(DateTime nowUtc)
        {
            var insights = new SessionInsights();

            if (_data.HourlyActivityCounts != null && _data.HourlyActivityCounts.Length == 24)
            {
                int best = 0;
                for (int h = 1; h < 24; h++)
                {
                    if (_data.HourlyActivityCounts[h] > _data.HourlyActivityCounts[best])
                    {
                        best = h;
                    }
                }
                insights.MostActiveHour = best;
            }

            if (_data.DayOfWeekActivityCounts != null && _data.DayOfWeekActivityCounts.Length == 7)
            {
                int best = 0;
                for (int d = 1; d < 7; d++)
                {
                    if (_data.DayOfWeekActivityCounts[d] > _data.DayOfWeekActivityCounts[best])
                    {
                        best = d;
                    }
                }

                if (_data.DayOfWeekActivityCounts[best] > 0)
                {
                    insights.MostActiveDayOfWeek = ((DayOfWeek)best).ToString();
                }
            }

            if (_data.Sessions.Count == 0)
            {
                return insights;
            }

            long totalTicks = 0;
            int counted = 0;
            var usedDates = new SortedSet<DateTime>();

            foreach (AppSessionRecord session in _data.Sessions)
            {
                DateTime end = session.LastAliveUtc > session.StartUtc ? session.LastAliveUtc : session.StartUtc;
                TimeSpan duration = end - session.StartUtc;

                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }
                else if (duration > TimeSpan.FromHours(24))
                {
                    // A guard against a corrupt/stale record (e.g. a
                    // heartbeat that never got to update LastAliveUtc
                    // for an exceptionally long-running session) rather
                    // than letting one bad entry skew the average or the
                    // total wildly.
                    duration = TimeSpan.FromHours(24);
                }

                totalTicks += duration.Ticks;
                counted++;
                insights.TotalUsageTime += duration;
                usedDates.Add(session.StartUtc.ToLocalTime().Date);
            }

            if (counted > 0)
            {
                insights.AverageSessionLength = TimeSpan.FromTicks(totalTicks / counted);
            }

            if (usedDates.Count > 0)
            {
                DateTime[] dates = usedDates.ToArray(); // ascending

                int running = 1;
                int longest = 1;
                for (int i = 1; i < dates.Length; i++)
                {
                    running = (dates[i] - dates[i - 1]).Days == 1 ? running + 1 : 1;
                    if (running > longest)
                    {
                        longest = running;
                    }
                }
                insights.LongestStreakDays = longest;

                DateTime today = nowUtc.ToLocalTime().Date;
                DateTime cursor = usedDates.Contains(today) ? today : today.AddDays(-1);

                if (usedDates.Contains(cursor))
                {
                    int current = 1;
                    DateTime prev = cursor.AddDays(-1);
                    while (usedDates.Contains(prev))
                    {
                        current++;
                        prev = prev.AddDays(-1);
                    }
                    insights.CurrentStreakDays = current;
                }
            }

            return insights;
        }

        #endregion

        public void ClearAllData()
        {
            try
            {
                lock (_gate)
                {
                    _data = new UserAnalyticsData();
                    _currentSession = new AppSessionRecord
                    {
                        StartUtc = DateTime.UtcNow,
                        LastAliveUtc = DateTime.UtcNow,
                        FoldersVisited = 0
                    };
                    _data.Sessions.Add(_currentSession);
                    _dirty = true;
                }

                FlushToDisk();
                _logger.Information("Analytics data cleared by user request");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clear analytics data");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Must run before _disposed is set — FlushToDisk (called
                // from EndSession) checks it and would otherwise bail
                // out, silently discarding this final save.
                EndSession();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to finalize the analytics session on shutdown");
            }

            _disposed = true;

            try { _heartbeatTimer.Dispose(); } catch { /* best-effort */ }
            try { _saveTimer.Dispose(); } catch { /* best-effort */ }
        }
    }
}
