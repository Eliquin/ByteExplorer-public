// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ByteExplorer.Core.Analytics;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Platform.Phones;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Owns the middle pane: triggers a background index build for
    /// whatever folder was navigated to, and swaps in a fresh
    /// VirtualizingFileList once it's ready. Follows the same
    /// supersede-and-cancel pattern as NavigationNodeViewModel — rapid
    /// navigation (clicking through folders quickly) cancels the
    /// previous load rather than racing two results against each other.
    ///
    /// Also owns the in-app benchmark: a deliberately simple, on-demand
    /// measurement of the current folder's real (uncached) index-build
    /// time and hydration throughput, logged with structured fields so
    /// numbers from different runs/machines/optimizations are directly
    /// comparable later. It lives here rather than as a separate Core
    /// service because it needs to construct FileListItemViewModel to
    /// measure real hydration cost — and FileListItemViewModel belongs
    /// to this layer, not Core, so this is where that dependency has to
    /// live.
    /// </summary>
    public sealed partial class FileListViewModel : ViewModelBase, IDisposable
    {

        private readonly IDirectoryIndexService _indexService;
        private readonly ISettingsService _settingsService;
        private readonly IFileOperationService _fileOperationService;
        private readonly IFileBackupService _fileBackupService;
        private readonly IUserAnalyticsService _analyticsService;
        private readonly ISoundFeedbackService _soundFeedbackService;
        private readonly IUiDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly AsyncRelayCommand _navigateUpCommand;
        private readonly AsyncRelayCommand _openSelectedItemCommand;
        private readonly AsyncRelayCommand _refreshCommand;
        private readonly AsyncRelayCommand _navigateBackCommand;
        private readonly AsyncRelayCommand _navigateForwardCommand;
        private readonly NavigationHistory _history = new NavigationHistory();
        private long _lastLoadRequestTicks = long.MinValue / 2;
        private bool _isNavigatingViaHistory;

        /// <summary>
        /// True while a Back/Forward navigation is loading its folder: the view restores that folder's scroll position
        /// and selection instead of starting at the top.
        /// </summary>
        public bool IsHistoryNavigation => _isNavigatingViaHistory;
        private FileCategoryFilter _categoryFilter = FileCategoryFilter.All;
        private readonly RelayCommand _copyCommand;
        private readonly RelayCommand _copyPathCommand;
        private readonly RelayCommand _propertiesCommand;
        private readonly RelayCommand _openTerminalCommand;
        private readonly RelayCommand _cutCommand;
        private readonly RelayCommand _cancelFileOperationCommand;
        private readonly AsyncRelayCommand _pasteCommand;
        private readonly AsyncRelayCommand _deleteCommand;
        private readonly AsyncRelayCommand _deletePermanentCommand;
        private readonly AsyncRelayCommand _newFolderCommand;
        private readonly AsyncRelayCommand _renameCommand;
        private CancellationTokenSource _loadCts;
        private CancellationTokenSource _viewSettingsSaveCts;
        private CancellationTokenSource _filterCts;
        private FolderChangeWatcher _folderWatcher;
        private bool _disposed;

        private DirectoryIndex _currentIndex;
        private DirectoryIndex _displayedIndex;
        private int _residentWindow = 500;
        private int _prefetchBufferSize = 150;

        private string _currentPath;
        private string _filterText = string.Empty;
        private FileListSortColumn _sortColumn = FileListSortColumn.Name;
        private bool _sortAscending = true;
        private IList _items;
        private FileListItemViewModel _selectedItem;
        private bool _isLoading;
        private string _statusText = string.Empty;
        private string _errorMessage;
        private FileListViewMode _viewMode = FileListViewMode.Details;
        private System.Collections.ObjectModel.ObservableCollection<TimelineGroup> _timelineGroups;
        private System.Collections.ObjectModel.ObservableCollection<ColumnPane> _columnPanes;
        private ColumnPane _focusedColumnPane;
        private System.Collections.ObjectModel.ObservableCollection<NexusCluster> _nexusClusters;
        private FileListViewPreset _selectedPreset = FileListViewPreset.Details;
        private double _iconSize = FileListViewPresets.MediumIconSize;

        public FileListViewModel(
            IDirectoryIndexService indexService,
            ISettingsService settingsService,
            IFileOperationService fileOperationService,
            IFileBackupService fileBackupService,
            IUserAnalyticsService analyticsService,
            ISoundFeedbackService soundFeedbackService,
            IUiDispatcher dispatcher,
            ILogger logger = null)
        {
            _indexService = indexService;
            _settingsService = settingsService;

            // Sorted by tags: tags given or taken away re-sort the list (a weak subscription: the static event must not
            // keep a closed window's panes alive).
            var weakSelf = new WeakReference<FileListViewModel>(this);
            EventHandler<ByteExplorer.Core.Tags.TagsChangedEventArgs> onTags = null;
            onTags = (sender, e) =>
            {
                if (!weakSelf.TryGetTarget(out FileListViewModel pane))
                {
                    ByteExplorer.Core.Tags.TagStore.AnyChanged -= onTags;
                }
                else if (pane._sortColumn == FileListSortColumn.Tags && ReferenceEquals(sender, ByteExplorer.Core.Tags.TagStore.Default))
                {
                    pane.RebuildDisplayedItemsDebounced(150);
                }
            };
            ByteExplorer.Core.Tags.TagStore.AnyChanged += onTags;
            _fileOperationService = fileOperationService;
            _fileBackupService = fileBackupService;
            _analyticsService = analyticsService;
            _soundFeedbackService = soundFeedbackService;
            _dispatcher = dispatcher;
            _logger = (logger ?? Log.Logger).ForContext<FileListViewModel>();
            Preview = new FilePreviewViewModel(_dispatcher, _logger,
                () => _settingsService?.Current?.FileList?.ShowDocumentPreviews ?? true);

            NavigateUpCommand = _navigateUpCommand = new AsyncRelayCommand(
                _ => NavigateUpAsync(),
                _ => HasParentPath(),
                allowOverlap: true);

            OpenSelectedItemCommand = _openSelectedItemCommand = new AsyncRelayCommand(
                _ => OpenItemAsync(SelectedItem),
                _ => SelectedItem != null);

            // Bound to F5. Live folder watching (see StartFolderWatchingAsync)
            // now catches most of what this used to be the only way to
            // see — a change made by another process while looking at
            // the same folder here. F5 stays as the explicit, always-
            // available fallback: it works even with live watching
            // disabled in settings, and it's an unambiguous "I want the
            // truth right now" for the rare case a watcher failed to
            // start (an exotic filesystem, a network share that
            // rejects change notifications) or hasn't caught up yet.
            RefreshCommand = _refreshCommand = new AsyncRelayCommand(
                _ => IsTrashView ? LoadTrashAsync() : RefreshCurrentFolderAsync(force: true),
                _ => (!string.IsNullOrEmpty(CurrentPath) || IsTrashView) && !_isLoading);

            NavigateBackCommand = _navigateBackCommand = new AsyncRelayCommand(
                _ => NavigateBackOrLeaveOverlayAsync(),
                _ => _history.CanGoBack || OverlayShown?.Invoke() == true,
                allowOverlap: true);

            NavigateForwardCommand = _navigateForwardCommand = new AsyncRelayCommand(
                _ => NavigateHistoryAsync(1),
                _ => _history.CanGoForward,
                allowOverlap: true);

            SelectAllCommand = new RelayCommand(
                _ => SelectAllRequested?.Invoke(this, EventArgs.Empty));

            InvertSelectionCommand = new RelayCommand(
                _ => InvertSelectionRequested?.Invoke(this, EventArgs.Empty));

            SelectNoneCommand = new RelayCommand(
                _ => SelectNoneRequested?.Invoke(this, EventArgs.Empty),
                _ => SelectedItems.Count > 0);

            CompressSelectionCommand = new AsyncRelayCommand(
                _ => CompressToZipAsync(SelectedItems.Select(i => i.FullPath).ToList()),
                _ => SelectedItems.Count > 0 && !IsInsideArchive && !Phones.IsPhonePath(CurrentPath));

            TogglePinSelectionCommand = new AsyncRelayCommand(
                _ => TogglePinSelectionAsync(),
                _ => Pins != null && SelectedPinnablePaths().Count > 0);

            ToggleShowHiddenItemsCommand = new AsyncRelayCommand(
                _ => ToggleHiddenOrSystemItemsAsync(showHidden: true),
                _ => _settingsService != null);

            ToggleShowSystemItemsCommand = new AsyncRelayCommand(
                _ => ToggleHiddenOrSystemItemsAsync(showHidden: false),
                _ => _settingsService != null);

            CopyCommand = _copyCommand = new RelayCommand(
                _ => CopySelectedToClipboard(),
                _ => SelectedItems.Count > 0);

            CopyPathCommand = _copyPathCommand = new RelayCommand(
                _ => CopySelectedPathsToClipboard(),
                _ => SelectedItems.Count > 0);

            PropertiesCommand = _propertiesCommand = new RelayCommand(
                _ => ShowPropertiesForSelection(),
                _ => SelectedItems.Count > 0 && !IsInsideArchive && !IsOnWindowsPhone);

            OpenTerminalCommand = _openTerminalCommand = new RelayCommand(
                _ => OpenTerminalAtEffectivePath(),
                _ => !string.IsNullOrEmpty(EffectiveTargetPath) && !IsInsideArchive && !PhonePaths.IsPhonePath(EffectiveTargetPath));

            CutCommand = _cutCommand = new RelayCommand(
                _ => CutSelectedToClipboard(),
                _ => SelectedItems.Count > 0 && !IsInsideArchive);

            PasteCommand = _pasteCommand = new AsyncRelayCommand(
                _ => PasteFromClipboardAsync(),
                _ => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive && ClipboardFileService.HasFiles());

            DeleteCommand = _deleteCommand = new AsyncRelayCommand(
                _ => DeleteSelectedAsync(permanent: false),
                _ => SelectedItems.Count > 0 && !IsInsideArchive);

            DeletePermanentCommand = _deletePermanentCommand = new AsyncRelayCommand(
                _ => DeleteSelectedAsync(permanent: true),
                _ => SelectedItems.Count > 0 && !IsInsideArchive);

            CancelFileOperationCommand = _cancelFileOperationCommand = new RelayCommand(
                _ => Operations?.CancelAll(),
                _ => Operations?.HasActiveJobs == true);

            NewFolderCommand = _newFolderCommand = new AsyncRelayCommand(
                _ => CreateNewFolderAsync(),
                _ => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive);

            NewTextFileCommand = new AsyncRelayCommand(
                _ => CreateNewTextFileAsync(),
                _ => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive);

            RenameCommand = _renameCommand = new AsyncRelayCommand(
                _ => RenameSelectedAsync(),
                _ => SelectedItems.Count > 0 && !IsInsideArchive);

            InitializeHomeCommands(out _refreshHomeCommand, out _clearAnalyticsDataCommand);
        }

        public IList Items
        {
            get => _items;
            private set
            {
                if (SetProperty(ref _items, value))
                {
                    RaisePropertyChanged(nameof(SelectionSummaryText));
                }
            }
        }

        /// <summary>
        /// Drives the status bar's item-count/selection summary —
        /// deliberately a separate property from StatusText, which
        /// continues to carry transient messages (loading, file-
        /// operation progress) rather than being overloaded with two
        /// different jobs. Recomputed live, not cached: both Items and
        /// SelectedItems can change independently, and this is cheap
        /// enough (folder/file counts are pre-computed once per folder
        /// load by VirtualizingFileList; summing a selection's sizes is
        /// bounded by however many items are actually selected, not
        /// the whole folder) to just recompute on demand rather than
        /// track invalidation by hand.
        /// </summary>
        public string SelectionSummaryText
        {
            get
            {
                // In Columns view, "the current folder" for this summary
                // is whichever column is focused, not necessarily the
                // root — see FocusedColumnPane's own remarks.
                VirtualizingFileList list = ViewMode == FileListViewMode.Columns
                    ? FocusedColumnPane?.Items
                    : Items as VirtualizingFileList;

                if (list == null)
                {
                    return string.Empty;
                }

                if (SelectedItems.Count == 0)
                {
                    // The item count itself is the status text beside this ("6 items"): here only how it splits, as
                    // Explorer's status bar doesn't say the count twice.
                    if (list.FolderCount + list.FileCount == 0)
                    {
                        return string.Empty;
                    }

                    return $"{list.FolderCount:N0} folder{(list.FolderCount == 1 ? string.Empty : "s")} \u2022 {list.FileCount:N0} file{(list.FileCount == 1 ? string.Empty : "s")}";
                }

                int selectedFolders = 0;
                int selectedFiles = 0;
                long selectedBytes = 0;
                bool uncounted = false;

                foreach (FileListItemViewModel item in SelectedItems)
                {
                    if (item.IsDirectory)
                    {
                        selectedFolders++;
                        long counted = FolderSizes.BytesOrUnknown(item.FullPath);
                        if (counted >= 0)
                        {
                            selectedBytes += counted; // a counted folder size (Calculate folder sizes)
                        }
                        else
                        {
                            uncounted = true;
                        }
                    }
                    else
                    {
                        selectedFiles++;
                        selectedBytes += item.SizeBytes;
                    }
                }

                // Deliberately no size shown when 0 — either nothing
                // selected has a known size (all folders, since we
                // don't compute recursive folder sizes anywhere in this
                // app), or the selected files are all genuinely empty;
                // either way, showing "0 bytes" would look like a bug
                // rather than accurately reflect what's known.
                string sizeText = selectedBytes > 0 ? $" {(uncounted ? "≥ " : string.Empty)}{FormatSelectionSize(selectedBytes)}" : string.Empty;

                return $"{SelectedItems.Count:N0} selected ({selectedFolders:N0} folder{(selectedFolders == 1 ? string.Empty : "s")} \u2022 {selectedFiles:N0} file{(selectedFiles == 1 ? string.Empty : "s")}){sizeText}";
            }
        }

        private static string FormatSelectionSize(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{bytes:N0} {units[0]}"
                : $"{size.ToString("N1", CultureInfo.CurrentCulture)} {units[unitIndex]}";
        }

        /// <summary>
        /// Currently selected row, bound TwoWay from both the Details
        /// and Icons ListView/ListBox — drives the details panel.
        /// Public setter since the selection genuinely originates from
        /// the view (user click), not from view-model logic.
        /// </summary>
        public FileListItemViewModel SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetProperty(ref _selectedItem, value))
                {
                    _openSelectedItemCommand.RaiseCanExecuteChanged();
                    RefreshPreviewSubject();
                }
            }
        }

        /// <summary>
        /// What the details pane describes: several selected items, the selected item, or (nothing selected) the folder
        /// shown; nothing on Home and This PC.
        /// </summary>
        private void RefreshPreviewSubject()
        {
            if (SelectedItems.Count > 1)
            {
                Preview.ShowSelection(SelectedItems);
            }
            else if (SelectedItem != null)
            {
                Preview.ShowItem(SelectedItem);
            }
            else if (_currentSurface == FileListSurface.StandardFolder && !string.IsNullOrEmpty(CurrentPath))
            {
                Preview.ShowFolder(CurrentPath);
            }
            else
            {
                Preview.ShowNothing();
            }
        }

        /// <summary>
        /// The full multi-selection, kept in sync by the view (see
        /// FileListView.xaml.cs's SelectionChanged handler) — WPF's
        /// ListView/ListBox.SelectedItems isn't directly bindable
        /// (not a dependency property with change notification), so
        /// this is the same code-behind bridge pattern already used
        /// for the nav tree's TreeView.SelectedItem.
        /// </summary>
        public BulkObservableCollection<FileListItemViewModel> SelectedItems { get; } = new BulkObservableCollection<FileListItemViewModel>();

        /// <summary>
        /// Called by the view whenever the underlying selection changes.
        /// Rebuilds SelectedItems and re-evaluates every command whose
        /// CanExecute depends on "is anything selected."
        /// </summary>
        public void UpdateSelection(IEnumerable<FileListItemViewModel> selectedItems)
        {
            SelectedItems.ReplaceAll(selectedItems);

            RaisePropertyChanged(nameof(SelectionSummaryText));
            RefreshPreviewSubject();

            _copyCommand.RaiseCanExecuteChanged();
            _copyPathCommand.RaiseCanExecuteChanged();
            _propertiesCommand.RaiseCanExecuteChanged();
            _cutCommand.RaiseCanExecuteChanged();
            _deleteCommand.RaiseCanExecuteChanged();
            _deletePermanentCommand.RaiseCanExecuteChanged();
            _newFolderCommand?.RaiseCanExecuteChanged();
            _renameCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// The app's file operation queue (set by MainViewModel, shared by both panes): copies, moves, deletes and
        /// archives run there, with progress, pause, name clash questions and undo. Nothing blocks the pane while they
        /// run; more operations queue up behind them.
        /// </summary>
        public FileOperations.FileOperationQueue Operations { get; set; }

        private string _revealFolder;
        private string _revealName;

        /// <summary>
        /// Opens the file's folder and selects the file (the omnibar, for a typed file path; Explorer's "show in folder").
        /// The view picks the name up when the folder's items arrive (<see cref="TakeRevealName"/>).
        /// </summary>
        public Task RevealAsync(string filePath)
        {
            string folder = PathUtil.GetParent(filePath);
            string name = PathUtil.GetName(filePath);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
            {
                return Task.CompletedTask;
            }

            _revealFolder = folder;
            _revealName = name;
            return LoadFolderAsync(folder, bypassCache: true);
        }

        /// <summary>True while a reveal waits for this folder's items.</summary>
        public bool HasPendingReveal(string folder) => _revealName != null && PathUtil.AreSame(_revealFolder, folder);

        /// <summary>The name to select in this folder, once (null when nothing is waiting).</summary>
        /// <summary>The name waiting to be selected in <paramref name="folder"/>, without taking it (null: none).</summary>
        public string PeekRevealName(string folder) => HasPendingReveal(folder) ? _revealName : null;

        public string TakeRevealName(string folder)
        {
            if (!HasPendingReveal(folder))
            {
                return null;
            }

            string name = _revealName;
            _revealFolder = null;
            _revealName = null;
            return name;
        }

        /// <summary>
        /// True once anything was asked to open in this pane (a folder, Home, This PC), even if it hasn't finished loading:
        /// the window opens the Home dashboard at startup only when nothing else was asked for first.
        /// </summary>
        public bool HasStartedNavigation { get; private set; }

        /// <summary>Shows a status line from outside the pane (connecting to a network location).</summary>
        public void ShowStatus(string text) => StatusText = text ?? string.Empty;

        /// <summary>Quick access (set by MainViewModel): the right-click menus pin and unpin folders.</summary>
        public Sidebar.PinnedFoldersViewModel Pins { get; set; }

        /// <summary>
        /// Opens folders in new tabs (the menu's "Open in new tab"; true: show the first one). Set by the window's view
        /// model; null where there are no tabs, and then the menu doesn't offer it.
        /// </summary>
        public Func<IReadOnlyList<string>, bool, Task> OpenInNewTabs { get; set; }

        /// <summary>
        /// Opens folders in new windows, one each (the menu's "Open in new window", the Windows menu's verb). Set by the
        /// window's view model; null where there is no window to open one from, and then the menu doesn't offer it.
        /// </summary>
        public Func<IReadOnlyList<string>, Task> OpenInNewWindows { get; set; }

        /// <summary>Opens a folder the way a middle click does (Settings › Tabs: a new tab, or the other pane). Set like <see cref="OpenInNewTabs"/>.</summary>
        public Func<string, Task> OpenWithMiddleClick { get; set; }

        public ICommand CopyCommand { get; }
        public ICommand CopyPathCommand { get; }
        public ICommand PropertiesCommand { get; }
        public ICommand OpenTerminalCommand { get; }
        public ICommand CutCommand { get; }
        public ICommand PasteCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand DeletePermanentCommand { get; }

        /// <summary>
        /// Bound to Escape while a Copy/Move/Delete is running. This
        /// was a real gap, not just an unbuilt nice-to-have — there was
        /// previously no way for the person to trigger cancellation of
        /// an in-progress file operation at all, despite the engine
        /// having full CancellationToken support underneath.
        /// </summary>
        public ICommand CancelFileOperationCommand { get; }

        public ICommand NewFolderCommand { get; }

        /// <summary>Creates "New text document.txt" (numbered if taken) in the current folder.</summary>
        public ICommand NewTextFileCommand { get; }

        public ICommand RenameCommand { get; }

        /// <summary>
        /// Raised when a Copy/Move/Delete operation finishes with one
        /// or more failures — carries a pre-formatted summary (not
        /// individual structured data) since the view's only job here
        /// is to display it, matching OpenItemFailed's shape.
        /// </summary>
        public event EventHandler<string> OperationFailed;

        /// <summary>
        /// Raised when the list could not be sorted or filtered (a bug, or data the sort cannot handle): a short message
        /// for a non-modal notification. The list keeps what it showed (or the folder in name order when it was just
        /// opened), the status bar says what failed, and the log has the exception. Repeats of the same failure within a
        /// few seconds (typing in the filter box) are not raised again.
        /// </summary>
        public event EventHandler<string> ViewBuildFailed;

        private static readonly TimeSpan ViewFailureQuietPeriod = TimeSpan.FromSeconds(5);
        private string _lastViewFailure;
        private DateTime _lastViewFailureUtc;

        /// <summary>The last failure to sort or filter the list (null when the last build worked), for tests and diagnostics.</summary>
        public string LastViewBuildFailure { get; private set; }

        /// <summary>
        /// Makes a failed sort or filter visible instead of leaving the list silently unchanged: logged with its context,
        /// shown in the status bar, and raised for a notification (throttled).
        /// </summary>
        private void ReportViewBuildFailure(Exception ex, string what, string outcome)
        {
            _logger.Error(ex, "Could not {What} in {Path} (sort {Column} {Direction}, filter \"{Filter}\")",
                what, CurrentPath, _sortColumn, _sortAscending ? "ascending" : "descending", FilterText);
            string message = $"Couldn't {what}. {outcome}";
            LastViewBuildFailure = message;
            StatusText = message;
            DateTime now = DateTime.UtcNow;
            if (string.Equals(message, _lastViewFailure, StringComparison.Ordinal) && now - _lastViewFailureUtc < ViewFailureQuietPeriod)
            {
                return;
            }

            _lastViewFailure = message;
            _lastViewFailureUtc = now;
            ViewBuildFailed?.Invoke(this, message);
        }

        /// <summary>
        /// The list for a folder just loaded or refreshed. When sorting or filtering fails, the failure is reported and the
        /// folder is shown in name order (with the same hidden-item and category rules, or as listed if even that fails)
        /// rather than not at all.
        /// </summary>
        private async Task<DirectoryIndex> GetDisplayedIndexOrFallbackAsync(DirectoryIndex source, string filter, CancellationToken cancellationToken)
        {
            try
            {
                DirectoryIndex displayed = await GetDisplayedIndexAsync(source, filter, cancellationToken).ConfigureAwait(true);
                LastViewBuildFailure = null;
                return displayed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportViewBuildFailure(ex, $"sort this folder by {SortColumnName(_sortColumn)}", "It is shown by name instead.");
                try
                {
                    return BuildSortedView(source, FileListSortColumn.Name, true, ShowHiddenItems, ShowSystemItems, _categoryFilter);
                }
                catch (Exception fallbackError)
                {
                    _logger.Error(fallbackError, "Could not list {Path} in name order either; showing it as listed", CurrentPath);
                    return source;
                }
            }
        }

        private static string SortColumnName(FileListSortColumn column) => column switch
        {
            FileListSortColumn.DateModified => "date modified",
            FileListSortColumn.Type => "type",
            FileListSortColumn.Size => "size",
            FileListSortColumn.Tags => "tags",
            FileListSortColumn.DateCreated => "date created",
            FileListSortColumn.DateAccessed => "date accessed",
            _ => "name"
        };

        /// <summary>Reports a failure from outside the view model (a context menu action) the same way as its own.</summary>
        public void ReportFailure(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                OperationFailed?.Invoke(this, message);
            }
        }

        public ICommand OpenSelectedItemCommand { get; }

        /// <summary>
        /// Forces a fresh reload of whatever folder is currently
        /// showing — see the constructor's remarks on why this exists.
        /// </summary>
        public ICommand RefreshCommand { get; }

        public ICommand ToggleShowHiddenItemsCommand { get; }
        public ICommand ToggleShowSystemItemsCommand { get; }

        /// <summary>
        /// Raised when opening/launching an item fails (e.g. no
        /// application associated with the file type, file deleted
        /// since the listing was built, access denied). The view
        /// subscribes to this to show a message — kept as an event
        /// rather than the view model calling MessageBox directly, so
        /// this class stays free of any WPF-specific UI dependency.
        /// </summary>
        public event EventHandler<string> OpenItemFailed;

        /// <summary>A navigation starts (a folder, Home, This PC; not a folder reloading itself): the lens view gives way.</summary>
        public event EventHandler NavigationRequested;

        /// <summary>
        /// Raised when switching to Timeline or Nexus is refused (the
        /// current folder has more items than
        /// FileListSettings.MaxItemsForGroupedViews), or when the view
        /// automatically falls back to Details because navigating into
        /// a folder this large happened while already in one of those
        /// two views. Kept as an event rather than the view model
        /// calling MessageBox directly, matching OperationFailed/
        /// OpenItemFailed's own reasoning — this class stays free of
        /// any WPF-specific UI dependency.
        /// </summary>
        public event EventHandler<string> GroupedViewUnavailable;

        /// <summary>
        /// Raised by SelectAllCommand/InvertSelectionCommand for the
        /// view (FileListView.xaml.cs) to act on — the view, not the
        /// view model, owns the actual per-view-mode selection
        /// mechanism (native ListBox selection for Details/List/
        /// Timeline/Nexus/Columns, RowViewState for Icons/Tiles), so
        /// this asks rather than trying to drive selection directly.
        /// Same request/response pattern GroupedViewUnavailable above
        /// already establishes for "view model needs the view to do
        /// something only the view can do."
        /// </summary>
        public event EventHandler SelectAllRequested;
        public event EventHandler InvertSelectionRequested;

        /// <summary>Raised by <see cref="SelectNoneCommand"/>: the view clears the selection (all its lists).</summary>
        public event EventHandler SelectNoneRequested;

        /// <summary>Select none: nothing selected.</summary>
        public ICommand SelectNoneCommand { get; }

        /// <summary>"Compress to ZIP file" for the selection (the toolbar's "..." menu).</summary>
        public ICommand CompressSelectionCommand { get; }

        /// <summary>Pins the selected folders to Quick access, or unpins them when they all are (the toolbar's "..." menu).</summary>
        public ICommand TogglePinSelectionCommand { get; }

        /// <summary>The selected folders (pinnable: folders and browsable archives, on a drive).</summary>
        public IReadOnlyList<string> SelectedPinnablePaths() =>
            SelectedItems.Where(i => i.IsDirectory || (ArchiveBrowser.IsArchiveFile(i.FullPath) && File.Exists(i.FullPath)))
                .Select(i => i.FullPath)
                .Where(p => !Phones.IsPhonePath(p) && !ArchiveBrowser.IsInsideArchive(p))
                .ToList();

        /// <summary>What <see cref="TogglePinSelectionCommand"/> does now: "Pin to Quick access", or "Unpin from Quick access".</summary>
        public string PinSelectionText
        {
            get
            {
                IReadOnlyList<string> folders = SelectedPinnablePaths();
                if (Pins != null && folders.Count > 0 && folders.All(Pins.Contains))
                {
                    return "Unpin from Quick access";
                }

                return folders.Count > 1 ? $"Pin {folders.Count} folders to Quick access" : "Pin to Quick access";
            }
        }

        private async Task TogglePinSelectionAsync()
        {
            IReadOnlyList<string> folders = SelectedPinnablePaths();
            if (Pins == null || folders.Count == 0)
            {
                return;
            }

            if (folders.All(Pins.Contains))
            {
                foreach (string folder in folders)
                {
                    await Pins.UnpinAsync(folder).ConfigureAwait(true);
                }
            }
            else
            {
                await Pins.PinAsync(folders).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Item count (files + folders) of the currently-loaded folder,
        /// or 0 if nothing is loaded or the last load errored. Backs the
        /// Timeline/Nexus size gate (see CanUseGroupedView) — exposed
        /// publicly since it's also generally useful information a view
        /// might want to show.
        /// </summary>
        public int CurrentFolderItemCount => _currentIndex?.Count ?? 0;

        /// <summary>
        /// Double-click (or Enter-key) entry point: navigates into
        /// folders, hands files to the OS via ShellExecute — the same
        /// mechanism Explorer itself uses, so file associations and
        /// "Open with" behave exactly as expected.
        /// </summary>
        public async Task OpenItemAsync(FileListItemViewModel item)
        {
            if (item == null)
            {
                return;
            }

            if (item.IsDirectory)
            {
                await LoadFolderAsync(item.FullPath).ConfigureAwait(true);
                return;
            }

            if (ArchiveBrowser.IsInsideArchive(PathUtil.GetParent(item.FullPath)))
            {
                await OpenFromArchiveAsync(item).ConfigureAwait(true);
                return;
            }

            if (PhonePaths.IsPhonePath(item.FullPath))
            {
                await OpenFromPhoneAsync(item).ConfigureAwait(true);
                return;
            }

            if (IsProtectedItem(item.FullPath))
            {
                // A password-protected item: its password, then its content, browsed like a ZIP file.
                await OpenProtectedAsync(item.FullPath).ConfigureAwait(true);
                return;
            }

            bool archivesAsFolders = _settingsService?.Current?.FileList?.OpenZipFilesAsFolders ?? true;
            if (archivesAsFolders && ArchiveBrowser.IsArchiveFile(item.FullPath))
            {
                // Explorer's way: an archive opens like a folder ("Open with" still hands it to an archiver).
                await LoadFolderAsync(item.FullPath).ConfigureAwait(true);
                return;
            }

            // An archive type nothing installed can read opens in its application, as any file; the status says what to install.
            string missingTool = archivesAsFolders ? Core.Services.Archives.ArchiveFormats.MissingToolHint(item.FullPath) : null;

            // Handing a file to its application is one fast OS call (ShellExecute, open, xdg-open), so it runs here
            // on the UI thread rather than through Task.Run: on Windows, ShellExecute's COM machinery can require an
            // STA thread for shortcuts and some shell extensions, and the UI thread is STA; pool threads are not.
            try
            {
                // A hand-off, not a wait for the other application to exit.
                ShellLauncher.Open(item.FullPath);

                _logger.Information("Opened {Path}", item.FullPath);
                _analyticsService.TrackFileOpened(item.FullPath);
                _soundFeedbackService?.PlayFileOpened();
                if (missingTool != null)
                {
                    StatusText = missingTool;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open {Path}", item.FullPath);
                OpenItemFailed?.Invoke(this, $"Couldn't open \"{item.Name}\". {Infrastructure.FriendlyErrors.Explain(ex)}"
                    + (missingTool != null ? "\n\n" + missingTool : string.Empty));
            }
        }

        /// <summary>
        /// Opens a file directly by path — used by the Home dashboard's
        /// "Recent videos/music/documents" rows, which only carry
        /// historical path/name data (RecentFileOpenEntry), not a live,
        /// currently-hydrated FileListItemViewModel the way OpenItemAsync
        /// above needs. Synchronous, unlike OpenItemAsync — there's no
        /// actual asynchronous work here (ShellExecute is a single, fast
        /// OS hand-off, same reasoning as OpenItemAsync's own remarks),
        /// so an async/Task wrapper would only be there for a
        /// consistency this method doesn't actually need.
        /// </summary>
        public void OpenRecentFile(string fullPath, string displayName)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            // A dashboard entry can easily outlive the file it points
            // to — it was opened at some point in the past, with no
            // guarantee it still exists (deleted, moved, renamed, a
            // removable/network drive no longer attached). Checked here
            // rather than just letting ShellExecute fail, since that
            // failure path produces a far less specific, less helpful
            // error message than this one.
            if (!File.Exists(fullPath))
            {
                OpenItemFailed?.Invoke(this, $"\"{displayName}\" could not be found. It may have been moved or deleted.");
                return;
            }

            try
            {
                ShellLauncher.Open(fullPath);

                _logger.Information("Opened {Path} from the Home dashboard", fullPath);
                _analyticsService.TrackFileOpened(fullPath);
                _soundFeedbackService?.PlayFileOpened();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open {Path} from the Home dashboard", fullPath);
                OpenItemFailed?.Invoke(this, $"Couldn't open \"{displayName}\". {Infrastructure.FriendlyErrors.Explain(ex)}");
            }
        }

        /// <summary>
        /// Shared by PasteFromClipboardAsync (Move only — Copy never
        /// puts the source at risk, so it's excluded) and
        /// DeleteSelectedAsync. Checks the setting, checks the size
        /// threshold (short-circuiting the size walk once it's known to
        /// be over budget), and creates the backup if both pass.
        /// </summary>
        private async Task<FileBackupResult> TryCreateBackupAsync(IReadOnlyList<string> paths, IProgress<string> progress, CancellationToken cancellationToken)
        {
            FileListSettings settings = _settingsService?.Current?.FileList;
            bool backupEnabled = settings?.BackupBeforeFileOperation ?? true;
            long maxBytes = settings?.BackupMaxTotalSizeBytes ?? (3L * 1024 * 1024 * 1024);

            if (!backupEnabled || maxBytes <= 0)
            {
                return FileBackupResult.NotAttempted();
            }

            progress?.Report("Checking size…");

            long totalSize;
            try
            {
                totalSize = await _fileBackupService.ComputeTotalSizeAsync(paths, maxBytes, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return FileBackupResult.Cancelled();
            }

            if (totalSize > maxBytes)
            {
                _logger.Information("Backup skipped — selection is {SizeBytes:N0} bytes, over the {MaxBytes:N0} byte threshold", totalSize, maxBytes);
                return FileBackupResult.SkippedTooLarge();
            }

            return await _fileBackupService.CreateBackupAsync(paths, progress, cancellationToken).ConfigureAwait(true);
        }

        /// <summary>
        /// Common handling for a backup attempt that didn't succeed
        /// enough to proceed (failed, or cancelled). Deliberately aborts
        /// the real operation entirely in the Failed case rather than
        /// falling back to "proceed without the safety net" — silently
        /// dropping the protection the user explicitly asked for would
        /// defeat the point of asking for it. The person can retry, fix
        /// whatever blocked the backup (disk space, permissions), or
        /// turn the setting off if they'd rather proceed without it.
        /// </summary>
        private void ReportBackupNotProceeding(FileBackupResult backupResult, string verb)
        {
            if (backupResult.Outcome == BackupOutcome.Cancelled)
            {
                StatusText = $"{verb} cancelled";
                return;
            }

            StatusText = $"{verb} not performed — backup failed";
            OperationFailed?.Invoke(this,
                $"Couldn't create a safety backup before this {verb.ToLowerInvariant()}, so it wasn't performed.\n\n" +
                $"{backupResult.ErrorMessage}\n\n" +
                "You can turn off backups in settings.json (FileList.BackupBeforeFileOperation) if you'd rather proceed without this protection.");
        }

        /// <summary>
        /// After the real operation finishes: delete the backup on full
        /// success (nothing left to protect against), keep it and make
        /// sure its location is visible otherwise — a kept backup that
        /// no one knows the location of isn't a real recovery path.
        /// </summary>
        private void ResolveBackupAfterOperation(FileBackupResult backupResult, FileOperationResult operationResult)
        {
            if (backupResult.Outcome != BackupOutcome.Success)
            {
                return;
            }

            if (operationResult.IsFullSuccess)
            {
                _fileBackupService.DeleteBackup(backupResult.BackupFolderPath);
            }
            else
            {
                _logger.Information("Backup kept at {Folder} — operation did not fully succeed", backupResult.BackupFolderPath);
                StatusText += $" — backup kept at {backupResult.BackupFolderPath}";
            }
        }

        private void CopySelectedToClipboard()
        {
            if (SelectedItems.Count == 0)
            {
                return;
            }

            var paths = new List<string>();
            foreach (FileListItemViewModel item in SelectedItems)
            {
                paths.Add(item.FullPath);
            }

            if (IsInsideArchive)
            {
                // The clipboard carries real files: extract the selection first, then copy those.
                CopyFromArchiveAsync(paths).Forget("copy from archive");
                return;
            }

            ClipboardFileService.SetFilesForCopy(paths);
            StatusText = $"{paths.Count:N0} item{(paths.Count == 1 ? string.Empty : "s")} copied";
        }

        // ------------------------------------------------------------------ inside ZIP files

        private string _archiveCheckedPath;
        private bool _archiveCheckResult;

        /// <summary>
        /// True while the pane shows a folder of a Windows phone ("phone://…"): items there have no file path, so what needs
        /// one (Properties, a terminal, compressing) is unavailable; copying, moving, deleting and renaming go through the
        /// phone. Linux phones are mounted folders and keep those commands.
        /// </summary>
        public bool IsOnWindowsPhone => PhonePaths.IsPhonePath(CurrentPath);

        /// <summary>
        /// True while the pane shows a folder inside a ZIP file (read-only): writing commands are disabled, Copy and Open
        /// extract first. Cached per path, since CanExecute asks often.
        /// </summary>
        public bool IsInsideArchive
        {
            get
            {
                string path = CurrentPath;
                if (!string.Equals(path, _archiveCheckedPath, StringComparison.Ordinal))
                {
                    _archiveCheckedPath = path;
                    _archiveCheckResult = ArchiveBrowser.IsInsideArchive(path);
                }

                return _archiveCheckResult;
            }
        }

        /// <summary>Extracts items from the archive to a temporary folder (a job in the operations panel) and returns their paths.</summary>
        private async Task<IReadOnlyList<string>> ExtractToTempAsync(IReadOnlyList<string> paths)
        {
            if (Operations == null)
            {
                return await Task.Run(() => ArchiveBrowser.ExtractToTemp(paths, null, CancellationToken.None)).ConfigureAwait(true);
            }

            ArchiveBrowser.TryResolve(paths[0], out string archive, out _);
            return await Operations.RunBackgroundAsync($"Extracting from {PathUtil.GetName(archive)}",
                (progress, token) => Task.Run(() => ArchiveBrowser.ExtractToTemp(paths, progress, token), token)).ConfigureAwait(true);
        }

        private async Task CopyFromArchiveAsync(IReadOnlyList<string> paths)
        {
            try
            {
                IReadOnlyList<string> extracted = await ExtractToTempAsync(paths).ConfigureAwait(true);
                ClipboardFileService.SetFilesForCopy(extracted);
                StatusText = $"{paths.Count:N0} item{(paths.Count == 1 ? string.Empty : "s")} copied from the archive";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                _logger.Warning(ex, "Copy from archive failed");
                OperationFailed?.Invoke(this, "Couldn't copy from the archive.\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        /// <summary>
        /// "Extract": the items (inside an archive) are extracted beside the archive, through the queue, so name clashes are
        /// asked about, progress shows, and Ctrl+Z removes what was extracted.
        /// </summary>
        public async Task ExtractFromArchiveAsync(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0 || Operations == null || !ArchiveBrowser.TryResolve(paths[0], out string archive, out _))
            {
                return;
            }

            string destination = PathUtil.GetParent(archive);
            try
            {
                IReadOnlyList<string> extracted = await ExtractToTempAsync(paths).ConfigureAwait(true);
                FileOperationOutcome outcome = await Operations.TransferAsync(FileOperationKind.Copy, extracted, destination).ConfigureAwait(true);
                ReportOperationResult(outcome.Result);
                TryDeleteExtraction(extracted);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                _logger.Warning(ex, "Extract from archive failed");
                OperationFailed?.Invoke(this, "Couldn't extract from the archive.\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        /// <summary>Removes a temporary extraction once its items were copied out (best effort; old ones go at startup).</summary>
        private void TryDeleteExtraction(IReadOnlyList<string> extracted)
        {
            if (extracted.Count == 0)
            {
                return;
            }

            string folder = PathUtil.GetParent(extracted[0]);
            if (folder != null && folder.StartsWith(ArchiveBrowser.TempRoot, PathUtil.Comparison))
            {
                Task.Run(() =>
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        _logger.Debug(ex, "Temporary extraction left for the next cleanup: {Folder}", folder);
                    }
                }).Forget("remove temporary extraction");
            }
        }

        /// <summary>A file inside an archive opens from a temporary extracted copy (changes to it don't go back into the archive).</summary>
        private async Task OpenFromArchiveAsync(FileListItemViewModel item)
        {
            try
            {
                IReadOnlyList<string> extracted = await ExtractToTempAsync(new[] { item.FullPath }).ConfigureAwait(true);
                ShellLauncher.Open(extracted[0]);
                _analyticsService.TrackFileOpened(item.FullPath);
                _soundFeedbackService?.PlayFileOpened();
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open {Path} from its archive", item.FullPath);
                OpenItemFailed?.Invoke(this, $"Couldn't open \"{item.Name}\" from the archive. {Infrastructure.FriendlyErrors.Explain(ex)}");
            }
        }

        /// <summary>
        /// "Copy path" — writes each selected item's full path as plain
        /// text, one per line, each wrapped in double quotes — matching
        /// Windows Explorer's own "Copy as path" convention exactly, so
        /// a single selected path pastes directly into a terminal
        /// command line without the user needing to add quoting
        /// themselves for a path containing spaces. Deliberately a
        /// separate command from CopyCommand above: that one copies the
        /// files themselves (CF_HDROP, for pasting into a folder), this
        /// one copies their paths as text (CF_UNICODETEXT, for pasting
        /// into a document, chat, or command line) — different
        /// clipboard formats for genuinely different purposes, not two
        /// names for the same operation.
        /// </summary>
        private void CopySelectedPathsToClipboard()
        {
            if (SelectedItems.Count == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            for (int i = 0; i < SelectedItems.Count; i++)
            {
                if (i > 0)
                {
                    builder.AppendLine();
                }

                builder.Append('"').Append(SelectedItems[i].FullPath).Append('"');
            }

            ClipboardFileService.SetText(builder.ToString());
            StatusText = $"{SelectedItems.Count:N0} path{(SelectedItems.Count == 1 ? string.Empty : "s")} copied";
        }

        /// <summary>
        /// "Properties" — asks the view to show the app's own Properties
        /// window for the selection (IDialogService.ShowProperties). The WPF build
        /// opened the Windows shell dialog; the cross-platform build has
        /// one dialog of its own for every OS, which also handles a
        /// multi-selection ("N items": combined size, contents count).
        /// </summary>
        private void ShowPropertiesForSelection()
        {
            if (SelectedItems.Count == 0)
            {
                return;
            }

            var paths = new List<string>();
            foreach (FileListItemViewModel item in SelectedItems)
            {
                paths.Add(item.FullPath);
            }

            DialogService?.ShowProperties(paths);
        }

        /// <summary>
        /// "Open in Terminal here" — launches a terminal (see ShellLauncher.OpenTerminal) with its working
        /// directory set to EffectiveTargetPath (CurrentPath everywhere
        /// except a focused Columns-view pane, matching Paste/New
        /// Folder/Delete's own scoping — see EffectiveTargetPath's own
        /// remarks). UseShellExecute:true, not a redirect of standard
        /// streams — this hands off to a real, independent, visible
        /// console window for the user to interact with directly,
        /// exactly like double-clicking cmd.exe in Explorer, not
        /// something this app reads output from or manages the
        /// lifetime of afterward.
        /// </summary>
        private void OpenTerminalAtEffectivePath()
        {
            string path = EffectiveTargetPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!Directory.Exists(path))
            {
                OperationFailed?.Invoke(this, "This folder is no longer available, so a terminal can't be opened here.");
                return;
            }

            try
            {
                ShellLauncher.OpenTerminal(path);

                _logger.Information("Opened terminal at {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open terminal at {Path}", path);
                OperationFailed?.Invoke(this, $"Couldn't open a terminal here. {Infrastructure.FriendlyErrors.Explain(ex)}");
            }
        }

        private void CutSelectedToClipboard()
        {
            if (SelectedItems.Count == 0)
            {
                return;
            }

            var paths = new List<string>();
            foreach (FileListItemViewModel item in SelectedItems)
            {
                paths.Add(item.FullPath);
            }

            ClipboardFileService.SetFilesForCut(paths);
            StatusText = $"{paths.Count:N0} item{(paths.Count == 1 ? string.Empty : "s")} cut";
        }

        /// <summary>
        /// Public entry point for drag-drop (see FileDragDropHelper) into
        /// the same Copy/Move execution core PasteFromClipboardAsync
        /// uses — same backup-before-Move safety net, same cache
        /// invalidation, same error reporting, no duplicated logic.
        /// isMove mirrors the drag's own resolved effect (Ctrl/Shift
        /// modifiers, same-drive default) rather than anything
        /// clipboard-related, since a drag-drop has no clipboard
        /// involved at all.
        /// </summary>
        /// <summary>Copies or moves into a folder (a drop, or a paste into a folder); true when everything was moved or copied.</summary>
        public Task<bool> ExecuteDropAsync(IReadOnlyList<string> sourcePaths, string destinationFolder, bool isMove)
        {
            return ExecuteCopyOrMoveAsync(sourcePaths, isMove, destinationFolder);
        }

        private async Task PasteFromClipboardAsync()
        {
            string destination = EffectiveTargetPath;
            if (string.IsNullOrEmpty(destination))
            {
                return;
            }

            (bool ok, IReadOnlyList<string> paths, bool isCut) = await ClipboardFileService.TryGetFilesAsync().ConfigureAwait(true);
            if (!ok || paths.Count == 0)
            {
                return;
            }

            bool movedSuccessfully = await ExecuteCopyOrMoveAsync(paths, isCut, destination).ConfigureAwait(true);

            if (isCut && movedSuccessfully)
            {
                // A Cut is "next paste consumes it," matching Explorer —
                // clear the clipboard so a second Paste can't silently
                // attempt to move the same (now-relocated) files again.
                // Drag-drop has no clipboard involved at all, which is
                // exactly why this line lives here and not inside the
                // shared ExecuteCopyOrMoveAsync below.
                await ClipboardFileService.ClearCutIfUnchangedAsync(paths).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Shared Copy/Move execution core — used by both
        /// PasteFromClipboardAsync (source: the system clipboard) and
        /// drag-drop's own drop handling (source: an OS/OLE drag
        /// operation, see FileDragDropHelper). Handles cancelling any
        /// in-flight operation, progress reporting, the backup-before-
        /// Move safety net (Copy never puts the source at risk, so this
        /// is skipped for Copy), the actual Copy/MoveAsync call, cache
        /// invalidation plus a Columns-pane refresh for every distinct
        /// source folder after a successful Move, and refreshing the
        /// destination. Returns true only if this was a Move that
        /// actually succeeded for at least one item — callers use that
        /// to decide whether their own Move-specific follow-up (clearing
        /// the clipboard, for Paste) should happen.
        /// </summary>
        private async Task<bool> ExecuteCopyOrMoveAsync(IReadOnlyList<string> paths, bool isMove, string destination)
        {
            if (Operations == null || paths == null || paths.Count == 0 || string.IsNullOrEmpty(destination))
            {
                return false;
            }

            // A backup only where data could be at risk: a move within a volume is a rename, and a cross-volume move never
            // removes a source before its copy is verified and on the disk; the backup is the extra net for the latter.
            FileBackupResult backupResult = FileBackupResult.NotAttempted();
            if (isMove && !System.Linq.Enumerable.All(paths, p => FileOperationService.IsSameVolume(p, destination)) &&
                !System.Linq.Enumerable.Any(paths, Phones.IsPhonePath))
            {
                backupResult = await TryCreateBackupAsync(paths, new Progress<string>(text => StatusText = text), CancellationToken.None).ConfigureAwait(true);
                if (!backupResult.ProceedWithOperation)
                {
                    ReportBackupNotProceeding(backupResult, "Move");
                    return false;
                }
            }

            FileOperationOutcome outcome = await Operations
                .TransferAsync(isMove ? FileOperationKind.Move : FileOperationKind.Copy, paths, destination)
                .ConfigureAwait(true);
            FileOperationResult result = outcome.Result;

            ReportOperationResult(result);
            ResolveBackupAfterOperation(backupResult, result);

            if (isMove && result.SuccessCount > 0)
            {
                // The destination is refreshed below (and by the queue's FoldersChanged); the source folders' cached
                // listings must not show the moved items either, nor a Columns column showing one of them.
                var sourceFolders = new HashSet<string>(PathUtil.Comparer);
                foreach (string sourcePath in paths)
                {
                    string parent = PathUtil.GetParent(sourcePath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        sourceFolders.Add(parent);
                    }
                }

                foreach (string sourceFolder in sourceFolders)
                {
                    _indexService?.InvalidateCache(sourceFolder);
                    if (ViewMode == FileListViewMode.Columns)
                    {
                        await RefreshColumnAsync(sourceFolder).ConfigureAwait(true);
                    }
                }
            }

            _indexService?.InvalidateCache(destination);
            await RefreshAfterFileOperationAsync(destination).ConfigureAwait(true);
            return isMove && result.SuccessCount > 0;
        }

        private Task DeleteSelectedAsync(bool permanent)
        {
            if (SelectedItems.Count == 0)
            {
                return Task.CompletedTask;
            }

            var paths = new List<string>(SelectedItems.Count);
            foreach (FileListItemViewModel item in SelectedItems)
            {
                paths.Add(item.FullPath);
            }

            return DeletePathsAsync(paths, permanent);
        }

        /// <summary>
        /// Deletes the given items (to the trash/Recycle Bin, or permanently), after confirmation. Used for the selection
        /// and by the context menu for items that are not part of the list's selection (a tree node, a sidebar folder).
        /// Never runs without an answer: with no dialog service there is nobody to confirm, so nothing is deleted.
        /// </summary>
        public async Task DeletePathsAsync(IReadOnlyList<string> paths, bool permanent)
        {
            if (paths == null || paths.Count == 0 || DialogService == null || Operations == null)
            {
                return;
            }

            // A phone has no recycle bin: deleting there is permanent, and the question says so.
            bool onPhone = System.Linq.Enumerable.Any(paths, Phones.IsPhonePath);
            permanent |= onPhone;

            // Confirmation before any destructive delete: expected even with the trash as a safety net, and required for
            // a permanent delete, which has none.
            bool confirmed = await DialogService.ConfirmDeleteAsync(paths, permanent).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            // To the trash is already recoverable: a backup only before a permanent delete (not of phone items: the
            // backup copies through the file system, which can't read a Windows phone).
            FileBackupResult backupResult = permanent && !onPhone
                ? await TryCreateBackupAsync(paths, new Progress<string>(text => StatusText = text), CancellationToken.None).ConfigureAwait(true)
                : FileBackupResult.NotAttempted();
            if (!backupResult.ProceedWithOperation)
            {
                ReportBackupNotProceeding(backupResult, "Delete");
                return;
            }

            FileOperationOutcome outcome = await Operations.DeleteAsync(paths, permanent).ConfigureAwait(true);
            ReportOperationResult(outcome.Result);
            ResolveBackupAfterOperation(backupResult, outcome.Result);
            await RefreshAfterFileOperationAsync(EffectiveTargetPath).ConfigureAwait(true);
        }

        private void ReportOperationResult(FileOperationResult result)
        {
            string verb = result.Kind == FileOperationKind.Copy
                ? "Copied"
                : result.Kind == FileOperationKind.Move ? "Moved" : "Deleted";

            // Usage statistics are counted by the queue (FileOperationQueue.OperationFinished), which also sees the jobs
            // that don't come through here (Redo, Retry failed).
            PlayOperationResultSound(result);

            if (result.IsFullSuccess)
            {
                StatusText = $"{verb} {result.SuccessCount:N0} item{(result.SuccessCount == 1 ? string.Empty : "s")}";
            }
            else if (result.WasCancelled)
            {
                StatusText = $"{verb} cancelled — {result.SuccessCount:N0} item{(result.SuccessCount == 1 ? string.Empty : "s")} completed";
            }
            else if (result.HasPartialMoves && !result.HasFailures)
            {
                StatusText = $"{verb} {result.SuccessCount:N0} item{(result.SuccessCount == 1 ? string.Empty : "s")} — {result.PartialMoves.Count:N0} original{(result.PartialMoves.Count == 1 ? string.Empty : "s")} still need manual cleanup";
            }
            else
            {
                StatusText = $"{verb} {result.SuccessCount:N0} item{(result.SuccessCount == 1 ? string.Empty : "s")}, {result.Failures.Count:N0} failed";
            }

            if (!result.HasFailures && !result.HasPartialMoves)
            {
                return;
            }

            var messageBuilder = new StringBuilder();

            if (result.HasPartialMoves)
            {
                // Deliberately framed as a heads-up, not a failure — the
                // file IS at the new location; this is specifically
                // "here's a manual cleanup step still needed," not
                // "something went wrong."
                messageBuilder.AppendLine($"{result.PartialMoves.Count} item(s) moved successfully but the original couldn't be removed yet:");
                int shownPartial = 0;
                foreach (var partial in result.PartialMoves)
                {
                    if (shownPartial >= 10)
                    {
                        messageBuilder.AppendLine($"...and {result.PartialMoves.Count - shownPartial} more.");
                        break;
                    }

                    messageBuilder.AppendLine($"- {Path.GetFileName(partial.SourcePath)}: {partial.Message}");
                    shownPartial++;
                }

                if (result.HasFailures)
                {
                    messageBuilder.AppendLine();
                }
            }

            if (result.HasFailures)
            {
                messageBuilder.AppendLine($"{result.Failures.Count} item(s) could not be {verb.ToLowerInvariant()}:");

                int shown = 0;
                foreach (var failure in result.Failures)
                {
                    if (shown >= 10)
                    {
                        messageBuilder.AppendLine($"...and {result.Failures.Count - shown} more.");
                        break;
                    }

                    messageBuilder.AppendLine($"- {Path.GetFileName(failure.SourcePath)}: {failure.Message}");
                    shown++;
                }
            }

            OperationFailed?.Invoke(this, messageBuilder.ToString());
        }

        /// <summary>
        /// One distinct built-in sound per successful operation kind, a
        /// shared "error" sound for anything short of a full clean
        /// success (real failures, or a move that succeeded but left
        /// its original behind), and silence for a deliberate cancel —
        /// that's an intentional stop, not a success or a failure, so
        /// it doesn't get a chime either way.
        /// </summary>
        private void PlayOperationResultSound(FileOperationResult result)
        {
            if (result.WasCancelled)
            {
                return;
            }

            if (!result.IsFullSuccess)
            {
                _soundFeedbackService?.PlayError();
                return;
            }

            switch (result.Kind)
            {
                case FileOperationKind.Copy:
                    _soundFeedbackService?.PlayCopySuccess();
                    break;
                case FileOperationKind.Move:
                    _soundFeedbackService?.PlayMoveSuccess();
                    break;
                case FileOperationKind.Delete:
                    _soundFeedbackService?.PlayDeleteSuccess();
                    break;
            }
        }

        /// <summary>
        /// Which of the three mutually-exclusive center-pane surfaces
        /// (a real folder listing, the This PC page, the Home
        /// dashboard) is currently shown — see FileListSurface's own
        /// remarks for why this is one field with derived bool
        /// properties rather than three independently-set bools.
        /// Changed only through SetSurface, never directly.
        /// </summary>
        private FileListSurface _currentSurface = FileListSurface.StandardFolder;

        /// <summary>
        /// The one place _currentSurface is ever assigned. Atomically
        /// updates all three derived visibility properties in a single
        /// call, so a caller switching to a new surface can never leave
        /// two surfaces' bools simultaneously true (the exact bug class
        /// this replaced independently-tracked bools to prevent).
        /// </summary>
        private void SetSurface(FileListSurface surface)
        {
            if (_currentSurface == surface)
            {
                return;
            }

            _currentSurface = surface;
            if (surface != FileListSurface.StandardFolder)
            {
                RefreshPreviewSubject(); // Home and This PC describe nothing
            }

            RaisePropertyChanged(nameof(IsStandardFolderView));
            RaisePropertyChanged(nameof(IsThisPcView));
            RaisePropertyChanged(nameof(IsHomeView));
            RaisePropertyChanged(nameof(IsTrashView));
            RaisePropertyChanged(nameof(LocationTitle));
            _refreshCommand?.RaiseCanExecuteChanged(); // F5 reloads the trash, which has no path

            // Home's commands (Refresh, Clear activity data) are enabled by the surface. WPF's CommandManager re-queried
            // on every input event; this one only on request, so without this the Home page opened with them disabled.
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>True when a real folder's Details/Icons/List/Tiles/Timeline/Nexus surface is shown (the default, ordinary browsing state).</summary>
        public bool IsStandardFolderView => _currentSurface == FileListSurface.StandardFolder;

        /// <summary>True when the This PC devices/folders/recent page is shown — see FileListViewModel_ThisPc.cs.</summary>
        public bool IsThisPcView => _currentSurface == FileListSurface.ThisPc;

        /// <summary>True when the Home usage-analytics dashboard is shown — see FileListViewModel_Home.cs.</summary>
        public bool IsHomeView => _currentSurface == FileListSurface.Home;

        /// <summary>True when the trash / Recycle Bin's items are shown — see FileListViewModel.Trash.cs.</summary>
        public bool IsTrashView => _currentSurface == FileListSurface.Trash;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    _refreshCommand.RaiseCanExecuteChanged();
                    _navigateBackCommand.RaiseCanExecuteChanged();
                    _navigateForwardCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set => SetProperty(ref _errorMessage, value);
        }

        public string CurrentPath
        {
            get => _currentPath;
            private set
            {
                if (SetProperty(ref _currentPath, value))
                {
                    _navigateUpCommand.RaiseCanExecuteChanged();
                    _refreshCommand.RaiseCanExecuteChanged();
                    _openTerminalCommand.RaiseCanExecuteChanged();
                    RefreshPreviewSubject();
                    RaisePropertyChanged(nameof(LocationTitle));
                    OnPaneLocationChangedForProtection();
                }
            }
        }

        /// <summary>The place shown, as a window title: the folder's name, "Home" or "This PC" (the Finder layout's toolbar).</summary>
        public string LocationTitle => _currentSurface switch
        {
            FileListSurface.Home => "Home",
            FileListSurface.ThisPc => "This PC",
            FileListSurface.Trash => Core.Platform.Trash.DisplayName,
            _ => string.IsNullOrEmpty(_currentPath) ? "ByteExplorer" : (PathUtil.GetName(_currentPath) is string name && name.Length > 0 ? name : _currentPath)
        };

        /// <summary>The places behind the current one, nearest first: the Back button's history menu.</summary>
        public IReadOnlyList<HistoryEntry> BackHistory => _history.BackEntries;

        /// <summary>The places ahead of the current one, nearest first: the Forward button's history menu.</summary>
        public IReadOnlyList<HistoryEntry> ForwardHistory => _history.ForwardEntries;

        /// <summary>
        /// Records arriving at a place in the back/forward history (see <see cref="NavigationHistory"/>). Called where
        /// each surface takes over the pane (a folder, Home, This PC), not from the CurrentPath setter: Home and This PC
        /// both have no path, so a path change alone cannot tell them apart. Back/Forward navigations record nothing;
        /// the history has already moved.
        /// </summary>
        private void RecordArrival(HistoryEntry entry)
        {
            if (_isNavigatingViaHistory)
            {
                return;
            }

            if (_history.Arrive(entry))
            {
                _navigateBackCommand.RaiseCanExecuteChanged();
                _navigateForwardCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Moves <paramref name="steps"/> places through the history (negative = back, positive = forward) and shows
        /// that place: Back/Forward move one step, the buttons' history menus jump several. Errors reaching the place (a
        /// folder deleted since, a network share gone) are LoadFolderAsync's to report, as for any navigation.
        /// </summary>
        /// <summary>The window shows something in this pane's place (a lens): Back leaves it before going back in history.</summary>
        public Func<bool> OverlayShown { get; set; }

        /// <summary>Leaves what <see cref="OverlayShown"/> reports (closes the lens; the folder shows again).</summary>
        public Action LeaveOverlay { get; set; }

        /// <summary>The overlay came or went: Back may be possible now (or no longer).</summary>
        public void NotifyOverlayChanged() => _navigateBackCommand.RaiseCanExecuteChanged();

        private Task NavigateBackOrLeaveOverlayAsync()
        {
            if (OverlayShown?.Invoke() == true && LeaveOverlay != null)
            {
                LeaveOverlay(); // Back from a lens returns to the folder it was opened over, as Explorer's Back leaves search
                return Task.CompletedTask;
            }

            return NavigateHistoryAsync(-1);
        }

        public async Task NavigateHistoryAsync(int steps)
        {
            if (!_history.TryMove(steps, out HistoryEntry target))
            {
                return;
            }

            _navigateBackCommand.RaiseCanExecuteChanged();
            _navigateForwardCommand.RaiseCanExecuteChanged();

            _isNavigatingViaHistory = true;
            try
            {
                switch (target.Place)
                {
                    case HistoryPlace.Home:
                        await LoadHomeAsync().ConfigureAwait(true);
                        break;
                    case HistoryPlace.ThisPc:
                        await LoadThisPcAsync().ConfigureAwait(true);
                        break;
                    case HistoryPlace.Trash:
                        await LoadTrashAsync().ConfigureAwait(true);
                        break;
                    default:
                        await LoadFolderAsync(target.Path).ConfigureAwait(true);
                        break;
                }
            }
            finally
            {
                _isNavigatingViaHistory = false;
            }
        }

        /// <summary>
        /// Quick filter — current folder only, no recursion into
        /// subfolders, no file-content search, matching purely on name.
        /// Deliberately fast: filtering happens over
        /// DirectoryIndex.Entries, the same lightweight, already-fully-
        /// loaded-in-memory struct list the folder's own index build
        /// already produced — not the file list, not the filesystem
        /// again. A folder that took tens of milliseconds to index in
        /// the first place filters in a similar (or shorter) amount of
        /// time, since a substring scan is cheaper than the native
        /// enumeration + sort that built the index.
        /// </summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetProperty(ref _filterText, value))
                {
                    RebuildDisplayedItemsDebounced(FilterTypingDelayMs);
                }
            }
        }

        /// <summary>
        /// Category-based filter (see FilterSelector) — a genuinely
        /// different, complementary axis to FilterText's name-substring
        /// filter, not a duplicate of it: FilterText narrows by what an
        /// item is called, this narrows by what kind of item it is.
        /// Applied together with FilterText and the hidden/system
        /// toggles in the same single pass over the folder's index (see
        /// BuildFilteredAndSortedIndex), not as a separate re-filter
        /// step.
        /// </summary>
        public FileCategoryFilter CategoryFilter
        {
            get => _categoryFilter;
            set
            {
                if (SetProperty(ref _categoryFilter, value))
                {
                    RebuildDisplayedItemsDebounced();
                }
            }
        }

        /// <summary>
        /// Mirrors NavigationSettings.ShowHiddenItems — the same setting
        /// the left tree already reads (combined with ShowSystemItems
        /// there, since the tree's own enumeration call doesn't
        /// distinguish the two — see NavigationNodeViewModel), so
        /// hidden-item visibility is one consistent, app-wide preference.
        /// Defaults to false until explicitly turned on.
        /// </summary>
        public bool ShowHiddenItems => _settingsService?.Current?.Navigation?.ShowHiddenItems ?? false;

        /// <summary>
        /// Mirrors NavigationSettings.ShowSystemItems. Kept as its own,
        /// independent toggle from ShowHiddenItems — a system item is
        /// sensitive enough to warrant a deliberate, separate opt-in
        /// rather than being bundled with the more common "just show me
        /// dotfiles" case. Defaults to false until explicitly turned on.
        /// </summary>
        public bool ShowSystemItems => _settingsService?.Current?.Navigation?.ShowSystemItems ?? false;

        public FileListSortColumn SortColumn => _sortColumn;
        public bool SortAscending => _sortAscending;

        /// <summary>
        /// Column-header click handler. Toggles direction if the same
        /// column is clicked again, matching Explorer's own convention
        /// (and this app's own view-preset toggle patterns elsewhere).
        /// </summary>
        public void SetSortColumn(FileListSortColumn column)
        {
            if (_sortColumn == column)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }

            RaisePropertyChanged(nameof(SortColumn));
            RaisePropertyChanged(nameof(SortAscending));
            RebuildDisplayedItemsDebounced();
        }

        /// <summary>
        /// Explicit column + direction setter for the Sort menu (see
        /// SortSelector) — distinct from SetSortColumn, which toggles
        /// direction when the same column is clicked again, correct for
        /// a column-header click but wrong for a menu: picking "Name"
        /// from a Sort menu should set Name, not silently flip direction
        /// depending on whatever was already active. Direction is
        /// chosen explicitly by separate Ascending/Descending menu
        /// items instead.
        /// </summary>
        public void SetSort(FileListSortColumn column, bool ascending)
        {
            _sortColumn = column;
            _sortAscending = ascending;

            RaisePropertyChanged(nameof(SortColumn));
            RaisePropertyChanged(nameof(SortAscending));
            RebuildDisplayedItemsDebounced();
        }

        /// <summary>How long typing in the filter box must pause before the list is replaced (one list per pause, not per key).</summary>
        private const int FilterTypingDelayMs = 50;

        /// <summary>
        /// Rebuilds the list for the current filter, sort and visibility settings, replacing any rebuild still pending.
        /// Only typing waits (<paramref name="delayMs"/>); a sort click or a setting change applies at once.
        /// </summary>
        private void RebuildDisplayedItemsDebounced(int delayMs = 0)
        {
            _filterCts?.Cancel();
            var cts = new CancellationTokenSource();
            _filterCts = cts;
            RebuildDisplayedItemsAsync(delayMs, cts.Token).Forget("RebuildDisplayedItemsAsync");
        }

        /// <summary>
        /// Flips either NavigationSettings.ShowHiddenItems or
        /// ShowSystemItems (per showHidden) and applies it immediately —
        /// no re-enumeration of the folder is needed, since hidden/system
        /// status was already captured for every entry during the
        /// original index build (see DirectoryIndexEntry.IsHidden/
        /// IsSystem), so this is exactly as cheap as toggling the text
        /// filter. Persisted right away rather than debounced: unlike a
        /// zoom slider, a discrete on/off toggle has no reason to wait
        /// before saving.
        /// </summary>
        private async Task ToggleHiddenOrSystemItemsAsync(bool showHidden)
        {
            NavigationSettings settings = _settingsService?.Current?.Navigation;
            if (settings == null)
            {
                return;
            }

            if (showHidden)
            {
                settings.ShowHiddenItems = !settings.ShowHiddenItems;
                RaisePropertyChanged(nameof(ShowHiddenItems));
            }
            else
            {
                settings.ShowSystemItems = !settings.ShowSystemItems;
                RaisePropertyChanged(nameof(ShowSystemItems));
            }

            RebuildDisplayedItemsDebounced();

            try
            {
                // SettingsService.SaveAsync already swallows and logs its
                // own failures internally — this try/catch is a cheap,
                // deliberately redundant extra layer, not load-bearing.
                await _settingsService.SaveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to persist hidden/system items visibility setting");
            }
        }

        private async Task RebuildDisplayedItemsAsync(int delayMs, CancellationToken cancellationToken)
        {
            // Typing waits for a pause (one list per pause); anything else only yields, so the caller's property
            // notifications finish before the list is replaced.
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_currentIndex == null)
            {
                return;
            }

            DirectoryIndex snapshot = _currentIndex;
            string filter = FilterText;
            FileListSortColumn sortColumn = _sortColumn;
            bool sortAscending = _sortAscending;

            DirectoryIndex result;
            try
            {
                result = await GetDisplayedIndexAsync(snapshot, filter, cancellationToken).ConfigureAwait(true);
                LastViewBuildFailure = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Was only logged by Forget, leaving a list that silently ignored the new sort or filter.
                ReportViewBuildFailure(ex, string.IsNullOrEmpty(filter) ? $"sort by {SortColumnName(sortColumn)}" : $"filter by \"{filter}\"",
                    "The list keeps its previous order.");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Items = new VirtualizingFileList(result, _residentWindow, _prefetchBufferSize, _logger);
            _displayedIndex = result;

            // Keep Timeline / Nexus in lockstep with the same
            // filtered+sorted index that Details/Icons/List/Tiles
            // already use.
            RebuildTimelineGroupsIfNeeded();
            RebuildNexusClustersIfNeeded();

            // Columns is handled separately, not via RebuildColumnPanesIfNeeded:
            // that rebuilds from just the root, which would destroy every
            // drilled-down column on every single filter-box keystroke.
            // Only the currently-focused column (which may well be a
            // drilled-down subfolder, not the root) gets re-filtered,
            // in place, leaving its siblings untouched.
            if (ViewMode == FileListViewMode.Columns)
            {
                RebuildFocusedColumnFilterAsync(filter, sortColumn, sortAscending).Forget("RebuildFocusedColumnFilterAsync");
            }
        }

        private static DirectoryIndex BuildFilteredAndSortedIndex(DirectoryIndex source, string filter, FileListSortColumn sortColumn, bool sortAscending, bool includeHidden, bool includeSystem, FileCategoryFilter categoryFilter) =>
            ApplyTextFilter(BuildSortedView(source, sortColumn, sortAscending, includeHidden, includeSystem, categoryFilter), filter);

        /// <summary>
        /// The folder as shown before any typed filter: hidden, system and category exclusions applied, then ordered
        /// (<see cref="DirectoryEntrySorter"/>). Nothing is copied when nothing is excluded and the index is already in the
        /// requested order (the usual case: name order straight from the index); name order descending is a reversal, not
        /// a sort.
        /// </summary>
        /// <summary>
        /// Tests only: makes building a sorted view of a folder throw (null: normal), to check that failures are reported.
        /// Given the folder and the column, so a test only breaks its own folder.
        /// </summary>
        internal static Func<string, FileListSortColumn, Exception> SortFaultForTests;

        private static DirectoryIndex BuildSortedView(DirectoryIndex source, FileListSortColumn sortColumn, bool sortAscending, bool includeHidden, bool includeSystem, FileCategoryFilter categoryFilter)
        {
            Exception fault = SortFaultForTests?.Invoke(source.ParentPath, sortColumn);
            if (fault != null)
            {
                throw fault;
            }

            IReadOnlyList<DirectoryIndexEntry> entries = source.Entries;
            List<DirectoryIndexEntry> kept = null; // created at the first excluded entry
            for (int i = 0; i < entries.Count; i++)
            {
                DirectoryIndexEntry entry = entries[i];
                bool include = (includeHidden || !entry.IsHidden) && (includeSystem || !entry.IsSystem) &&
                               (categoryFilter == FileCategoryFilter.All || FileCategoryClassifier.Matches(entry.Name, entry.IsDirectory, categoryFilter));
                if (!include)
                {
                    if (kept == null)
                    {
                        kept = new List<DirectoryIndexEntry>(entries.Count);
                        for (int j = 0; j < i; j++)
                        {
                            kept.Add(entries[j]);
                        }
                    }
                }
                else
                {
                    kept?.Add(entry);
                }
            }

            bool nameAscending = sortColumn == FileListSortColumn.Name && sortAscending;
            if (source.IsSortedByName && nameAscending)
            {
                return kept == null ? source : new DirectoryIndex(source.ParentPath, kept, false, null) { IsSortedByName = true };
            }

            Func<DirectoryIndexEntry, string> tagKey = null;
            if (sortColumn == FileListSortColumn.Tags)
            {
                ByteExplorer.Core.Tags.TagStore tags = ByteExplorer.Core.Tags.TagStore.Default;
                string parent = source.ParentPath ?? string.Empty;
                tagKey = entry => tags.SortKey(System.IO.Path.Combine(parent, entry.Name));
            }

            Func<DirectoryIndexEntry, long> folderSize = null;
            if (sortColumn == FileListSortColumn.Size)
            {
                string parent = source.ParentPath ?? string.Empty;
                folderSize = entry => FolderSizes.BytesOrUnknown(PathUtil.Combine(parent, entry.Name));
            }

            DirectoryIndexEntry[] sorted = DirectoryEntrySorter.Sort((IReadOnlyList<DirectoryIndexEntry>)kept ?? entries, ToEntryOrder(sortColumn), sortAscending, source.IsSortedByName, tagKey, folderSize);
            return new DirectoryIndex(source.ParentPath, sorted, false, null) { IsSortedByName = nameAscending };
        }

        /// <summary>The entries whose name contains <paramref name="filter"/> (case-insensitive), in their order.</summary>
        private static DirectoryIndex ApplyTextFilter(DirectoryIndex sorted, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return sorted;
            }

            IReadOnlyList<DirectoryIndexEntry> entries = sorted.Entries;
            var list = new List<DirectoryIndexEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(entries[i]);
                }
            }

            return new DirectoryIndex(sorted.ParentPath, list, false, null) { IsSortedByName = sorted.IsSortedByName };
        }

        private static EntryOrder ToEntryOrder(FileListSortColumn column) => column switch
        {
            FileListSortColumn.Size => EntryOrder.Size,
            FileListSortColumn.DateModified => EntryOrder.DateModified,
            FileListSortColumn.Type => EntryOrder.Type,
            FileListSortColumn.Tags => EntryOrder.Tags,
            FileListSortColumn.DateCreated => EntryOrder.DateCreated,
            FileListSortColumn.DateAccessed => EntryOrder.DateAccessed,
            _ => EntryOrder.Name
        };

        /// <summary>The settings a sorted view was built for (see <see cref="GetDisplayedIndexAsync"/>).</summary>
        /// <summary>The settings a sorted view was built for; sorted by tags, also the tag store's version (tags changed: sort again).</summary>
        private readonly record struct SortedViewKey(FileListSortColumn Column, bool Ascending, bool Hidden, bool System, FileCategoryFilter Category, long TagVersion);

        private DirectoryIndex _sortedViewSource;
        private SortedViewKey _sortedViewKey;
        private DirectoryIndex _sortedView;
        private string _lastTextFilter;
        private DirectoryIndex _lastTextFiltered;

        private SortedViewKey CurrentSortedViewKey =>
            new SortedViewKey(_sortColumn, _sortAscending, ShowHiddenItems, ShowSystemItems, _categoryFilter,
                _sortColumn == FileListSortColumn.Tags ? ByteExplorer.Core.Tags.TagStore.Default.Version
                    : _sortColumn == FileListSortColumn.Size ? FolderSizes.Version : 0);

        /// <summary>
        /// What the list shows for <paramref name="source"/> with the current settings and <paramref name="filter"/>.
        /// The sorted view is kept between calls, so typing in the filter box never sorts again: each keystroke only
        /// filters, and a filter that extends the previous one ("rep" after "re") filters the previous result instead of
        /// the whole folder. The work runs off the UI thread; the caches are read and written on it. Cancellation
        /// throws OperationCanceledException.
        /// </summary>
        private async Task<DirectoryIndex> GetDisplayedIndexAsync(DirectoryIndex source, string filter, CancellationToken cancellationToken)
        {
            filter ??= string.Empty;
            SortedViewKey key = CurrentSortedViewKey;
            DirectoryIndex view = ReferenceEquals(_sortedViewSource, source) && _sortedViewKey.Equals(key) ? _sortedView : null;
            DirectoryIndex narrowFrom = view != null && !string.IsNullOrEmpty(_lastTextFilter) && _lastTextFiltered != null &&
                                        filter.Contains(_lastTextFilter, StringComparison.OrdinalIgnoreCase)
                ? _lastTextFiltered
                : null;

            DirectoryIndex builtView = view;
            DirectoryIndex filtered;
            if (view != null && narrowFrom == null && filter.Length == 0)
            {
                filtered = view; // nothing to do
            }
            else
            {
                (builtView, filtered) = await Task.Run(() =>
                {
                    DirectoryIndex sortedView = view ?? BuildSortedView(source, key.Column, key.Ascending, key.Hidden, key.System, key.Category);
                    return (sortedView, ApplyTextFilter(narrowFrom ?? sortedView, filter));
                }, cancellationToken).ConfigureAwait(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _sortedViewSource = source;
            _sortedViewKey = key;
            _sortedView = builtView;
            _lastTextFilter = filter;
            _lastTextFiltered = filtered;
            return filtered;
        }

        /// <summary>True when two listings of the same folder (both in index order) show exactly the same entries.</summary>
        private static bool HaveSameEntries(DirectoryIndex a, DirectoryIndex b)
        {
            if (a == null || b == null || a.Count != b.Count || a.IsSortedByName != b.IsSortedByName)
            {
                return false;
            }

            IReadOnlyList<DirectoryIndexEntry> x = a.Entries;
            IReadOnlyList<DirectoryIndexEntry> y = b.Entries;
            for (int i = 0; i < x.Count; i++)
            {
                DirectoryIndexEntry p = x[i];
                DirectoryIndexEntry q = y[i];
                if (p.IsDirectory != q.IsDirectory || p.SizeBytes != q.SizeBytes || p.LastWriteTimeUtc != q.LastWriteTimeUtc ||
                    p.Attributes != q.Attributes || !string.Equals(p.Name, q.Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Re-reads the folder shown and updates the list in place: the filter text, sort, selection (by name) and
        /// scroll position stay, nothing flashes "Loading...", the folder watcher keeps running, and the visit is not
        /// counted again for Home's frequent folders. Used by the live folder watcher, F5 and the refresh after a file
        /// operation, which all used to reload the folder as if navigating to it: a file created while the person was
        /// filtering cleared the filter and the selection. When nothing changed (a temporary file that came and went),
        /// the list is left alone. A folder that has gone, or any state other than a listed folder, gets a full load,
        /// which shows the error or the page as a navigation would.
        /// </summary>
        /// <summary>
        /// The folder shown changed on disk (seen by its watcher, a refresh or a file operation): the global search index
        /// updates that folder (MainViewModel forwards it).
        /// </summary>
        public event EventHandler<string> FolderChangedOnDisk;

        public async Task RefreshCurrentFolderAsync(bool force = false)
        {
            string path = CurrentPath;
            CancellationTokenSource cts = _loadCts;
            if (force && !string.IsNullOrEmpty(path))
            {
                // F5: counted folder sizes here are counted again (another program may have changed them).
                FolderSizes.ForgetChildren(path);
                StartAutomaticFolderSizesAfterRefresh(path);
            }

            if (string.IsNullOrEmpty(path) || _currentSurface != FileListSurface.StandardFolder || _currentIndex == null ||
                ErrorMessage != null || cts == null || _isLoading)
            {
                if (!string.IsNullOrEmpty(path) && !_isLoading)
                {
                    await LoadFolderAsync(path, bypassCache: true).ConfigureAwait(true);
                }

                return;
            }

            try
            {
                int networkTimeoutMs = _settingsService?.Current?.FileList?.NetworkEnumerationTimeoutMs ?? 8000;
                DirectoryIndex index = await _indexService.BuildIndexAsync(path, networkTimeoutMs, bypassCache: true, cancellationToken: cts.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_loadCts, cts) || !string.Equals(CurrentPath, path, PathUtil.Comparison))
                {
                    return; // navigated away meanwhile
                }

                if (index.HasError)
                {
                    await LoadFolderAsync(path, bypassCache: true).ConfigureAwait(true);
                    return;
                }

                bool changedOnDisk = !HaveSameEntries(_currentIndex, index);
                if (changedOnDisk)
                {
                    FolderChangedOnDisk?.Invoke(this, path);
                }

                if (!force && !changedOnDisk)
                {
                    return;
                }

                DirectoryIndex displayed = await GetDisplayedIndexOrFallbackAsync(index, FilterText, cts.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_loadCts, cts) || !string.Equals(CurrentPath, path, PathUtil.Comparison))
                {
                    return;
                }

                _currentIndex = index;
                _displayedIndex = displayed;
                Items = new VirtualizingFileList(displayed, _residentWindow, _prefetchBufferSize, _logger);
                RebuildTimelineGroupsIfNeeded();
                RebuildColumnPanesIfNeeded();
                RebuildNexusClustersIfNeeded();
                if (LastViewBuildFailure == null)
                {
                    StatusText = $"{displayed.Count:N0} item{(displayed.Count == 1 ? string.Empty : "s")}";
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a navigation.
            }
        }

        public ICommand NavigateUpCommand { get; }
        public ICommand NavigateBackCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand InvertSelectionCommand { get; }
        public ICommand NavigateForwardCommand { get; }

        private Task NavigateUpAsync()
        {
            string parent = TryGetParentPath();
            return string.IsNullOrEmpty(parent) ? Task.CompletedTask : LoadFolderAsync(parent);
        }

        private bool HasParentPath() => !string.IsNullOrEmpty(TryGetParentPath());

        private string TryGetParentPath()
        {
            if (string.IsNullOrEmpty(CurrentPath))
            {
                return null;
            }

            try
            {
                // Path.GetDirectoryName returns null for a drive root
                // (e.g. "C:\") — correctly signaling "nowhere to go up
                // to" rather than throwing.
                return PathUtil.GetParent(CurrentPath);
            }
            catch (Exception)
            {
                // A malformed CurrentPath (shouldn't happen, since it
                // only ever comes from successful navigations) — treat
                // as "no parent" rather than let Up crash.
                return null;
            }
        }

        /// <summary>
        /// The underlying layout engine (Details vs Icons). Bound by
        /// FileListView to toggle which sub-view is visible.
        /// </summary>
        public FileListViewMode ViewMode
        {
            get => _viewMode;
            private set
            {
                if (SetProperty(ref _viewMode, value))
                {
                    RebuildTimelineGroupsIfNeeded();
                    RebuildColumnPanesIfNeeded();
                    RebuildNexusClustersIfNeeded();
                    _analyticsService.TrackViewModeChanged(value.ToString());
                }
            }
        }

        /// <summary>
        /// Date-grouped items for Timeline view. Rebuilt whenever the
        /// underlying index or view mode changes. Null/empty when not
        /// in Timeline mode so the panel stays cheap.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<TimelineGroup> TimelineGroups
        {
            get => _timelineGroups;
            private set => SetProperty(ref _timelineGroups, value);
        }



        public System.Collections.ObjectModel.ObservableCollection<ColumnPane> ColumnPanes
        {
            get => _columnPanes;
            private set => SetProperty(ref _columnPanes, value);
        }

        /// <summary>
        /// The column the user most recently interacted with, in
        /// Columns view — null in every other view mode. Drives
        /// SelectionSummaryText, EffectiveTargetPath, and which
        /// column's header shows the "focused" highlight. Set from
        /// OnColumnSelectionChanged/OnColumnFocused (code-behind calls
        /// these whenever a column's ListBox gains focus or its
        /// selection changes), never assumed to still be valid across a
        /// full ColumnPanes rebuild — RebuildColumnPanesIfNeeded always
        /// re-points this at the new root pane.
        /// </summary>
        public ColumnPane FocusedColumnPane
        {
            get => _focusedColumnPane;
            private set
            {
                if (ReferenceEquals(_focusedColumnPane, value))
                {
                    return;
                }

                if (_focusedColumnPane != null)
                {
                    _focusedColumnPane.IsFocused = false;
                }

                _focusedColumnPane = value;

                if (_focusedColumnPane != null)
                {
                    _focusedColumnPane.IsFocused = true;
                }

                RaisePropertyChanged(nameof(FocusedColumnPane));
                RaisePropertyChanged(nameof(SelectionSummaryText));
            }
        }

        /// <summary>
        /// Where New Folder / Paste actually create/place things, and
        /// what a folder refresh after a file operation targets.
        /// CurrentPath everywhere except Columns view with a
        /// drilled-down column focused, where it's that column's own
        /// folder instead — without this, Paste/New Folder in Columns
        /// view always silently landed in the root column's folder
        /// regardless of which column was actually focused.
        /// </summary>
        public string EffectiveTargetPath =>
            ViewMode == FileListViewMode.Columns && FocusedColumnPane != null
                ? FocusedColumnPane.Path
                : CurrentPath;

        public System.Collections.ObjectModel.ObservableCollection<NexusCluster> NexusClusters
        {
            get => _nexusClusters;
            private set => SetProperty(ref _nexusClusters, value);
        }


        /// <summary>
        /// The named option shown selected in the toolbar dropdown.
        /// Public setter is the two-way ComboBox binding target — it
        /// applies the preset's mode and (for icon presets) exact icon
        /// size, then persists, rather than just recording the
        /// selection.
        /// </summary>
        public FileListViewPreset SelectedPreset
        {
            get => _selectedPreset;
            set => ApplyViewPreset(value);
        }

        /// <summary>Whether image items show thumbnails (settings, read live like the other view settings).</summary>
        public bool ShowThumbnails => _settingsService?.Current?.FileList?.ShowThumbnails ?? true;

        /// <summary>Folder options › "Show Windows icons": the views draw Explorer's icons (Windows only).</summary>
        public bool ShowSystemIcons => Infrastructure.SystemIcons.SystemIconCache.IsActive;

        /// <summary>Tells the views a display setting changed in the settings window (thumbnails, spacing), so they redraw at once.</summary>
        /// <summary>Folder options: a check box on each item (the template views bind this; the others draw it).</summary>
        public bool ShowCheckBoxes => FolderOptions.Current.UseCheckBoxes;

        /// <summary>Folder options: recently used files on Home and This PC.</summary>
        public bool ShowRecentFiles => FolderOptions.Current.ShowRecentFiles;

        /// <summary>Folder options: frequently used folders on Home and in the left panel.</summary>
        public bool ShowFrequentFolders => FolderOptions.Current.ShowFrequentFolders;

        public void NotifyDisplaySettingsChanged()
        {
            RaisePropertyChanged(nameof(ShowCheckBoxes));
            RaisePropertyChanged(nameof(ShowRecentFiles));
            RaisePropertyChanged(nameof(ShowFrequentFolders));
            RaisePropertyChanged(nameof(HasSidebarFrequentFolders));
            RaisePropertyChanged(nameof(ShowThumbnails));
            RaisePropertyChanged(nameof(ShowSystemIcons));
            RaisePropertyChanged(nameof(DetailsItemMargin));
            RaisePropertyChanged(nameof(ListItemMargin));
            RaisePropertyChanged(nameof(IconsItemMargin));
            RaisePropertyChanged(nameof(TilesItemMargin));
        }

        // The item spacing settings as each item's margin (half the gap on each side, so neighbours make the whole gap).
        // The margin is outside the item, where a press is on the list itself: that is where a rubber band starts.

        public Avalonia.Thickness DetailsItemMargin => new Avalonia.Thickness(0, Spacing(f => f.ItemSpacingDetails, Core.Models.FileListSettings.DefaultItemSpacingDetails) / 2.0);

        public Avalonia.Thickness ListItemMargin => new Avalonia.Thickness(0, Spacing(f => f.ItemSpacingList, Core.Models.FileListSettings.DefaultItemSpacingList) / 2.0);

        public Avalonia.Thickness IconsItemMargin => new Avalonia.Thickness(Spacing(f => f.ItemSpacingIcons, Core.Models.FileListSettings.DefaultItemSpacingIcons) / 2.0);

        public Avalonia.Thickness TilesItemMargin => new Avalonia.Thickness(Spacing(f => f.ItemSpacingTiles, Core.Models.FileListSettings.DefaultItemSpacingTiles) / 2.0);

        private int Spacing(Func<Core.Models.FileListSettings, int> read, int fallback)
        {
            Core.Models.FileListSettings settings = _settingsService?.Current?.FileList;
            return settings == null ? fallback : Math.Clamp(read(settings), 0, Core.Models.FileListSettings.MaxItemSpacing);
        }

        /// <summary>
        /// The single underlying zoom value (32-256), historically named
        /// for its original, only use (Icons view's icon size). Every
        /// other view mode now also derives its own icon/font/row-height
        /// from this same value — see ZoomFraction and the DetailsXxx/
        /// ListXxx/TilesXxx/TimelineXxx/ColumnsXxx/NexusXxx properties
        /// below — rather than each view having its own independent
        /// zoom state. One shared driver means the zoom slider and
        /// Ctrl+scroll behave consistently no matter which view is
        /// active: moving the slider to the same relative position
        /// always means "this small" or "this spacious" everywhere,
        /// and switching views mid-session keeps that same relative
        /// feel instead of resetting.
        /// </summary>
        public double IconSize
        {
            get => _iconSize;
            set => SetIconSize(value);
        }

        // Derived tile dimensions for the Icons view grid (icon + label +
        // padding) — kept on the view model rather than computed in
        // XAML so the same numbers drive both the panel's cell size
        // and the label's wrap width consistently.
        public double TileWidth => IconSize + 24;
        public double TileHeight => IconSize + 40;

        // ---------------------------------------------------------------
        // Per-view zoom mapping. IconSize's full 32-256 range always maps
        // to the same normalized 0-1 fraction (see ZoomFraction); each
        // view below Lerps that fraction into its own appropriate pixel
        // range instead of sharing Icons view's own 32-256 icon-pixel
        // range directly — a "32px icon" is sensible for Icons view but
        // would be absurd for a single Details row, and a font that
        // scaled across Icons view's full range would be unreadable at
        // one end and comically large at the other. Every range below is
        // calibrated so the DEFAULT IconSize (64, "Medium icons") lands
        // close to each view's original hardcoded value, so existing
        // users see no jarring jump in a view they've never zoomed.
        //
        // All are simple, cheap, allocation-free expression-bodied
        // getters — recomputed on demand from the one _iconSize field,
        // not cached, since the arithmetic involved is trivial. Every
        // one of them is re-raised together by
        // RaiseAllZoomDerivedPropertiesChanged whenever _iconSize
        // changes, from the one place that method is called, rather
        // than requiring every call site that changes zoom to remember
        // to raise each property individually — the exact class of
        // "forgot one" bug that's cheap to introduce by hand and
        // expensive to notice (a stale font/icon size in one view,
        // silently wrong until someone happens to look).
        // ---------------------------------------------------------------

        /// <summary>0 at the smallest zoom (IconSize 32), 1 at the largest (IconSize 256).</summary>
        private double ZoomFraction
        {
            get
            {
                const double min = 32.0, max = 256.0;
                double t = (_iconSize - min) / (max - min);
                return t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
            }
        }

        private static double Lerp(double min, double max, double t) => min + (max - min) * t;

        public double DetailsIconSize => Math.Round(Lerp(14, 30, ZoomFraction));
        public double DetailsFontSize => Math.Round(Lerp(11, 15, ZoomFraction));

        public double ListRowHeight => Math.Round(Lerp(20, 40, ZoomFraction));
        public double ListIconSize => Math.Round(Lerp(12, 26, ZoomFraction));
        public double ListFontSize => Math.Round(Lerp(11, 15, ZoomFraction));

        public double TilesIconSize => Math.Round(Lerp(24, 56, ZoomFraction));
        public double TilesNameFontSize => Math.Round(Lerp(12, 16, ZoomFraction));
        public double TilesSubFontSize => Math.Round(Lerp(10, 13, ZoomFraction));
        public double TilesItemWidth => Math.Round(Lerp(220, 320, ZoomFraction));
        public double TilesItemHeight => Math.Round(TilesIconSize + 40);

        public double TimelineRowHeight => Math.Round(Lerp(26, 44, ZoomFraction));
        public double TimelineIconSize => Math.Round(Lerp(14, 28, ZoomFraction));
        public double TimelineFontSize => Math.Round(Lerp(12, 15, ZoomFraction));

        public double ColumnsRowHeight => Math.Round(Lerp(24, 40, ZoomFraction));
        public double ColumnsIconSize => Math.Round(Lerp(12, 24, ZoomFraction));
        public double ColumnsFontSize => Math.Round(Lerp(12, 15, ZoomFraction));
        public double ColumnsWidth => Math.Round(Lerp(200, 260, ZoomFraction));

        public double NexusRowHeight => Math.Round(Lerp(22, 34, ZoomFraction));
        public double NexusIconSize => Math.Round(Lerp(10, 20, ZoomFraction));
        public double NexusFontSize => Math.Round(Lerp(11, 14, ZoomFraction));

        /// <summary>
        /// IconSize expressed as a percentage for the Nemo layout's
        /// zoom-row display — 100% at IconSize 64, the same "Medium
        /// icons" default documented above every Lerp range, so a fresh
        /// install's zoom row reads "100%" rather than some arbitrary
        /// number.
        /// </summary>
        public string IconZoomPercentDisplay => Math.Round(_iconSize / 64.0 * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// Raises every zoom-derived property in one call — the single
        /// place all of them are kept in sync, called from every site
        /// that changes _iconSize (InitializeFromSettings, ApplyViewPreset,
        /// SetIconSize) instead of each of those three remembering its
        /// own subset of RaisePropertyChanged calls.
        /// </summary>
        private void RaiseAllZoomDerivedPropertiesChanged()
        {
            RaisePropertyChanged(nameof(TileWidth));
            RaisePropertyChanged(nameof(TileHeight));
            RaisePropertyChanged(nameof(DetailsIconSize));
            RaisePropertyChanged(nameof(DetailsFontSize));
            RaisePropertyChanged(nameof(ListRowHeight));
            RaisePropertyChanged(nameof(ListIconSize));
            RaisePropertyChanged(nameof(ListFontSize));
            RaisePropertyChanged(nameof(TilesIconSize));
            RaisePropertyChanged(nameof(TilesNameFontSize));
            RaisePropertyChanged(nameof(TilesSubFontSize));
            RaisePropertyChanged(nameof(TilesItemWidth));
            RaisePropertyChanged(nameof(TilesItemHeight));
            RaisePropertyChanged(nameof(TimelineRowHeight));
            RaisePropertyChanged(nameof(TimelineIconSize));
            RaisePropertyChanged(nameof(TimelineFontSize));
            RaisePropertyChanged(nameof(ColumnsRowHeight));
            RaisePropertyChanged(nameof(ColumnsIconSize));
            RaisePropertyChanged(nameof(ColumnsFontSize));
            RaisePropertyChanged(nameof(ColumnsWidth));
            RaisePropertyChanged(nameof(NexusRowHeight));
            RaisePropertyChanged(nameof(NexusIconSize));
            RaisePropertyChanged(nameof(NexusFontSize));
            RaisePropertyChanged(nameof(IconZoomPercentDisplay));
        }

        public IReadOnlyList<FileListViewPresetOption> ViewPresetOptions { get; } = FileListViewPresetOption.BuildAll();

        /// <summary>
        /// Reads the persisted view mode/icon size from settings and
        /// applies them. Called once from MainViewModel.InitializeAsync
        /// after settings have actually loaded from disk — calling this
        /// from the constructor would only ever see un-loaded defaults.
        /// </summary>
        public void InitializeFromSettings()
        {
            FileListSettings settings = _settingsService?.Current?.FileList;
            if (settings == null)
            {
                return;
            }

            ApplyViewSettings();

            // Fire-and-forget, deliberately not awaited: removes backup
            // folders left behind by a session that ended before its own
            // cleanup ran (crashed, or a failed operation's backup that
            // was deliberately kept and has now aged out). Not required
            // for correctness — a failure here is caught internally by
            // FileBackupService.CleanupOldBackups itself.
            int retentionDays = settings.BackupRetentionDays;
            _ = Task.Run(() => _fileBackupService?.CleanupOldBackups(TimeSpan.FromDays(retentionDays)));

            // Best-effort — see RefreshSidebarFrequentFolders's own
            // remarks on why a startup-timing gap here self-corrects
            // almost immediately regardless.
            RefreshSidebarFrequentFolders();
        }

        /// <summary>
        /// Shows the saved default view (view mode, icon size) and the sidebar's frequent folders: what a pane made after
        /// the settings loaded needs (a new tab), without <see cref="InitializeFromSettings"/>'s once-per-start cleanup.
        /// </summary>
        public void ApplyViewSettings()
        {
            FileListSettings settings = _settingsService?.Current?.FileList;
            if (settings == null)
            {
                return;
            }

            FileListViewPreset preset = FileListViewPresets.FromSettingsString(settings.DefaultViewMode);
            SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset));
            ViewMode = FileListViewPresets.ModeFor(preset);

            double savedIconSize = settings.DefaultIconSize > 0 ? settings.DefaultIconSize : FileListViewPresets.IconSizeFor(preset);
            SetProperty(ref _iconSize, savedIconSize, nameof(IconSize));
            RaiseAllZoomDerivedPropertiesChanged();
            RefreshSidebarFrequentFolders();
        }

        /// <summary>
        /// Shows the same view as <paramref name="other"/> (view mode, icon size): a new tab or a split shows what the
        /// person is looking at. Unlike setting <see cref="SelectedPreset"/>, it doesn't save the view as the default nor
        /// count as a view change in the usage statistics; the pane is empty, so nothing needs rebuilding.
        /// </summary>
        public void CopyViewFrom(FileListViewModel other)
        {
            if (other == null || ReferenceEquals(other, this))
            {
                return;
            }

            SetProperty(ref _selectedPreset, other._selectedPreset, nameof(SelectedPreset));
            SetProperty(ref _viewMode, other._viewMode, nameof(ViewMode));
            SetProperty(ref _iconSize, other._iconSize, nameof(IconSize));
            RaiseAllZoomDerivedPropertiesChanged();
            RefreshSidebarFrequentFolders();
        }

        /// <summary>
        /// Ctrl+scroll zoom, called from FileListView's PreviewMouseWheel
        /// handler. Meaningful in every view mode now — each maps the
        /// same underlying IconSize value through its own range (see the
        /// DetailsXxx/ListXxx/etc. properties above), so this no longer
        /// needs to gate on ViewMode the way it used to when only Icons
        /// view had anything to zoom.
        /// </summary>
        public void AdjustZoom(double delta)
        {
            SetIconSize(IconSize + delta);
        }

        private static bool IsGroupedViewMode(FileListViewMode mode)
        {
            return mode == FileListViewMode.Timeline || mode == FileListViewMode.Nexus || mode == FileListViewMode.Columns;
        }

        /// <summary>
        /// See FileListSettings.MaxItemsForGroupedViews' remarks —
        /// Timeline, Nexus, and Columns all build a fully-materialized
        /// FileListItemViewModel for every single item up front
        /// (TimelineGroup/NexusCluster/ColumnPane all hold a plain,
        /// eager list or ObservableCollection, not a VirtualizingFileList)
        /// rather than truly virtualizing like every other view here, so
        /// this is a deliberately blunt guard against that turning into
        /// an actual UI freeze on a large folder, not a fix for the
        /// underlying limitation itself. Columns was originally left out
        /// of this guard — BuildColumnPane's eager materialization loop
        /// had no size protection at all until this was added.
        /// </summary>
        private bool CanUseGroupedView(FileListViewMode mode, out string blockReason)
        {
            int threshold = _settingsService?.Current?.FileList?.MaxItemsForGroupedViews ?? 5000;
            int count = CurrentFolderItemCount;

            if (count > threshold)
            {
                string modeName = mode == FileListViewMode.Timeline
                    ? "Timeline"
                    : mode == FileListViewMode.Nexus ? "Nexus" : "Columns";
                blockReason = $"{modeName} view isn't available for this folder ({count:N0} items) — it can become slow or unresponsive on folders this large (over {threshold:N0} items). Try Details, List, Icons, or Tiles instead.";
                return false;
            }

            blockReason = null;
            return true;
        }

        private void ApplyViewPreset(FileListViewPreset preset)
        {
            FileListViewMode targetMode = FileListViewPresets.ModeFor(preset);

            if (IsGroupedViewMode(targetMode) && !CanUseGroupedView(targetMode, out string blockReason))
            {
                // Refuse the switch outright — SelectedPreset/ViewMode are
                // left exactly as they were. Still explicitly re-raise
                // SelectedPreset even though its value isn't changing:
                // WPF updates a TwoWay-bound picker's own visual selection
                // as soon as the user picks something, independent of
                // whether the source property the binding writes to
                // actually changes — without this, the dropdown would
                // keep showing the rejected Timeline/Nexus selection even
                // though the view underneath never switched.
                RaisePropertyChanged(nameof(SelectedPreset));
                GroupedViewUnavailable?.Invoke(this, blockReason);
                return;
            }

            bool presetChanged = SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset));

            ViewMode = targetMode;

            if (ViewMode == FileListViewMode.Icons)
            {
                double presetSize = FileListViewPresets.IconSizeFor(preset);
                if (Math.Abs(presetSize - _iconSize) > 0.5)
                {
                    SetProperty(ref _iconSize, presetSize, nameof(IconSize));
                    RaiseAllZoomDerivedPropertiesChanged();
                }
            }

            if (presetChanged)
            {
                PersistViewSettingsDebounced();
            }
        }

        private void SetIconSize(double newSize)
        {
            newSize = Math.Max(32, Math.Min(256, newSize));
            if (Math.Abs(newSize - _iconSize) < 0.5)
            {
                return;
            }

            SetProperty(ref _iconSize, newSize, nameof(IconSize));
            RaiseAllZoomDerivedPropertiesChanged();

            // Keep the dropdown label tracking continuous zoom the same
            // way Explorer's view switcher does — bypassing the public
            // SelectedPreset setter here deliberately: going through it
            // would re-trigger ApplyViewPreset's mode/size logic and
            // risk a feedback loop against the zoom that's already in
            // progress.
            FileListViewPreset nearest = FileListViewPresets.NearestPresetFor(newSize);
            if (nearest != _selectedPreset)
            {
                SetProperty(ref _selectedPreset, nearest, nameof(SelectedPreset));
            }

            PersistViewSettingsDebounced();
        }

        private void PersistViewSettingsDebounced()
        {
            _viewSettingsSaveCts?.Cancel();
            var cts = new CancellationTokenSource();
            _viewSettingsSaveCts = cts;

            DebouncedPersistViewSettingsAsync(cts).Forget("DebouncedPersistViewSettingsAsync");
        }

        private async Task DebouncedPersistViewSettingsAsync(CancellationTokenSource cts)
        {
            // 400ms debounce: a Ctrl+scroll zoom gesture can fire dozens
            // of deltas per second — saving settings.json on every one
            // would mean dozens of atomic file writes per second, which
            // is wasteful and unnecessary. Only the final size after the
            // gesture settles gets persisted.
            try
            {
                await Task.Delay(400, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!ReferenceEquals(_viewSettingsSaveCts, cts))
            {
                return; // superseded by a newer zoom/preset change
            }

            FileListSettings settings = _settingsService?.Current?.FileList;
            if (settings == null)
            {
                return;
            }

            settings.DefaultViewMode = FileListViewPresets.ToSettingsString(SelectedPreset);
            settings.DefaultIconSize = IconSize;

            try
            {
                // SettingsService.SaveAsync already swallows and logs its
                // own failures internally — this try/catch is a cheap,
                // deliberately redundant extra layer, not load-bearing.
                await _settingsService.SaveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to persist view settings");
            }
        }

        public async Task LoadFolderAsync(string path, bool bypassCache = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!bypassCache || !PathUtil.AreSame(path, _currentPath ?? string.Empty))
            {
                // The person went somewhere (not the folder reloading itself after a change on disk).
                NavigationRequested?.Invoke(this, EventArgs.Empty);
            }

            HasStartedNavigation = true;

            // Captured now, before CurrentPath gets reassigned below —
            // distinguishes a genuine navigation to a different folder
            // from a same-folder refresh (F5, the live folder watcher's
            // own auto-refresh, or an external-change notification from
            // the tree's shell context menu). Only the former should
            // ever play the navigate sound — a random beep triggered by
            // a background refresh the person didn't actually click
            // anything for would be confusing, not useful feedback.
            bool isNavigatingToNewFolder = !string.Equals(CurrentPath, path, PathUtil.Comparison);
            if (isNavigatingToNewFolder)
            {
                CancelFolderSizes(); // what still counts in the folder being left stops
            }

            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            // Debounce: a folder the user is only passing through
            // (rapid clicks or arrow-keying through the tree) never
            // reaches the real load below — the delay is cancelled by
            // the next navigation before any disk access happens. Only
            // the folder the selection actually settles on pays the
            // real cost. UI state (IsLoading, etc.) is deliberately not
            // touched until after this point, so passing-through
            // folders never even flash a loading state.
            // Only a burst waits (keys held down in a list of folders, a script of navigations): each request then
            // replaces the last, and only the one that settles reads the disk. A single navigation starts at once; it
            // used to wait the debounce time every time, latency added to every folder opened.
            int debounceMs = _settingsService?.Current?.FileList?.SelectionDebounceMs ?? 80;
            long requestTicks = Environment.TickCount64;
            bool inBurst = requestTicks - _lastLoadRequestTicks < debounceMs;
            _lastLoadRequestTicks = requestTicks;
            if (debounceMs > 0 && inBurst)
            {
                try
                {
                    await Task.Delay(debounceMs, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return; // superseded before settling — nothing was touched
                }
            }

            IsLoading = true;
            ErrorMessage = null;
            SetSurface(FileListSurface.StandardFolder);
            CurrentPath = path;
            RecordArrival(HistoryEntry.Folder(path));
            StatusText = "Loading…";
            LastViewBuildFailure = null;

            // Whatever folder was being live-watched before no longer
            // applies — stopped unconditionally here (not only on
            // success below) since even a load that ends up failing
            // means we're no longer showing the PREVIOUS folder's
            // contents either. Also stops a lingering This PC Recent
            // watcher if the person is navigating here directly from
            // that page.
            StopAllLiveWatching();

            try
            {
                int networkTimeoutMs = _settingsService?.Current?.FileList?.NetworkEnumerationTimeoutMs ?? 8000;

                DirectoryIndex index;
                try
                {
                    index = await _indexService
                        .BuildIndexAsync(path, networkTimeoutMs, bypassCache: bypassCache, cancellationToken: cts.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return; // superseded by a newer navigation
                }

                if (!ReferenceEquals(_loadCts, cts))
                {
                    return; // superseded while we were awaiting
                }

                // Hidden/system exclusion and sort run on the thread pool: sorting a 100k-entry folder by a
                // non-default column takes long enough to freeze the window. The settings are captured here and
                // re-checked on the UI thread below, in case the person changed them while this ran.
                SortedViewKey loadKey = CurrentSortedViewKey;
                DirectoryIndex preparedIndex = null;
                if (!index.HasError)
                {
                    try
                    {
                        preparedIndex = await GetDisplayedIndexOrFallbackAsync(index, string.Empty, cts.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!ReferenceEquals(_loadCts, cts))
                    {
                        return;
                    }
                }

                FileListSettings fileListSettings = _settingsService?.Current?.FileList;
                int residentWindow = fileListSettings?.ResidentItemWindow ?? 500;
                int prefetchBufferSize = fileListSettings?.PrefetchBufferSize ?? 150;

                await _dispatcher.InvokeAsync(() =>
                {
                    if (index.HasError)
                    {
                        _currentIndex = null;
                        Items = null;
                        ErrorMessage = index.ErrorMessage;
                        StatusText = string.Empty;
                    }
                    else
                    {
                        _currentIndex = index;
                        _displayedIndex = index;
                        _residentWindow = residentWindow;
                        _prefetchBufferSize = prefetchBufferSize;

                        if (IsGroupedViewMode(ViewMode) && !CanUseGroupedView(ViewMode, out string groupedViewBlockReason))
                        {
                            // Falls back before the expensive
                            // Rebuild*IfNeeded calls below run, so
                            // TimelineGroups/NexusClusters are never
                            // actually built for a folder this large —
                            // both correctly no-op/null out once ViewMode
                            // no longer matches their own mode (see their
                            // own guards).
                            SetProperty(ref _selectedPreset, FileListViewPreset.Details, nameof(SelectedPreset));
                            ViewMode = FileListViewMode.Details;
                            GroupedViewUnavailable?.Invoke(this, groupedViewBlockReason + " Switched to Details view.");
                            PersistViewSettingsDebounced();
                        }

                        // A filter is scoped to the folder it was typed
                        // in — carrying it over to a newly-navigated
                        // folder would silently hide items the person
                        // never asked to hide there. SetProperty (not
                        // the FilterText property setter) specifically
                        // to avoid re-triggering the debounced filter
                        // rebuild here — Items is about to be set fresh
                        // below anyway.
                        SetProperty(ref _filterText, string.Empty, nameof(FilterText));

                        // Hidden/system exclusion and sort must be
                        // applied here too, not left to whatever happens
                        // to trigger RebuildDisplayedItemsAsync next —
                        // this was a real, confirmed bug: every fresh
                        // folder load (including the very first one at
                        // startup) built Items directly from the raw
                        // index, so ShowHiddenAndSystemItems being off
                        // had no effect until something else (a filter
                        // keystroke, the toggle itself) happened to
                        // trigger the proper filtered rebuild — and even
                        // then, the toggle's own first click looked like
                        // a no-op, since flipping the already-wrong
                        // "everything visible" state to "show hidden"
                        // changes nothing visually; only the second
                        // click, flipping back to "hide", ever ran the
                        // real filter. Building the filtered index right
                        // here, on every load, closes that gap
                        // completely rather than relying on a later
                        // trigger to correct it.
                        DirectoryIndex filteredIndex = preparedIndex;
                        if (!loadKey.Equals(CurrentSortedViewKey))
                        {
                            // The sort or a visibility setting changed while the folder was read: rare, done here at once.
                            filteredIndex = BuildFilteredAndSortedIndex(index, string.Empty, _sortColumn, _sortAscending, ShowHiddenItems, ShowSystemItems, _categoryFilter);
                        }

                        _displayedIndex = filteredIndex;

                        Items = new VirtualizingFileList(filteredIndex, residentWindow, prefetchBufferSize, _logger);
                        RebuildTimelineGroupsIfNeeded();
                        RebuildColumnPanesIfNeeded();
                        RebuildNexusClustersIfNeeded();
                        string count = $"{filteredIndex.Count:N0} item{(filteredIndex.Count == 1 ? string.Empty : "s")}";
                        StatusText = LastViewBuildFailure == null ? count : count + " · " + LastViewBuildFailure; // keep the failure visible
                    }
                }).ConfigureAwait(true);

                bool isNetworkPath = PathClassifier.IsNetworkPath(path);
                if (isNetworkPath)
                {
                    // "Timed out" is the one error message
                    // DirectoryIndexService's network path produces for
                    // this specific failure mode (see
                    // BuildIndexOverNetworkAsync) — a string match is a
                    // pragmatic way to distinguish "the network stalled"
                    // from "access denied" / "folder doesn't exist"
                    // without widening DirectoryIndex's public contract
                    // with a dedicated error-kind enum just for this.
                    bool timedOut = index.HasError &&
                        !string.IsNullOrEmpty(index.ErrorMessage) &&
                        index.ErrorMessage.IndexOf("Timed out", StringComparison.OrdinalIgnoreCase) >= 0;
                    _analyticsService.TrackNetworkOperation(timedOut);
                }

                if (!index.HasError)
                {
                    _analyticsService.TrackFolderVisited(path);

                    // SidebarFrequentFolders is bound in the view: it must change on the UI thread (it used to be
                    // refreshed from a pool thread, which threw and left the sidebar stale).
                    await _dispatcher.InvokeAsync(RefreshSidebarFrequentFolders).ConfigureAwait(true);

                    if (isNavigatingToNewFolder)
                    {
                        _soundFeedbackService?.PlayNavigate();
                    }

                    // Best-effort — see StartFolderWatchingAsync. Only
                    // attempted on a successful listing: watching a
                    // path that just failed to enumerate (access
                    // denied, doesn't exist, timed out) isn't valuable
                    // enough to justify the extra state to reason about
                    // for what would usually just fail the same way
                    // again anyway.
                    await StartFolderWatchingAsync(path, cts).ConfigureAwait(true);
                    StartAutomaticFolderSizes(path);
                }
            }
            catch (Exception ex)
            {
                // Last-resort guard: a bug anywhere in the load path
                // must surface as a friendly in-pane message, never a
                // frozen or crashed middle pane.
                _logger.Error(ex, "Unexpected error loading folder {Path}", path);
                await _dispatcher.InvokeAsync(() =>
                {
                    Items = null;
                    ErrorMessage = "Something went wrong loading this folder.";
                    StatusText = string.Empty;
                }).ConfigureAwait(false);
            }
            finally
            {
                // IsLoading re-queries commands through CommandManager, which only works on the UI thread.
                _dispatcher.Invoke(() =>
                {
                    if (ReferenceEquals(_loadCts, cts))
                    {
                        IsLoading = false;
                    }
                });
            }
        }

        /// <summary>
        /// Starts live-watching <paramref name="path"/> for changes so
        /// the file list refreshes automatically — a file/folder
        /// created, deleted, renamed, or resized by this app, the
        /// navigation tree's shell context menu, or any other program.
        /// Best-effort throughout: disabled entirely via
        /// FileList.EnableLiveFolderWatching, and even when enabled,
        /// FolderChangeWatcher itself never throws past construction —
        /// a failed watch just means this folder behaves exactly as it
        /// did before this feature existed (F5 / navigate away and back
        /// still work normally).
        /// </summary>
        private async Task StartFolderWatchingAsync(string path, CancellationTokenSource cts)
        {
            bool enabled = _settingsService?.Current?.FileList?.EnableLiveFolderWatching ?? true;
            if (!enabled || !ReferenceEquals(_loadCts, cts) || ArchiveBrowser.IsInsideArchive(path) || Phones.IsPhonePath(path))
            {
                return;
            }

            int debounceMs = _settingsService?.Current?.FileList?.LiveRefreshDebounceMs ?? 400;
            int networkTimeoutMs = _settingsService?.Current?.FileList?.NetworkEnumerationTimeoutMs ?? 8000;

            FolderChangeWatcher watcher = await FolderChangeWatcher.CreateAsync(
                path,
                directoriesOnly: false, // the file list shows both files and folders
                debounceMs,
                networkTimeoutMs,
                cts.Token,
                _logger).ConfigureAwait(true);

            // Back on the caller's (UI) thread, so this check and the assignment below can't interleave with a newer
            // navigation stopping the watcher (which used to leak a watcher that kept reloading the new folder).
            if (_disposed || !ReferenceEquals(_loadCts, cts))
            {
                // Disposed or superseded by a newer navigation while the
                // watcher was starting — don't leave it running for a
                // folder that isn't displayed anymore.
                watcher.Dispose();
                return;
            }

            watcher.Changed += OnWatchedFolderChanged;
            _folderWatcher = watcher;
        }

        private void OnWatchedFolderChanged(object sender, EventArgs e)
        {
            // Raised on a background thread (FolderChangeWatcher is
            // deliberately UI-framework-agnostic) — LoadFolderAsync
            // touches UI-bound properties synchronously before its
            // first await, so this must be marshaled onto the UI thread
            // first, exactly like every other caller of LoadFolderAsync
            // already is (they're all invoked directly from UI-thread
            // event handlers).
            try
            {
                var ignored = _dispatcher.InvokeAsync(() =>
                {
                    // bypassCache is required — otherwise the short-TTL
                    // index cache could hand back the very same stale
                    // snapshot the watcher just fired because of.
                    RefreshCurrentFolderAsync().Forget("RefreshCurrentFolderAsync");
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to marshal a live folder-change reload for {Path} — likely shutting down", CurrentPath);
            }
        }

        private void StopFolderWatching()
        {
            if (_folderWatcher != null)
            {
                _folderWatcher.Changed -= OnWatchedFolderChanged;
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }
        }

        /// <summary>
        /// Stops every live watcher this view model can hold — the
        /// regular open-folder watcher and the This PC page's Recent-
        /// folder watcher (see FileListViewModel_ThisPc.cs). Both are
        /// mutually exclusive in practice (never both active — IsThisPcView
        /// and a real CurrentPath don't coexist) but calling both
        /// unconditionally at every navigation boundary and at Dispose
        /// is simpler and safer than trying to track which one might
        /// currently be running.
        /// </summary>
        private void StopAllLiveWatching()
        {
            StopFolderWatching();
            StopRecentWatching();
        }

        /// <summary>
        /// Releases the live folder watcher and stops any in-flight
        /// load. Wired into MainViewModel.Dispose() on application
        /// shutdown — without this, a still-active FileSystemWatcher (a
        /// real OS handle) would simply leak for the lifetime of the
        /// process, however short-lived that turns out to be after the
        /// window closes.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                OnPaneLocationChangedForProtection(); // a closed tab no longer holds an item unlocked "until I leave"
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Couldn't check the protected items after closing a pane");
            }

            try
            {
                _loadCts?.Cancel();
            }
            catch
            {
                // Best-effort — the process is either fine or exiting
                // either way.
            }

            try
            {
                StopAllLiveWatching();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the folder watcher cleanly");
            }

            try
            {
                Preview?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the preview view model cleanly");
            }
        }

        /// <summary>
        /// Called (via MainViewModel, forwarding NavigationViewModel's
        /// ExternalChangeDetected event) when a shell context-menu
        /// action invoked from the navigation tree may have changed
        /// something on disk outside this app's own file-operation
        /// pipeline — a Rename/Delete/New made through the real Explorer
        /// menu on a tree node. Without this, renaming or deleting a
        /// folder from the tree while that same folder (or one nested
        /// inside it) is open in the center pane left the center pane
        /// silently showing stale contents until the person happened to
        /// navigate away and back within the cache's TTL window.
        ///
        /// Always invalidates the affected path's cache entry outright,
        /// even if it isn't the folder currently open — a stale cached
        /// listing for a folder the person isn't looking at right now is
        /// still wrong the next time they navigate into it. Only
        /// force-reloads (bypassing cache, hitting disk again) when the
        /// affected path is the folder currently open or an ancestor of
        /// it, since that's the only case where doing so is actually
        /// necessary rather than pure extra I/O for its own sake.
        /// </summary>
        public async Task NotifyExternalChangeAsync(string affectedPath)
        {
            if (string.IsNullOrEmpty(affectedPath))
            {
                return;
            }

            try
            {
                _indexService?.InvalidateCache(affectedPath);
            }
            catch (Exception ex)
            {
                // Cache invalidation must never be able to take the app
                // down over what is, worst case, a stale cache entry
                // that would have expired on its own TTL anyway.
                _logger.Debug(ex, "Failed to invalidate cache for {Path} after an external change", affectedPath);
            }

            string currentPath = CurrentPath;
            if (string.IsNullOrEmpty(currentPath) || !IsSameOrAncestorPath(affectedPath, currentPath))
            {
                return;
            }

            try
            {
                await RefreshCurrentFolderAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Best-effort refresh — if it fails for any reason, the
                // person still has whatever was on screen before; this
                // must never surface as an unhandled fault over what
                // amounts to a convenience refresh.
                _logger.Debug(ex, "Failed to refresh {Path} after an external change to {AffectedPath}", currentPath, affectedPath);
            }
        }

        /// <summary>
        /// True when <paramref name="candidateAncestor"/> is exactly
        /// <paramref name="path"/>, or a folder somewhere above it in
        /// the tree. Path comparison only — no disk access, so this is
        /// safe to call against a path that may no longer exist (e.g.
        /// the folder was just deleted via the shell menu that
        /// triggered this check in the first place).
        /// </summary>
        private static bool IsSameOrAncestorPath(string candidateAncestor, string path)
        {
            string normalizedAncestor = NormalizePathForComparison(candidateAncestor);
            string normalizedPath = NormalizePathForComparison(path);

            if (string.IsNullOrEmpty(normalizedAncestor) || string.IsNullOrEmpty(normalizedPath))
            {
                return false;
            }

            if (string.Equals(normalizedAncestor, normalizedPath, PathUtil.Comparison))
            {
                return true;
            }

            return normalizedPath.StartsWith(PathUtil.WithTrailingSeparator(normalizedAncestor), PathUtil.Comparison);
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                return PathUtil.Normalize(path);
            }
            catch (Exception)
            {
                // A malformed path can't meaningfully be compared —
                // treat it as its own, non-matching normalized form
                // rather than throwing out of a comparison helper.
                return PathUtil.TrimTrailingSeparator(path);
            }
        }


        private void RebuildColumnPanesIfNeeded()
        {
            if (ViewMode != FileListViewMode.Columns || string.IsNullOrEmpty(CurrentPath))
            {
                ColumnPanes = null;
                FocusedColumnPane = null;
                return;
            }

            // Seed with a single column for the current (root) folder —
            // deeper columns are pushed asynchronously as the user
            // drills down (see OnColumnSelectionChangedAsync). Always
            // resets to just the root: this only ever runs on entering
            // Columns view or navigating to a new root folder, either
            // of which legitimately invalidates whatever was drilled
            // into before. A filter-text change does NOT come through
            // here — see RebuildFocusedColumnFilterAsync, which
            // re-filters only the focused column in place instead.
            var panes = new System.Collections.ObjectModel.ObservableCollection<ColumnPane>();
            ColumnPane root = BuildRootColumnPane();
            panes.Add(root);
            ColumnPanes = panes;
            FocusedColumnPane = root;
        }

        /// <summary>
        /// Builds the root column by reusing whatever DirectoryIndex the
        /// main pane already has in memory — the filtered/displayed one
        /// when it matches the current folder, so the status-bar filter
        /// applies to the root column exactly the way it already does
        /// for Details/Icons/List/Tiles — rather than re-enumerating or
        /// awaiting anything, since this data is already resident by
        /// the time this is ever called. Synchronous and cheap.
        /// </summary>
        private ColumnPane BuildRootColumnPane()
        {
            DirectoryIndex indexForPane = (_displayedIndex != null && !_displayedIndex.HasError &&
                string.Equals(_displayedIndex.ParentPath, CurrentPath, PathUtil.Comparison))
                ? _displayedIndex
                : _currentIndex;

            return BuildColumnPaneFromIndex(CurrentPath, indexForPane);
        }

        /// <summary>
        /// Builds a drill-down column for a subfolder the user selected
        /// — goes through the same native, indexed enumeration
        /// (IDirectoryIndexService, backed by FindFirstFileEx) every
        /// other view in this app uses, on a background thread, instead
        /// of the ad hoc synchronous DirectoryInfo.EnumerateDirectories/
        /// EnumerateFiles loop this used to run directly on the UI
        /// thread — an unguarded blocking-I/O risk for any subfolder
        /// with a large number of items, on top of never producing a
        /// VirtualizingFileList (every entry became a fully-constructed
        /// FileListItemViewModel unconditionally, for the whole
        /// subfolder, no matter how large).
        /// </summary>
        private async Task<ColumnPane> BuildDrillDownColumnPaneAsync(string path, bool bypassCache = false)
        {
            int networkTimeoutMs = _settingsService?.Current?.FileList?.NetworkEnumerationTimeoutMs ?? 8000;

            DirectoryIndex index;
            try
            {
                index = await _indexService
                    .BuildIndexAsync(path, networkTimeoutMs, bypassCache: bypassCache, cancellationToken: default)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to build column index for {Path}", path);
                return null;
            }

            return BuildColumnPaneFromIndex(path, index);
        }

        private ColumnPane BuildColumnPaneFromIndex(string path, DirectoryIndex index)
        {
            string header = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(header))
            {
                header = path; // drive root
            }

            if (index == null || index.HasError)
            {
                // An empty, valid (not null) index rather than surfacing
                // an error UI inside one column of a multi-column
                // browser — the folder simply appears empty, a
                // reasonable, low-drama degradation for something as
                // secondary as one drill-down column failing to
                // enumerate (permissions, a race with deletion, etc.)
                // rather than disrupting the whole Columns view over it.
                index = new DirectoryIndex(path, new List<DirectoryIndexEntry>(), false, null);
            }

            var virtualizingList = new VirtualizingFileList(index, _residentWindow, _prefetchBufferSize, _logger);
            return new ColumnPane(path, header, index, virtualizingList);
        }

        /// <summary>
        /// Re-filters/sorts only the currently-focused column from its
        /// own SourceIndex, replacing just that pane's entry in
        /// ColumnPanes in place — sibling columns, to either side, are
        /// left completely untouched. Called from RebuildDisplayedItemsAsync
        /// instead of RebuildColumnPanesIfNeeded specifically because a
        /// filter-text keystroke must not destroy every drilled-down
        /// column the person has open just to apply to the one they're
        /// actually looking at.
        /// </summary>
        private async Task RebuildFocusedColumnFilterAsync(string filter, FileListSortColumn sortColumn, bool sortAscending)
        {
            ColumnPane target = FocusedColumnPane;
            if (target == null || ColumnPanes == null)
            {
                return;
            }

            int index = ColumnPanes.IndexOf(target);
            if (index < 0)
            {
                return;
            }

            DirectoryIndex filtered;
            try
            {
                filtered = await Task.Run(() => BuildFilteredAndSortedIndex(target.SourceIndex, filter, sortColumn, sortAscending, ShowHiddenItems, ShowSystemItems, _categoryFilter)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ReportViewBuildFailure(ex, $"sort or filter the column {PathUtil.GetName(target.Path)}", "It keeps its previous order.");
                return;
            }

            // Re-validate after the await — the collection, or this
            // pane's position within it, may have changed (a folder
            // navigation, a view-mode switch) while filtering ran.
            int currentIndex = ColumnPanes?.IndexOf(target) ?? -1;
            if (currentIndex < 0)
            {
                return;
            }

            var replacement = new ColumnPane(
                target.Path,
                target.Header,
                target.SourceIndex,
                new VirtualizingFileList(filtered, _residentWindow, _prefetchBufferSize, _logger));

            ColumnPanes[currentIndex] = replacement;

            if (ReferenceEquals(FocusedColumnPane, target))
            {
                FocusedColumnPane = replacement;
            }
        }

        /// <summary>
        /// Called from Columns view's code-behind whenever a column's
        /// selection changes — Ctrl/Shift-click multi-select all reach
        /// here now, not just a single click the way this used to only
        /// support. Marks this column as focused (see FocusedColumnPane),
        /// mirrors its selection into the app's global SelectedItems so
        /// every file-operation command and title-bar tool operates on
        /// it, and — only when exactly one directory is selected —
        /// drills down into a new column, dropping any columns beyond
        /// this one that belonged to a since-changed selection.
        /// </summary>
        public async Task OnColumnSelectionChangedAsync(ColumnPane pane, IReadOnlyList<FileListItemViewModel> selectedItems)
        {
            if (ColumnPanes == null || pane == null)
            {
                return;
            }

            int index = ColumnPanes.IndexOf(pane);
            if (index < 0)
            {
                return;
            }

            pane.SelectedItems.Clear();
            foreach (FileListItemViewModel item in selectedItems ?? Array.Empty<FileListItemViewModel>())
            {
                pane.SelectedItems.Add(item);
            }

            FocusedColumnPane = pane;

            // Mirror into the app-wide selection so every command,
            // title-bar tool, and keyboard shortcut (Delete, Ctrl+C,
            // Ctrl+X, F2, ...) operates on whatever's selected here,
            // exactly as if this were Details or Icons view.
            UpdateSelection(pane.SelectedItems);
            SelectedItem = pane.SelectedItems.Count > 0 ? pane.SelectedItems[0] : null;

            // Drop columns to the right of this one — a fresh selection
            // here always invalidates whatever was drilled into before,
            // regardless of what the new selection turns out to be.
            while (ColumnPanes.Count > index + 1)
            {
                ColumnPanes.RemoveAt(ColumnPanes.Count - 1);
            }

            // Drilling down only makes sense for a single selected
            // directory — ambiguous with multiple items selected, and
            // meaningless for a file.
            if (pane.SelectedItems.Count == 1 && pane.SelectedItems[0].IsDirectory)
            {
                string childPath = pane.SelectedItems[0].FullPath;
                ColumnPane child = await BuildDrillDownColumnPaneAsync(childPath).ConfigureAwait(true);

                // Re-validate after the await — this pane's position
                // (or its very presence in ColumnPanes) may have
                // changed while the drill-down was building, if the
                // user selected something else, navigated away, or
                // switched view mode before this finished.
                int currentIndex = ColumnPanes?.IndexOf(pane) ?? -1;
                if (child != null && currentIndex >= 0 && ColumnPanes.Count == currentIndex + 1)
                {
                    ColumnPanes.Add(child);
                }
            }
        }

        /// <summary>
        /// Called from Columns view's code-behind when a column's
        /// ListBox gains keyboard focus without its selection itself
        /// changing (Tab-ing between columns, clicking an
        /// already-selected item) — still needs to update
        /// FocusedColumnPane so the status bar and EffectiveTargetPath
        /// track the column the person is now looking at.
        /// </summary>
        public void OnColumnFocused(ColumnPane pane)
        {
            if (pane != null && ColumnPanes != null && ColumnPanes.Contains(pane))
            {
                FocusedColumnPane = pane;
            }
        }

        /// <summary>
        /// The single place every file-operation method (Paste, New
        /// Folder, ...) goes to refresh after changing affectedPath's
        /// contents. When affectedPath is the root folder (true outside
        /// Columns view, and inside it whenever the root column is the
        /// one focused), this is the existing, full LoadFolderAsync
        /// refresh — correct, since the root itself changed and
        /// everything downstream (including any drilled-down columns)
        /// is invalidated by that anyway. When affectedPath is a
        /// drilled-down column instead, only that one column is
        /// refreshed in place — LoadFolderAsync would reload the root
        /// (which never changed) and, via its own RebuildColumnPanesIfNeeded
        /// call, destroy every drilled-down column the person has open
        /// just to refresh one that has nothing to do with what changed.
        /// </summary>
        private async Task RefreshAfterFileOperationAsync(string affectedPath)
        {
            FolderSizes.Invalidate(affectedPath); // a rename, a new folder or file changed what the folders above contain
            bool isRootPath = string.Equals(affectedPath, CurrentPath, PathUtil.Comparison);

            if (isRootPath || ViewMode != FileListViewMode.Columns)
            {
                await RefreshCurrentFolderAsync().ConfigureAwait(true);
                return;
            }

            await RefreshColumnAsync(affectedPath).ConfigureAwait(true);
        }

        /// <summary>
        /// Rebuilds one specific column's contents from a fresh,
        /// cache-bypassing index build, replacing just that pane's
        /// entry in ColumnPanes in place — every other column, to
        /// either side, is left completely untouched.
        /// </summary>
        private async Task RefreshColumnAsync(string path)
        {
            if (ColumnPanes == null)
            {
                return;
            }

            ColumnPane existing = null;
            foreach (ColumnPane candidate in ColumnPanes)
            {
                if (string.Equals(candidate.Path, path, PathUtil.Comparison))
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing == null)
            {
                return;
            }

            _indexService?.InvalidateCache(path);
            ColumnPane refreshed = await BuildDrillDownColumnPaneAsync(path, bypassCache: true).ConfigureAwait(true);

            // Re-validate after the await — the column may have been
            // dropped (a fresh selection elsewhere collapsed it) while
            // the refresh was in flight.
            int currentIndex = ColumnPanes?.IndexOf(existing) ?? -1;
            if (refreshed == null || currentIndex < 0)
            {
                return;
            }

            ColumnPanes[currentIndex] = refreshed;

            if (ReferenceEquals(FocusedColumnPane, existing))
            {
                FocusedColumnPane = refreshed;
            }
        }

        /// <summary>
        /// Display metadata for one Nexus category. A record rather than the
        /// original raw tuple specifically so the fields are named at the
        /// construction site — the previous d[0]/d[1]/d[2]/d[3] indexing
        /// doesn't compile on a ValueTuple, and named fields make the
        /// argument order to NexusCluster's constructor self-evident.
        /// </summary>
        private sealed record NexusCategoryDef(
            string Key,
            string Title,
            ByteExplorer.Icons.Symbol Glyph,
            string AccentBrushKey);

        private static readonly NexusCategoryDef[] NexusCategoryDefs =
        {
            new("folders",   "Folders",   ByteExplorer.Icons.Symbol.Folder,   "FolderYellow"),
            new("images",    "Images",    ByteExplorer.Icons.Symbol.Image,    "FolderBlue"),
            new("media",     "Media",     ByteExplorer.Icons.Symbol.Video,    "MusicPink"),
            new("documents", "Documents", ByteExplorer.Icons.Symbol.Document, "FolderBlueLid"),
            new("code",      "Code",      ByteExplorer.Icons.Symbol.Code,     "VideoPurple"),
            new("archives",  "Archives",  ByteExplorer.Icons.Symbol.Archive,  "FolderGreen"),
            new("other",     "Other",     ByteExplorer.Icons.Symbol.Apps,     "IconGray"),
};

        private void RebuildNexusClustersIfNeeded()
        {
            DirectoryIndex source = _displayedIndex ?? _currentIndex;
            if (ViewMode != FileListViewMode.Nexus || source == null || source.HasError)
            {
                NexusClusters = null;
                return;
            }

            NexusClusters = new System.Collections.ObjectModel.ObservableCollection<NexusCluster>(
                BuildNexusClusters(source));
        }

        private static System.Collections.Generic.List<NexusCluster> BuildNexusClusters(
            Core.Models.DirectoryIndex index)
        {
            // One list + running byte total per category, keyed by the same
            // category keys used in NexusCategoryDefs and returned by
            // ClassifyForNexus.
            var buckets = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FileListItemViewModel>>(
                System.StringComparer.Ordinal);
            var sizes = new System.Collections.Generic.Dictionary<string, long>(
                System.StringComparer.Ordinal);

            foreach (NexusCategoryDef def in NexusCategoryDefs)
            {
                buckets[def.Key] = new System.Collections.Generic.List<FileListItemViewModel>();
                sizes[def.Key] = 0;
            }

            string parent = index.ParentPath;
            foreach (Core.Models.DirectoryIndexEntry entry in index.Entries)
            {
                var vm = new FileListItemViewModel(entry, parent);
                string key = ClassifyForNexus(entry);

                // ClassifyForNexus only ever returns one of the seven keys
                // defined in NexusCategoryDefs ("other" is its fallback), so
                // this lookup is always safe.
                buckets[key].Add(vm);

                if (!entry.IsDirectory)
                {
                    sizes[key] += entry.SizeBytes;
                }
            }

            var result = new System.Collections.Generic.List<NexusCluster>();
            foreach (NexusCategoryDef def in NexusCategoryDefs)
            {
                System.Collections.Generic.List<FileListItemViewModel> list = buckets[def.Key];
                if (list.Count == 0)
                {
                    continue;
                }

                result.Add(new NexusCluster(
                    def.Key,
                    def.Title,
                    def.Glyph,
                    def.AccentBrushKey,
                    list,
                    sizes[def.Key]));
            }

            return result;
        }

        private static string ClassifyForNexus(Core.Models.DirectoryIndexEntry entry)
        {
            if (entry.IsDirectory) return "folders";

            string ext = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                case ".svg":
                case ".ico":
                case ".tif":
                case ".tiff":
                    return "images";
                case ".mp3":
                case ".wav":
                case ".flac":
                case ".aac":
                case ".ogg":
                case ".mp4":
                case ".mkv":
                case ".avi":
                case ".mov":
                case ".wmv":
                case ".webm":
                case ".m4a":
                    return "media";
                case ".pdf":
                case ".doc":
                case ".docx":
                case ".xls":
                case ".xlsx":
                case ".ppt":
                case ".pptx":
                case ".txt":
                case ".rtf":
                case ".odt":
                case ".csv":
                case ".md":
                    return "documents";
                case ".cs":
                case ".js":
                case ".ts":
                case ".py":
                case ".cpp":
                case ".c":
                case ".h":
                case ".java":
                case ".go":
                case ".rs":
                case ".html":
                case ".css":
                case ".json":
                case ".xml":
                case ".yml":
                case ".yaml":
                case ".sql":
                case ".sh":
                case ".ps1":
                case ".vb":
                    return "code";
                case ".zip":
                case ".rar":
                case ".7z":
                case ".tar":
                case ".gz":
                case ".bz2":
                case ".iso":
                case ".cab":
                    return "archives";
                default:
                    return "other";
            }
        }



        /// <summary>
        /// Advances to the next named view preset in FileListViewPresets.All,
        /// wrapping to the first after the last. Used by the status-bar
        /// cycle button.
        /// </summary>
        public void CycleViewPreset()
        {
            var all = FileListViewPresets.All;
            if (all == null || all.Count == 0)
            {
                return;
            }

            int index = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == SelectedPreset)
                {
                    index = i;
                    break;
                }
            }

            int next = (index + 1) % all.Count;
            SelectedPreset = all[next];
        }

        private readonly ArchiveService _archiveService = new ArchiveService();

        /// <summary>"Compress to ZIP file": zips the items next to the first one, then selects nothing and refreshes.</summary>
        public Task CompressToZipAsync(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return Task.CompletedTask;
            }

            string destination = ArchiveService.SuggestArchivePath(paths);
            return RunArchiveOperationAsync(
                (progress, token) => _archiveService.CompressAsync(paths, destination, progress, token),
                PathUtil.GetParent(destination),
                created => $"Created {PathUtil.GetName(created)}",
                "Couldn't create the ZIP file.",
                $"Compress to {PathUtil.GetName(destination)}");
        }

        /// <summary>
        /// "Extract here" (<paramref name="destinationParent"/> null) and "Extract all…" (a chosen folder): unpacks an
        /// archive (ZIP, TAR, or what an installed archiver reads) into a new folder named after it.
        /// </summary>
        public Task ExtractArchiveAsync(string archivePath, string destinationParent = null)
        {
            if (!ArchiveService.CanExtract(archivePath))
            {
                return Task.CompletedTask;
            }

            string parent = destinationParent ?? PathUtil.GetParent(archivePath);
            return RunArchiveOperationAsync(
                (progress, token) => _archiveService.ExtractAsync(archivePath, destinationParent, progress, token),
                parent,
                folder => $"Extracted to {PathUtil.GetName(folder)}",
                $"Couldn't extract \"{PathUtil.GetName(archivePath)}\".",
                $"Extract {PathUtil.GetName(archivePath)}");
        }

        /// <summary>Archive operations run as queue jobs (progress, cancel, undo of what they created) with their own messages.</summary>
        private async Task RunArchiveOperationAsync(Func<IProgress<string>, CancellationToken, Task<string>> operation, string refreshFolder,
            Func<string, string> successText, string failureText, string title)
        {
            if (Operations == null)
            {
                return;
            }

            try
            {
                string result = await Operations.RunCreatingAsync(title, title, operation).ConfigureAwait(true);
                StatusText = successText(result);
                if (!string.IsNullOrEmpty(refreshFolder))
                {
                    _indexService.InvalidateCache(refreshFolder);
                    await RefreshAfterFileOperationAsync(refreshFolder).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is NotSupportedException)
            {
                _logger.Warning(ex, "Archive operation failed");
                OperationFailed?.Invoke(this, failureText + "\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        /// <summary>
        /// Redo of "New folder" / "New text document": creates the same item at the same path again (CreateNew for the
        /// file, so nothing that appeared there since is opened or truncated) and records it for undo.
        /// </summary>
        private async Task RecreateAsync(string path, bool isFolder, string description)
        {
            string parent = PathUtil.GetParent(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                OperationFailed?.Invoke(this, $"Couldn't redo \"{description}\": \"{PathUtil.GetName(path)}\" already exists.");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    if (isFolder)
                    {
                        Directory.CreateDirectory(path);
                    }
                    else
                    {
                        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                        }
                    }
                }).ConfigureAwait(true);

                Operations?.Record(new FileOperationRecord(description, new UndoAction[] { new CreatedAction(path) }, new[] { parent })
                {
                    Redo = () => RecreateAsync(path, isFolder, description)
                });
                _indexService.InvalidateCache(parent);
                await RefreshAfterFileOperationAsync(parent).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Redo of {Description} failed for {Path}", description, path);
                OperationFailed?.Invoke(this, $"Couldn't redo \"{description}\".\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        /// <summary>
        /// Creates an empty text file with a free name. FileMode.CreateNew makes "pick a free name" and "create" one
        /// atomic step: if another program takes the name in between, the next number is tried instead of the file
        /// being opened or truncated.
        /// </summary>
        private Task CreateNewTextFileAsync() =>
            CreateNewFileAsync("New text document", ".txt", null, "New text document");

        /// <summary>
        /// The New menu's file kinds (a Word document, a Compressed (zipped) Folder, a Templates folder's file): a new file
        /// with a free name in the open folder, from the kind's template, recorded for undo (and redo, with the same
        /// contents). Not on phones or inside ZIP files.
        /// </summary>
        public Task CreateNewFromTemplateAsync(NewItemTemplate template)
        {
            if (template == null)
            {
                return Task.CompletedTask;
            }

            return CreateNewFileAsync(template.BaseName, template.Extension, template.WriteContent, "New " + template.DisplayName);
        }

        /// <summary>Whether the New menu's file kinds can be made here (a folder on a drive, not a phone or a ZIP file).</summary>
        public bool CanCreateFromTemplates => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive && !Phones.IsPhonePath(EffectiveTargetPath);

        /// <summary>
        /// Creates a file with a free name ("base.ext", "base (2).ext"…) and the contents <paramref name="write"/> puts in
        /// it (none: empty). FileMode.CreateNew makes "pick a free name" and "create" one atomic step: if another program
        /// takes the name in between, the next number is tried instead of the file being opened or truncated. A file left
        /// half-written by a failing template is removed.
        /// </summary>
        private async Task CreateNewFileAsync(string baseName, string extension, Action<Stream> write, string description)
        {
            string targetPath = EffectiveTargetPath;
            if (Phones.IsPhonePath(targetPath))
            {
                if (write == null && extension == ".txt")
                {
                    await CreateOnPhoneAsync(targetPath, isFolder: false).ConfigureAwait(true);
                }
                else
                {
                    OperationFailed?.Invoke(this, "Only folders and text documents can be made on a phone.");
                }

                return;
            }

            if (string.IsNullOrEmpty(targetPath) || IsInsideArchive || !Directory.Exists(targetPath))
            {
                return;
            }

            try
            {
                string created = await Task.Run(() => WriteNewFile(targetPath, baseName, extension, write)).ConfigureAwait(true);
                Operations?.Record(new FileOperationRecord(description, new UndoAction[] { new CreatedAction(created) }, new[] { targetPath })
                {
                    Redo = () => RecreateFileAsync(created, write, description)
                });
                _indexService.InvalidateCache(targetPath);
                await RefreshAfterFileOperationAsync(targetPath).ConfigureAwait(true);
                StatusText = description + " created";
                _logger.Information("Created {Path}", created);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Failed to create a new file in {Path}", targetPath);
                OperationFailed?.Invoke(this, "Couldn't create the new file.\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        private static string WriteNewFile(string folder, string baseName, string extension, Action<Stream> write)
        {
            for (int suffix = 1; suffix < 10000; suffix++)
            {
                string name = (suffix == 1 ? baseName : $"{baseName} ({suffix})") + extension;
                string candidate = Path.Combine(folder, name);
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                FileStream stream;
                try
                {
                    stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    continue; // taken in the meantime: try the next number
                }

                try
                {
                    using (stream)
                    {
                        write?.Invoke(stream);
                    }
                }
                catch
                {
                    try
                    {
                        File.Delete(candidate);
                    }
                    catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException)
                    {
                    }

                    throw;
                }

                return candidate;
            }

            throw new IOException("No free name was found for the new file.");
        }

        /// <summary>Redo of a new file: the same file at the same path, with the same contents, if the name is still free.</summary>
        private async Task RecreateFileAsync(string path, Action<Stream> write, string description)
        {
            string parent = PathUtil.GetParent(path);
            if (File.Exists(path) || Directory.Exists(path))
            {
                OperationFailed?.Invoke(this, $"Couldn't redo \"{description}\": \"{PathUtil.GetName(path)}\" already exists.");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    write?.Invoke(stream);
                }).ConfigureAwait(true);
                Operations?.Record(new FileOperationRecord(description, new UndoAction[] { new CreatedAction(path) }, new[] { parent })
                {
                    Redo = () => RecreateFileAsync(path, write, description)
                });
                _indexService.InvalidateCache(parent);
                await RefreshAfterFileOperationAsync(parent).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Redo of {Description} failed for {Path}", description, path);
                OperationFailed?.Invoke(this, $"Couldn't redo \"{description}\".\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        private async Task CreateNewFolderAsync()
        {
            string targetPath = EffectiveTargetPath;
            if (string.IsNullOrEmpty(targetPath))
            {
                return;
            }

            if (Phones.IsPhonePath(targetPath))
            {
                await CreateOnPhoneAsync(targetPath, isFolder: true).ConfigureAwait(true);
                return;
            }

            // Recycle Bin and other virtual shell folders cannot host new directories.
            if (targetPath.StartsWith("::", StringComparison.Ordinal) ||
                targetPath.IndexOf("shell:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                OperationFailed?.Invoke(this, "Can't create a new folder here.");
                return;
            }

            string baseName = "New folder";
            string candidate = Path.Combine(targetPath, baseName);
            int suffix = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(targetPath, string.Format("{0} ({1})", baseName, suffix++));
            }

            try
            {
                await Task.Run(() => Directory.CreateDirectory(candidate)).ConfigureAwait(true);
                Operations?.Record(new FileOperationRecord("New folder",
                    new UndoAction[] { new CreatedAction(candidate) }, new[] { targetPath })
                {
                    Redo = () => RecreateAsync(candidate, isFolder: true, "New folder")
                });

                // Invalidate index cache so the new folder appears immediately.
                _indexService.InvalidateCache(targetPath);
                await RefreshAfterFileOperationAsync(targetPath).ConfigureAwait(true);
                StatusText = "New folder created";
                _analyticsService.TrackFolderCreated();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to create folder in {Path}", targetPath);
                OperationFailed?.Invoke(this, "Couldn't create the new folder.\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        /// <summary>
        /// Where dialogs come from (set by MainViewModel; the view implements it). Null in tests or before the window
        /// exists, in which case commands that need an answer do nothing.
        /// </summary>
        public IDialogService DialogService { get; set; }

        /// <summary>
        /// Renames the single selected item. The name is checked with the OS's own rules (FileNameRules) before any I/O.
        /// A change of letter case only ("report.txt" to "Report.txt") is a real rename everywhere: on a case-sensitive
        /// file system it is a different name, and on Windows/macOS Explorer and Finder allow it too. The WPF build
        /// rejected it as "no change" and, had it got through, would have reported "already exists" (the case-insensitive
        /// lookup finds the item itself). On a case-insensitive file system a folder goes through a temporary name, since
        /// Directory.Move refuses source == destination there.
        /// </summary>
        /// <summary>
        /// Set by the view: edits an item's name in place (Explorer's F2) and returns the new name, null when cancelled,
        /// or <see cref="InlineRenameUnavailable"/> when the item cannot be edited in place (not on screen, a view without
        /// in-place editing), in which case the rename dialog is used.
        /// </summary>
        public Func<FileListItemViewModel, Task<string>> InlineRenamer { get; set; }

        /// <summary>Returned by <see cref="InlineRenamer"/> to fall back to the rename dialog.</summary>
        public const string InlineRenameUnavailable = "\0";

        /// <summary>One item: renamed in place (or in the rename dialog). Several: the "Rename items" dialog (bulk rename).</summary>
        private async Task RenameSelectedAsync()
        {
            if (SelectedItems.Count > 1)
            {
                await RenameSeveralAsync().ConfigureAwait(true);
                return;
            }

            if (SelectedItems.Count != 1)
            {
                return;
            }

            FileListItemViewModel item = SelectedItems[0];
            string newName = InlineRenamer != null ? await InlineRenamer(item).ConfigureAwait(true) : InlineRenameUnavailable;
            if (newName == InlineRenameUnavailable)
            {
                if (DialogService == null)
                {
                    return;
                }

                newName = await DialogService.PromptRenameAsync(item.Name, item.IsDirectory).ConfigureAwait(true);
            }

            await RenameItemAsync(item, newName).ConfigureAwait(true);
        }

        /// <summary>Renames an item to a name the person chose (dialog or in place); null or the same name does nothing.</summary>
        public Task RenameItemAsync(FileListItemViewModel item, string newName) =>
            item == null ? Task.CompletedTask : RenamePathAsync(item.FullPath, item.IsDirectory, newName);

        /// <summary>
        /// Asks for a new name for a file or folder that is not (necessarily) in the list, e.g. a folder in the
        /// navigation tree (F2 there), and renames it.
        /// </summary>
        public async Task PromptRenamePathAsync(string path)
        {
            if (string.IsNullOrEmpty(path) || DialogService == null || Path.GetDirectoryName(PathUtil.TrimTrailingSeparator(path)) == null)
            {
                return; // a drive or volume root has no name to change here
            }

            bool isDirectory = Directory.Exists(path);
            string newName = await DialogService.PromptRenameAsync(PathUtil.GetName(path), isDirectory).ConfigureAwait(true);
            await RenamePathAsync(path, isDirectory, newName).ConfigureAwait(true);
        }

        /// <summary>
        /// Renames a file or folder in place: validates the name, refuses to replace an existing item, handles a
        /// case-only folder rename on a case-insensitive disk, records the undo step and refreshes the parent. When the
        /// pane shows the renamed folder (or a folder inside it), it follows to the new path.
        /// </summary>
        public async Task RenamePathAsync(string fullPath, bool isDirectory, string newName)
        {
            if (string.IsNullOrEmpty(fullPath) || newName == null)
            {
                return;
            }

            fullPath = PathUtil.TrimTrailingSeparator(fullPath);
            string oldName = PathUtil.GetName(fullPath);
            newName = newName.Trim();
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                return;
            }

            string problem = FileNameRules.Validate(newName);
            if (problem != null)
            {
                OperationFailed?.Invoke(this, problem);
                return;
            }

            if (Phones.IsPhonePath(fullPath))
            {
                await RenameOnPhoneAsync(fullPath, oldName, newName).ConfigureAwait(true);
                return;
            }

            string parent = Path.GetDirectoryName(fullPath) ?? CurrentPath;
            string destination = Path.Combine(parent, newName);
            bool caseOnly = string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase);

            if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
            {
                OperationFailed?.Invoke(this, "A file or folder with that name already exists.");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    if (!isDirectory)
                    {
                        // overwrite: false — a file created at the destination after the check above is never replaced.
                        File.Move(fullPath, destination, overwrite: false);
                    }
                    else if (caseOnly && PathUtil.Comparison == StringComparison.OrdinalIgnoreCase)
                    {
                        string temporary = Path.Combine(parent, "." + newName + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".renaming");
                        Directory.Move(fullPath, temporary);
                        try
                        {
                            Directory.Move(temporary, destination);
                        }
                        catch (Exception renameEx)
                        {
                            try
                            {
                                Directory.Move(temporary, fullPath); // put it back under its old name
                            }
                            catch (Exception putBackEx) when (putBackEx is IOException || putBackEx is UnauthorizedAccessException)
                            {
                                // Never leave it silently under the temporary name: say where it is.
                                throw new IOException($"The rename failed ({renameEx.Message}), and the folder couldn't get its old name back: " +
                                                      $"it is now \"{temporary}\". Rename it there.", putBackEx);
                            }

                            throw;
                        }
                    }
                    else
                    {
                        Directory.Move(fullPath, destination);
                    }
                }).ConfigureAwait(true);

                string renamedTo = newName;
                Operations?.Record(new FileOperationRecord($"Rename \"{oldName}\" to \"{newName}\"",
                    new UndoAction[] { new MovedAction(fullPath, destination) }, new[] { parent })
                {
                    // Redo renames it again, with the same checks (refused if the new name was taken since).
                    Redo = () => RenamePathAsync(fullPath, Directory.Exists(fullPath), renamedTo)
                });
                _indexService.InvalidateCache(parent);
                string shown = CurrentPath;
                if (isDirectory && shown != null && PathUtil.IsSameOrInside(shown, fullPath))
                {
                    await LoadFolderAsync(destination + shown.Substring(fullPath.Length)).ConfigureAwait(true);
                }
                else
                {
                    await RefreshAfterFileOperationAsync(parent).ConfigureAwait(true);
                }

                StatusText = "Renamed successfully";
                _analyticsService.TrackItemRenamed(success: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Rename failed for {Path}", fullPath);
                _analyticsService.TrackItemRenamed(success: false);
                OperationFailed?.Invoke(this, "Couldn't rename \"" + oldName + "\".\n\n" + Infrastructure.FriendlyErrors.Explain(ex));
            }
        }

        private void RebuildTimelineGroupsIfNeeded()
        {
            DirectoryIndex source = _displayedIndex ?? _currentIndex;
            if (ViewMode != FileListViewMode.Timeline || source == null || source.HasError)
            {
                TimelineGroups = null;
                return;
            }

            var groups = BuildTimelineGroups(source);
            TimelineGroups = new System.Collections.ObjectModel.ObservableCollection<TimelineGroup>(groups);
        }

        private static System.Collections.Generic.List<TimelineGroup> BuildTimelineGroups(
            Core.Models.DirectoryIndex index)
        {
            var nowLocal = System.DateTime.Now;
            var today = nowLocal.Date;
            var yesterday = today.AddDays(-1);

            int diff = (7 + (today.DayOfWeek - System.DayOfWeek.Monday)) % 7;
            var startOfWeek = today.AddDays(-diff);
            var startOfLastWeek = startOfWeek.AddDays(-7);
            var startOfMonth = new System.DateTime(today.Year, today.Month, 1);

            var buckets = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FileListItemViewModel>>(System.StringComparer.Ordinal)
            {
                ["Today"] = new System.Collections.Generic.List<FileListItemViewModel>(),
                ["Yesterday"] = new System.Collections.Generic.List<FileListItemViewModel>(),
                ["Earlier this week"] = new System.Collections.Generic.List<FileListItemViewModel>(),
                ["Last week"] = new System.Collections.Generic.List<FileListItemViewModel>(),
                ["Earlier this month"] = new System.Collections.Generic.List<FileListItemViewModel>(),
                ["Older"] = new System.Collections.Generic.List<FileListItemViewModel>(),
            };

            string parentPath = index.ParentPath;

            foreach (var entry in index.Entries)
            {
                var vm = new FileListItemViewModel(entry, parentPath);
                System.DateTime local = entry.LastWriteTimeUtc == System.DateTime.MinValue
                    ? System.DateTime.MinValue
                    : entry.LastWriteTimeUtc.ToLocalTime().Date;

                if (local >= today)
                    buckets["Today"].Add(vm);
                else if (local >= yesterday)
                    buckets["Yesterday"].Add(vm);
                else if (local >= startOfWeek)
                    buckets["Earlier this week"].Add(vm);
                else if (local >= startOfLastWeek)
                    buckets["Last week"].Add(vm);
                else if (local >= startOfMonth)
                    buckets["Earlier this month"].Add(vm);
                else
                    buckets["Older"].Add(vm);
            }

            var result = new System.Collections.Generic.List<TimelineGroup>();
            foreach (var key in new[] { "Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Older" })
            {
                var list = buckets[key];
                if (list.Count > 0)
                {
                    result.Add(new TimelineGroup(key, list));
                }
            }

            return result;
        }

    }
}
