// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ByteExplorer.Core;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Search;
using ByteSearcher;
using Serilog;

namespace ByteExplorer.ViewModels.Search
{
    /// <summary>A quick filter of the results view ("Pictures", "Folders"...).</summary>
    public sealed class SearchFilterChip : ViewModelBase
    {
        private bool _isSelected;

        public SearchFilterChip(string label, FileKind? kind, bool? foldersOnly)
        {
            Label = label;
            Kind = kind;
            FoldersOnly = foldersOnly;
        }

        public string Label { get; }

        public FileKind? Kind { get; }

        public bool? FoldersOnly { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>
    /// Global search as the window shows it: the query (searched as you type, 60 ms after the last key), the order, the
    /// quick filters and the "this folder" scope, the results (every match, scrollable, see
    /// <see cref="SearchResultList"/>) and their status line. Re-runs itself when the index changes (a build finished, a
    /// file was created or deleted), keeping the selected item selected. UI thread.
    /// </summary>
    public sealed class SearchViewModel : ViewModelBase
    {
        private const int TypingDelayMs = 60;
        private const int IndexChangeDelayMs = 700;

        private readonly GlobalSearchService _service;
        private readonly Func<FileListViewModel> _activePane;
        private readonly ISettingsService _settings;
        private readonly IUserAnalyticsService _analytics;
        private readonly ILogger _logger;
        private readonly RelayCommand _sortCommand;
        private CancellationTokenSource _searchCts;
        private CancellationTokenSource _refreshCts;
        private string _query = string.Empty;
        private SearchResultList _results;
        private bool _isActive;
        private bool _isRunning;
        private string _statusText = string.Empty;
        private SearchSort _sort = SearchSort.Relevance;
        private bool _descending;
        private bool _scopeToCurrentFolder;
        private SearchResultRow _selectedRow;
        private string _selectedPath;
        private int _generation;

        public SearchViewModel(GlobalSearchService service, Func<FileListViewModel> activePane, ISettingsService settings, IUserAnalyticsService analytics = null, ILogger logger = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _activePane = activePane ?? (() => null);
            _settings = settings;
            _analytics = analytics;
            _logger = (logger ?? Log.Logger).ForContext<SearchViewModel>();
            Filters = new[]
            {
                new SearchFilterChip("All", null, null) { IsSelected = true },
                new SearchFilterChip("Folders", null, true),
                new SearchFilterChip("Documents", FileKind.Document, null),
                new SearchFilterChip("Pictures", FileKind.Image, null),
                new SearchFilterChip("Videos", FileKind.Video, null),
                new SearchFilterChip("Music", FileKind.Audio, null),
                new SearchFilterChip("Archives", FileKind.Archive, null),
                new SearchFilterChip("Code", FileKind.Code, null)
            };
            SelectFilterCommand = new RelayCommand(chip => SelectFilter(chip as SearchFilterChip));
            _sortCommand = new RelayCommand(column => SortBy(column as string));
            ClearCommand = new RelayCommand(_ => Clear());
            ToggleScopeCommand = new RelayCommand(_ => ScopeToCurrentFolder = !ScopeToCurrentFolder);
            _service.IndexChanged += OnIndexChanged;
            ByteExplorer.Core.Tags.TagStore.AnyChanged += OnTagsChanged;
        }

        /// <summary>The search line. Setting it searches (after a short pause in typing).</summary>
        public string Query
        {
            get => _query;
            set
            {
                if (SetProperty(ref _query, value ?? string.Empty))
                {
                    Schedule(TypingDelayMs);
                }
            }
        }

        /// <summary>A search is shown (the results replace the file list).</summary>
        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set => SetProperty(ref _isRunning, value);
        }

        public SearchResultList Results
        {
            get => _results;
            private set
            {
                if (SetProperty(ref _results, value))
                {
                    RaisePropertyChanged(nameof(HasResults));
                    RaisePropertyChanged(nameof(IsEmptyStateVisible));
                }
            }
        }

        public bool HasResults => _results != null && _results.Count > 0;

        /// <summary>Shows ready-made results without running a search (tests: large result sets from a synthetic index).</summary>
        internal void ShowResultsForTests(SearchResults results)
        {
            IsActive = true;
            Results = new SearchResultList(results);
        }

        /// <summary>The search finished and found nothing.</summary>
        public bool IsEmptyStateVisible => _isActive && !_isRunning && _results != null && _results.Count == 0;

        /// <summary>"1,234 results · 12 ms", "Searching…", what went wrong.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string EmptyText => string.IsNullOrWhiteSpace(_query) ? "Nothing matches these filters" : $"No results for “{_query.Trim()}”";

        public IReadOnlyList<SearchFilterChip> Filters { get; }

        public ICommand SelectFilterCommand { get; }

        /// <summary>Sorts by a column ("Name", "Folder", "Size", "Modified", "Type", "Relevance"); the same column again flips the direction.</summary>
        public ICommand SortCommand => _sortCommand;

        public ICommand ClearCommand { get; }

        public ICommand ToggleScopeCommand { get; }

        public SearchSort Sort
        {
            get => _sort;
            private set
            {
                if (SetProperty(ref _sort, value))
                {
                    RaisePropertyChanged(nameof(SortDescription));
                }
            }
        }

        public bool Descending
        {
            get => _descending;
            private set
            {
                if (SetProperty(ref _descending, value))
                {
                    RaisePropertyChanged(nameof(SortDescription));
                }
            }
        }

        public string SortDescription => _sort == SearchSort.Relevance ? "Best match" : $"{_sort} {(_descending ? "↓" : "↑")}";

        /// <summary>Only results inside the active pane's folder.</summary>
        public bool ScopeToCurrentFolder
        {
            get => _scopeToCurrentFolder;
            set
            {
                if (SetProperty(ref _scopeToCurrentFolder, value))
                {
                    RaisePropertyChanged(nameof(ScopeLabel));
                    Schedule(0);
                }
            }
        }

        /// <summary>"Everywhere", or "In Documents" for the folder of the active pane.</summary>
        public string ScopeLabel
        {
            get
            {
                string folder = _activePane()?.CurrentPath;
                return _scopeToCurrentFolder && !string.IsNullOrEmpty(folder) ? "In " + PathUtil.GetName(folder) : "Everywhere";
            }
        }

        /// <summary>The selected row (the list's primary selection).</summary>
        public SearchResultRow SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetProperty(ref _selectedRow, value) && value != null)
                {
                    _selectedPath = value.FullPath;
                }
            }
        }

        /// <summary>Clears the query and the results.</summary>
        public void Clear()
        {
            _searchCts?.Cancel();
            _refreshCts?.Cancel();
            _generation++;
            SetProperty(ref _query, string.Empty, nameof(Query));
            Results = null;
            SelectedRow = null;
            _selectedPath = null;
            IsRunning = false;
            IsActive = false;
            StatusText = string.Empty;
            RaisePropertyChanged(nameof(IsEmptyStateVisible));
        }

        /// <summary>Runs the current search again now (the index changed, the hidden-items setting changed).</summary>
        public void Refresh() => Schedule(0);

        // ------------------------------------------------------------------ running searches

        private void Schedule(int delayMs)
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            int generation = ++_generation;
            RunAsync(generation, delayMs, cts.Token).Forget("SearchViewModel.RunAsync");
        }

        private SearchOptions BuildOptions()
        {
            SearchFilterChip filter = Filters.FirstOrDefault(f => f.IsSelected);
            var navigation = _settings?.Current?.Navigation;
            return new SearchOptions
            {
                Sort = _sort,
                Descending = _descending,
                Kind = filter?.Kind,
                FoldersOnly = filter?.FoldersOnly,
                IncludeHidden = navigation == null || navigation.ShowHiddenItems || navigation.ShowSystemItems,
                ScopeFolder = _scopeToCurrentFolder ? _activePane()?.CurrentPath : null,
                SearchContents = _settings?.Current?.Index?.SearchFileContents ?? false
            };
        }

        private bool HasConstraints(SearchOptions options) =>
            !string.IsNullOrWhiteSpace(_query) || options.Kind != null || options.FoldersOnly != null;

        private async Task RunAsync(int generation, int delayMs, CancellationToken token)
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, token);
                }

                SearchOptions options = BuildOptions();
                if (string.IsNullOrWhiteSpace(_query))
                {
                    // An empty box ends the search (a filter chip alone does not start one).
                    if (generation == _generation)
                    {
                        Results = null;
                        IsRunning = false;
                        IsActive = false;
                        StatusText = string.Empty;
                    }

                    return;
                }

                if (ByteExplorer.Core.Tags.TagSearch.TryParse(_query, out _, out _))
                {
                    await RunTagSearchAsync(generation, options, token);
                    return;
                }

                if (!_service.IsSearchEnabled)
                {
                    IsActive = true;
                    Results = new SearchResultList(SearchResults.Empty);
                    StatusText = _service.DetailStatus;
                    return;
                }

                IsActive = true;
                IsRunning = true;
                RaisePropertyChanged(nameof(IsEmptyStateVisible));
                if (Results == null || Results.Count == 0)
                {
                    StatusText = "Searching…";
                }

                var stopwatch = Stopwatch.StartNew();
                SearchResults results = await _service.SearchAsync(_query, options, token);
                if (token.IsCancellationRequested || generation != _generation)
                {
                    return;
                }

                var list = new SearchResultList(results);
                Results = list;
                IsRunning = false;
                RaisePropertyChanged(nameof(EmptyText));
                RaisePropertyChanged(nameof(IsEmptyStateVisible));
                StatusText = Describe(results, stopwatch.Elapsed);
                int index = list.IndexOfPath(_selectedPath);
                SelectedRow = index >= 0 ? list[index] : null;
                _analytics?.TrackSearchPerformed(_query, results.Count);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Search failed for {Query}", _query);
                if (generation == _generation)
                {
                    IsRunning = false;
                    Results = new SearchResultList(SearchResults.Empty);
                    StatusText = "Search didn't finish. Try again; if it keeps failing, the details are in the log.";
                }
            }
        }

        /// <summary>
        /// A tag query (tag:Work …): the tag's items, straight from the tag store (no index needed), in the same results
        /// view. Re-runs when tags change while it is shown.
        /// </summary>
        private async Task RunTagSearchAsync(int generation, SearchOptions options, CancellationToken token)
        {
            IsActive = true;
            IsRunning = true;
            string query = _query;
            ByteExplorer.Core.Tags.TagStore store = ByteExplorer.Core.Tags.TagStore.Default;
            string unknown = null;
            var stopwatch = Stopwatch.StartNew();
            SearchResults results = await Task.Run(() => ByteExplorer.Core.Tags.TagSearch.Run(store, query, options, token, out unknown), token);
            if (token.IsCancellationRequested || generation != _generation)
            {
                return;
            }

            var list = new SearchResultList(results);
            Results = list;
            IsRunning = false;
            RaisePropertyChanged(nameof(EmptyText));
            RaisePropertyChanged(nameof(IsEmptyStateVisible));
            StatusText = unknown != null
                ? $"No tag named \"{unknown}\""
                : results.Count == 0
                    ? "No tagged items"
                    : $"{results.Count:N0} tagged item{(results.Count == 1 ? string.Empty : "s")} · {stopwatch.Elapsed.TotalMilliseconds:N0} ms";
            int index = list.IndexOfPath(_selectedPath);
            SelectedRow = index >= 0 ? list[index] : null;
        }

        private void OnTagsChanged(object sender, ByteExplorer.Core.Tags.TagsChangedEventArgs e)
        {
            if (_isActive && ReferenceEquals(sender, ByteExplorer.Core.Tags.TagStore.Default) &&
                ByteExplorer.Core.Tags.TagSearch.TryParse(_query, out _, out _))
            {
                Schedule(150);
            }
        }

        private string Describe(SearchResults results, TimeSpan elapsed)
        {
            string time = elapsed.TotalMilliseconds < 1000 ? $"{elapsed.TotalMilliseconds:N0} ms" : $"{elapsed.TotalSeconds:N1} s";
            string scope = _scopeToCurrentFolder ? " in " + PathUtil.GetName(_activePane()?.CurrentPath ?? string.Empty) : string.Empty;
            string updating = _service.IsIndexing ? " · index updating" : string.Empty;
            if (results.TotalMatches == 0)
            {
                return $"No results{scope}{updating}";
            }

            string count = results.IsTruncated
                ? $"{results.Count:N0} of {results.TotalMatches:N0} results"
                : $"{results.TotalMatches:N0} result{(results.TotalMatches == 1 ? string.Empty : "s")}";
            return $"{count}{scope} · {time}{updating}";
        }

        private void SelectFilter(SearchFilterChip chip)
        {
            if (chip == null)
            {
                return;
            }

            foreach (SearchFilterChip filter in Filters)
            {
                filter.IsSelected = ReferenceEquals(filter, chip);
            }

            Schedule(0);
        }

        /// <summary>
        /// A column header click: that order (by name, size... ascending; by date, newest first), again to reverse it.
        /// Re-orders the shown matches without searching again.
        /// </summary>
        private void SortBy(string column)
        {
            if (!Enum.TryParse(column, ignoreCase: true, out SearchSort sort))
            {
                return;
            }

            if (sort == _sort && sort != SearchSort.Relevance)
            {
                Descending = !_descending;
            }
            else
            {
                Sort = sort;
                Descending = sort == SearchSort.Modified || sort == SearchSort.Size;
            }

            ResortAsync().Forget("SearchViewModel.ResortAsync");
        }

        private async Task ResortAsync()
        {
            SearchResultList current = _results;
            if (current == null || current.Count == 0)
            {
                return;
            }

            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            int generation = ++_generation;
            try
            {
                IsRunning = true;
                SearchSort sort = _sort;
                bool descending = _descending;
                SearchResults sorted = await Task.Run(() => current.Results.Sorted(sort, descending, cts.Token), cts.Token);
                if (generation != _generation)
                {
                    return;
                }

                var list = new SearchResultList(sorted);
                Results = list;
                int index = list.IndexOfPath(_selectedPath);
                SelectedRow = index >= 0 ? list[index] : null;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (generation == _generation)
                {
                    IsRunning = false;
                }
            }
        }

        private void OnIndexChanged(object sender, EventArgs e)
        {
            // Raised on a background thread; the search re-runs on the UI thread a moment later (one refresh per burst).
            ByteExplorer.Infrastructure.UiThread.Post(() =>
            {
                if (!_isActive || string.IsNullOrWhiteSpace(_query))
                {
                    return;
                }

                _refreshCts?.Cancel();
                var cts = new CancellationTokenSource();
                _refreshCts = cts;
                Task.Delay(IndexChangeDelayMs, cts.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && _isActive)
                    {
                        Schedule(0);
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext()).Forget("SearchViewModel.OnIndexChanged");
            });
        }

        /// <summary>The scope label follows the active pane's folder.</summary>
        public void NotifyActiveFolderChanged()
        {
            RaisePropertyChanged(nameof(ScopeLabel));
            if (_scopeToCurrentFolder && _isActive)
            {
                Schedule(0);
            }
        }

        public void Detach()
        {
            _service.IndexChanged -= OnIndexChanged;
            ByteExplorer.Core.Tags.TagStore.AnyChanged -= OnTagsChanged;
            _searchCts?.Cancel();
            _refreshCts?.Cancel();
        }
    }
}
