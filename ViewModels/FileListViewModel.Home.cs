// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Analytics;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The Home dashboard surface — a read-only view over whatever
    /// IUserAnalyticsService currently has tracked, rebuilt fresh every
    /// time this page is opened or explicitly refreshed. Nothing here
    /// tracks anything itself; every Track* call that feeds this data
    /// lives at the point in FileListViewModel/MainViewModel where the
    /// real event actually happens (LoadFolderAsync, OpenItemAsync,
    /// ReportOperationResult, and so on).
    /// </summary>
    public sealed partial class FileListViewModel
    {
        private AnalyticsDashboardViewModel _homeDashboard;

        private readonly AsyncRelayCommand _refreshHomeCommand;
        private readonly AsyncRelayCommand _clearAnalyticsDataCommand;

        public AnalyticsDashboardViewModel HomeDashboard
        {
            get => _homeDashboard;
            private set => SetProperty(ref _homeDashboard, value);
        }

        /// <summary>
        /// Top 3 frecency-ranked folders (frequency + recency, same
        /// scoring as HomeDashboard.FavoriteFolders) for the left
        /// sidebar's "Frequent folders" row — the same concept Windows
        /// 11 Explorer's own Quick Access section shows beneath its
        /// pinned folders. Deliberately a separate, independently-
        /// refreshed collection rather than reusing HomeDashboard
        /// itself: HomeDashboard only exists once the Home page has
        /// actually been opened at least once, whereas the sidebar
        /// needs this to be populated the moment the app starts,
        /// regardless of whether the user ever visits Home at all — see
        /// RefreshSidebarFrequentFolders for where this gets kept
        /// current instead.
        /// </summary>
        public ObservableCollection<FavoriteFolderRowViewModel> SidebarFrequentFolders { get; } = new ObservableCollection<FavoriteFolderRowViewModel>();

        private bool _hasSidebarFrequentFolders;

        /// <summary>
        /// Mirrors SidebarFrequentFolders.Count > 0 — a plain bool
        /// property rather than binding XAML directly to .Count with a
        /// converter, matching the established Has* pattern
        /// AnalyticsDashboardViewModel already uses throughout
        /// (HasFavoriteFolders, HasRecentVideos, etc.).
        /// </summary>
        public bool HasSidebarFrequentFolders
        {
            get => _hasSidebarFrequentFolders && FolderOptions.Current.ShowFrequentFolders;
            private set => SetProperty(ref _hasSidebarFrequentFolders, value);
        }

        /// <summary>
        /// Refreshes SidebarFrequentFolders from the lightweight
        /// UserAnalyticsService.GetTopFolders (not the full GetSummary —
        /// see its own remarks on why) — called once at startup
        /// (InitializeFromSettings, best-effort: the analytics file
        /// loads on its own background thread and may not have finished
        /// yet at that exact moment) and, more importantly, after every
        /// successful folder navigation (LoadFolderAsync, right after
        /// TrackFolderVisited), which self-corrects any startup-timing
        /// gap almost immediately in practice — the analytics load
        /// itself typically completes in low-single-digit milliseconds,
        /// so by the time a user has navigated to even one folder, it's
        /// essentially always already done.
        /// </summary>
        private void RefreshSidebarFrequentFolders()
        {
            try
            {
                IReadOnlyList<FavoriteFolderInsight> topFolders = _analyticsService.GetTopFolders(3);

                SidebarFrequentFolders.Clear();
                foreach (FavoriteFolderInsight insight in topFolders)
                {
                    SidebarFrequentFolders.Add(new FavoriteFolderRowViewModel(insight));
                }

                HasSidebarFrequentFolders = SidebarFrequentFolders.Count > 0;
            }
            catch (Exception ex)
            {
                // Sidebar convenience feature — a failure here must
                // never be able to disrupt folder navigation itself.
                _logger.Debug(ex, "Failed to refresh sidebar frequent folders");
            }
        }

        public AsyncRelayCommand RefreshHomeCommand => _refreshHomeCommand;

        public AsyncRelayCommand ClearAnalyticsDataCommand => _clearAnalyticsDataCommand;

        /// <summary>
        /// Called once from the main constructor (see FileListViewModel.cs)
        /// to wire up this surface's two commands — kept as a separate,
        /// explicitly-invoked method rather than field/property
        /// initializers so command construction stays in one place
        /// (the constructor body) alongside every other command this
        /// class exposes, matching the existing style throughout this
        /// file rather than introducing a second pattern just for Home.
        /// </summary>
        private void InitializeHomeCommands(
            out AsyncRelayCommand refreshHomeCommand,
            out AsyncRelayCommand clearAnalyticsDataCommand)
        {
            refreshHomeCommand = new AsyncRelayCommand(
                _ => LoadHomeAsync(),
                _ => IsHomeView && !_isLoading);

            clearAnalyticsDataCommand = new AsyncRelayCommand(
                _ => ClearAnalyticsDataAsync(),
                _ => IsHomeView);
        }

        /// <summary>
        /// Loads the Home dashboard. Cheap and entirely local — no disk
        /// I/O beyond what UserAnalyticsService already keeps in
        /// memory, no network access, nothing that benefits from a
        /// background thread — so unlike LoadFolderAsync/LoadThisPcAsync
        /// this runs synchronously on the UI thread apart from the
        /// async signature kept for consistency with this class's other
        /// Load*Async entry points, so it slots into the exact same
        /// NavigateToNodeAsync-style call pattern.
        /// </summary>
        public Task LoadHomeAsync()
        {
            NavigationRequested?.Invoke(this, EventArgs.Empty);
            HasStartedNavigation = true;
            _loadCts?.Cancel();
            CancelFolderSizes();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            StopAllLiveWatching();

            SetSurface(FileListSurface.Home);
            RecordArrival(HistoryEntry.Home);
            ErrorMessage = null;
            CurrentPath = null;
            Items = null;
            TimelineGroups = null;
            ColumnPanes = null;
            NexusClusters = null;
            SelectedItem = null;
            SelectedItems.Clear();

            RefreshHomeDashboard();

            return Task.CompletedTask;
        }

        internal void RefreshHomeDashboard()
        {
            try
            {
                HomeDashboard = new AnalyticsDashboardViewModel(_analyticsService.GetSummary());
                StatusText = HomeDashboard.IsEnabled
                    ? string.Empty
                    : "Activity history is turned off. Turn it on in Settings › Privacy to see your frequent folders here.";

                // Extra safeguard beyond LoadFolderAsync's own
                // post-navigation call — cheap to repeat here too, and
                // means the sidebar is guaranteed fresh any time
                // analytics data is known to have just been queried.
                RefreshSidebarFrequentFolders();
            }
            catch (Exception ex)
            {
                // The dashboard is a convenience view over already-safe
                // data (GetSummary itself never throws — see
                // UserAnalyticsService) — this is a last-resort guard so
                // a bug in the dashboard's own display formatting can
                // never take down the Home page entirely.
                _logger.Error(ex, "Failed to build the Home dashboard");
                ErrorMessage = "Couldn't load your activity dashboard.";
            }
        }

        /// <summary>
        /// Clearing analytics is destructive and irreversible, so it gets the same "ask first" treatment as a
        /// permanent delete (IDialogService.ConfirmAsync).
        /// </summary>
        private async Task ClearAnalyticsDataAsync()
        {
            if (DialogService == null)
            {
                return;
            }

            bool confirmed = await DialogService.ConfirmAsync(
                "Clear activity history",
                "Clear all usage history (recent files, frequent folders, searches)? This can't be undone.",
                "Clear").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            try
            {
                _analyticsService.ClearAllData();
                RefreshHomeDashboard();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clear analytics data");
                OperationFailed?.Invoke(this, "Couldn't clear your activity data.\n\n" + ex.Message);
            }
        }
    }
}
