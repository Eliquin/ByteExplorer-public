// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Owns the left navigation pane: drive detection and the root of
    /// the folder tree. Everything here runs off the UI thread except
    /// the final, dispatcher-marshaled writes into the observable
    /// collections that the TreeView binds to.
    /// </summary>
    public sealed class NavigationViewModel : ViewModelBase, IDisposable
    {
        private readonly IDriveEnumerationService _driveService;
        private readonly IFileSystemNavigationService _navigationService;
        private readonly ISettingsService _settingsService;
        private readonly IUiDispatcher _dispatcher;
        private readonly ISoundFeedbackService _soundFeedbackService;
        private readonly ILogger _logger;
        private Timer _driveRefreshTimer;
        private NavigationNodeViewModel _thisPcNode;
        private WatcherBudget _watcherBudget;
        private int _refreshInProgress; // 0 = idle, 1 = a refresh tick is running (Interlocked-guarded)
        private volatile bool _disposed;

        private const int DriveRefreshIntervalMs = 3000;

        public NavigationViewModel(
            IDriveEnumerationService driveService,
            IFileSystemNavigationService navigationService,
            ISettingsService settingsService,
            IUiDispatcher dispatcher,
            ISoundFeedbackService soundFeedbackService = null,
            ILogger logger = null)
        {
            _driveService = driveService;
            _navigationService = navigationService;
            _settingsService = settingsService;
            _dispatcher = dispatcher;
            _soundFeedbackService = soundFeedbackService;
            _logger = (logger ?? Log.Logger).ForContext<NavigationViewModel>();
        }

        public ObservableCollection<NavigationNodeViewModel> RootNodes { get; } = new ObservableCollection<NavigationNodeViewModel>();

        /// <summary>The tree flattened to its visible rows, for the virtualizing navigation pane (see NavigationTreeRows).</summary>
        public NavigationTreeRows VisibleRows => _visibleRows ??= new NavigationTreeRows(RootNodes);

        private NavigationTreeRows _visibleRows;

        private NavigationNodeViewModel _selectedNode;

        /// <summary>
        /// TreeView.SelectedItem is read-only in WPF, so there's no
        /// direct two-way binding possible — this property is the
        /// accepted bridge point, set from NavigationPaneView's
        /// code-behind on SelectedItemChanged. MainViewModel no longer
        /// listens to this directly (see NodeActivated's remarks) — it
        /// exists for the code-behind bridge and for RefreshDrivesAsync's
        /// own fallback-to-"This PC" logic below.
        /// </summary>
        public NavigationNodeViewModel SelectedNode
        {
            get => _selectedNode;
            set => SetProperty(ref _selectedNode, value);
        }

        /// <summary>
        /// The single, unified "the user wants to navigate here" signal.
        /// NavigationPaneView's code-behind raises this exactly once per
        /// real navigation intent: once when SelectedItemChanged fires
        /// for a genuinely new selection (covers both mouse clicks that
        /// land on a previously-unselected node AND keyboard/arrow-key
        /// navigation, which never raises a mouse event at all), and
        /// once more only for the complementary case SelectedItemChanged
        /// cannot cover — re-clicking a node that was already selected,
        /// which is a real, previously-confirmed bug fix in its own
        /// right (re-clicking an already-selected drive/folder after
        /// navigating elsewhere via the file list used to do nothing).
        /// MainViewModel subscribes only to this event now, not to
        /// SelectedNode's PropertyChanged — previously both fired for an
        /// ordinary click, driving two full navigations (two debounce
        /// timers, two index builds racing each other) for one click.
        /// </summary>
        public event EventHandler<NavigationNodeViewModel> NodeActivated;

        public void NotifyNodeActivated(NavigationNodeViewModel node)
        {
            NodeActivated?.Invoke(this, node);
        }

        /// <summary>
        /// Raised when a shell context-menu action on a tree node
        /// (Rename/Delete/New, invoked via the real Explorer menu —
        /// see NavigationPaneView.xaml.cs's RefreshAfterShellAction) may
        /// have changed something on disk at the given path, outside
        /// this app's own file-operation pipeline. MainViewModel
        /// forwards this to FileListViewModel so the center pane can
        /// invalidate its cache for that path and, if it's currently
        /// showing that folder or one nested inside it, reload it —
        /// otherwise a rename or delete made from the tree would leave
        /// the center pane silently showing stale contents.
        /// </summary>
        public event EventHandler<string> ExternalChangeDetected;

        public void NotifyExternalChange(string affectedPath)
        {
            if (!string.IsNullOrEmpty(affectedPath))
            {
                ExternalChangeDetected?.Invoke(this, affectedPath);
            }
        }

        /// <summary>
        /// Raised every time this class has a current drive snapshot —
        /// once after the initial build in InitializeAsync, and again
        /// after every periodic/on-demand refresh — carrying the full
        /// current list. This is the single source of truth for drive
        /// state in the app: FileListViewModel's This PC page subscribes
        /// (via MainViewModel) and maps this same snapshot into its own
        /// display rather than running a second, independent
        /// DriveInfo-probing pass of its own — previously a real, if
        /// harmless, duplication (two parallel implementations of the
        /// same readiness/timeout/capacity logic that could silently
        /// drift apart) and a real gap (the This PC page never refreshed
        /// on its own, unlike the tree, until the person navigated away
        /// and back). Always raised from within an already
        /// dispatcher-marshaled block, so subscribers receive it on the
        /// UI thread and can touch UI-bound state directly.
        /// </summary>
        public event EventHandler<IReadOnlyList<DriveNodeInfo>> DrivesRefreshed;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            // Created here rather than in the constructor specifically
            // because NavigationViewModel is constructed before
            // SettingsService.LoadAsync runs (see MainViewModel's own
            // construction/initialization order) — sizing this from
            // settings at construction time would silently always use
            // the hardcoded default instead of a hand-edited
            // settings.json value, since Current wouldn't reflect the
            // real loaded settings yet.
            int maxWatchedFolders = _settingsService?.Current?.Navigation?.MaxLiveWatchedFolders ?? 64;
            _watcherBudget = new WatcherBudget(maxWatchedFolders);

            // "This PC" is a synthetic root: its children come from drive
            // detection below rather than a filesystem enumeration, so
            // hasChildrenHint is false — no placeholder, no accidental
            // background load (see the FullPath == null guard on
            // NavigationNodeViewModel.IsExpanded).
            var thisPc = new NavigationNodeViewModel(
                "This PC", null, NavigationNodeType.Computer, _navigationService, _settingsService, _dispatcher,
                hasChildrenHint: false, isUnreachable: false, watcherBudget: _watcherBudget, soundFeedbackService: _soundFeedbackService, logger: _logger);

            await _dispatcher.InvokeAsync(() => RootNodes.Add(thisPc)).ConfigureAwait(false);

            IReadOnlyList<DriveNodeInfo> drives;
            try
            {
                drives = VisibleDrives(await _driveService.GetDrivesAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // DriveEnumerationService already guards itself against
                // this, but this call site must never let a startup
                // failure here prevent the rest of the app from coming
                // up — worst case, the tree just starts with no drives
                // and the periodic refresh fills them in once it can.
                _logger.Error(ex, "Unexpected error detecting drives during startup");
                drives = Array.Empty<DriveNodeInfo>();
            }

            await _dispatcher.InvokeAsync(() =>
            {
                foreach (DriveNodeInfo drive in drives)
                {
                    if (drive.Phone?.NeedsMount == true)
                    {
                        continue; // not mounted yet: This PC opens it (mounting it), then it appears here
                    }

                    NavigationNodeType nodeType = drive.Phone != null ? NavigationNodeType.Phone : MapDriveType(drive.DriveType);
                    var driveNode = new NavigationNodeViewModel(
                        FormatDriveLabel(drive),
                        drive.RootPath,
                        nodeType,
                        _navigationService,
                        _settingsService,
                        _dispatcher,
                        hasChildrenHint: drive.IsReady,
                        isUnreachable: drive.ProbeTimedOut,
                        watcherBudget: _watcherBudget,
                        soundFeedbackService: _soundFeedbackService,
                        logger: _logger);

                    thisPc.Children.Add(driveNode);
                }

                thisPc.IsExpanded = true;
                ApplyShowAllFolders();
                DrivesRefreshed?.Invoke(this, drives);
            }).ConfigureAwait(false);

            _thisPcNode = thisPc;

            if (_disposed)
            {
                return;
            }

            // Removable drives (a USB stick) and network shares can
            // appear or disappear at any time with no notification this
            // app currently listens for — a real, confirmed bug: an
            // unplugged drive stayed visible in the tree indefinitely.
            // Periodic re-checking is a simpler, lower-risk fix here
            // than hooking the native WM_DEVICECHANGE window message,
            // and drive presence isn't latency-sensitive the way, say,
            // scroll responsiveness is — a few seconds' delay noticing
            // a removal is an acceptable trade for the much smaller
            // amount of new, native-adjacent code this avoids. Runs on
            // a plain Timer (thread-pool, not UI-bound) rather than a
            // DispatcherTimer specifically so this class doesn't need a
            // direct WPF Dispatcher dependency beyond the IUiDispatcher
            // abstraction it already has — RefreshDrivesAsync marshals
            // its UI-affecting work through that, same as InitializeAsync
            // already does above.
            //
            // OnDriveRefreshTimerTick below guards against two failure
            // modes a raw Timer callback is exposed to: (1) an unhandled
            // exception on a Timer's ThreadPool callback terminates the
            // process — there is no caller to observe or catch a fault
            // here, so this class must catch everything itself; (2) if
            // one tick is still waiting on a slow/unresponsive network
            // drive when the next tick fires (a real risk now that
            // GetDrivesAsync can take up to the configured network probe
            // timeout per network drive), firing again unconditionally
            // would pile up overlapping enumerations, each also waiting
            // on the same slow drive — an Interlocked guard skips a tick
            // outright rather than letting that accumulate.
            _driveRefreshTimer?.Dispose();
            _driveRefreshTimer = new Timer(
                OnDriveRefreshTimerTick,
                null,
                DriveRefreshIntervalMs,
                DriveRefreshIntervalMs);
        }

        /// <summary>
        /// Triggers an immediate drive-list refresh instead of waiting
        /// for the next periodic tick (up to DriveRefreshIntervalMs
        /// away). Used after a shell context-menu action on a top-level
        /// drive node (rename of the volume label, eject, etc.) so the
        /// tree picks it up right away. Reuses the exact same
        /// reentrancy-guarded, exception-safe path the periodic timer
        /// uses — if a refresh (periodic or another manual request) is
        /// already in progress, this is a safe no-op rather than
        /// stacking a second concurrent enumeration.
        /// </summary>
        public void RequestImmediateDriveRefresh()
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
            {
                _logger.Debug("Skipping on-demand drive refresh — a refresh is already in progress");
                return;
            }

            var ignored = RunRefreshTickAsync();
        }

        private void OnDriveRefreshTimerTick(object state)
        {
            if (_disposed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
            {
                _logger.Debug("Skipping drive refresh tick — a previous refresh is still in progress (likely waiting on a slow network drive)");
                return;
            }

            var ignored = RunRefreshTickAsync();
        }

        private async Task RunRefreshTickAsync()
        {
            try
            {
                await RefreshDrivesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Last-resort guard — see OnDriveRefreshTimerTick's
                // remarks on why this must never propagate.
                _logger.Warning(ex, "Unexpected error during drive refresh tick");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInProgress, 0);
            }
        }

        private async Task RefreshDrivesAsync()
        {
            if (_disposed)
            {
                return;
            }

            IReadOnlyList<DriveNodeInfo> currentDrives;
            try
            {
                currentDrives = VisibleDrives(await _driveService.GetDrivesAsync().ConfigureAwait(false));
            }
            catch (Exception)
            {
                // Best-effort background refresh — a failure here just
                // tries again on the next tick, never surfaces as an
                // error to the person.
                return;
            }

            if (_disposed)
            {
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_thisPcNode == null)
                    {
                        return;
                    }

                    var currentByPath = new Dictionary<string, DriveNodeInfo>(PathUtil.Comparer);
                    foreach (DriveNodeInfo drive in currentDrives)
                    {
                        currentByPath[drive.RootPath] = drive;
                    }

                    // Remove nodes for drives that are no longer present.
                    for (int i = _thisPcNode.Children.Count - 1; i >= 0; i--)
                    {
                        NavigationNodeViewModel child = _thisPcNode.Children[i];
                        if (child.FullPath != null && !currentByPath.ContainsKey(child.FullPath))
                        {
                            bool wasSelected = ReferenceEquals(SelectedNode, child);
                            _thisPcNode.Children.RemoveAt(i);

                            // Releases this drive node's own live
                            // watcher (if it was expanded) and cascades
                            // to any of its children that were also
                            // expanded — without this, a removed drive's
                            // watchers would keep running indefinitely
                            // for a node no longer reachable from the
                            // tree at all.
                            child.Dispose();

                            if (wasSelected)
                            {
                                // Fall back to "This PC" rather than leaving
                                // the selection pointing at a node that no
                                // longer exists in the tree at all.
                                SelectedNode = _thisPcNode;
                            }
                        }
                    }

                    // Update in-place status (e.g. unreachable <-> reachable)
                    // for drives that are still present, and collect which
                    // paths already have a node so new drives can be added.
                    var existingPaths = new HashSet<string>(PathUtil.Comparer);
                    foreach (NavigationNodeViewModel child in _thisPcNode.Children)
                    {
                        if (child.FullPath == null)
                        {
                            continue;
                        }

                        existingPaths.Add(child.FullPath);

                        if (currentByPath.TryGetValue(child.FullPath, out DriveNodeInfo drive))
                        {
                            child.UpdateUnreachable(drive.ProbeTimedOut);
                            child.Rename(FormatDriveLabel(drive)); // a new volume label, or the drive-letter options changed
                        }
                    }

                    // Add nodes for drives that are newly present.
                    foreach (DriveNodeInfo drive in currentDrives)
                    {
                        if (existingPaths.Contains(drive.RootPath))
                        {
                            continue;
                        }

                        if (drive.Phone?.NeedsMount == true)
                        {
                            continue;
                        }

                        NavigationNodeType nodeType = drive.Phone != null ? NavigationNodeType.Phone : MapDriveType(drive.DriveType);
                        var driveNode = new NavigationNodeViewModel(
                            FormatDriveLabel(drive),
                            drive.RootPath,
                            nodeType,
                            _navigationService,
                            _settingsService,
                            _dispatcher,
                            hasChildrenHint: drive.IsReady,
                            isUnreachable: drive.ProbeTimedOut,
                            watcherBudget: _watcherBudget,
                            soundFeedbackService: _soundFeedbackService,
                            logger: _logger);

                        _thisPcNode.Children.Add(driveNode);
                    }

                    DrivesRefreshed?.Invoke(this, currentDrives);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The dispatcher can legitimately fail here if the app is
                // mid-shutdown (window/dispatcher already tearing down)
                // while a background refresh was still in flight — that
                // is an expected race at exit, not a bug, so it's logged
                // at Debug rather than surfaced any louder.
                _logger.Debug(ex, "Drive refresh UI update skipped (dispatcher unavailable — likely shutting down)");
            }
        }

        /// <summary>Whether the tree expands to the folder the active pane shows (NavigationSettings.ExpandTreeToCurrentFolder).</summary>
        public bool ExpandToCurrentFolder => _settingsService?.Current?.Navigation?.ExpandTreeToCurrentFolder ?? true;

        /// <summary>Whether the tree follows the folder the active pane shows (NavigationSettings.TreeFollowsCurrentFolder).</summary>
        public bool FollowCurrentFolder => _settingsService?.Current?.Navigation?.TreeFollowsCurrentFolder ?? true;

        /// <summary>Following was turned on or off, or expanding changed: the view syncs again.</summary>
        public event EventHandler FollowSettingsChanged;

        public void NotifyFollowSettingsChanged() => FollowSettingsChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>The synthetic "This PC" root (null until <see cref="InitializeAsync"/> has built it).</summary>
        public NavigationNodeViewModel ThisPcNode => _thisPcNode;

        /// <summary>
        /// The node showing <paramref name="path"/>, expanding the tree down to it when <paramref name="expand"/> is true:
        /// from the drive holding the path (the longest matching root, so a mount inside "/" wins over "/"), one folder
        /// level at a time, each loaded in the background. Null when no drive holds the path, a folder on the way is not
        /// in the tree (hidden, deleted, inside an archive), a level does not answer, or the walk is cancelled (a newer
        /// navigation); the levels already expanded stay expanded. Without <paramref name="expand"/> only already-visible
        /// levels are followed. Runs on the UI thread.
        /// </summary>
        public async Task<NavigationNodeViewModel> RevealAsync(string path, bool expand, CancellationToken cancellationToken = default)
        {
            if (_thisPcNode == null || string.IsNullOrEmpty(path) || _disposed)
            {
                return null;
            }

            string target = PathUtil.TrimTrailingSeparator(path);
            NavigationNodeViewModel node = null;
            int bestLength = -1;
            foreach (NavigationNodeViewModel drive in _thisPcNode.Children)
            {
                if (drive.FullPath == null)
                {
                    continue;
                }

                string root = PathUtil.TrimTrailingSeparator(drive.FullPath);
                if (root.Length > bestLength && (string.Equals(root, target, PathUtil.Comparison) ||
                                                 target.StartsWith(PathUtil.WithTrailingSeparator(root), PathUtil.Comparison)))
                {
                    node = drive;
                    bestLength = root.Length;
                }
            }

            if (node != null && expand && !_thisPcNode.IsExpanded)
            {
                _thisPcNode.IsExpanded = true;
            }

            while (node != null)
            {
                string nodePath = PathUtil.TrimTrailingSeparator(node.FullPath);
                if (string.Equals(nodePath, target, PathUtil.Comparison))
                {
                    return node;
                }

                if (node.IsUnreachable || (!expand && (!node.IsExpanded || !node.IsLoaded)))
                {
                    return null;
                }

                if (expand)
                {
                    await node.ExpandAndLoadAsync().ConfigureAwait(true);
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        return null;
                    }
                }

                // The next level is the first segment of what remains of the path below this node.
                string prefix = PathUtil.WithTrailingSeparator(nodePath);
                int end = prefix.Length;
                while (end < target.Length && !PathUtil.IsSeparator(target[end]))
                {
                    end++;
                }

                node = node.FindChild(target.Substring(0, end));
            }

            return null;
        }

        /// <summary>Collapses every folder in the tree, leaving This PC open with its drives.</summary>
        public void CollapseAll()
        {
            foreach (NavigationNodeViewModel root in RootNodes)
            {
                root.CollapseDescendants();
            }
        }

        private static NavigationNodeType MapDriveType(DriveType driveType)
        {
            switch (driveType)
            {
                case DriveType.Fixed:
                    return NavigationNodeType.DriveFixed;
                case DriveType.Removable:
                    return NavigationNodeType.DriveRemovable;
                case DriveType.Network:
                    return NavigationNodeType.DriveNetwork;
                case DriveType.CDRom:
                    return NavigationNodeType.DriveOptical;
                default:
                    return NavigationNodeType.DriveUnknown;
            }
        }

        private NavigationNodeViewModel _homeRoot;

        /// <summary>
        /// Folder options' "Show all folders": the home folder (named after the user, as Explorer names it), with its
        /// subfolders, as a root above This PC; removed again when turned off. Call on the UI thread.
        /// </summary>
        public void ApplyShowAllFolders()
        {
            bool show = _settingsService?.Current?.FolderOptions?.ShowAllFolders ?? false;
            string home = SpecialFolders.Home;
            if (show && _homeRoot == null && !string.IsNullOrEmpty(home) && Directory.Exists(home))
            {
                string name = string.IsNullOrEmpty(Environment.UserName) ? PathUtil.GetName(home) : Environment.UserName;
                _homeRoot = new NavigationNodeViewModel(name, home, NavigationNodeType.Folder, _navigationService, _settingsService, _dispatcher,
                    hasChildrenHint: true, isUnreachable: false, watcherBudget: _watcherBudget, soundFeedbackService: _soundFeedbackService, logger: _logger);
                RootNodes.Insert(0, _homeRoot);
            }
            else if (!show && _homeRoot != null)
            {
                if (ReferenceEquals(SelectedNode, _homeRoot) || (SelectedNode?.FullPath != null && PathUtil.IsSameOrInside(SelectedNode.FullPath, home)))
                {
                    SelectedNode = null;
                }

                RootNodes.Remove(_homeRoot);
                _homeRoot.Dispose();
                _homeRoot = null;
            }
        }

        private string FormatDriveLabel(DriveNodeInfo drive)
        {
            Core.Models.FolderOptionsSettings options = _settingsService?.Current?.FolderOptions;
            return DriveLabels.Format(drive, options?.ShowDriveLetters ?? true, options?.DriveLettersFirst ?? false);
        }

        /// <summary>
        /// The drives to show: all, or (Folder options' "Hide empty drives") without the removable and optical drives
        /// that have no media in them. A network drive that doesn't answer is kept: it isn't empty, it's unreachable.
        /// </summary>
        private IReadOnlyList<DriveNodeInfo> VisibleDrives(IReadOnlyList<DriveNodeInfo> drives) =>
            FilterDrives(drives, hideEmpty: _settingsService?.Current?.FolderOptions?.HideEmptyDrives ?? false);

        internal static IReadOnlyList<DriveNodeInfo> FilterDrives(IReadOnlyList<DriveNodeInfo> drives, bool hideEmpty)
        {
            if (drives == null || !hideEmpty)
            {
                return drives;
            }

            var shown = new List<DriveNodeInfo>(drives.Count);
            foreach (DriveNodeInfo drive in drives)
            {
                bool empty = !drive.IsReady && !drive.ProbeTimedOut && drive.Phone == null &&
                             (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.Unknown);
                if (!empty)
                {
                    shown.Add(drive);
                }
            }

            return shown;
        }

        /// <summary>
        /// Disposes the periodic drive-refresh timer and cascades
        /// disposal through the entire navigation tree, releasing every
        /// live folder watcher anywhere in it. Safe to call more than
        /// once. Without the timer half of this, the Timer — and every
        /// closure/thread it can still trigger — outlives the view
        /// model indefinitely, including after the window closes, since
        /// nothing previously held or released it (MainViewModel is
        /// IDisposable but never disposed this class as part of that).
        /// Without the tree half, every currently-expanded folder's live
        /// watcher (a real OS handle) would leak the same way.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _visibleRows?.Dispose();

            try
            {
                _driveRefreshTimer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the drive refresh timer cleanly");
            }

            try
            {
                foreach (NavigationNodeViewModel root in RootNodes)
                {
                    root.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the navigation tree cleanly");
            }
        }
    }
}
