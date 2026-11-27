// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Search;
using ByteSearcher;
using Serilog;

namespace ByteExplorer.ViewModels.Lens
{
    /// <summary>What the lenses search: the search index (a fake in tests).</summary>
    public interface ILensSource
    {
        bool IsReady { get; }

        /// <summary>The index became ready, or its contents changed.</summary>
        event EventHandler Changed;

        Task<SearchResults> SearchAsync(string query, SearchOptions options, CancellationToken cancellationToken);
    }

    /// <summary>The search index as the lenses' source.</summary>
    public sealed class SearchIndexLensSource : ILensSource
    {
        private readonly GlobalSearchService _service;

        public SearchIndexLensSource(GlobalSearchService service) => _service = service;

        public bool IsReady => _service?.IsIndexReady == true;

        public event EventHandler Changed
        {
            add
            {
                if (_service != null)
                {
                    _service.StatusChanged += value;
                    _service.IndexChanged += value;
                }
            }
            remove
            {
                if (_service != null)
                {
                    _service.StatusChanged -= value;
                    _service.IndexChanged -= value;
                }
            }
        }

        public Task<SearchResults> SearchAsync(string query, SearchOptions options, CancellationToken cancellationToken) =>
            _service == null ? Task.FromResult(SearchResults.Empty) : _service.SearchAsync(query, options, cancellationToken);
    }

    /// <summary>A lens's row in the left panel: its icon, name and how many files it holds.</summary>
    public sealed class LensEntryViewModel : ViewModelBase
    {
        private string _countText = string.Empty;
        private bool _isActive;

        public LensEntryViewModel(LensDefinition definition) => Definition = definition;

        public LensDefinition Definition { get; }
        public string Name => Definition.Name;
        public Symbol Icon => Definition.Icon;
        /// <summary>What it shows and, once counted, how many files ("Every picture on your drives · 8,412 files").</summary>
        public string Tooltip => string.IsNullOrEmpty(_countText) ? Definition.Description : $"{Definition.Description} · {_countText} files";

        /// <summary>"27", "8.4k"; empty until counted.</summary>
        public string CountText
        {
            get => _countText;
            set
            {
                if (SetProperty(ref _countText, value ?? string.Empty))
                {
                    RaisePropertyChanged(nameof(Tooltip));
                }
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }
    }

    /// <summary>
    /// The lenses (left panel › Lens) and the lens view: a lens is a search of the whole index (Today, Photos, Large
    /// files…), shown newest first in time groups ("This afternoon", "Last week", "March 2025") with the time river beside
    /// the list, one bar per month, to jump through time. The operating system's and programs' folders are left out
    /// (<see cref="LensCatalog.SystemFolders"/>), hidden files too unless "Show hidden files" is on.
    /// </summary>
    public sealed class LensViewModel : ViewModelBase
    {
        private static readonly TimeSpan CountsRefreshInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(250);

        private ILensSource _source;
        private readonly IUiDispatcher _dispatcher;
        private readonly Func<bool> _includeHidden;
        private readonly ILogger _logger;
        private CancellationTokenSource _run;
        private int _countsRunning;
        private DateTime _countedUtc = DateTime.MinValue;
        private LensEntryViewModel _active;
        private bool _isActive;
        private bool _isLoading;
        private string _filterText = string.Empty;
        private LensRowList _rows = LensRowList.Empty;
        private IReadOnlyList<LensRiverBucket> _river = Array.Empty<LensRiverBucket>();
        private int _currentRiver = -1;
        private string _summary = string.Empty;
        private string _emptyText = string.Empty;

        public LensViewModel(ILensSource source, IUiDispatcher dispatcher, Func<bool> includeHidden, ILogger logger = null)
        {
            _source = source;
            _dispatcher = dispatcher;
            _includeHidden = includeHidden ?? (() => false);
            _logger = logger ?? Log.Logger;
            Lenses = new ObservableCollection<LensEntryViewModel>(LensCatalog.All.Select(d => new LensEntryViewModel(d)));
            if (_source != null)
            {
                _source.Changed += OnSourceChanged;
            }
        }

        public ObservableCollection<LensEntryViewModel> Lenses { get; }

        /// <summary>Replaces the source (tests: a fake index).</summary>
        internal void UseSourceForTests(ILensSource source)
        {
            if (_source != null)
            {
                _source.Changed -= OnSourceChanged;
            }

            _source = source;
            _countedUtc = DateTime.MinValue;
            if (_source != null)
            {
                _source.Changed += OnSourceChanged;
            }
        }

        /// <summary>The lens shown, or null.</summary>
        public LensEntryViewModel Active
        {
            get => _active;
            private set
            {
                if (ReferenceEquals(_active, value))
                {
                    return;
                }

                if (_active != null)
                {
                    _active.IsActive = false;
                }

                _active = value;
                if (_active != null)
                {
                    _active.IsActive = true;
                }

                RaisePropertyChanged();
                RaisePropertyChanged(nameof(Title));
                RaisePropertyChanged(nameof(Description));
                RaisePropertyChanged(nameof(Icon));
            }
        }

        /// <summary>The lens view is shown (in place of the file list).</summary>
        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        public string Title => _active?.Name ?? string.Empty;
        public string Description => _active?.Definition.Description ?? string.Empty;
        public Symbol Icon => _active?.Icon ?? Symbol.Sparkle;

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        /// <summary>Narrows the lens (any search words or filters: "invoice", "ext:pdf", "size:&gt;10mb").</summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetProperty(ref _filterText, value ?? string.Empty) && _active != null)
                {
                    RunAsync(_active, FilterDelay).Forget("filter the lens");
                }
            }
        }

        /// <summary>Headings and files, for the list.</summary>
        public LensRowList Rows
        {
            get => _rows;
            private set
            {
                if (SetProperty(ref _rows, value))
                {
                    RaisePropertyChanged(nameof(IsEmpty));
                }
            }
        }

        public bool IsEmpty => !_isLoading && _rows.Count == 0;

        /// <summary>The time river: the last twelve months, then older years.</summary>
        public IReadOnlyList<LensRiverBucket> River
        {
            get => _river;
            private set => SetProperty(ref _river, value);
        }

        /// <summary>The river bar of the period at the top of the list (accent), or -1.</summary>
        public int CurrentRiverIndex
        {
            get => _currentRiver;
            private set
            {
                if (_currentRiver == value)
                {
                    return;
                }

                for (int i = 0; i < _river.Count; i++)
                {
                    _river[i].IsCurrent = i == value;
                }

                _currentRiver = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>"27 files", "1,000,000 of 1,204,311 files shown".</summary>
        public string Summary
        {
            get => _summary;
            private set => SetProperty(ref _summary, value);
        }

        /// <summary>Why the list is empty (nothing matches, the index isn't ready yet).</summary>
        public string EmptyText
        {
            get => _emptyText;
            private set => SetProperty(ref _emptyText, value);
        }

        /// <summary>Shows a lens (from the left panel); the same lens again refreshes it.</summary>
        public Task OpenAsync(LensEntryViewModel lens)
        {
            if (lens == null)
            {
                return Task.CompletedTask;
            }

            if (!ReferenceEquals(lens, _active))
            {
                _filterText = string.Empty;
                RaisePropertyChanged(nameof(FilterText));
            }

            Active = lens;
            IsActive = true;
            return RunAsync(lens, TimeSpan.Zero);
        }

        /// <summary>Shows a lens by its id ("photos").</summary>
        public Task OpenAsync(string id) => OpenAsync(Lenses.FirstOrDefault(l => string.Equals(l.Definition.Id, id, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Runs the lens again (F5, or files changed meanwhile).</summary>
        public Task RefreshAsync() => _active == null ? Task.CompletedTask : RunAsync(_active, TimeSpan.Zero);

        /// <summary>Leaves the lens view (a folder was opened, a search started, the tab changed).</summary>
        public void Close()
        {
            if (!IsActive)
            {
                return;
            }

            _run?.Cancel();
            IsActive = false;
            Active = null;
            IsLoading = false;
            Rows = LensRowList.Empty;
            River = Array.Empty<LensRiverBucket>();
            _currentRiver = -1;
            Summary = string.Empty;
        }

        /// <summary>The list scrolled: the river lights the period now at the top.</summary>
        public void SetTopRow(int row)
        {
            int group = _rows.GroupOfRow(row);
            if (group < 0 || _river.Count == 0)
            {
                CurrentRiverIndex = -1;
                return;
            }

            // The bar whose period starts at or before this row, the latest such (bars are newest first).
            int best = -1;
            for (int i = 0; i < _river.Count; i++)
            {
                int first = _river[i].FirstRow;
                if (first >= 0 && first <= row)
                {
                    best = i;
                }
            }

            CurrentRiverIndex = best;
        }

        private SearchOptions Options(LensDefinition lens, int maxResults) => new SearchOptions
        {
            Sort = SearchSort.Modified,
            Descending = true,
            FoldersOnly = false,
            IncludeHidden = _includeHidden(),
            ScopeFolder = lens.Scope?.Invoke(),
            ExcludedFolders = LensCatalog.SystemFolders,
            MaxResults = maxResults
        };

        private async Task RunAsync(LensEntryViewModel lens, TimeSpan delay)
        {
            _run?.Cancel();
            var cts = new CancellationTokenSource();
            _run = cts;
            IsLoading = true;
            RaisePropertyChanged(nameof(IsEmpty));
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token);
                }

                if (_source == null || !_source.IsReady)
                {
                    Rows = LensRowList.Empty;
                    River = Array.Empty<LensRiverBucket>();
                    Summary = string.Empty;
                    EmptyText = "Lenses look through the search index, which is still being built. They fill in as soon as it is ready.";
                    return;
                }

                string query = (lens.Definition.Query + " " + _filterText).Trim();
                SearchOptions options = Options(lens.Definition, 1_000_000);
                SearchResults results = await _source.SearchAsync(query, options, cts.Token);
                DateTime now = DateTime.Now;
                LensLayout layout = await Task.Run(() => LensLayout.Build(results.Count, results.ModifiedUtcAt, now, cts.Token), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(_active, lens))
                {
                    return;
                }

                Rows = new LensRowList(results, layout);
                River = layout.River;
                _currentRiver = -1;
                SetTopRow(0);
                Summary = results.IsTruncated
                    ? $"{results.Count:N0} of {results.TotalMatches:N0} files shown"
                    : results.Count == 1 ? "1 file" : $"{results.Count:N0} files";
                EmptyText = string.IsNullOrWhiteSpace(_filterText)
                    ? $"Nothing here yet: no files match “{lens.Name}”."
                    : $"No files in “{lens.Name}” match “{_filterText.Trim()}”.";
                if (string.IsNullOrWhiteSpace(_filterText))
                {
                    lens.CountText = Compact(results.TotalMatches); // the panel's count follows what was just seen
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "The lens {Lens} failed", lens.Name);
                Rows = LensRowList.Empty;
                EmptyText = "This lens couldn't be shown. " + FriendlyErrors.Explain(ex);
            }
            finally
            {
                if (ReferenceEquals(_run, cts))
                {
                    IsLoading = false;
                    RaisePropertyChanged(nameof(IsEmpty));
                }
            }
        }

        /// <summary>
        /// Counts every lens for the left panel (each a search that keeps one result and counts the rest), in the
        /// background, at most once a minute.
        /// </summary>
        public async Task RefreshCountsAsync(bool force = false)
        {
            if (_source == null || !_source.IsReady || (!force && DateTime.UtcNow - _countedUtc < CountsRefreshInterval))
            {
                return;
            }

            if (Interlocked.Exchange(ref _countsRunning, 1) == 1)
            {
                return;
            }

            _countedUtc = DateTime.UtcNow;
            try
            {
                foreach (LensEntryViewModel lens in Lenses.ToArray())
                {
                    SearchResults results = await _source.SearchAsync(lens.Definition.Query, Options(lens.Definition, 1), CancellationToken.None);
                    lens.CountText = Compact(results.TotalMatches);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Counting the lenses failed");
            }
            finally
            {
                Interlocked.Exchange(ref _countsRunning, 0);
            }
        }

        private void OnSourceChanged(object sender, EventArgs e)
        {
            _dispatcher?.InvokeAsync(() =>
            {
                RefreshCountsAsync().Forget("count the lenses");
                if (_active != null && _rows.Count == 0 && !_isLoading && _source.IsReady)
                {
                    RunAsync(_active, TimeSpan.Zero).Forget("show the lens once the index is ready");
                }
            }).Forget("lens source changed");
        }

        /// <summary>"0", "27", "8.4k", "1.2M".</summary>
        public static string Compact(long count) => count switch
        {
            < 1_000 => count.ToString("N0"),
            < 10_000 => (count / 1000d).ToString("0.#") + "k",
            < 1_000_000 => (count / 1000) + "k",
            _ => (count / 1_000_000d).ToString("0.#") + "M"
        };
    }
}
