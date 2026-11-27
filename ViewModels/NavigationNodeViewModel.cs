// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// A single node in the navigation tree — a drive or a folder.
    ///
    /// Lazy-loading contract: a node that might have children gets a
    /// single placeholder child at construction time, which is enough
    /// for WPF to draw an expander arrow without touching disk. The
    /// real children are only enumerated the first time the node is
    /// expanded, on a background thread, and the placeholder is
    /// replaced once (and only once) that completes. A node the user
    /// never expands never touches the filesystem — not once, not ever,
    /// for the life of the app run.
    ///
    /// Re-entrancy: expanding, collapsing, and re-expanding a node in
    /// quick succession cancels any in-flight load for that node before
    /// starting a new one, so rapid clicking never stacks concurrent
    /// filesystem calls or races a stale result into the UI.
    ///
    /// Network timeout: any node whose FullPath is network-backed — the
    /// drive itself, or a folder several levels underneath it — gets
    /// NetworkDriveTimeoutMs (from settings) to respond. This is
    /// determined per-node from the path itself (PathClassifier), not
    /// from NodeType, specifically so a folder nested under a network
    /// drive is protected exactly as well as the drive's own top-level
    /// node — expanding "\\server\share\deeply\nested\folder" used to
    /// get no timeout at all, since only the DriveNetwork node itself
    /// was ever checked. A timeout is distinguished from being
    /// superseded by a newer load purely by reference identity on
    /// _loadCts — if this call's token fired but it's no longer the
    /// active load, it was superseded and silently yields to the
    /// newer one; if it's still the active load, it genuinely timed
    /// out and the tree shows a retry hint instead of hanging forever.
    /// The underlying enumeration itself runs under
    /// BlockingOperationRunner (see IFileSystemNavigationService's
    /// implementation) on a dedicated thread, which is what makes that
    /// timeout an actual wall-clock guarantee rather than a token the
    /// blocking native call never gets a chance to observe.
    ///
    /// Live updates: once a node successfully loads its children and is
    /// still expanded, it starts a FolderChangeWatcher on its own path
    /// (see StartWatchingIfNeededAsync) — a subfolder created, renamed,
    /// or deleted underneath it, by this app or anything else, triggers
    /// an automatic RequestReload without the person needing to
    /// collapse/re-expand or otherwise ask for it. Scope is deliberately
    /// bounded to exactly what's currently expanded and visible: a
    /// watcher starts on expand and is disposed on collapse (see
    /// IsExpanded's setter) or whenever this node itself is discarded
    /// (see Dispose) — never left running for a folder that's no longer
    /// showing in the tree. A shared WatcherBudget additionally caps how
    /// many of these can be active at once across the whole tree, so
    /// expanding an unusually large number of nodes degrades to "some
    /// folders don't auto-refresh" rather than accumulating an unbounded
    /// number of OS watch handles.
    /// </summary>
    public sealed class NavigationNodeViewModel : ViewModelBase, IDisposable
    {
        private readonly IFileSystemNavigationService _navigationService;
        private readonly ISettingsService _settingsService;
        private readonly IUiDispatcher _dispatcher;
        private readonly WatcherBudget _watcherBudget;
        private readonly ISoundFeedbackService _soundFeedbackService;
        private readonly ILogger _logger;
        private bool _isExpanded;
        private bool _isLoading;
        private bool _isLoaded;
        private bool _expandQuietly;
        private Task _loadTask = Task.CompletedTask;

        /// <summary>
        /// Above this many added or removed subfolders, a reload swaps the whole child list in one ranged change instead
        /// of one change per subfolder (the expanded subfolders are still kept, with their own expanded children).
        /// </summary>
        private const int MaxIncrementalChanges = 64;
        private bool _isUnreachable;
        private bool _disposed;
        private bool _watcherBudgetAcquired;
        private CancellationTokenSource _loadCts;
        private FolderChangeWatcher _watcher;

        public NavigationNodeViewModel(
            string name,
            string fullPath,
            NavigationNodeType nodeType,
            IFileSystemNavigationService navigationService,
            ISettingsService settingsService,
            IUiDispatcher dispatcher,
            bool hasChildrenHint = true,
            bool isUnreachable = false,
            WatcherBudget watcherBudget = null,
            ISoundFeedbackService soundFeedbackService = null,
            ILogger logger = null)
        {
            Name = name;
            FullPath = fullPath;
            NodeType = nodeType;
            _navigationService = navigationService;
            _settingsService = settingsService;
            _dispatcher = dispatcher;
            _isUnreachable = isUnreachable;
            _watcherBudget = watcherBudget;
            _soundFeedbackService = soundFeedbackService;
            _logger = (logger ?? Log.Logger).ForContext<NavigationNodeViewModel>();

            if (hasChildrenHint)
            {
                Children.Add(CreatePlaceholder());
            }
        }

        private string _name;

        public string Name
        {
            get => _name;
            private set => SetProperty(ref _name, value);
        }

        /// <summary>A drive's label changed (Folder options' drive letters): the row shows the new one.</summary>
        public void Rename(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                Name = name;
            }
        }

        public string FullPath { get; }
        public NavigationNodeType NodeType { get; }
        public ByteExplorer.Icons.Symbol IconGlyph => Infrastructure.IconGlyphs.For(NodeType, _isExpanded);
        /// <summary>
        /// Loaded children. Replaced in one step (ReplaceAll, a single Reset notification) when a load finishes, so a
        /// folder with 50,000 subfolders costs one change notification instead of 50,000.
        /// </summary>
        public BulkObservableCollection<NavigationNodeViewModel> Children { get; } = new BulkObservableCollection<NavigationNodeViewModel>();

        /// <summary>
        /// True when this drive did not respond to its background
        /// presence probe (see DriveEnumerationService) or the last
        /// attempt to expand it timed out. Set at construction for
        /// drive nodes from DriveNodeInfo.ProbeTimedOut, kept in sync by
        /// NavigationViewModel's periodic refresh via
        /// <see cref="UpdateUnreachable"/>, and set directly by this
        /// class itself when an expand attempt times out or recovers.
        /// Purely a display hint (dimmed row + tooltip in the tree) —
        /// never blocks the user from trying to expand or open the node
        /// again regardless of its current value.
        /// </summary>
        public bool IsUnreachable
        {
            get => _isUnreachable;
            private set
            {
                if (SetProperty(ref _isUnreachable, value))
                {
                    RaisePropertyChanged(nameof(StatusTooltip));
                }
            }
        }

        /// <summary>Tooltip shown on the row only while IsUnreachable is true; null otherwise (no tooltip).</summary>
        public string StatusTooltip => _isUnreachable
            ? "This network location isn't responding right now."
            : null;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!SetProperty(ref _isExpanded, value))
                {
                    return;
                }

                RaisePropertyChanged(nameof(IconGlyph));

                if (!value)
                {
                    // Collapsed — nothing under this node is visible
                    // anymore, so there's no reason to keep live-
                    // watching it (or holding a watcher-budget slot
                    // another expanded folder could use instead).
                    StopWatching();
                    return;
                }

                // FullPath == null marks a node whose children are
                // populated externally (the synthetic "This PC" root) —
                // it must never trigger its own background load.
                if (!_isLoaded && FullPath != null)
                {
                    StartLoad(playSoundOnSuccess: !_expandQuietly);
                }
            }
        }

        /// <summary>True once the subfolders have been read (and not invalidated by a reload request since).</summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>
        /// Expands the node and completes when its subfolders are loaded (at once if they already are): the tree's
        /// "reveal the current folder" walks down a path this way. No navigation sound: the person did not click this
        /// node. A load that times out or fails also completes the task; the children then hold the message row.
        /// </summary>
        public async Task ExpandAndLoadAsync()
        {
            if (_disposed || FullPath == null)
            {
                return;
            }

            if (!_isExpanded)
            {
                _expandQuietly = true;
                try
                {
                    IsExpanded = true;
                }
                finally
                {
                    _expandQuietly = false;
                }
            }
            else if (!_isLoaded && _loadTask.IsCompleted)
            {
                StartLoad(playSoundOnSuccess: false);
            }

            // A load superseded by a newer one (a watcher reload while expanding) ends early: wait for the newest.
            Task task;
            while (!(task = _loadTask).IsCompleted)
            {
                try
                {
                    await task.ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Loading the subfolders of {Path} failed", FullPath);
                    return;
                }
            }
        }

        /// <summary>The loaded child whose path is <paramref name="path"/> (compared per OS, trailing separators ignored), or null.</summary>
        public NavigationNodeViewModel FindChild(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string wanted = Core.PathUtil.TrimTrailingSeparator(path);
            foreach (NavigationNodeViewModel child in Children)
            {
                if (child.FullPath != null &&
                    string.Equals(Core.PathUtil.TrimTrailingSeparator(child.FullPath), wanted, Core.PathUtil.Comparison))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>Collapses every expanded node below this one (this node keeps its own state).</summary>
        public void CollapseDescendants()
        {
            var stack = new Stack<NavigationNodeViewModel>(Children);
            while (stack.Count > 0)
            {
                NavigationNodeViewModel node = stack.Pop();
                if (!node._isExpanded)
                {
                    continue;
                }

                foreach (NavigationNodeViewModel child in node.Children)
                {
                    stack.Push(child);
                }

                node.IsExpanded = false;
            }
        }

        private void StartLoad(bool playSoundOnSuccess)
        {
            Task task = LoadChildrenSafeAsync(playSoundOnSuccess);
            _loadTask = task;
            task.Forget("LoadChildrenSafeAsync");
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        /// <summary>
        /// Called by NavigationViewModel's periodic drive refresh to
        /// keep an existing drive node's unreachable status in sync
        /// without rebuilding the node (which would lose its expanded
        /// state and any already-loaded children).
        /// </summary>
        internal void UpdateUnreachable(bool isUnreachable)
        {
            IsUnreachable = isUnreachable;
        }

        /// <summary>
        /// Forces this node to forget it has already loaded its
        /// children and, if it's currently expanded, reloads them
        /// immediately. Used after a shell context-menu action
        /// (rename/delete/new item) invoked from this node or a child of
        /// it, and as the reaction to this node's own live folder
        /// watcher firing — neither this class nor
        /// IFileSystemNavigationService has any other way to learn that
        /// something changed outside this app's own file-operation
        /// pipeline. Safe to call on a collapsed node (it just clears
        /// the flag so the next expand does a fresh load) or a node with
        /// no children loaded yet (a no-op). Goes through the exact same
        /// cancel-in-flight, timeout-bounded LoadChildrenSafeAsync path
        /// a normal expand would use — nothing here bypasses those
        /// protections.
        /// </summary>
        public void RequestReload()
        {
            _isLoaded = false;

            if (_isExpanded && FullPath != null)
            {
                StartLoad(playSoundOnSuccess: false);
            }
        }

        private static NavigationNodeViewModel CreatePlaceholder()
        {
            return new NavigationNodeViewModel("Loading…", null, NavigationNodeType.Placeholder, null, null, null, hasChildrenHint: false);
        }

        private static NavigationNodeViewModel CreateMessageNode(string message)
        {
            return new NavigationNodeViewModel(message, null, NavigationNodeType.Placeholder, null, null, null, hasChildrenHint: false);
        }

        /// <summary>
        /// Puts the subfolders just read into <see cref="Children"/>. The first load swaps the placeholder for the whole
        /// list in one ranged change. A reload (the folder watcher, F5, a shell action) changes only what changed and
        /// keeps every subfolder still there as the same node: its expanded state, its loaded children and its live
        /// watcher survive, and the rows around it (with the tree's selection) stay where they are. A reload used to
        /// rebuild every child, so creating one folder in an expanded folder collapsed everything open below it.
        /// </summary>
        private void ApplyLoadedChildren(IReadOnlyList<DirectoryNodeInfo> subdirectories)
        {
            bool hasRealChildren = false;
            foreach (NavigationNodeViewModel child in Children)
            {
                if (child.NodeType != NavigationNodeType.Placeholder)
                {
                    hasRealChildren = true;
                    break;
                }
            }

            if (!hasRealChildren)
            {
                DisposeChildren();
                var loaded = new List<NavigationNodeViewModel>(subdirectories.Count);
                foreach (DirectoryNodeInfo directory in subdirectories)
                {
                    loaded.Add(CreateFolderNode(directory));
                }

                Children.ReplaceAll(loaded);
                return;
            }

            // Same path and same spelling: the same node. A case-only rename on a case-insensitive disk gets a new node
            // (its name is fixed at construction).
            var existing = new Dictionary<string, NavigationNodeViewModel>(Core.PathUtil.Comparer);
            foreach (NavigationNodeViewModel child in Children)
            {
                if (child.FullPath != null && child.NodeType != NavigationNodeType.Placeholder)
                {
                    existing.TryAdd(child.FullPath, child);
                }
            }

            var target = new List<NavigationNodeViewModel>(subdirectories.Count);
            var kept = new HashSet<NavigationNodeViewModel>(ReferenceEqualityComparer.Instance);
            int added = 0;
            foreach (DirectoryNodeInfo directory in subdirectories)
            {
                if (existing.TryGetValue(directory.FullPath, out NavigationNodeViewModel node) &&
                    string.Equals(node.Name, directory.Name, StringComparison.Ordinal) && kept.Add(node))
                {
                    target.Add(node);
                }
                else
                {
                    target.Add(CreateFolderNode(directory));
                    added++;
                }
            }

            int removed = Children.Count - kept.Count;
            if (added + removed > MaxIncrementalChanges)
            {
                foreach (NavigationNodeViewModel child in Children)
                {
                    if (!kept.Contains(child))
                    {
                        child.Dispose();
                    }
                }

                Children.ReplaceAll(target);
                return;
            }

            for (int i = Children.Count - 1; i >= 0; i--)
            {
                NavigationNodeViewModel child = Children[i];
                if (!kept.Contains(child))
                {
                    Children.RemoveAt(i);
                    child.Dispose();
                }
            }

            // Children now holds the kept nodes in their old order; bring it to the new order, inserting the new ones.
            for (int i = 0; i < target.Count; i++)
            {
                NavigationNodeViewModel wanted = target[i];
                if (i < Children.Count && ReferenceEquals(Children[i], wanted))
                {
                    continue;
                }

                if (kept.Contains(wanted))
                {
                    Children.Move(Children.IndexOf(wanted), i);
                }
                else
                {
                    Children.Insert(i, wanted);
                }
            }
        }

        private NavigationNodeViewModel CreateFolderNode(DirectoryNodeInfo directory) =>
            new NavigationNodeViewModel(
                directory.Name,
                directory.FullPath,
                NavigationNodeType.Folder,
                _navigationService,
                _settingsService,
                _dispatcher,
                watcherBudget: _watcherBudget,
                soundFeedbackService: _soundFeedbackService,
                logger: _logger)
            {
                IsHidden = directory.IsHidden,
                IsSystem = directory.IsSystem
            };

        /// <summary>A hidden folder (shown only with "hidden items" on): drawn translucent, as in the file list.</summary>
        public bool IsHidden { get; private init; }

        /// <summary>A system folder: its name is drawn in the system-item color.</summary>
        public bool IsSystem { get; private init; }

        /// <summary>
        /// playSoundOnSuccess distinguishes a genuine, direct user
        /// expand (IsExpanded's setter, true) from a passive reload
        /// (RequestReload, false — triggered by this node's own live
        /// folder watcher or a shell context-menu action elsewhere in
        /// the tree, neither of which the person directly clicked this
        /// node for). Only the former plays PlayNavigate — a beep with
        /// no corresponding click would be confusing, not useful
        /// feedback.
        /// </summary>
        private async Task LoadChildrenSafeAsync(bool playSoundOnSuccess)
        {
            // Every await resumes on the UI thread (callers are the IsExpanded setter and dispatcher callbacks):
            // IsLoading/IsUnreachable/_watcher are UI state, and assigning _watcher on a pool thread raced
            // collapse/StopWatching.
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            bool isNetworkPath = PathClassifier.IsNetworkPath(FullPath);
            if (isNetworkPath)
            {
                int timeoutMs = _settingsService?.Current?.Navigation?.NetworkDriveTimeoutMs ?? 4000;
                cts.CancelAfter(timeoutMs);
            }

            bool includeHiddenAndSystem =
                (_settingsService?.Current?.Navigation?.ShowHiddenItems ?? false) ||
                (_settingsService?.Current?.Navigation?.ShowSystemItems ?? false);

            IsLoading = true;
            try
            {
                IReadOnlyList<DirectoryNodeInfo> subdirectories = null;
                bool timedOut = false;

                try
                {
                    subdirectories = await _navigationService
                        .GetSubdirectoriesAsync(FullPath, includeHiddenAndSystem, cts.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    if (!ReferenceEquals(_loadCts, cts))
                    {
                        // Superseded by a newer expand cycle — the newer
                        // call owns updating the UI, this one just exits.
                        return;
                    }

                    timedOut = true;
                }

                if (timedOut)
                {
                    if (isNetworkPath)
                    {
                        IsUnreachable = true;
                    }

                    await _dispatcher.InvokeAsync(() =>
                    {
                        DisposeChildren();
                        Children.ReplaceAll(new[] { CreateMessageNode("Timed out — collapse and expand to retry") });
                    }).ConfigureAwait(true);
                    return;
                }

                await _dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed)
                    {
                        ApplyLoadedChildren(subdirectories);
                        _isLoaded = true;
                    }
                }).ConfigureAwait(true);

                if (playSoundOnSuccess)
                {
                    _soundFeedbackService?.PlayNavigate();
                }

                // A previously-timed-out network node that just
                // succeeded has recovered — clear the stale indicator
                // rather than leaving it dimmed after it visibly worked.
                if (isNetworkPath && _isUnreachable)
                {
                    IsUnreachable = false;
                }

                // Only (re)acquire a watcher if one isn't already
                // running — RequestReload can re-run this method many
                // times while staying continuously expanded (triggered
                // by the watcher itself, or by a shell action), and
                // tearing down + recreating a live OS watch handle on
                // every single one of those would be pure waste. A
                // fresh watcher is only actually needed after a genuine
                // collapse (StopWatching already cleared _watcher then).
                if (_watcher == null && ReferenceEquals(_loadCts, cts))
                {
                    await StartWatchingIfNeededAsync(cts).ConfigureAwait(true);
                }
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    IsLoading = false;
                }
            }
        }

        private async Task StartWatchingIfNeededAsync(CancellationTokenSource cts)
        {
            if (!_isExpanded || FullPath == null || _watcherBudget == null)
            {
                return;
            }

            bool enabled = _settingsService?.Current?.Navigation?.EnableLiveFolderWatching ?? true;
            if (!enabled)
            {
                return;
            }

            if (!_watcherBudget.TryAcquire())
            {
                _logger.Debug("Live-watch budget exhausted — {Path} will not auto-refresh until another expanded folder is collapsed", FullPath);
                return;
            }

            _watcherBudgetAcquired = true;

            int debounceMs = _settingsService?.Current?.Navigation?.LiveRefreshDebounceMs ?? 500;
            int networkTimeoutMs = _settingsService?.Current?.Navigation?.NetworkDriveTimeoutMs ?? 4000;

            FolderChangeWatcher watcher = await FolderChangeWatcher.CreateAsync(
                FullPath,
                directoriesOnly: true, // the tree only ever displays subfolders
                debounceMs,
                networkTimeoutMs,
                cts.Token,
                _logger).ConfigureAwait(true);

            if (_disposed || !_isExpanded || !ReferenceEquals(_loadCts, cts))
            {
                // Collapsed, disposed, or superseded by a newer load
                // while the watcher was starting — don't leave an
                // orphaned watcher (and a held budget slot) around for a
                // node that's no longer the active one.
                watcher.Dispose();
                ReleaseWatcherBudget();
                return;
            }

            watcher.Changed += OnWatchedFolderChanged;
            _watcher = watcher;
        }

        private void OnWatchedFolderChanged(object sender, EventArgs e)
        {
            // Raised on a background thread (FolderChangeWatcher is
            // deliberately UI-framework-agnostic) — RequestReload leads
            // into LoadChildrenSafeAsync, which touches UI-bound
            // properties (IsLoading, Children) synchronously before its
            // first await, so this must be marshaled onto the UI thread
            // first.
            try
            {
                var ignored = _dispatcher.InvokeAsync(RequestReload);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to marshal a live folder-change reload for {Path} — likely shutting down", FullPath);
            }
        }

        private void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.Changed -= OnWatchedFolderChanged;
                _watcher.Dispose();
                _watcher = null;
            }

            ReleaseWatcherBudget();
        }

        private void ReleaseWatcherBudget()
        {
            if (_watcherBudgetAcquired)
            {
                _watcherBudgetAcquired = false;
                _watcherBudget?.Release();
            }
        }

        /// <summary>
        /// Disposes every currently-held child before Children is
        /// cleared/replaced — a child that was itself expanded (and so
        /// holds its own live watcher and budget slot) would otherwise
        /// leak that watcher the moment its NavigationNodeViewModel
        /// instance is discarded here, since nothing else would ever
        /// call Dispose on an instance no longer referenced by the tree.
        /// </summary>
        private void DisposeChildren()
        {
            foreach (NavigationNodeViewModel child in Children)
            {
                child.Dispose();
            }
        }

        /// <summary>
        /// Releases this node's own live watcher (if any) and cascades
        /// to every currently-held child, recursively, so disposing the
        /// root of a subtree releases every watcher anywhere beneath it
        /// in one call. Safe to call multiple times. Called when this
        /// node is removed from the tree entirely (a drive that
        /// disappeared, a parent's children being rebuilt — see
        /// DisposeChildren) and, ultimately, from NavigationViewModel's
        /// own Dispose on application shutdown.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatching();
            DisposeChildren();
        }
    }
}
