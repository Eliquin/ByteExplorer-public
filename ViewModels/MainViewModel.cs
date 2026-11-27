// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Search;
using ByteExplorer.ViewModels.Tabs;
using ByteSearcher;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Root view model for the shell window. Composition point where
    /// every subsystem is assembled and wired to the others — selection
    /// in the tree drives the file list; global search drives the
    /// results overlay in the center panel.
    /// </summary>
    public sealed class MainViewModel : ViewModelBase, IDisposable, ITabHost
    {
        // Immutable brushes are thread-agnostic (the WPF version froze them for the same reason): the index status
        // can change from a background thread's callback.
        private static readonly IBrush IndexIconRed = new ImmutableSolidColorBrush(Color.Parse("#FFE81123"));
        private static readonly IBrush IndexIconYellow = new ImmutableSolidColorBrush(Color.Parse("#FFF0A000"));
        private static readonly IBrush IndexIconGreen = new ImmutableSolidColorBrush(Color.Parse("#FF107C10"));
        private static readonly IBrush IndexIconGray = new ImmutableSolidColorBrush(Color.Parse("#FF5B5B5B"));

        private readonly IUiDispatcher _dispatcher;
        private readonly IDialogService _dialogService;
        private readonly ILogger _logger;
        private readonly GlobalSearchService _searchService;
        private readonly IUserAnalyticsService _analyticsService;
        private readonly ISoundFeedbackService _soundFeedbackService;
        private readonly IDirectoryIndexService _indexService;
        private readonly IFileOperationService _fileOperationService;
        private readonly IFileBackupService _fileBackupService;
        private TabViewModel _observedTab;
        private bool _settingsLoaded;
        private readonly AsyncRelayCommand _toggleSplitViewCommand;
        private string _indexShortStatus = "No index";
        private string _indexDetailStatus = "Search index has not been initialized yet.";
        private bool _isSearchEnabled;
        private bool _isIndexing;
        private IBrush _indexIconBrush = IndexIconRed;
        private readonly AppServices _services;

        private readonly AsyncRelayCommand _refreshIndexCommand;
        private readonly RelayCommand _stopIndexingCommand;

        /// <summary>
        /// A window's view model. <paramref name="services"/> are the process-wide services the main windows share (see
        /// <see cref="AppServices"/>); without them the view model gets services of its own, disposed with it (tests).
        /// </summary>
        public MainViewModel(IUiDispatcher dispatcher, IDialogService dialogService = null, AppServices services = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dialogService = dialogService;
            _logger = Log.Logger.ForContext<MainViewModel>();

            _services = services ?? AppServices.CreatePrivate(_dispatcher);
            _services.Attach(this);
            SettingsService = _services.Settings;
            _analyticsService = _services.Analytics;
            _soundFeedbackService = _services.SoundFeedback;

            var navigationService = new FileSystemNavigationService(Log.Logger);
            var driveService = new DriveEnumerationService(SettingsService, Log.Logger);
            NavigationViewModel = new NavigationViewModel(driveService, navigationService, SettingsService, _dispatcher, soundFeedbackService: _soundFeedbackService, logger: Log.Logger);

            IDirectoryIndexService indexService = _services.DirectoryIndex;
            _indexService = indexService;
            _fileOperationService = _services.OperationService;
            _fileBackupService = _services.BackupService;
            // The queue is the app's (every window shows its jobs and shares its undo); the caches, tags and analytics
            // follow its changes once, in AppServices. This window only reloads its own panes.
            Operations = _services.Operations;
            Operations.FoldersChanged += OnOperationFoldersChanged;
            Pins = new Sidebar.PinnedFoldersViewModel(SettingsService);
            NetworkLocations = new SavedLocationList(SettingsService, n => n.NetworkLocations, (n, list) => n.NetworkLocations = list);

            // The tabs: each owns its panes (made by CreatePane below, wired like every pane); the window shows the selected
            // one. Created before anything that follows the active pane (the address bar, Quick access, search).
            Tabs = new TabsViewModel(this, () => SettingsService?.Current?.Tabs, SaveTabSettingsSoon);
            Tabs.SelectedTabChanged += OnSelectedTabChanged;
            ObserveTab(Tabs.SelectedTab);
            AddNetworkLocationCommand = new AsyncRelayCommand(_ => AddNetworkLocationAsync());
            TogglePinCommand = new AsyncRelayCommand(_ => ToggleCurrentFolderPinAsync(), _ => !string.IsNullOrEmpty(FileListViewModel?.CurrentPath) && !Core.Platform.Phones.Phones.IsPhonePath(FileListViewModel.CurrentPath));
            Omnibar = new Omnibar.OmnibarViewModel(this, indexService, _analyticsService, SettingsService);
            Pins.Follow(this);

            FolderOptions.Attach(() => SettingsService?.Current?.FolderOptions);
            Infrastructure.SystemIcons.SystemIconCache.AttachSetting(() => SettingsService?.Current?.FileList?.UseShellIcons ?? true);
            TabOptions.Attach(() => SettingsService?.Current?.Tabs);
            Core.Platform.Phones.Phones.IsEnabled = () => FolderOptions.Current.ShowPhones;

            _searchService = _services.Search;
            _searchService.StatusChanged += OnSearchServiceStatusChanged;
            Search = new ByteExplorer.ViewModels.Search.SearchViewModel(_searchService, () => FileListViewModel, SettingsService, _analyticsService, _logger);
            Search.PropertyChanged += OnSearchPropertyChanged;
            Lens = new ByteExplorer.ViewModels.Lens.LensViewModel(new ByteExplorer.ViewModels.Lens.SearchIndexLensSource(_searchService), dispatcher,
                () => SettingsService?.Current?.Navigation?.ShowHiddenItems ?? false, _logger);
            Lens.PropertyChanged += OnLensPropertyChanged;

            // Seed UI from the service's initial state (No Index / red).
            ApplySearchServiceStatus();

            SetThemeCommand = new AsyncRelayCommand(parameter => SetThemeAsync(parameter as string));

            RefreshIndexCommand = _refreshIndexCommand = new AsyncRelayCommand(
                _ => RefreshIndexAsync(),
                _ => !IsIndexing);

            StopIndexingCommand = _stopIndexingCommand = new RelayCommand(
                _ => StopIndexing(),
                _ => IsIndexing);

            ToggleSplitViewCommand = _toggleSplitViewCommand = new AsyncRelayCommand(
                _ => ToggleSplitViewAsync(),
                _ => CanToggleSplitView());

            // NavigationViewModel.NodeActivated is now the single,
            // de-duplicated "navigate here" signal (see
            // NavigationPaneView.xaml.cs's remarks) — this used to also
            // subscribe to NavigationViewModel.PropertyChanged for
            // SelectedNode, which meant an ordinary click drove two full
            // navigations (two debounce timers, two index builds racing
            // each other) instead of one. Removed; NodeActivated alone
            // now covers every real navigation intent exactly once.
            NavigationViewModel.NodeActivated += OnNavigationNodeActivated;

            // Forwards shell context-menu actions invoked from the
            // navigation tree (Rename/Delete/New via the real Explorer
            // menu) to the center pane, so a change made from the tree
            // doesn't leave FileListViewModel silently showing stale
            // contents for the same or a nested folder.
            NavigationViewModel.ExternalChangeDetected += OnNavigationExternalChangeDetected;

            // Forwards every drive snapshot (initial, periodic, or
            // on-demand) to the This PC page — see
            // NavigationViewModel.DrivesRefreshed and
            // FileListViewModel.UpdateThisPcDrives for why this is the
            // single source of truth rather than a second, independent
            // probing pass.
            NavigationViewModel.DrivesRefreshed += OnNavigationDrivesRefreshed;
        }

        public SettingsService SettingsService { get; }

        /// <summary>The color theme setting: "System", "Light" or "Dark" (the window applies it).</summary>
        public string Theme => SettingsService?.Current?.Appearance?.Theme ?? "System";

        /// <summary>Switches the color theme (parameter "System", "Light" or "Dark") and saves the choice.</summary>
        public ICommand SetThemeCommand { get; }
        public NavigationViewModel NavigationViewModel { get; }

        /// <summary>The window's tabs (see <see cref="TabsViewModel"/>); the panes below are the selected tab's.</summary>
        public TabsViewModel Tabs { get; }

        /// <summary>
        /// The selected tab's left/primary pane — unaffected by split view being on or off. Bind the primary
        /// <see cref="Views.FileListView"/>'s DataContext to this, not to <see cref="FileListViewModel"/> below: it changes
        /// when another tab is selected (the view switches with it), never when focus moves between the panes.
        /// </summary>
        public FileListViewModel PrimaryFileListViewModel => Tabs?.SelectedTab?.Primary;

        /// <summary>
        /// The selected tab's right/secondary pane — null whenever that tab isn't split. Constructed fresh, and seeded with
        /// the primary pane's current folder and view, every time split view is turned on (see ToggleSplitView); disposed
        /// every time it's turned back off, not merely hidden: a live pane nobody can see (its folder watcher, its timers)
        /// costs something for nothing. Bind the secondary FileListView's DataContext to this.
        /// </summary>
        public FileListViewModel SecondaryFileListViewModel => Tabs?.SelectedTab?.Secondary;

        /// <summary>Every pane of every tab (tabs in the background included).</summary>
        public IEnumerable<FileListViewModel> AllPanes => Tabs?.AllPanes() ?? Enumerable.Empty<FileListViewModel>();

        /// <summary>A pane was made (a tab opened, a split began): the window hooks its messages.</summary>
        public event EventHandler<FileListViewModel> PaneAdded;

        /// <summary>A pane is about to be disposed (its tab closed, its split ended): the window unhooks it.</summary>
        public event EventHandler<FileListViewModel> PaneRemoved;

        /// <summary>The app's file operations: the queue the operations panel shows, and the undo history (Ctrl+Z).</summary>
        public FileOperations.FileOperationQueue Operations { get; }

        /// <summary>The address bar: breadcrumbs, suggestions, commands (see <see cref="Omnibar.OmnibarViewModel"/>).</summary>
        public Omnibar.OmnibarViewModel Omnibar { get; }

        /// <summary>Quick access: the pinned places at the top of the sidebar, saved in the settings.</summary>
        public Sidebar.PinnedFoldersViewModel Pins { get; }

        /// <summary>
        /// Opens a folder in the pane that doesn't have focus, turning split view on first if needed (middle click or
        /// "Open in the other pane" on a pinned place).
        /// </summary>
        public async Task OpenInOtherPaneAsync(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            TabViewModel tab = Tabs.SelectedTab;
            bool wasPrimary = ReferenceEquals(tab.ActivePane, tab.Primary);
            if (!tab.IsSplitViewActive)
            {
                await ToggleSplitViewAsync().ConfigureAwait(true);
            }

            if (tab.IsClosed)
            {
                return;
            }

            FileListViewModel other = wasPrimary ? tab.Secondary : tab.Primary;
            if (other != null)
            {
                ClearSearch();
                if (folder == TabLocation.Trash)
                {
                    await other.LoadTrashAsync().ConfigureAwait(true);
                }
                else
                {
                    await other.LoadFolderAsync(folder).ConfigureAwait(true);
                }
            }
        }

        /// <summary>Whether the sidebar section saved under <paramref name="key"/> is collapsed.</summary>
        public bool IsSidebarSectionCollapsed(string key) =>
            SettingsService?.Current?.Navigation?.CollapsedSidebarSections?.Contains(key) == true;

        /// <summary>Remembers a sidebar section collapsed or expanded.</summary>
        public void SetSidebarSectionCollapsed(string key, bool collapsed)
        {
            var navigation = SettingsService?.Current?.Navigation;
            if (navigation == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            navigation.CollapsedSidebarSections ??= new System.Collections.Generic.List<string>();
            bool changed = collapsed ? !navigation.CollapsedSidebarSections.Contains(key) : navigation.CollapsedSidebarSections.Remove(key);
            if (collapsed && changed)
            {
                navigation.CollapsedSidebarSections.Add(key);
            }

            if (changed)
            {
                SettingsService.SaveAsync().Forget("save sidebar sections");
            }
        }

        /// <summary>Saved network locations (sidebar "Network").</summary>
        public SavedLocationList NetworkLocations { get; }

        /// <summary>Asks for an address and adds it to the network locations.</summary>
        public ICommand AddNetworkLocationCommand { get; }

        /// <summary>Pins the active pane's folder to Quick access, or unpins it (Ctrl+D).</summary>
        public ICommand TogglePinCommand { get; }

        private async Task AddNetworkLocationAsync()
        {
            if (_dialogService == null)
            {
                return;
            }

            string example = OperatingSystem.IsWindows() ? @"\\server\share" : "smb://server/share";
            string address = await _dialogService.PromptTextAsync(
                "Add network location",
                OperatingSystem.IsWindows()
                    ? "Type the address of a shared folder, like \\\\server\\share."
                    : "Type the address of a shared folder: smb://server/share, sftp://user@host/folder, ftp://, dav:// … or the path of a mounted share.",
                string.Empty, example, "Add", Core.Platform.NetworkLocations.Validate).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(address))
            {
                await NetworkLocations.AddAsync(address).ConfigureAwait(true);
            }
        }

        private Task ToggleCurrentFolderPinAsync()
        {
            string folder = FileListViewModel?.CurrentPath;
            // A phone folder is there only while the phone is plugged in and unlocked: not something to pin.
            return string.IsNullOrEmpty(folder) || Core.Platform.Phones.Phones.IsPhonePath(folder) ? Task.CompletedTask : Pins.ToggleAsync(folder);
        }

        /// <summary>
        /// Opens a saved network location in the active pane: connects first where the OS needs it (GIO on Linux,
        /// Finder on macOS), then browses the folder. A failure is shown as a message.
        /// </summary>
        public async Task OpenNetworkLocationAsync(string address)
        {
            FileListViewModel pane = FileListViewModel;
            if (pane == null || string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            ClearSearch();
            pane.ShowStatus("Connecting to " + Core.Platform.NetworkLocations.DisplayName(address) + "…");
            try
            {
                string folder = await Core.Platform.NetworkLocations.ConnectAsync(address, CancellationToken.None).ConfigureAwait(true);
                await pane.LoadFolderAsync(folder).ConfigureAwait(true);
            }
            catch (System.IO.IOException ex)
            {
                pane.ShowStatus(string.Empty);
                if (_dialogService != null)
                {
                    await _dialogService.ShowMessageAsync("Couldn't open the network location", Infrastructure.FriendlyErrors.Explain(ex)).ConfigureAwait(true);
                }
            }
        }

        /// <summary>
        /// An operation or an undo changed these folders: a pane showing one of them reloads (the operation's own pane
        /// already refreshes; this covers the other pane and undo, which can come from anywhere).
        /// </summary>
        private async void OnOperationFoldersChanged(object sender, IReadOnlyList<string> folders)
        {
            try
            {
                // The caches, the index and the tags have followed already (AppServices handles the event first).
                foreach (FileListViewModel pane in AllPanes.ToArray())
                {
                    if (pane != null && pane.IsTrashView)
                    {
                        // Any operation may have put items in the trash or taken them out (a delete, an undo, a restore).
                        await pane.ReloadTrashAsync().ConfigureAwait(true);
                        continue;
                    }

                    if (pane == null || string.IsNullOrEmpty(pane.CurrentPath))
                    {
                        continue;
                    }

                    foreach (string folder in folders)
                    {
                        if (PathUtil.AreSame(folder, pane.CurrentPath))
                        {
                            await pane.LoadFolderAsync(pane.CurrentPath, bypassCache: true).ConfigureAwait(true);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                _logger.Warning(ex, "Refreshing after a file operation failed");
            }
        }

        /// <summary>Whether the selected tab is split (each tab keeps its own split).</summary>
        public bool IsSplitViewActive => Tabs?.SelectedTab?.IsSplitViewActive ?? false;

        /// <summary>
        /// True while keyboard focus is somewhere inside the secondary
        /// pane rather than the primary one — set via SetFocusedPane,
        /// which each window's own XAML calls from an
        /// IsKeyboardFocusWithinChanged handler on each FileListView
        /// instance (see MainWindow.xaml's own remarks). This is what
        /// FileListViewModel below actually redirects on; it has no
        /// effect at all while split view is off, since there is no
        /// secondary pane to focus in that case.
        /// </summary>
        public bool IsSecondaryPaneFocused => Tabs?.SelectedTab?.IsSecondaryPaneFocused ?? false;

        /// <summary>
        /// Every existing binding throughout this app that already reads
        /// "{Binding FileListViewModel.XXX}" — the shared toolbar's New/
        /// Cut/Copy/Paste/Delete buttons and Sort/View/Filter/More
        /// selectors, the breadcrumb and search box, the status bar, the
        /// details/preview panel, the window's own keyboard shortcuts —
        /// lives OUTSIDE the split panes themselves (above/below them in
        /// each window's own layout), and was written once, long before
        /// split view existed, on the assumption there's exactly one
        /// FileListViewModel per window. Rather than rewrite every one
        /// of those bindings to know about two panes, this property
        /// keeps that assumption true from their point of view: it's a
        /// single FileListViewModel, just one that transparently points
        /// at whichever pane currently has focus. The two ACTUAL, fixed-
        /// identity instances — one per pane, never swapping which
        /// physical pane they represent — are PrimaryFileListViewModel
        /// and SecondaryFileListViewModel above; those are what each
        /// pane's own FileListView is bound to directly. With tabs it
        /// also follows the selected tab: still one pane, the selected
        /// tab's focused one.
        /// </summary>
        public FileListViewModel FileListViewModel => Tabs?.SelectedTab?.ActivePane;

        // ------------------------------------------------------------------ Folder options: title, startup, last session

        /// <summary>
        /// The window title: "ByteExplorer", or (Folder options' "Display the full path in the title bar") the active
        /// pane's full path ("Home", "This PC" on those pages; a phone's path as "Pixel 7 › Internal shared storage").
        /// </summary>
        public string WindowTitle
        {
            get
            {
                FileListViewModel pane = FileListViewModel;
                if (!FolderOptions.Current.FullPathInTitleBar || pane == null)
                {
                    return "ByteExplorer";
                }

                string path = pane.CurrentPath;
                if (string.IsNullOrEmpty(path))
                {
                    return pane.IsThisPcView ? "This PC" : pane.IsTrashView ? Core.Platform.Trash.DisplayName : "Home";
                }

                return Core.Platform.Phones.PhonePaths.IsPhonePath(path)
                    ? string.Join(" › ", Core.Platform.Phones.PhonePaths.Segments(path))
                    : path;
            }
        }


        /// <summary>
        /// Opens the window where Folder options say: Home, This PC, a chosen folder, or the folders open at the last close
        /// (both panes and the split view). A place that's gone (an unplugged drive, a deleted folder) falls back to Home.
        /// </summary>
        public async Task OpenStartupLocationAsync()
        {
            FolderOptionsSettings options = FolderOptions.Current;
            TabSettings tabSettings = SettingsService?.Current?.Tabs;
            List<TabSessionEntry> saved = tabSettings?.SessionTabs ?? new List<TabSessionEntry>();

            // Open where I left off: every tab as it was (each restored tab sleeps until it's first selected).
            if (options.OpenAt == FolderOptionsSettings.OpenAtLastSession && saved.Count > 0)
            {
                await Tabs.RestoreAsync(saved, tabSettings.SessionSelectedTab).ConfigureAwait(true);
                return;
            }

            // Otherwise pinned tabs still come back (that is what pinning is for), followed by the startup place.
            List<TabSessionEntry> pinned = saved.Where(t => t.Pinned).ToList();
            if (pinned.Count > 0)
            {
                string startup = options.OpenAt switch
                {
                    FolderOptionsSettings.OpenAtThisPc => TabLocation.ThisPc,
                    FolderOptionsSettings.OpenAtFolder when TabLocation.IsValid(options.StartupFolder) => options.StartupFolder,
                    _ => TabLocation.Home
                };
                pinned.Add(new TabSessionEntry { Location = startup });
                await Tabs.RestoreAsync(pinned, pinned.Count - 1).ConfigureAwait(true);
                return;
            }

            FileListViewModel pane = PrimaryFileListViewModel;
            switch (options.OpenAt)
            {
                case FolderOptionsSettings.OpenAtThisPc:
                    await pane.LoadThisPcAsync().ConfigureAwait(true);
                    return;
                case FolderOptionsSettings.OpenAtFolder when await FolderExistsAsync(options.StartupFolder).ConfigureAwait(true):
                    await pane.LoadFolderAsync(options.StartupFolder).ConfigureAwait(true);
                    return;
                case FolderOptionsSettings.OpenAtLastSession:
                    await RestoreLastSessionAsync(options).ConfigureAwait(true);
                    return;
                default:
                    await pane.LoadHomeAsync().ConfigureAwait(true);
                    return;
            }
        }

        /// <summary>The last session as files written before tabs kept it: one folder, and a second one while split.</summary>
        private async Task RestoreLastSessionAsync(FolderOptionsSettings options)
        {
            FileListViewModel pane = PrimaryFileListViewModel;
            if (await FolderExistsAsync(options.LastSessionFolder).ConfigureAwait(true))
            {
                await pane.LoadFolderAsync(options.LastSessionFolder).ConfigureAwait(true);
            }
            else
            {
                await pane.LoadHomeAsync().ConfigureAwait(true);
            }

            if (options.LastSessionSplitView && !IsSplitViewActive && _toggleSplitViewCommand.CanExecute(null))
            {
                await ToggleSplitViewAsync().ConfigureAwait(true);
                FileListViewModel second = SecondaryFileListViewModel;
                if (second != null && await FolderExistsAsync(options.LastSessionSecondFolder).ConfigureAwait(true))
                {
                    await second.LoadFolderAsync(options.LastSessionSecondFolder).ConfigureAwait(true);
                }
            }
        }

        /// <summary>Whether a folder can be opened at startup (checked off the UI thread: it may be a slow network share).</summary>
        private static async Task<bool> FolderExistsAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (Core.Platform.Phones.Phones.IsPhonePath(path))
            {
                return false; // a phone may well be unplugged since: start somewhere that's surely there
            }

            Task<bool> probe = Task.Run(() => System.IO.Directory.Exists(path));
            return await Task.WhenAny(probe, Task.Delay(2000)).ConfigureAwait(true) == probe && probe.Result;
        }

        /// <summary>
        /// Remembers the tabs open now, for the next start (pinned tabs always come back; all of them with Folder options'
        /// open where I left off). The selected tab's folders are also kept the way files before tabs kept them.
        /// </summary>
        public async Task SaveSessionAsync()
        {
            FolderOptionsSettings options = SettingsService?.Current?.FolderOptions;
            TabSettings tabSettings = SettingsService?.Current?.Tabs;
            if (options == null || tabSettings == null)
            {
                return;
            }

            tabSettings.SessionTabs = Tabs.CaptureSession(out int selected);
            tabSettings.SessionSelectedTab = selected;
            options.LastSessionFolder = PrimaryFileListViewModel?.CurrentPath ?? string.Empty;
            options.LastSessionSplitView = IsSplitViewActive;
            options.LastSessionSecondFolder = IsSplitViewActive ? SecondaryFileListViewModel?.CurrentPath ?? string.Empty : string.Empty;
            try
            {
                await SettingsService.SaveAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Saving the open folders for the next start failed");
            }
        }

        /// <summary>A folder option changed (Settings): everything that shows one follows at once.</summary>
        public void ApplyFolderOptions()
        {
            FolderOptions.NotifyChanged();
            foreach (MainViewModel window in AllWindows)
            {
                window.ApplyFolderOptionsToWindow(); // the settings are the app's: every window follows
            }
        }

        private void ApplyFolderOptionsToWindow()
        {
            RaisePropertyChanged(nameof(WindowTitle));
            foreach (FileListViewModel pane in AllPanes)
            {
                pane.NotifyDisplaySettingsChanged();
            }

            Tabs.RefreshTitles();
            NavigationViewModel.ApplyShowAllFolders();
            NavigationViewModel.RequestImmediateDriveRefresh();
        }

        // ------------------------------------------------------------------ tabs

        /// <summary>
        /// Opens a folder the way a middle click does (Settings › Tabs): in a new tab next to this one (behind it, unless
        /// the setting says to switch), or in the other pane.
        /// </summary>
        public Task OpenWithMiddleClickAsync(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return Task.CompletedTask;
            }

            return SettingsService?.Current?.Tabs?.MiddleClickOpens == TabSettings.MiddleClickOtherPane
                ? OpenInOtherPaneAsync(folder)
                : OpenInNewTabAsync(folder);
        }

        /// <summary>Opens a folder where <paramref name="target"/> says (a search shown in the active pane ends first).</summary>
        public Task OpenFolderAsync(string folder, FolderOpenTarget target)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return Task.CompletedTask;
            }

            switch (target)
            {
                case FolderOpenTarget.NewTab:
                    return OpenInNewTabAsync(folder, select: true);
                case FolderOpenTarget.MiddleClick:
                    return OpenWithMiddleClickAsync(folder);
                case FolderOpenTarget.OtherPane:
                    ClearSearch();
                    return OpenInOtherPaneAsync(folder);
                default:
                    ClearSearch();
                    return folder == TabLocation.Trash ? FileListViewModel.LoadTrashAsync() : FileListViewModel.LoadFolderAsync(folder);
            }
        }

        /// <summary>Opens a place in a new tab next to the selected one ("Open in new tab").</summary>
        public Task OpenInNewTabAsync(string location, bool? select = null)
        {
            if (string.IsNullOrEmpty(location))
            {
                return Task.CompletedTask;
            }

            ClearSearchIfShowing(select);
            return Tabs.OpenInNewTabAsync(location, select);
        }

        /// <summary>
        /// "Open in new tab" for a selection: one folder opens in a tab shown at once (as Explorer's menu does); several open
        /// behind, in their order, and <paramref name="showFirst"/> shows the first of them.
        /// </summary>
        public async Task OpenFoldersInNewTabsAsync(IReadOnlyList<string> folders, bool showFirst)
        {
            if (folders == null || folders.Count == 0)
            {
                return;
            }

            ClearSearchIfShowing(showFirst);
            TabViewModel origin = Tabs.SelectedTab;
            TabViewModel first = await Tabs.OpenInNewTabAsync(folders[0], select: false).ConfigureAwait(true);
            if (folders.Count > 1)
            {
                await Tabs.OpenInNewTabsAsync(folders.Skip(1).ToList()).ConfigureAwait(true);
            }

            if (showFirst && first != null && !first.IsClosed && ReferenceEquals(Tabs.SelectedTab, origin))
            {
                Tabs.Select(first);
            }
        }

        /// <summary>Opening a tab in front ends a search shown now (its results would cover the new tab's folder).</summary>
        private void ClearSearchIfShowing(bool? select)
        {
            if ((select ?? SettingsService?.Current?.Tabs?.SwitchToNewTab ?? false) && Search.IsActive)
            {
                Tabs.SelectedTab.SavedSearch = Search.Query; // coming back to this tab brings the search back
                ClearSearch();
            }
        }

        /// <summary>
        /// The selected tab changed: everything bound to the panes follows (the views swap their view models, the toolbar
        /// and address bar move to the new tab's active pane), and the window's search follows the tab: the one left keeps
        /// its query, the one shown gets its own back (or none).
        /// </summary>
        private void OnSelectedTabChanged(object sender, TabSelectionChangedEventArgs e)
        {
            if (e.OldTab != null && !e.OldTab.IsClosed && Search != null)
            {
                e.OldTab.SavedSearch = Search.IsActive ? Search.Query : null;
            }

            ObserveTab(e.NewTab);
            Lens?.Close(); // each tab shows its own folder

            // The panes first, then the split: a window laying out the split finds the right panes in place.
            RaisePropertyChanged(nameof(PrimaryFileListViewModel));
            RaisePropertyChanged(nameof(SecondaryFileListViewModel));
            RaisePropertyChanged(nameof(IsSplitViewActive));
            RaisePropertyChanged(nameof(IsSecondaryPaneFocused));
            RaisePropertyChanged(nameof(FileListViewModel));
            RaisePropertyChanged(nameof(WindowTitle));
            _toggleSplitViewCommand?.RaiseCanExecuteChanged();
            UpdatePreviewActivity();

            if (Search == null)
            {
                return;
            }

            string query = e.NewTab.SavedSearch;
            e.NewTab.SavedSearch = null;
            if (!string.IsNullOrEmpty(query) && IsSearchEnabled)
            {
                SearchText = query;
            }
            else if (Search.IsActive)
            {
                ClearSearch();
            }

            Search.NotifyActiveFolderChanged();
        }

        private void ObserveTab(TabViewModel tab)
        {
            if (_observedTab != null)
            {
                _observedTab.PropertyChanged -= OnObservedTabPropertyChanged;
            }

            _observedTab = tab;
            if (tab != null)
            {
                tab.PropertyChanged += OnObservedTabPropertyChanged;
            }
        }

        /// <summary>The selected tab's split or focus changed: the window's panes and active pane follow.</summary>
        private void OnObservedTabPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TabViewModel.Secondary):
                    RaisePropertyChanged(nameof(SecondaryFileListViewModel));
                    break;
                case nameof(TabViewModel.IsSplitViewActive):
                    RaisePropertyChanged(nameof(IsSplitViewActive));
                    RaisePropertyChanged(nameof(FileListViewModel));
                    RaisePropertyChanged(nameof(WindowTitle)); // the other pane may be the active one now
                    _toggleSplitViewCommand?.RaiseCanExecuteChanged();
                    UpdatePreviewActivity();
                    break;
                case nameof(TabViewModel.IsSecondaryPaneFocused):
                    RaisePropertyChanged(nameof(IsSecondaryPaneFocused));
                    RaisePropertyChanged(nameof(FileListViewModel));
                    RaisePropertyChanged(nameof(WindowTitle));
                    UpdatePreviewActivity();
                    Search?.NotifyActiveFolderChanged();
                    break;
            }
        }

        /// <summary>
        /// A tab setting changed (the bar's position or width, a tab pinned): saved now, with the tabs as they are, so a
        /// pinned tab comes back even if the app doesn't close normally. Nothing is written before the settings are read:
        /// that would replace the file with defaults.
        /// </summary>
        private void SaveTabSettingsSoon()
        {
            TabSettings tabSettings = SettingsService?.Current?.Tabs;
            if (!_settingsLoaded || tabSettings == null || Tabs == null)
            {
                return;
            }

            tabSettings.SessionTabs = Tabs.CaptureSession(out int selected);
            tabSettings.SessionSelectedTab = selected;
            SaveSettingsSoon();
        }

        /// <summary>Re-reads the mounted volumes (the address bar names them) and the tabs' titles with them.</summary>
        private async Task RefreshVolumesAsync()
        {
            await Omnibar.RefreshVolumesAsync().ConfigureAwait(true);
            Tabs.RefreshTitles();
        }

        FileListViewModel ITabHost.CreatePane(FileListViewModel viewFrom)
        {
            var pane = new FileListViewModel(_indexService, SettingsService, _fileOperationService, _fileBackupService, _analyticsService, _soundFeedbackService, _dispatcher, Log.Logger)
            {
                DialogService = _dialogService,
                Operations = Operations,
                Pins = Pins
            };
            pane.OpenInNewTabs = OpenFoldersInNewTabsAsync;
            pane.OpenInNewWindows = _openNewWindow == null ? null : OpenFoldersInNewWindowsAsync;
            pane.OpenWithMiddleClick = OpenWithMiddleClickAsync;
            pane.FolderChangedOnDisk += OnPaneFolderChangedOnDisk;
            pane.PropertyChanged += OnPanePropertyChanged;
            pane.NavigationRequested += OnPaneNavigationRequested;
            pane.OverlayShown = () => IsLensActive && ReferenceEquals(pane, FileListViewModel);
            pane.LeaveOverlay = () => Lens?.Close();
            if (viewFrom != null)
            {
                pane.CopyViewFrom(viewFrom);
            }
            else if (_settingsLoaded)
            {
                pane.ApplyViewSettings();
            }

            PaneAdded?.Invoke(this, pane);
            return pane;
        }

        void ITabHost.ReleasePane(FileListViewModel pane)
        {
            if (pane == null)
            {
                return;
            }

            pane.FolderChangedOnDisk -= OnPaneFolderChangedOnDisk;
            pane.PropertyChanged -= OnPanePropertyChanged;
            pane.NavigationRequested -= OnPaneNavigationRequested;
            try
            {
                PaneRemoved?.Invoke(this, pane);
                pane.Dispose(); // its folder watcher and timers
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose a pane cleanly");
            }
        }

        async Task ITabHost.LoadAsync(FileListViewModel pane, string location, bool fallBackToHome)
        {
            try
            {
                switch (location)
                {
                    case TabLocation.Home:
                        await pane.LoadHomeAsync().ConfigureAwait(true);
                        break;
                    case TabLocation.ThisPc:
                        await pane.LoadThisPcAsync().ConfigureAwait(true);
                        break;
                    case TabLocation.Trash:
                        await pane.LoadTrashAsync().ConfigureAwait(true);
                        break;
                    default:
                        if (fallBackToHome && !await FolderExistsAsync(location).ConfigureAwait(true))
                        {
                            await pane.LoadHomeAsync().ConfigureAwait(true);
                        }
                        else
                        {
                            await pane.LoadFolderAsync(location).ConfigureAwait(true);
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open {Location} in a tab", location);
            }
        }

        TabDescription ITabHost.Describe(string location) => DescribeLocation(location);

        void ITabHost.ShowMessage(string message) =>
            _dialogService?.ShowMessageAsync("ByteExplorer", message).Forget("tabs message");

        /// <summary>
        /// How a place reads in a tab, named as the address bar names it (the last breadcrumb): "Home" and "This PC" for
        /// the pages, a drive by its label, a share as "photos on nas", a phone's folders by name; the home folder itself by
        /// its name, so it can't be mistaken for the Home page. The tip is the full path.
        /// </summary>
        internal TabDescription DescribeLocation(string location)
        {
            switch (location)
            {
                case TabLocation.Home:
                    return new TabDescription("Home", "Home", Icons.Symbol.Home);
                case TabLocation.ThisPc:
                    return new TabDescription("This PC", "This PC", Icons.Symbol.Desktop);
                case TabLocation.Trash:
                    return new TabDescription(Core.Platform.Trash.DisplayName, Core.Platform.Trash.DisplayName, Icons.Symbol.Delete);
            }

            string tip = Core.Platform.Phones.PhonePaths.IsPhonePath(location)
                ? string.Join(" › ", Core.Platform.Phones.PhonePaths.Segments(location))
                : location;
            IReadOnlyList<Core.Omnibar.Crumb> crumbs = Core.Omnibar.BreadcrumbBuilder.Build(location, SpecialFolders.Home,
                Omnibar?.Volumes ?? Array.Empty<Core.Omnibar.CrumbVolume>());
            if (crumbs.Count == 0)
            {
                return new TabDescription(PathUtil.GetName(location) ?? location, tip, Icons.Symbol.Folder);
            }

            Core.Omnibar.Crumb last = crumbs[crumbs.Count - 1];
            string title = last.Name;
            Icons.Symbol icon;
            switch (last.Kind)
            {
                case Core.Omnibar.CrumbKind.Home:
                    title = PathUtil.GetName(last.Path) is string name && name.Length > 0 ? name : last.Name;
                    icon = Icons.Symbol.Home;
                    break;
                case Core.Omnibar.CrumbKind.Root:
                case Core.Omnibar.CrumbKind.Volume:
                    icon = Icons.Symbol.HardDrive;
                    break;
                case Core.Omnibar.CrumbKind.Network:
                    icon = Icons.Symbol.GlobeDesktop;
                    break;
                case Core.Omnibar.CrumbKind.Archive:
                    icon = Icons.Symbol.FolderZip;
                    break;
                case Core.Omnibar.CrumbKind.Phone:
                    icon = Icons.Symbol.Phone;
                    break;
                default:
                    icon = Icons.Symbol.Folder;
                    break;
            }

            return new TabDescription(string.IsNullOrEmpty(title) ? location : title, tip, icon);
        }

        public ICommand ToggleSplitViewCommand { get; }
        public ISoundFeedbackService SoundFeedbackService => _soundFeedbackService;

        /// <summary>Global search: query, results, order, filters (see <see cref="ByteExplorer.ViewModels.Search.SearchViewModel"/>).</summary>
        public ByteExplorer.ViewModels.Search.SearchViewModel Search { get; }

        /// <summary>The search index service (tests drive builds through it).</summary>
        internal GlobalSearchService SearchService => _searchService;

        public ICommand RefreshIndexCommand { get; }
        public ICommand StopIndexingCommand { get; }

        /// <summary>
        /// True while a load or full build is in progress. Used by the
        /// window Closing handler to prompt before exit.
        /// </summary>
        public bool IsIndexing
        {
            get { return _isIndexing; }
            private set
            {
                if (SetProperty(ref _isIndexing, value))
                {
                    _refreshIndexCommand.RaiseCanExecuteChanged();
                    _stopIndexingCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>True only when a complete index is loaded and searchable.</summary>
        public bool IsSearchEnabled
        {
            get { return _isSearchEnabled; }
            private set
            {
                if (SetProperty(ref _isSearchEnabled, value))
                {
                    RaisePropertyChanged(nameof(IsSearchPlaceholderVisible));
                    RaisePropertyChanged(nameof(SearchBoxDisplayText));
                    RaisePropertyChanged(nameof(SearchPlaceholder));
                }
            }
        }

        /// <summary>Short status shown in the search box while search is disabled.</summary>
        public string IndexShortStatus
        {
            get { return _indexShortStatus; }
            private set
            {
                if (SetProperty(ref _indexShortStatus, value))
                {
                    RaisePropertyChanged(nameof(SearchBoxDisplayText));
                }
            }
        }

        /// <summary>Detail status for the magnifying-glass tooltip.</summary>
        public string IndexDetailStatus
        {
            get { return _indexDetailStatus; }
            private set { SetProperty(ref _indexDetailStatus, value); }
        }

        /// <summary>
        /// Brush for the magnifying-glass glyph:
        /// red = no index / error, yellow = indexing, green = ready.
        /// </summary>
        public IBrush IndexIconBrush
        {
            get { return _indexIconBrush; }
            private set { SetProperty(ref _indexIconBrush, value); }
        }

        /// <summary>
        /// The search box's text. While no index is loaded (the first build) it shows the index status and cannot be
        /// edited; otherwise it is the search query (<see cref="ByteExplorer.ViewModels.Search.SearchViewModel.Query"/>).
        /// </summary>
        public string SearchBoxDisplayText
        {
            get => IsSearchEnabled ? Search.Query : IndexShortStatus;
            set
            {
                if (IsSearchEnabled)
                {
                    Search.Query = value ?? string.Empty;
                }
            }
        }

        /// <summary>The search query (same as <see cref="ByteExplorer.ViewModels.Search.SearchViewModel.Query"/>).</summary>
        public string SearchText
        {
            get => Search.Query;
            set => Search.Query = value ?? string.Empty;
        }

        public bool IsSearchPlaceholderVisible => IsSearchEnabled && string.IsNullOrEmpty(Search.Query);

        /// <summary>The empty search box's hint: "Search 1.2M items", or the index status while it updates.</summary>
        public string SearchPlaceholder => IsSearchEnabled ? IndexShortStatus : string.Empty;

        /// <summary>A search is shown (its results replace the file list).</summary>
        public bool IsSearchActive => Search.IsActive;

        /// <summary>The lenses (left panel › Lens) and the lens view.</summary>
        public ByteExplorer.ViewModels.Lens.LensViewModel Lens { get; }

        /// <summary>A lens is shown in place of the file list.</summary>
        public bool IsLensActive => Lens?.IsActive == true;

        /// <summary>The name of the lens shown ("Photos"), for the address bar.</summary>
        public string LensTitle => Lens?.Title ?? string.Empty;

        /// <summary>The panes show (neither search results nor a lens has taken their place).</summary>
        public bool ShowsPanes => !IsSearchActive && !IsLensActive;

        /// <summary>Shows a lens: the search ends, the lens takes the file list's place until a folder is opened.</summary>
        public System.Threading.Tasks.Task OpenLensAsync(ByteExplorer.ViewModels.Lens.LensEntryViewModel lens)
        {
            if (Search.IsActive)
            {
                ClearSearch();
            }

            return Lens.OpenAsync(lens);
        }

        private void OnLensPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ByteExplorer.ViewModels.Lens.LensViewModel.IsActive))
            {
                RaisePropertyChanged(nameof(IsLensActive));
                RaisePropertyChanged(nameof(ShowsPanes));
                FileListViewModel?.NotifyOverlayChanged(); // Back now leaves the lens
            }
            else if (e.PropertyName == nameof(ByteExplorer.ViewModels.Lens.LensViewModel.Title))
            {
                RaisePropertyChanged(nameof(LensTitle));
            }
        }

        private void OnPaneNavigationRequested(object sender, EventArgs e)
        {
            if (ReferenceEquals(sender, FileListViewModel))
            {
                Lens?.Close(); // the person went to a folder: the file list comes back
            }
        }

        /// <summary>Backward-compatible alias used by older XAML bindings.</summary>
        public string IndexStatusText => IndexDetailStatus;

        private void OnSearchPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ByteExplorer.ViewModels.Search.SearchViewModel.IsActive):
                    if (Search.IsActive)
                    {
                        Lens?.Close(); // a search takes the list's place
                    }

                    RaisePropertyChanged(nameof(IsSearchActive));
                    RaisePropertyChanged(nameof(ShowsPanes));
                    break;
                case nameof(ByteExplorer.ViewModels.Search.SearchViewModel.Query):
                    RaisePropertyChanged(nameof(SearchText));
                    RaisePropertyChanged(nameof(SearchBoxDisplayText));
                    RaisePropertyChanged(nameof(IsSearchPlaceholderVisible));
                    break;
            }
        }

        /// <summary>A folder shown in a pane changed on disk (its watcher, a refresh): the index updates that folder.</summary>
        private void OnPaneFolderChangedOnDisk(object sender, string folder) =>
            _searchService.NotifyFoldersChanged(new[] { folder });

        private void OnSearchServiceStatusChanged(object sender, EventArgs e)
        {
            // Service may raise from a thread-pool thread.
            if (_dispatcher.CheckAccess())
            {
                ApplySearchServiceStatus();
            }
            else
            {
                _dispatcher.InvokeAsync(ApplySearchServiceStatus).Forget("apply search status");
            }
        }

        private void ApplySearchServiceStatus()
        {
            SearchIndexState state = _searchService.State;
            bool indexing = _searchService.IsIndexing;
            bool ready = _searchService.IsIndexReady;

            IsIndexing = indexing;
            IsSearchEnabled = ready;
            IndexShortStatus = _searchService.ShortStatus;
            IndexDetailStatus = _searchService.DetailStatus;

            switch (state)
            {
                case SearchIndexState.Ready:
                    IndexIconBrush = IndexIconGreen;
                    break;
                case SearchIndexState.Building:
                case SearchIndexState.Checking:
                    IndexIconBrush = IndexIconYellow;
                    break;
                case SearchIndexState.Error:
                case SearchIndexState.Cancelled:
                case SearchIndexState.NotStarted:
                default:
                    IndexIconBrush = IndexIconRed;
                    break;
            }

            RaisePropertyChanged(nameof(IndexStatusText));
            RaisePropertyChanged(nameof(SearchPlaceholder));
            RaisePropertyChanged(nameof(SearchBoxDisplayText));
            RaisePropertyChanged(nameof(IsSearchPlaceholderVisible));
        }

        private void OnNavigationNodeActivated(object sender, NavigationNodeViewModel node)
        {
            var ignored = NavigateToNodeAsync(node);
        }

        private void OnNavigationExternalChangeDetected(object sender, string affectedPath)
        {
            var ignored = HandleExternalChangeAsync(affectedPath);
        }

        private void OnNavigationDrivesRefreshed(object sender, System.Collections.Generic.IReadOnlyList<ByteExplorer.Core.Models.DriveNodeInfo> drives)
        {
            try
            {
                // A pin on a drive that came or went changes availability.
                Pins?.RefreshAvailabilityAsync().Forget("check Quick access availability");
                RefreshVolumesAsync().Forget("omnibar volumes");

                // A global, app-wide event (the whole system's drive
                // list changed) — updates every active pane, not just
                // whichever one currently has focus, unlike the
                // redirector FileListViewModel is elsewhere in this
                // class. A This PC page left showing yesterday's drive
                // list in the pane that DIDN'T happen to have focus at
                // the moment a USB drive was unplugged would be a real,
                // confusing bug, not a cosmetic one.
                foreach (FileListViewModel pane in AllPanes)
                {
                    pane.UpdateThisPcDrives(drives);
                }
            }
            catch (Exception ex)
            {
                // A display-mapping failure here must never be able to
                // take down the app — worst case the This PC page's
                // drive tiles stay whatever they were a moment ago.
                _logger.Warning(ex, "Failed to update the This PC drive list");
            }
        }

        private async Task HandleExternalChangeAsync(string affectedPath)
        {
            try
            {
                // Same reasoning as OnNavigationDrivesRefreshed above — a
                // shell action taken from the navigation tree (Rename/
                // Delete/New via the real Explorer menu) can affect a
                // folder either pane happens to be showing, not only
                // whichever one currently has focus.
                foreach (FileListViewModel pane in AllPanes.ToArray())
                {
                    await pane.NotifyExternalChangeAsync(affectedPath).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                // FileListViewModel.NotifyExternalChangeAsync already
                // guards its own internal steps individually — this is
                // a last-resort net so a shell-triggered tree refresh
                // can never surface as an unhandled fault.
                _logger.Warning(ex, "Failed to propagate external change for {Path} to the file list", affectedPath);
            }
        }

        private async Task NavigateToNodeAsync(NavigationNodeViewModel node)
        {
            if (node == null)
            {
                return;
            }

            ClearSearch();

            try
            {
                // Synthetic "This PC" root has no filesystem path — show the
                // Windows 11-style devices + recent home surface instead.
                if (node.NodeType == ByteExplorer.Core.Models.NavigationNodeType.Computer
                    || node.FullPath == null)
                {
                    if (node.NodeType == ByteExplorer.Core.Models.NavigationNodeType.Computer)
                    {
                        await FileListViewModel.LoadThisPcAsync().ConfigureAwait(true);
                    }
                    return;
                }

                await FileListViewModel.LoadFolderAsync(node.FullPath).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load folder for selected node {Path}", node.FullPath);
            }
        }

        // ------------------------------------------------------------------ panels and tree settings

        private bool _isNavigationPaneVisible = true;
        private bool _isDetailsPaneVisible;
        private bool _isToolbarVisible = true;
        private bool _isStatusBarVisible = true;

        /// <summary>The left pane (sidebar and tree). The window lays itself out from these four; they are remembered.</summary>
        public bool IsNavigationPaneVisible
        {
            get => _isNavigationPaneVisible;
            set => SetPanel(ref _isNavigationPaneVisible, value, s => s.StartWithLeftPanelCollapsed = !value);
        }

        /// <summary>The right pane (details and preview). Its preview loads only while it is visible.</summary>
        /// <summary>Folder options › "Show Windows icons" is on and this system has them (the folder tree binds it).</summary>
        public bool ShowSystemIcons => Infrastructure.SystemIcons.SystemIconCache.IsActive;

        /// <summary>The icons setting changed: the tree and every pane redraw with the other icons.</summary>
        public void NotifySystemIconsChanged()
        {
            foreach (MainViewModel window in AllWindows)
            {
                window.RaisePropertyChanged(nameof(ShowSystemIcons));
                foreach (FileListViewModel pane in window.AllPanes)
                {
                    pane?.NotifyDisplaySettingsChanged();
                }
            }
        }

        /// <summary>The tab settings changed (the settings window): every window's tab bar follows at once.</summary>
        public void NotifyTabSettingsChanged()
        {
            foreach (MainViewModel window in AllWindows)
            {
                window.Tabs.NotifySettingsChanged();
            }
        }

        public bool IsDetailsPaneVisible
        {
            get => _isDetailsPaneVisible;
            set
            {
                if (SetPanel(ref _isDetailsPaneVisible, value, s => s.StartWithRightPanelCollapsed = !value))
                {
                    UpdatePreviewActivity();
                }
            }
        }

        /// <summary>The command toolbar (Modern layout).</summary>
        public bool IsToolbarVisible
        {
            get => _isToolbarVisible;
            set => SetPanel(ref _isToolbarVisible, value, s => s.StartWithToolbarCollapsed = !value);
        }

        public bool IsStatusBarVisible
        {
            get => _isStatusBarVisible;
            set => SetPanel(ref _isStatusBarVisible, value, s => s.StartWithStatusBarCollapsed = !value);
        }

        /// <summary>
        /// True when the application menu opens a category's items in a submenu beside it (AppearanceSettings
        /// .MenuSubmenuStyle "Horizontal", the default); false unfolds them in place.
        /// </summary>
        public bool IsMenuSubmenuHorizontal
        {
            get => !string.Equals(SettingsService?.Current?.Appearance?.MenuSubmenuStyle,
                ByteExplorer.Core.Models.AppearanceSettings.SubmenuVertical, StringComparison.OrdinalIgnoreCase);
            set
            {
                var appearance = SettingsService?.Current?.Appearance;
                if (appearance == null || value == IsMenuSubmenuHorizontal)
                {
                    return;
                }

                appearance.MenuSubmenuStyle = value
                    ? ByteExplorer.Core.Models.AppearanceSettings.SubmenuHorizontal
                    : ByteExplorer.Core.Models.AppearanceSettings.SubmenuVertical;
                foreach (MainViewModel window in AllWindows)
                {
                    window.RaisePropertyChanged(nameof(IsMenuSubmenuHorizontal));
                }

                SaveSettingsSoon();
            }
        }

        /// <summary>Whether the navigation tree follows the folder shown in the file list (NavigationSettings.TreeFollowsCurrentFolder).</summary>
        public bool TreeFollowsCurrentFolder
        {
            get => SettingsService?.Current?.Navigation?.TreeFollowsCurrentFolder ?? true;
            set => SetNavigationSetting(value, n => n.TreeFollowsCurrentFolder, (n, v) => n.TreeFollowsCurrentFolder = v, nameof(TreeFollowsCurrentFolder));
        }

        /// <summary>Whether following expands the tree down to the folder, or only selects a row already visible.</summary>
        public bool ExpandTreeToCurrentFolder
        {
            get => SettingsService?.Current?.Navigation?.ExpandTreeToCurrentFolder ?? true;
            set => SetNavigationSetting(value, n => n.ExpandTreeToCurrentFolder, (n, v) => n.ExpandTreeToCurrentFolder = v, nameof(ExpandTreeToCurrentFolder));
        }

        /// <summary>Reads the remembered panel state after the settings load (the window follows the property changes).</summary>
        private void LoadPanelState()
        {
            var fileList = SettingsService?.Current?.FileList;
            if (fileList == null)
            {
                return;
            }

            SetProperty(ref _isNavigationPaneVisible, !fileList.StartWithLeftPanelCollapsed, nameof(IsNavigationPaneVisible));
            SetProperty(ref _isDetailsPaneVisible, !fileList.StartWithRightPanelCollapsed, nameof(IsDetailsPaneVisible));
            SetProperty(ref _isToolbarVisible, !fileList.StartWithToolbarCollapsed, nameof(IsToolbarVisible));
            SetProperty(ref _isStatusBarVisible, !fileList.StartWithStatusBarCollapsed, nameof(IsStatusBarVisible));
            RaisePropertyChanged(nameof(TreeFollowsCurrentFolder));
            RaisePropertyChanged(nameof(ExpandTreeToCurrentFolder));
            RaisePropertyChanged(nameof(IsMenuSubmenuHorizontal));
            UpdatePreviewActivity();
        }

        private bool SetPanel(ref bool field, bool value, Action<ByteExplorer.Core.Models.FileListSettings> remember, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (!SetProperty(ref field, value, name))
            {
                return false;
            }

            var fileList = SettingsService?.Current?.FileList;
            if (fileList != null)
            {
                remember(fileList);
                SaveSettingsSoon();
            }

            return true;
        }

        private void SetNavigationSetting(bool value, Func<ByteExplorer.Core.Models.NavigationSettings, bool> read,
            Action<ByteExplorer.Core.Models.NavigationSettings, bool> write, string name)
        {
            var navigation = SettingsService?.Current?.Navigation;
            if (navigation == null || read(navigation) == value)
            {
                return;
            }

            write(navigation, value);
            foreach (MainViewModel window in AllWindows)
            {
                window.RaisePropertyChanged(name);
                window.NavigationViewModel.NotifyFollowSettingsChanged();
            }

            SaveSettingsSoon();
        }

        /// <summary>
        /// Forgets all usage history (the settings window's Privacy page, which asks first) and refreshes a Home
        /// dashboard on show in either pane.
        /// </summary>
        public void ClearActivityData()
        {
            try
            {
                _analyticsService.ClearAllData();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clear the activity data");
                return;
            }

            foreach (FileListViewModel pane in AllPanesOfAllWindows)
            {
                if (pane != null && pane.IsHomeView)
                {
                    pane.RefreshHomeDashboard();
                }
            }
        }

        /// <summary>
        /// Saves the settings in the background after a change made outside the view models (the settings window). The
        /// settings are the app's: the other windows re-read what they show straight from them.
        /// </summary>
        public void PersistSettings()
        {
            foreach (MainViewModel window in AllWindows)
            {
                if (!ReferenceEquals(window, this))
                {
                    window.RaiseSettingsDerivedProperties();
                }
            }

            SaveSettingsSoon();
        }

        /// <summary>The properties read straight from the settings, raised after another window changed them.</summary>
        private void RaiseSettingsDerivedProperties()
        {
            RaisePropertyChanged(nameof(Theme));
            RaisePropertyChanged(nameof(IsMenuSubmenuHorizontal));
            RaisePropertyChanged(nameof(TreeFollowsCurrentFolder));
            RaisePropertyChanged(nameof(ExpandTreeToCurrentFolder));
            RaisePropertyChanged(nameof(ShowSystemIcons));
            SyncSidebarWithSettings();
        }

        /// <summary>
        /// Quick access and the network places as the settings have them now: another window may have pinned, unpinned,
        /// renamed or moved one (only a list that differs is rebuilt).
        /// </summary>
        public void SyncSidebarWithSettings()
        {
            Pins.ReloadIfChanged();
            NetworkLocations.ReloadIfChanged();
        }

        /// <summary>Raised when a look-and-layout customization changed (section order, area colors and sizes, background).</summary>
        public event EventHandler CustomizationChanged;

        /// <summary>The settings window changed a customization: the window applies it now and the settings are saved.</summary>
        public void NotifyCustomizationChanged()
        {
            foreach (MainViewModel window in AllWindows)
            {
                window.CustomizationChanged?.Invoke(window, EventArgs.Empty); // the look is the app's: every window applies it
            }

            SaveSettingsSoon();
        }

        private void SaveSettingsSoon()
        {
            SettingsService.SaveAsync().ContinueWith(
                t => _logger.Warning(t.Exception, "Could not save the settings"),
                System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>Only the active pane's preview works, and only while the details pane is visible.</summary>
        private void UpdatePreviewActivity()
        {
            FileListViewModel active = FileListViewModel;
            foreach (FileListViewModel pane in AllPanes)
            {
                if (pane != null)
                {
                    pane.Preview.IsActive = _isDetailsPaneVisible && ReferenceEquals(pane, active);
                }
            }
        }

        /// <summary>The tags of files and folders (tags.json in the data folder); also <see cref="Core.Tags.TagStore.Default"/>. The app's, shared by every window.</summary>
        public Core.Tags.TagStore Tags => _services.Tags;

        /// <summary>The process-wide services this window shares with the other main windows.</summary>
        public AppServices Services => _services;

        /// <summary>The window's dialogs (null for a view model without a window).</summary>
        public IDialogService DialogService => _dialogService;

        /// <summary>Every window's view model sharing these services, this one included (for settings that apply to every window).</summary>
        private IEnumerable<MainViewModel> AllWindows => _services.Windows.ToArray();

        /// <summary>The window was activated: dialogs of shared work (running operations) appear over it from now on.</summary>
        public void MarkActive() => _services.MarkActive(this);

        /// <summary>Every pane of every window (settings that change how panes show apply to all of them).</summary>
        public IEnumerable<FileListViewModel> AllPanesOfAllWindows => AllWindows.SelectMany(w => w.AllPanes).ToArray();

        /// <summary>
        /// Opens a new main window showing a location (a folder, <see cref="TabLocation.Home"/> or
        /// <see cref="TabLocation.ThisPc"/>). Set by the window; null where there is no window to open one from.
        /// </summary>
        public Action<string> OpenNewWindow
        {
            get => _openNewWindow;
            set
            {
                _openNewWindow = value;
                foreach (FileListViewModel pane in AllPanes)
                {
                    pane.OpenInNewWindows = value == null ? null : OpenFoldersInNewWindowsAsync;
                }
            }
        }

        private Action<string> _openNewWindow;

        /// <summary>
        /// A new window showing the active pane's place (Ctrl+N, the app menu's "New window"); Home when it shows nothing
        /// yet. Disabled for a view model without a window.
        /// </summary>
        public ICommand NewWindowCommand => _newWindowCommand ??= new RelayCommand(
            _ => OpenNewWindow?.Invoke(TabLocation.Of(FileListViewModel) ?? TabLocation.Home),
            _ => OpenNewWindow != null);

        private RelayCommand _newWindowCommand;

        /// <summary>Opens folders in new windows, one each (the menu's "Open in new window", Explorer's verb).</summary>
        public Task OpenFoldersInNewWindowsAsync(IReadOnlyList<string> folders)
        {
            if (OpenNewWindow != null && folders != null)
            {
                foreach (string folder in folders.Where(f => !string.IsNullOrEmpty(f)))
                {
                    OpenNewWindow(folder);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>Opens a location in a pane of this window: the place a new window was asked to show.</summary>
        public Task OpenLocationAsync(string location) =>
            ((ITabHost)this).LoadAsync(PrimaryFileListViewModel, string.IsNullOrEmpty(location) ? TabLocation.Home : location, fallBackToHome: false);

        private ByteExplorer.ViewModels.Sidebar.TagsSidebarViewModel _tagSidebar;

        /// <summary>The sidebar's Tags section (created on first use, over <see cref="Tags"/>).</summary>
        public ByteExplorer.ViewModels.Sidebar.TagsSidebarViewModel TagSidebar
        {
            get
            {
                if (_tagSidebar == null)
                {
                    _tagSidebar = new ByteExplorer.ViewModels.Sidebar.TagsSidebarViewModel(Tags)
                    {
                        OpenTag = row => SearchText = Core.Tags.TagSearch.QueryFor(row.FullName)
                    };
                    Search.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(ByteExplorer.ViewModels.Search.SearchViewModel.Query))
                        {
                            _tagSidebar.SetActiveQuery(Search.Query);
                        }
                    };
                }

                return _tagSidebar;
            }
        }

        /// <summary>Where running copies and moves keep their crash journal (see OperationJournal).</summary>
        public static string OperationJournalFolder => Core.AppPaths.Combine("operations");

        public async Task InitializeAsync()
        {
            // The settings, the tags and the recovery of interrupted operations are the app's: loaded once, by the first
            // window; a window opened later finds them ready.
            await _services.EnsureSettingsReadyAsync().ConfigureAwait(true);
            PrimaryFileListViewModel.InitializeFromSettings();
            _settingsLoaded = true; // panes made from now on (new tabs) start with the saved view
            Tabs.NotifySettingsChanged();
            LoadPanelState();
            Pins.Reload();
            NetworkLocations.Reload();
            RefreshVolumesAsync().Forget("omnibar volumes");
            await NavigationViewModel.InitializeAsync().ConfigureAwait(true);
            await _services.FinishStartupAsync().ConfigureAwait(true); // tags loaded, the index check started (once)
        }

        private async Task RefreshIndexAsync()
        {
            ClearSearch();
            await _services.RefreshIndexAsync().ConfigureAwait(true);
        }

        private void StopIndexing() => _services.StopIndexing();

        /// <summary>
        /// Never gates turning split view OFF — only ON. Only Nexus is
        /// excluded: its category clusters are laid out in a wide
        /// WrapPanel that needs the window's width to breathe. Columns
        /// is allowed: each pane hosts its own column browser and scrolls
        /// its columns horizontally, so a half-width pane works (two
        /// column browsers side by side is the classic two-panel
        /// workflow). The other modes are plain item lists.
        /// </summary>
        private bool CanToggleSplitView()
        {
            if (IsSplitViewActive)
            {
                return true;
            }

            return PrimaryFileListViewModel?.ViewMode != FileListViewMode.Nexus;
        }

        /// <summary>
        /// Every pane's changes, of every tab; only the selected tab's panes matter here. Re-evaluates whether split view
        /// can be turned ON when the primary pane's view mode changes (Nexus needs the window's full width; see
        /// CanToggleSplitView), keeps the window title and the search's "this folder" scope on the active pane, and
        /// re-runs a search when hidden items are shown or hidden.
        /// </summary>
        private void OnPanePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            TabViewModel tab = Tabs?.SelectedTab;
            if (tab == null || !(sender is FileListViewModel pane))
            {
                return;
            }

            bool isActive = ReferenceEquals(pane, tab.ActivePane);
            switch (e.PropertyName)
            {
                case nameof(ByteExplorer.ViewModels.FileListViewModel.ViewMode):
                    if (ReferenceEquals(pane, tab.Primary))
                    {
                        _toggleSplitViewCommand?.RaiseCanExecuteChanged();
                    }

                    break;
                case nameof(ByteExplorer.ViewModels.FileListViewModel.CurrentPath):
                    if (isActive)
                    {
                        Search?.NotifyActiveFolderChanged(); // the "this folder" scope follows the pane
                        RaisePropertyChanged(nameof(WindowTitle));
                    }

                    break;
                case nameof(ByteExplorer.ViewModels.FileListViewModel.IsThisPcView):
                case nameof(ByteExplorer.ViewModels.FileListViewModel.IsTrashView):
                    if (isActive)
                    {
                        RaisePropertyChanged(nameof(WindowTitle));
                    }

                    break;
                case nameof(ByteExplorer.ViewModels.FileListViewModel.ShowHiddenItems):
                case nameof(ByteExplorer.ViewModels.FileListViewModel.ShowSystemItems):
                    if (isActive && Search?.IsActive == true)
                    {
                        Search.Refresh(); // hidden results follow the setting
                    }

                    break;
            }
        }

        /// <summary>
        /// Activating clones the primary pane's CURRENT state into a
        /// freshly-constructed secondary one — same view mode (via
        /// SelectedPreset, the same public entry point the toolbar's own
        /// ViewModeSelector uses) and the same location, Home dashboard
        /// included. After that initial clone, the two panes are fully
        /// independent — navigating one never affects the other, which
        /// is the entire point of a second pane (comparing two folders
        /// side by side, or dragging between them — see
        /// FileDragDropHelper, which already works between any two drop
        /// targets regardless of which window or pane they're in).
        ///
        /// Deactivating disposes the secondary pane outright rather than
        /// merely hiding it — its own folder watcher and debounce timers
        /// have a real, ongoing cost for a pane nobody can see, and
        /// there's no state worth preserving across an off/on cycle that
        /// re-cloning the primary pane's state doesn't already recreate.
        /// Focus always falls back to the primary pane on deactivation
        /// (see IsSecondaryPaneFocused's own setter).
        /// </summary>
        private async Task ToggleSplitViewAsync()
        {
            TabViewModel tab = Tabs.SelectedTab;
            if (tab.IsSplitViewActive)
            {
                Tabs.Unsplit(tab);
                return;
            }

            await Tabs.SplitAsync(tab).ConfigureAwait(true);
        }

        /// <summary>
        /// Called from each window's own XAML — an
        /// IsKeyboardFocusWithinChanged handler on each pane's
        /// FileListView instance reports here whenever focus genuinely
        /// moves into that pane (see MainWindow.xaml's own remarks on
        /// why IsKeyboardFocusWithinChanged specifically, not GotFocus/
        /// LostFocus). Harmless to call with isSecondary: true while
        /// split view is off — IsSecondaryPaneFocused simply has no
        /// effect on FileListViewModel's own redirect in that case.
        /// </summary>
        public void SetFocusedPane(bool isSecondary)
        {
            TabViewModel tab = Tabs.SelectedTab;
            tab.IsSecondaryPaneFocused = isSecondary && tab.IsSplitViewActive;
        }

        /// <summary>Ends the search: clears the query and the results, the file list shows again.</summary>
        public void ClearSearch() => Search.Clear();

        /// <summary>
        /// Opens a search result: a folder opens in the active pane (ending the search), a file with its application (the
        /// results stay).
        /// </summary>
        public async Task OpenSearchResultAsync(ByteExplorer.ViewModels.Search.SearchResultRow item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullPath))
            {
                return;
            }

            try
            {
                if (item.IsDirectory)
                {
                    ClearSearch();
                    await FileListViewModel.LoadFolderAsync(item.FullPath).ConfigureAwait(true);
                }
                else
                {
                    ShellLauncher.Open(item.FullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open search result {Path}", item.FullPath);
                FileListViewModel?.ReportFailure("Couldn't open \"" + item.Name + "\".\n\n" + ex.Message);
            }
        }

        /// <summary>Shows a search result in its folder, selected ("Open file location"), ending the search.</summary>
        public async Task OpenSearchResultLocationAsync(ByteExplorer.ViewModels.Search.SearchResultRow item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullPath))
            {
                return;
            }

            ClearSearch();
            try
            {
                await FileListViewModel.RevealAsync(item.FullPath).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to show search result {Path} in its folder", item.FullPath);
            }
        }

        private async Task SetThemeAsync(string theme)
        {
            if (SettingsService?.Current?.Appearance == null ||
                !(theme == "System" || theme == "Light" || theme == "Dark") ||
                string.Equals(SettingsService.Current.Appearance.Theme, theme, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SettingsService.Current.Appearance.Theme = theme;
            foreach (MainViewModel window in AllWindows)
            {
                window.RaisePropertyChanged(nameof(Theme));
            }

            try
            {
                await SettingsService.SaveAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not save the theme setting");
            }
        }

        public void Dispose()
        {
            // The queue, the index and the analytics are the app's: they stop with the last window (AppServices.Detach
            // below), never with another one, so closing a window leaves the others' operations running.
            Operations.FoldersChanged -= OnOperationFoldersChanged;
            Omnibar.Dispose();

            try
            {
                _searchService.StatusChanged -= OnSearchServiceStatusChanged;
            }
            catch
            {
            }

            try
            {
                NavigationViewModel.NodeActivated -= OnNavigationNodeActivated;
                NavigationViewModel.ExternalChangeDetected -= OnNavigationExternalChangeDetected;
                NavigationViewModel.DrivesRefreshed -= OnNavigationDrivesRefreshed;
            }
            catch
            {
            }

            Search.Detach();

            try
            {
                // Previously never disposed anywhere — its periodic
                // drive-refresh Timer, and everything it could still
                // trigger, outlived the view model indefinitely,
                // including past window close.
                NavigationViewModel.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose NavigationViewModel cleanly");
            }

            try
            {
                // Releases every tab's panes and their live folder watchers (real OS handles).
                Tabs.SelectedTabChanged -= OnSelectedTabChanged;
                ObserveTab(null);
                Tabs.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the tabs cleanly");
            }

            // The last window's disposal stops the shared services: running operations are cancelled, the index stops,
            // the analytics session is flushed.
            _services.Detach(this);
        }
    }
}
