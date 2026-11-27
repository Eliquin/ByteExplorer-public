// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Services;
using ByteSearcher;
using Serilog;

namespace ByteExplorer.Infrastructure.Search
{
    /// <summary>Where the search index is in its life.</summary>
    public enum SearchIndexState
    {
        NotStarted,

        /// <summary>Loading the saved index.</summary>
        Checking,

        /// <summary>A build is running (searches use the previous index, if there is one).</summary>
        Building,

        Ready,

        /// <summary>The first build was stopped: there is no index yet.</summary>
        Cancelled,

        /// <summary>No index could be loaded or built.</summary>
        Error
    }

    /// <summary>
    /// Owns the app's <see cref="SearchIndex"/>: loads it at startup (or builds it the first time), rebuilds it in the
    /// background on a schedule, keeps it current in between, and runs searches.
    /// <list type="bullet">
    /// <item>Search is available whenever an index is loaded: while a rebuild runs, after a rebuild was stopped or failed,
    /// and with a partial index. A rebuild never deletes the current index (the new one replaces it atomically when
    /// complete). The previous service deleted the index files before every rebuild, the scheduled one included (every 6
    /// hours, or 30 seconds after a launch when overdue), and disabled search until the whole crawl finished.</item>
    /// <item>Live updates: folders reported by <see cref="NotifyFoldersChanged"/> (the app's own operations, folders being
    /// viewed) and by file system watchers (<see cref="IndexChangeWatcher"/>, where affordable) are applied in batches,
    /// 400 ms after the last report, then saved 15 seconds after the last update, and at exit.</item>
    /// <item>A watcher that lost events, or an index carrying many deleted entries, schedules a full rebuild.</item>
    /// <item>File contents (Settings › Search › "Index file contents"): a <see cref="ContentIndexer"/> keeps a content index
    /// in the index folder's "Content" subfolder in step with the name index, in the background, paused while the name
    /// index is built; <c>content:</c> searches and, when asked, the words use it.</item>
    /// </list>
    /// Status changes are raised on background threads (<see cref="StatusChanged"/>, <see cref="IndexChanged"/>).
    /// </summary>
    public sealed class GlobalSearchService : IDisposable
    {
        private static readonly ILogger Logger = Log.Logger.ForContext<GlobalSearchService>();
        private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(15);

        private readonly string _indexDirectory;
        private readonly SearchIndex _index;
        private readonly ISettingsService _settingsService;
        private readonly object _gate = new object();
        private readonly object _pendingGate = new object();
        private readonly HashSet<string> _pendingFolders = new HashSet<string>(ByteExplorer.Core.PathUtil.Comparer);
        private readonly Timer _applyTimer;
        private readonly Timer _saveTimer;
        private readonly Timer _rebuildTimer;
        private bool _disposed;
        private int _applying;
        private SearchIndexState _state = SearchIndexState.NotStarted;
        private string _shortStatus = "No index";
        private string _detailStatus = "The search index has not been loaded yet.";
        private string _lastError;
        private CancellationTokenSource _buildCts;
        private Task _buildTask = Task.CompletedTask;
        private int _generation;
        private IndexChangeWatcher _watcher;
        private IndexBuildProgress _progress;
        private readonly string _contentDirectory;
        private readonly object _contentGate = new object();
        private readonly object _contentApplyGate = new object();
        private ContentIndex _contentIndex;
        private ContentIndexer _contentIndexer;
        private ContentIndexerOptions _contentOptions;

        public GlobalSearchService(ISettingsService settingsService = null, string indexDirectory = null)
        {
            _settingsService = settingsService;
            _indexDirectory = indexDirectory ?? ByteExplorer.Core.AppPaths.Combine("Index");
            try
            {
                Directory.CreateDirectory(_indexDirectory);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not create the search index folder {Dir}", _indexDirectory);
            }

            _index = new SearchIndex(_indexDirectory);
            _index.IndexChanged += (_, _) =>
            {
                Raise(IndexChanged);
                _contentIndexer?.RequestSync(TimeSpan.FromSeconds(5)); // read what changed, once the burst is over
            };
            _contentDirectory = Path.Combine(_indexDirectory, "Content");
            _applyTimer = new Timer(_ => ApplyPending(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _rebuildTimer = new Timer(_ => OnRebuildDue(), null, Timeout.Infinite, Timeout.Infinite);
            Logger.Information("Search index folder: {Dir}", _indexDirectory);
        }

        /// <summary>The state or the status texts changed.</summary>
        public event EventHandler StatusChanged;

        /// <summary>The searchable data changed (a build finished, the index loaded, a live update): re-run a shown search.</summary>
        public event EventHandler IndexChanged;

        public SearchIndexState State
        {
            get { lock (_gate) return _state; }
        }

        /// <summary>A few words for the search box (also its placeholder when search is available).</summary>
        public string ShortStatus
        {
            get { lock (_gate) return _shortStatus; }
        }

        /// <summary>A sentence or two for the tooltip and the results header.</summary>
        public string DetailStatus
        {
            get { lock (_gate) return _detailStatus; }
        }

        public string LastError
        {
            get { lock (_gate) return _lastError; }
        }

        /// <summary>An index is loaded: searches can run (also during a rebuild).</summary>
        public bool IsIndexReady => _index.IsIndexLoaded;

        public bool IsSearchEnabled => _index.IsIndexLoaded;

        public bool IsIndexing
        {
            get
            {
                lock (_gate)
                {
                    return _state == SearchIndexState.Building || _state == SearchIndexState.Checking;
                }
            }
        }

        public long TotalItems => _index.TotalItems;

        public bool IsPartialIndex => _index.IsPartialIndex;

        /// <summary>The roots of the loaded index (drives, home folder, volumes).</summary>
        public IReadOnlyList<string> IndexedRoots => _index.IndexedRoots;

        /// <summary>
        /// The content index's state for the settings: off, or how many files' contents are indexed and how many are left.
        /// </summary>
        public string ContentStatus
        {
            get
            {
                ContentIndexer indexer = _contentIndexer;
                if (indexer != null)
                {
                    return indexer.Status;
                }

                if (_contentIndex != null)
                {
                    return $"Kept by another ByteExplorer window: {_contentIndex.DocumentCount:N0} files' contents searchable here.";
                }

                return (_settingsService?.Current?.Index?.IndexFileContents ?? false)
                    ? "Starts once the search index is ready."
                    : "Off: only names are searched.";
            }
        }

        /// <summary>The content indexer, when content indexing is on (tests).</summary>
        internal ContentIndexer ContentIndexer => _contentIndexer;

        /// <summary>The latest progress of a running build.</summary>
        public IndexBuildProgress Progress
        {
            get { lock (_gate) return _progress; }
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Loads the saved index, or builds one when there is none (or it cannot be read).</summary>
        public Task EnsureIndexAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_state == SearchIndexState.Building || _state == SearchIndexState.Checking)
                {
                    return _buildTask;
                }

                if (_index.IsIndexLoaded && _state == SearchIndexState.Ready)
                {
                    return Task.CompletedTask;
                }

                return StartLocked(loadFirst: true, cancellationToken);
            }
        }

        /// <summary>Rebuilds the index in the background; search keeps using the current one until the new one is ready.</summary>
        public Task RefreshIndexAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                CancelBuildLocked();
                return StartLocked(loadFirst: false, cancellationToken);
            }
        }

        /// <summary>Stops a running build; the current index (if any) stays in use.</summary>
        public void StopIndexing()
        {
            lock (_gate)
            {
                if (_state != SearchIndexState.Building && _state != SearchIndexState.Checking)
                {
                    return;
                }

                Logger.Information("Indexing stopped by the user");
                CancelBuildLocked();
            }

            if (_index.IsIndexLoaded)
            {
                PublishReady(_generation, "Index update stopped: search uses the previous index.");
            }
            else
            {
                Publish(_generation, SearchIndexState.Cancelled, "Index stopped", "Indexing was stopped before it finished. Refresh the index from the search icon's menu to build it.");
            }
        }

        /// <summary>Runs a search on a background thread (see <see cref="SearchQuery"/> for the syntax).</summary>
        public Task<SearchResults> SearchAsync(string query, SearchOptions options, CancellationToken cancellationToken)
        {
            if (!_index.IsIndexLoaded || string.IsNullOrWhiteSpace(query) && options?.Kind == null && options?.FoldersOnly == null)
            {
                return Task.FromResult(SearchResults.Empty);
            }

            return Task.Run(() => _index.Search(query, options, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Tells the index that these folders' contents may have changed (the app copied, moved, renamed, deleted or
        /// created something there, or a viewed folder changed). Applied in a batch shortly after the last report.
        /// </summary>
        public void NotifyFoldersChanged(IEnumerable<string> folders)
        {
            if (_disposed || folders == null || !_index.IsIndexLoaded)
            {
                return;
            }

            bool any = false;
            lock (_pendingGate)
            {
                foreach (string folder in folders)
                {
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        any |= _pendingFolders.Add(folder);
                    }
                }
            }

            if (any)
            {
                TryChange(_applyTimer, ApplyDelay);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_gate)
            {
                CancelBuildLocked();
            }

            foreach (Timer timer in new[] { _applyTimer, _saveTimer, _rebuildTimer })
            {
                timer.Dispose();
            }

            _watcher?.Dispose();
            lock (_contentApplyGate)
            {
                StopContentIndexing(deleteIndex: false);
            }

            try
            {
                _index.SaveIfChanged(); // live updates made since the last save
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Saving the search index at exit failed");
            }

            _index.Dispose();
        }

        // ------------------------------------------------------------------ load and build

        private Task StartLocked(bool loadFirst, CancellationToken externalToken)
        {
            int generation = ++_generation;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _buildCts = cts;
            _buildTask = Task.Run(() => RunAsync(generation, loadFirst, cts.Token), CancellationToken.None);
            return _buildTask;
        }

        private void CancelBuildLocked()
        {
            try
            {
                _buildCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _generation++;
        }

        private async Task RunAsync(int generation, bool loadFirst, CancellationToken cancellationToken)
        {
            try
            {
                if (loadFirst && !_index.IsIndexLoaded && _index.IndexExistsAndValid())
                {
                    Publish(generation, SearchIndexState.Checking, "Loading index…", "Loading the search index from disk.");
                    try
                    {
                        _index.LoadIndex();
                        Logger.Information("Search index loaded: {Count} items", _index.TotalItems);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Logger.Warning(ex, "The saved search index could not be loaded; building a new one");
                    }
                }

                if (loadFirst && _index.IsIndexLoaded)
                {
                    PublishReady(generation, null);
                    StartWatching();
                    ApplyContentSettings();
                    if (_index.IsPartialIndex)
                    {
                        ScheduleRebuild(TimeSpan.FromSeconds(10)); // the last build was paused: finish the job soon
                    }
                    else
                    {
                        ScheduleAutoRebuild();
                    }

                    return;
                }

                await BuildAsync(generation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // StopIndexing/RefreshIndexAsync already published the outcome.
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Search index load or build failed");
                if (_index.IsIndexLoaded)
                {
                    PublishReady(generation, "The search index couldn't be updated; results may be out of date.", ex.Message);
                }
                else
                {
                    Publish(generation, SearchIndexState.Error, "Search unavailable", "Search is unavailable: the index couldn't be read or built. The details are in the log.", ex.Message);
                }
            }
        }

        private async Task BuildAsync(int generation, CancellationToken cancellationToken)
        {
            ConfigureIndex();
            bool hadIndex = _index.IsIndexLoaded;
            Publish(generation, SearchIndexState.Building,
                hadIndex ? "Updating index…" : "Indexing…",
                hadIndex ? "Rebuilding the search index in the background. Search uses the current index meanwhile." : "Building the search index. This can take a while on large drives.");
            Logger.Information("Search index build started");

            List<string> locations = _settingsService?.Current?.Index?.Locations?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            IndexBuildSummary summary;
            SetContentPaused(true); // the crawl has the disk; contents are read after it
            try
            {
                summary = await _index.BuildAsync(
                    new DirectProgress(p => OnBuildProgress(generation, hadIndex, p)),
                    cancellationToken,
                    restrictToRoots: locations != null && locations.Count > 0 ? locations : null).ConfigureAwait(false);
            }
            finally
            {
                SetContentPaused(false);
            }

            Logger.Information("Search index built: {Items} items in {Seconds:F1} s ({Unreadable} unreadable folders)", summary.Items, summary.Duration.TotalSeconds, summary.UnreadableFolders);
            PersistLastBuild();
            if (!IsCurrent(generation))
            {
                return;
            }

            PublishReady(generation, null);
            StartWatching();
            ApplyContentSettings();
            ScheduleAutoRebuild();
        }

        private void OnBuildProgress(int generation, bool hadIndex, IndexBuildProgress progress)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _progress = progress;
            }

            string items = FormatCount(progress.Items);
            Publish(generation, SearchIndexState.Building,
                hadIndex ? $"Updating index… {items}" : $"Indexing… {items}",
                $"{progress.Items:N0} items in {progress.Folders:N0} folders so far ({progress.Elapsed:mm\\:ss}).\n{progress.CurrentFolder}" +
                (hadIndex ? "\nSearch uses the current index until the new one is ready." : string.Empty));
        }

        /// <summary>Exclusions from the settings plus the index's own folder (its saves are not worth indexing).</summary>
        private void ConfigureIndex()
        {
            var settings = _settingsService?.Current?.Index;
            _index.ExcludedPathPrefixes.Clear();
            _index.ExcludedPathPrefixes.Add(_indexDirectory);
            foreach (string folder in settings?.ExcludedFolders ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    _index.ExcludedPathPrefixes.Add(folder);
                }
            }

            _index.ExcludedDirectoryNames.Clear();
            _index.ExcludedDirectoryNames.UnionWith(SearchIndex.DefaultExcludedDirectoryNames);
            foreach (string name in settings?.ExcludedNames ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _index.ExcludedDirectoryNames.Add(name.Trim());
                }
            }
        }

        private void PersistLastBuild()
        {
            if (_settingsService?.Current?.Index == null)
            {
                return;
            }

            try
            {
                _settingsService.Current.Index.LastBuildUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                _settingsService.SaveAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not record the index build time");
            }
        }

        // ------------------------------------------------------------------ live updates

        private void StartWatching()
        {
            IndexChangeWatcher previous = _watcher;
            _watcher = null;
            previous?.Dispose();
            if (_disposed || !(_settingsService?.Current?.Index?.WatchForChanges ?? true))
            {
                return;
            }

            if (!IndexChangeWatcher.CanWatch(_index.TotalFolders))
            {
                Logger.Information("Changes on disk are not watched ({Folders} folders exceed what this system can watch); the index follows the app's own changes and rebuilds on schedule", _index.TotalFolders);
                return;
            }

            _watcher = new IndexChangeWatcher(_index.IndexedRoots, _indexDirectory, NotifyFoldersChanged, () => ScheduleRebuild(TimeSpan.FromMinutes(1)));
            Logger.Information("Watching {Count} indexed root(s) for changes", _watcher.WatchedRoots);
        }

        private void ApplyPending()
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.Exchange(ref _applying, 1) == 1)
            {
                TryChange(_applyTimer, ApplyDelay); // one update at a time: try again shortly
                return;
            }

            try
            {
                List<string> folders;
                lock (_pendingGate)
                {
                    folders = _pendingFolders.ToList();
                    _pendingFolders.Clear();
                }

                if (folders.Count == 0)
                {
                    return;
                }

                int changed = _index.ApplyChanges(folders);
                if (changed > 0)
                {
                    Logger.Debug("Search index updated: {Changed} entries in {Folders} folder(s)", changed, folders.Count);
                    TryChange(_saveTimer, SaveDelay);
                    PublishReadyIfIdle();
                }

                if (_index.NeedsRebuild)
                {
                    ScheduleRebuild(TimeSpan.FromMinutes(1));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Applying changes to the search index failed");
            }
            finally
            {
                Interlocked.Exchange(ref _applying, 0);
                bool more;
                lock (_pendingGate)
                {
                    more = _pendingFolders.Count > 0;
                }

                if (more)
                {
                    TryChange(_applyTimer, ApplyDelay);
                }
            }
        }

        private void SaveNow()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _index.SaveIfChanged();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Saving the updated search index failed; it is retried after the next change");
            }
        }

        // ------------------------------------------------------------------ file contents

        /// <summary>
        /// Applies Settings › Search's content options: starts the content indexer (once an index is loaded) when "Index file
        /// contents" is on, stops it and deletes the content index when it is off, and takes a new maximum file size (files
        /// newly in or out of range are read or dropped at the next pass).
        /// </summary>
        public void ApplyContentSettings()
        {
            // One change at a time: stopping waits for the worker, and a quick off-on must not start a new index while the
            // old one is being deleted.
            lock (_contentApplyGate)
            {
                ApplyContentSettingsCore();
            }
        }

        private void ApplyContentSettingsCore()
        {
            if (_disposed)
            {
                return;
            }

            var settings = _settingsService?.Current?.Index;
            bool enabled = settings?.IndexFileContents ?? false;
            if (!enabled)
            {
                StopContentIndexing(deleteIndex: true);
                Raise(StatusChanged);
                return;
            }

            if (!_index.IsIndexLoaded)
            {
                return; // started when the index is ready
            }

            lock (_contentGate)
            {
                ContentIndexerOptions options = _contentOptions ?? new ContentIndexerOptions();
                long maxSize = Math.Clamp(settings.ContentMaxFileSizeMB, 1, 500) * 1024L * 1024L;
                bool sizeChanged = options.MaxFileSize != maxSize;
                options.MaxFileSize = maxSize;
                var excluded = new List<string>(ByteExplorer.ViewModels.Lens.LensCatalog.SystemFolders) { _indexDirectory };
                excluded.AddRange((settings.ExcludedFolders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)));
                options.ExcludedFolders = excluded;
                _contentOptions = options;
                if (_contentIndex == null)
                {
                    _contentIndex = new ContentIndex(_contentDirectory);
                    if (_contentIndex.TryLock())
                    {
                        _contentIndexer = new ContentIndexer(_contentIndex, () => _index.CurrentData, () => _index.IsSearching, options);
                        _contentIndexer.StatusChanged += (_, _) => Raise(StatusChanged);
                        _index.Content = _contentIndex;
                        _contentIndexer.Start();
                        Logger.Information("Content indexing started ({Folder})", _contentDirectory);
                    }
                    else
                    {
                        // Another ByteExplorer keeps the content index: search what it has committed, write nothing.
                        ContentIndex shared = _contentIndex;
                        _index.Content = shared;
                        Task.Run(() => shared.Load());
                        Logger.Information("The content index is kept by another ByteExplorer; it is searched read-only");
                    }
                }
                else if (sizeChanged)
                {
                    _contentIndexer.RequestSync(TimeSpan.FromSeconds(2));
                }
            }

            Raise(StatusChanged);
        }

        private void StopContentIndexing(bool deleteIndex)
        {
            ContentIndexer indexer;
            ContentIndex index;
            lock (_contentGate)
            {
                indexer = _contentIndexer;
                index = _contentIndex;
                _contentIndexer = null;
                _contentIndex = null;
                _index.Content = null;
            }

            indexer?.Dispose();
            if (index == null)
            {
                if (deleteIndex && File.Exists(Path.Combine(_contentDirectory, ContentIndex.DocumentsFileName)))
                {
                    // Turned off while the app was closed: the files of the last run go too (unless another instance uses them).
                    var leftover = new ContentIndex(_contentDirectory);
                    if (leftover.TryLock())
                    {
                        leftover.Clear();
                    }

                    leftover.Dispose();
                }

                return;
            }

            if (deleteIndex)
            {
                index.Clear();
                Logger.Information("Content indexing turned off; the content index was deleted");
            }

            index.Dispose();
        }

        private void SetContentPaused(bool paused)
        {
            ContentIndexer indexer = _contentIndexer;
            if (indexer != null)
            {
                indexer.Paused = paused;
            }
        }

        // ------------------------------------------------------------------ rebuild schedule

        private void ScheduleAutoRebuild()
        {
            var settings = _settingsService?.Current?.Index;
            if (_disposed || settings == null || !settings.EnableAutoRebuild)
            {
                return;
            }

            double hours = Math.Clamp(settings.AutoRebuildIntervalHours, 0.5, 720);
            DateTime last = new[] { settings.GetLastBuildUtcOrMinValue(), _index.LastBuildDate }.Max();
            TimeSpan delay = last == DateTime.MinValue ? TimeSpan.FromHours(hours) : last.AddHours(hours) - DateTime.UtcNow;

            // Overdue (the app was closed past the interval): after startup has settled. The rebuild runs in the
            // background and search keeps working, so it no longer matters much when it starts.
            ScheduleRebuild(delay < TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delay);
        }

        /// <summary>
        /// Applies a change of IndexSettings.EnableAutoRebuild or AutoRebuildIntervalHours made while the app runs:
        /// the next automatic rebuild is planned again from the last build (or cancelled when turned off).
        /// </summary>
        public void ApplyScheduleSettings()
        {
            if (_disposed)
            {
                return;
            }

            if (_settingsService?.Current?.Index?.EnableAutoRebuild == true)
            {
                ScheduleAutoRebuild();
            }
            else
            {
                TryChange(_rebuildTimer, Timeout.InfiniteTimeSpan);
            }
        }

        private void ScheduleRebuild(TimeSpan delay)
        {
            if (_disposed)
            {
                return;
            }

            double milliseconds = Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue - 1);
            TryChange(_rebuildTimer, TimeSpan.FromMilliseconds(milliseconds));
        }

        private void OnRebuildDue()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_state == SearchIndexState.Building || _state == SearchIndexState.Checking)
                {
                    return; // running already; it schedules the next one when done
                }

                Logger.Information("Scheduled search index rebuild starting");
                StartLocked(loadFirst: false, CancellationToken.None);
            }
        }

        // ------------------------------------------------------------------ status

        private void PublishReady(int generation, string note, string lastError = null)
        {
            long items = _index.TotalItems;
            string age = DescribeAge(_index.LastBuildDate);
            string watching = _watcher != null ? "Following changes on disk." : "Updated by the app's own changes and rebuilt on schedule.";
            string detail = $"{items:N0} items indexed ({_index.TotalFiles:N0} files, {_index.TotalFolders:N0} folders), built {age}." +
                            (_index.IsPartialIndex ? " The last build was paused: some places may be missing." : string.Empty) +
                            "\n" + watching + (note != null ? "\n" + note : string.Empty);
            Publish(generation, SearchIndexState.Ready, items == 1 ? "Search 1 item" : $"Search {FormatCount(items)} items", detail, lastError);
        }

        private void PublishReadyIfIdle()
        {
            int generation;
            lock (_gate)
            {
                if (_state != SearchIndexState.Ready)
                {
                    return;
                }

                generation = _generation;
            }

            PublishReady(generation, null);
        }

        private void Publish(int generation, SearchIndexState state, string shortStatus, string detailStatus, string lastError = null)
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    return;
                }

                _state = state;
                _shortStatus = shortStatus ?? string.Empty;
                _detailStatus = detailStatus ?? string.Empty;
                _lastError = lastError;
            }

            Raise(StatusChanged);
        }

        private bool IsCurrent(int generation)
        {
            lock (_gate)
            {
                return generation == _generation && !_disposed;
            }
        }

        private void Raise(EventHandler handler)
        {
            try
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "A search service event handler failed");
            }
        }

        private static void TryChange(Timer timer, TimeSpan due)
        {
            try
            {
                timer.Change(due, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>"950", "12.3K", "1.2M".</summary>
        public static string FormatCount(long count) =>
            count >= 1_000_000 ? (count / 1_000_000d).ToString("0.#") + "M"
            : count >= 10_000 ? (count / 1_000d).ToString("0") + "K"
            : count.ToString("N0");

        private static string DescribeAge(DateTime utc)
        {
            if (utc == DateTime.MinValue)
            {
                return "at an unknown time";
            }

            TimeSpan age = DateTime.UtcNow - utc;
            return age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
                : $"{(int)age.TotalDays} days ago";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GlobalSearchService));
            }
        }

        private sealed class DirectProgress : IProgress<IndexBuildProgress>
        {
            private readonly Action<IndexBuildProgress> _report;

            public DirectProgress(Action<IndexBuildProgress> report) => _report = report;

            public void Report(IndexBuildProgress value) => _report(value);
        }
    }
}
