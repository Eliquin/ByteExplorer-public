// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Core;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The file list pane. Its DataContext is a <see cref="FileListViewModel"/>; the view owns only what has no meaning
    /// outside the UI: which surface is visible, the lists' selection and its mirror into the view model, pointer
    /// gestures (open, drag out, rubber band, drops, context menus), keyboard navigation, zoom and scroll prefetch.
    /// <para>
    /// Split across partial files: this one (view model wiring, surfaces, Details header, zoom, prefetch, keyboard, the
    /// This PC and Home surfaces), <c>FileListView.Selection.cs</c>, <c>FileListView.Pointer.cs</c> (open, drag out,
    /// rubber band), <c>FileListView.DragDrop.cs</c> and <c>FileListView.Menus.cs</c>.
    /// </para>
    /// </summary>
    public partial class FileListView : UserControl
    {
        /// <summary>Icon-size change per wheel notch for Ctrl+wheel zoom (the WPF build's 120-unit notch scaled by 8).</summary>
        private const double ZoomPixelsPerWheelNotch = 8.0;

        /// <summary>Minimum spacing of scroll-driven prefetch requests; the last position is always sent (trailing call).</summary>
        private static readonly TimeSpan PrefetchThrottle = TimeSpan.FromMilliseconds(40);

        /// <summary>The surfaces of the pane; exactly one is visible.</summary>
        private enum Surface
        {
            None,
            Details,
            List,
            Icons,
            Tiles,
            Timeline,
            Columns,
            Nexus,
            ThisPc,
            Home,
            Trash,
            Error
        }

        /// <summary>The kinds of list the view hosts, which differ in how their selection reaches the view model.</summary>
        private enum ListKind
        {
            None,
            Flat,
            Timeline,
            Column,
            Nexus
        }

        private readonly DispatcherTimer _prefetchTimer;
        private readonly TranslateTransform _detailsHeaderOffset = new TranslateTransform();

        private FileListViewModel _vm;
        private Surface _surface = Surface.None;
        private DetailsColumnLayout _columns = DetailsColumnsState.Current;

        // What each surface currently shows, so a refresh only touches a surface whose source actually changed.
        private ListBox _activeFlatList;
        private IList _activeFlatItems;
        private object _timelineSource;
        private object _columnsSource;
        private object _nexusSource;
        private INotifyCollectionChanged _hookedColumnPanes;

        // The folder whose items the lists were last given: selection is carried over only within the same folder.
        private string _appliedFolder;

        private DateTime _lastPrefetchUtc = DateTime.MinValue;
        private bool _lastScrollForward = true;

        /// <summary>
        /// Gives keyboard focus to the items (the selected one, else the list), so typing and arrows work right after
        /// going somewhere from the omnibar. Deferred until the new folder's items are laid out.
        /// </summary>
        public void FocusItems()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ListBox list = _activeFlatList;
                if (list == null || !list.IsEffectivelyVisible)
                {
                    Focus();
                    return;
                }

                int anchor = list.Selection.AnchorIndex;
                Control target = anchor >= 0 ? list.ContainerFromIndex(anchor) : null;
                (target ?? list).Focus(NavigationMethod.Directional);
            }, DispatcherPriority.Loaded);
        }

        public FileListView()
        {
            InitializeComponent();

            _prefetchTimer = new DispatcherTimer { Interval = PrefetchThrottle };
            _prefetchTimer.Tick += OnPrefetchTimerTick;

            DetailsRowPanel.SetColumns(DetailsHost, _columns);
            DetailsHeader.RenderTransform = _detailsHeaderOffset;
            InitializeColumns();

            AddHandler(PointerWheelChangedEvent, OnPointerWheelTunnel, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnKeyDownBubble, RoutingStrategies.Bubble);
            AddHandler(GotFocusEvent, OnGotFocusRouted, RoutingStrategies.Bubble);
            AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChangedRouted, RoutingStrategies.Bubble);
            AddHandler(FileListBox.SelectionModelChangedEvent, OnSelectionModelChangedRouted, RoutingStrategies.Bubble);
            AddHandler(ContextRequestedEvent, OnContextRequestedRouted, RoutingStrategies.Bubble);
            InitializePointerHandling();
            InitializeDropHandling();
            InitializeTypeAhead();
        }

        // ------------------------------------------------------------------ view model wiring

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (this.IsAttachedToVisualTree())
            {
                AttachViewModel(DataContext as FileListViewModel);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DetailsColumnsState.Changed += OnDetailsColumnsChanged;
            ApplyColumns(DetailsColumnsState.Current); // changed while this view was away (another tab)
            AttachViewModel(DataContext as FileListViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            // Unhooked while detached so a discarded view never stays reachable from a live view model's events.
            DetailsColumnsState.Changed -= OnDetailsColumnsChanged;
            AttachViewModel(null);
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Switches the view to another view model (or none). The WPF view subscribed once on Loaded, so a pane whose
        /// DataContext changed afterwards kept driving, and being driven by, the old view model.
        /// </summary>
        private void AttachViewModel(FileListViewModel viewModel)
        {
            if (ReferenceEquals(viewModel, _vm))
            {
                return;
            }

            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                _vm.SelectAllRequested -= OnSelectAllRequested;
                _vm.SelectNoneRequested -= OnSelectNoneRequested;
                _vm.InvertSelectionRequested -= OnInvertSelectionRequested;
                _vm.SelectNamesRequested -= OnSelectNamesRequested;
                if (_vm.InlineRenamer == (Func<FileListItemViewModel, Task<string>>)RenameInlineAsync)
                {
                    _vm.InlineRenamer = null;
                }

                CompleteInlineRename(commit: false);
                HookColumnPanes(null);
            }

            RememberDetachedPane();
            EndBand();
            ResetArm();
            _prefetchTimer.Stop();
            ClearSurfaceSources();
            _vm = viewModel;
            TakePaneMemory(viewModel);

            if (_vm != null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
                _vm.SelectAllRequested += OnSelectAllRequested;
                _vm.SelectNoneRequested += OnSelectNoneRequested;
                _vm.InvertSelectionRequested += OnInvertSelectionRequested;
                _vm.SelectNamesRequested += OnSelectNamesRequested;
                _vm.InlineRenamer = RenameInlineAsync;
                HookColumnPanes(_vm.ColumnPanes);
            }

            RefreshSurface();
            RestoreDetachedScroll();
            UpdateSortGlyphs();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!UiThread.CheckAccess())
            {
                // The view model raises on the UI thread today; this keeps a stray background raise from touching controls.
                string name = e.PropertyName;
                UiThread.Post(() => OnViewModelPropertyChanged(sender, new PropertyChangedEventArgs(name)));
                return;
            }

            if (!ReferenceEquals(sender, _vm))
            {
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(FileListViewModel.Items):
                case nameof(FileListViewModel.ViewMode):
                case nameof(FileListViewModel.ErrorMessage):
                case nameof(FileListViewModel.IsStandardFolderView):
                case nameof(FileListViewModel.IsThisPcView):
                case nameof(FileListViewModel.IsHomeView):
                case nameof(FileListViewModel.IsTrashView):
                case nameof(FileListViewModel.IsLoading):
                case nameof(FileListViewModel.TimelineGroups):
                case nameof(FileListViewModel.NexusClusters):
                case nameof(FileListViewModel.FilterText):
                case nameof(FileListViewModel.CategoryFilter):
                    RefreshSurface();
                    break;
                case nameof(FileListViewModel.ColumnPanes):
                    HookColumnPanes(_vm.ColumnPanes);
                    RefreshSurface();
                    break;
                case nameof(FileListViewModel.SortColumn):
                case nameof(FileListViewModel.SortAscending):
                    UpdateSortGlyphs();
                    break;
                case null:
                case "":
                    // "Everything changed".
                    HookColumnPanes(_vm.ColumnPanes);
                    RefreshSurface();
                    UpdateSortGlyphs();
                    break;
            }
        }

        // ------------------------------------------------------------------ surfaces

        private Surface ResolveSurface()
        {
            if (_vm == null)
            {
                return Surface.None;
            }

            // An error replaces whichever surface failed to load. In the WPF build the error text sat underneath the
            // opaque This PC/Home pages, so a failure to load either of them showed an empty page instead of the message.
            if (!string.IsNullOrEmpty(_vm.ErrorMessage))
            {
                return Surface.Error;
            }

            if (_vm.IsThisPcView)
            {
                return Surface.ThisPc;
            }

            if (_vm.IsHomeView)
            {
                return Surface.Home;
            }

            if (_vm.IsTrashView)
            {
                return Surface.Trash;
            }

            switch (_vm.ViewMode)
            {
                case FileListViewMode.List: return Surface.List;
                case FileListViewMode.Icons: return Surface.Icons;
                case FileListViewMode.Tiles: return Surface.Tiles;
                case FileListViewMode.Timeline: return Surface.Timeline;
                case FileListViewMode.Columns: return Surface.Columns;
                case FileListViewMode.Nexus: return Surface.Nexus;
                default: return Surface.Details;
            }
        }

        /// <summary>
        /// The one place that decides what is shown: makes the resolved surface visible, gives it its data (carrying the
        /// selection over within the same folder), and keeps the view model's selection equal to what is on screen.
        /// </summary>
        private void RefreshSurface()
        {
            Surface target = ResolveSurface();
            bool hadFocus = IsKeyboardFocusWithin;

            DetailsHost.IsVisible = target == Surface.Details;
            ListModeList.IsVisible = target == Surface.List;
            IconsList.IsVisible = target == Surface.Icons;
            TilesList.IsVisible = target == Surface.Tiles;
            TimelineList.IsVisible = target == Surface.Timeline;
            ColumnsScroller.IsVisible = target == Surface.Columns;
            NexusScroller.IsVisible = target == Surface.Nexus;
            ThisPcScroller.IsVisible = target == Surface.ThisPc;
            HomeScroller.IsVisible = target == Surface.Home;
            TrashSurface.IsVisible = target == Surface.Trash;
            if (target == Surface.Trash && _surface != Surface.Trash && hadFocus)
            {
                TrashSurface.FocusListSoon();
            }

            if (target != _surface)
            {
                EndBand();
                ResetArm();
            }

            ApplyFlatSurface(target, hadFocus);
            ApplyTimelineSurface(target, hadFocus);
            ApplyGroupedSurfaces(target);

            if (target == Surface.ThisPc || target == Surface.Home || target == Surface.Trash || target == Surface.Error || target == Surface.None)
            {
                // Nothing selectable is on screen; a stale selection would let Delete or Ctrl+X act on invisible items.
                ClearViewModelSelection();
            }

            _surface = target;
            UpdateStateMessage();
        }

        /// <summary>Details/List/Icons/Tiles: only the visible list holds the folder's items.</summary>
        private void ApplyFlatSurface(Surface target, bool hadFocus)
        {
            ListBox list = FlatListFor(target);
            IList items = list != null ? _vm?.Items : null;
            if (ReferenceEquals(list, _activeFlatList) && ReferenceEquals(items, _activeFlatItems))
            {
                return;
            }

            // Leaving a folder: remember where the person was (Back returns there).
            if (_renameBox != null)
            {
                CompleteInlineRename(commit: true);
            }

            string newFolder = _vm?.CurrentPath;
            string previousFolder = _appliedFolder;
            bool folderChanged = previousFolder != null && newFolder != null && !PathUtil.AreSame(previousFolder, newFolder);
            if (folderChanged)
            {
                SaveFolderState(_activeFlatList, previousFolder);
            }

            SelectionSnapshot restore = CaptureSelection();
            _syncingSelection = true;
            try
            {
                if (_activeFlatList != null && !ReferenceEquals(_activeFlatList, list))
                {
                    _activeFlatList.ItemsSource = null;
                }

                if (list != null)
                {
                    list.ItemsSource = items;
                    RestoreSelection(list, items as VirtualizingFileList, restore);
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            _activeFlatList = list;
            _activeFlatItems = items;
            if (list == null)
            {
                return;
            }

            _appliedFolder = _vm?.CurrentPath;
            SyncSelectionFromList(list);
            if (folderChanged || _vm?.HasPendingReveal(newFolder) == true)
            {
                RestoreFolderState(list, newFolder, previousFolder, _vm?.IsHistoryNavigation ?? false);
            }

            // Prepare the next page (items and their text) once the first one is on screen.
            _lastScrollForward = true;
            Dispatcher.UIThread.Post(() =>
            {
                RunPrefetch();
                if (ReferenceEquals(list, _activeFlatList) && list.ItemsPanelRoot is VirtualizingWrapPanel panel)
                {
                    StartPoolWarming(list, panel);
                }
            }, DispatcherPriority.Background);
            if (hadFocus)
            {
                FocusListSoon(list);
            }
        }

        /// <summary>Timeline: the view model's date groups, flattened into one virtualized list (see <see cref="TimelineRow"/>).</summary>
        private void ApplyTimelineSurface(Surface target, bool hadFocus)
        {
            object groups = target == Surface.Timeline ? _vm?.TimelineGroups : null;
            if (ReferenceEquals(groups, _timelineSource) && (groups == null) == (TimelineList.ItemsSource == null))
            {
                return;
            }

            SelectionSnapshot restore = groups != null ? CaptureSelection() : null;
            var rows = groups != null ? TimelineRow.Flatten(_vm.TimelineGroups) : null;
            _syncingSelection = true;
            try
            {
                TimelineList.ItemsSource = rows;
                if (rows != null)
                {
                    RestoreTimelineSelection(rows, restore);
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            _timelineSource = groups;
            if (rows == null)
            {
                return;
            }

            _appliedFolder = _vm.CurrentPath;
            SyncSelectionFromList(TimelineList);
            if (hadFocus)
            {
                FocusListSoon(TimelineList);
            }
        }

        /// <summary>
        /// Columns and Nexus build their own lists from the view model's panes/clusters; a new set starts with nothing
        /// selected, so the view model's selection is cleared to match (in the WPF build it kept the previous view's
        /// selection, invisible, until the first click).
        /// </summary>
        private void ApplyGroupedSurfaces(Surface target)
        {
            object panes = target == Surface.Columns ? _vm?.ColumnPanes : null;
            if (!ReferenceEquals(panes, _columnsSource))
            {
                _columnsSource = panes;
                if (panes != null)
                {
                    ClearViewModelSelection();
                }
            }

            object clusters = target == Surface.Nexus ? _vm?.NexusClusters : null;
            if (!ReferenceEquals(clusters, _nexusSource))
            {
                _nexusSource = clusters;
                if (clusters != null)
                {
                    ClearViewModelSelection();
                }
            }
        }

        /// <summary>Drops every list's items (view model detached): nothing keeps the old folder alive.</summary>
        private void ClearSurfaceSources()
        {
            _syncingSelection = true;
            try
            {
                foreach (ListBox list in new[] { DetailsList, ListModeList, IconsList, TilesList, TimelineList })
                {
                    list.ItemsSource = null;
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            _activeFlatList = null;
            _activeFlatItems = null;
            _timelineSource = null;
            _columnsSource = null;
            _nexusSource = null;
            _appliedFolder = null;
            _surface = Surface.None;
        }

        private ListBox FlatListFor(Surface surface)
        {
            switch (surface)
            {
                case Surface.Details: return DetailsList;
                case Surface.List: return ListModeList;
                case Surface.Icons: return IconsList;
                case Surface.Tiles: return TilesList;
                default: return null;
            }
        }

        /// <summary>The error text, or, for a folder with nothing to show, why (an empty folder or a filter that matches nothing).</summary>
        private void UpdateStateMessage()
        {
            string message = null;
            if (_surface == Surface.Error)
            {
                message = _vm.ErrorMessage;
            }
            else if (_vm != null && !_vm.IsLoading && _surface != Surface.Columns && _surface != Surface.ThisPc &&
                     _surface != Surface.Home && _surface != Surface.None && _vm.Items != null && _vm.Items.Count == 0)
            {
                bool filtered = !string.IsNullOrEmpty(_vm.FilterText) || _vm.CategoryFilter != FileCategoryFilter.All;
                message = filtered ? "No items match your filter." : "This folder is empty.";
            }

            StateMessage.Text = message;
            StateMessage.IsVisible = message != null;
        }

        private static void FocusListSoon(ListBox list)
        {
            // After the layout pass that realizes the new items, so the selected container can take focus.
            Dispatcher.UIThread.Post(() => FocusList(list), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Gives keyboard focus back to a list whose items were just replaced: to its current (anchor) item when that is
        /// on screen, otherwise to the first visible item, so the arrow keys keep working after opening a folder with
        /// Enter or a double-click. (A ListBox itself is not focusable in Avalonia; its items are.)
        /// </summary>
        private static void FocusList(ListBox list)
        {
            if (!list.IsEffectivelyVisible || list.ItemCount == 0)
            {
                return;
            }

            list.UpdateLayout(); // realize the new items if no layout pass has run yet
            int anchor = list.Selection.AnchorIndex;
            Control container = anchor >= 0 ? list.ContainerFromIndex(anchor) : null;
            if (container == null)
            {
                int first = FirstRealizedIndex(list);
                container = first >= 0 ? list.ContainerFromIndex(first) : null;
            }

            container?.Focus(NavigationMethod.Directional);
        }

        private static int FirstRealizedIndex(ListBox list)
        {
            switch (list.ItemsPanelRoot)
            {
                case VirtualizingStackPanel stack:
                    return stack.FirstRealizedIndex;
                case VirtualizingWrapPanel wrap:
                    return wrap.FirstRealizedIndex;
                default:
                    return list.ItemCount > 0 ? 0 : -1;
            }
        }

        // ------------------------------------------------------------------ Columns

        private void HookColumnPanes(object panes)
        {
            var observable = panes as INotifyCollectionChanged;
            if (ReferenceEquals(observable, _hookedColumnPanes))
            {
                return;
            }

            if (_hookedColumnPanes != null)
            {
                _hookedColumnPanes.CollectionChanged -= OnColumnPanesChanged;
            }

            _hookedColumnPanes = observable;
            if (_hookedColumnPanes != null)
            {
                _hookedColumnPanes.CollectionChanged += OnColumnPanesChanged;
            }
        }

        /// <summary>A drilled-down column was added: scroll it into view, as Finder does (the WPF view left it off-screen).</summary>
        private void OnColumnPanesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && _surface == Surface.Columns)
            {
                Dispatcher.UIThread.Post(() => ColumnsScroller.ScrollToEnd(), DispatcherPriority.Loaded);
            }
        }

        private ListBox ListForPane(ColumnPane pane)
        {
            if (pane == null)
            {
                return null;
            }

            return ColumnsHost.ContainerFromItem(pane)?.FindDescendantOfType<ListBox>();
        }

        // ------------------------------------------------------------------ Details header

        // ------------------------------------------------------------------ zoom and scrolling

        /// <summary>Ctrl+wheel zooms the active view (every view derives its sizes from the one zoom value).</summary>
        private void OnPointerWheelTunnel(object sender, PointerWheelEventArgs e)
        {
            if (_vm == null || e.KeyModifiers != KeyModifiers.Control || e.Delta.Y == 0)
            {
                return;
            }

            _vm.AdjustZoom(e.Delta.Y * ZoomPixelsPerWheelNotch);
            e.Handled = true;
        }

        private void OnScrollChangedRouted(object sender, ScrollChangedEventArgs e)
        {
            if (!(e.Source is ScrollViewer scroller) || !(scroller.TemplatedParent is ListBox list))
            {
                return;
            }

            if (_renameBox != null && ReferenceEquals(list, _renameList) && (e.OffsetDelta.X != 0 || e.OffsetDelta.Y != 0))
            {
                CompleteInlineRename(commit: true); // the box would no longer sit over its item
            }

            if (ReferenceEquals(list, DetailsList))
            {
                // The header lives outside the list's scroll viewer; it follows the rows horizontally.
                _detailsHeaderOffset.X = -scroller.Offset.X;
            }

            if (!ReferenceEquals(list, _activeFlatList) || e.OffsetDelta.Y == 0)
            {
                return;
            }

            _lastScrollForward = e.OffsetDelta.Y > 0;
            if (DateTime.UtcNow - _lastPrefetchUtc >= PrefetchThrottle)
            {
                RunPrefetch();
            }
            else if (!_prefetchTimer.IsEnabled)
            {
                _prefetchTimer.Start();
            }
        }

        private void OnPrefetchTimerTick(object sender, EventArgs e)
        {
            _prefetchTimer.Stop();
            RunPrefetch();
        }

        /// <summary>
        /// Asks the list to build the items just beyond the realized range, in the direction of travel, on a background
        /// thread, so scrolling finds them ready. The realized range comes from the panel itself, which is exact for
        /// every view (the WPF view re-derived it per view from scroll offsets in mixed item/pixel units).
        /// </summary>
        private void RunPrefetch()
        {
            _lastPrefetchUtc = DateTime.UtcNow;
            if (!(_activeFlatItems is VirtualizingFileList items) || items.Count == 0 || _activeFlatList == null)
            {
                return;
            }

            if (!(_activeFlatList.ItemsPanelRoot is VirtualizingWrapPanel panel))
            {
                return;
            }

            int first = panel.FirstRealizedIndex;
            int last = panel.LastRealizedIndex;
            if (first >= 0 && last >= first)
            {
                items.RequestPrefetch(first, Math.Min(last, items.Count - 1), _lastScrollForward);
                StartPrewarm(_activeFlatList, panel, items, _lastScrollForward);
            }
        }

        // ------------------------------------------------------------------ keyboard

        private void OnKeyDownTunnel(object sender, KeyEventArgs e)
        {
            _lastModifiers = e.KeyModifiers;

            // Nothing selected yet (a folder just opened, focus on its first item): the first arrow key selects the
            // focused item instead of moving past it, as in Explorer. (Left/Right in Columns move between columns.)
            if (e.KeyModifiers == KeyModifiers.None && IsNavigationKey(e.Key) &&
                FindList(e.Source as Visual, out ListBoxItem container, out _) is ListBox list && container != null &&
                list.Selection.SelectedIndexes.Count == 0 &&
                !(KindOf(list) == ListKind.Column && (e.Key == Key.Left || e.Key == Key.Right)))
            {
                int index = list.IndexFromContainer(container);
                if (index >= 0)
                {
                    list.Selection.Select(index);
                    e.Handled = true;
                }
            }
        }

        private void OnKeyDownBubble(object sender, KeyEventArgs e)
        {
            if (e.Handled || _vm == null)
            {
                return;
            }

            ListBox list = FindList(e.Source as Visual, out _, out _);
            if (list == null)
            {
                return;
            }

            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _vm.SelectedItems.Count > 0)
            {
                // Esc clears the selection, as in Explorer (a rename in progress takes Esc before this).
                e.Handled = true;
                _vm.SelectNoneCommand.Execute(null);
                return;
            }

            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                FileListItemViewModel item = _vm.SelectedItem;
                if (item != null)
                {
                    e.Handled = true;
                    if (_vm.SelectedItems.Count > 1 && KindOf(list) != ListKind.Column)
                    {
                        _vm.OpenSelectionAsync(item).Forget("open the selection"); // every selected item, as Explorer does
                    }
                    else
                    {
                        OpenFromListAsync(list, item).Forget("OpenFromListAsync");
                    }
                }

                return;
            }

            if (KindOf(list) == ListKind.Column && e.KeyModifiers == KeyModifiers.None && (e.Key == Key.Left || e.Key == Key.Right))
            {
                e.Handled = MoveBetweenColumns(list, e.Key == Key.Right);
            }
        }

        private static bool IsNavigationKey(Key key) =>
            key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right ||
            key == Key.Home || key == Key.End || key == Key.PageUp || key == Key.PageDown;

        /// <summary>
        /// Columns: Right enters the next (drilled-down) column, selecting its first item if none is; Left returns to the
        /// previous column's selected item, as in Finder.
        /// </summary>
        private bool MoveBetweenColumns(ListBox list, bool forward)
        {
            if (!(list.DataContext is ColumnPane pane) || _vm.ColumnPanes == null)
            {
                return false;
            }

            int index = _vm.ColumnPanes.IndexOf(pane);
            int targetIndex = forward ? index + 1 : index - 1;
            if (index < 0 || targetIndex < 0 || targetIndex >= _vm.ColumnPanes.Count)
            {
                return false;
            }

            ListBox target = ListForPane(_vm.ColumnPanes[targetIndex]);
            if (target == null || target.ItemCount == 0)
            {
                return false;
            }

            if (target.Selection.SelectedIndexes.Count == 0)
            {
                target.Selection.Select(0);
            }

            int focusIndex = target.Selection.AnchorIndex >= 0 ? target.Selection.AnchorIndex : 0;
            target.ScrollIntoView(focusIndex);
            if (!(target.ContainerFromIndex(focusIndex)?.Focus(NavigationMethod.Directional) ?? false))
            {
                target.Focus(NavigationMethod.Directional);
            }

            return true;
        }

        private void OnGotFocusRouted(object sender, GotFocusEventArgs e)
        {
            // A column gaining focus without a selection change (Tab, a click on an already-selected row) still becomes
            // the column New folder/Paste and the status bar target.
            if (_vm != null && FindList(e.Source as Visual, out _, out _) is ListBox list && list.DataContext is ColumnPane pane &&
                KindOf(list) == ListKind.Column)
            {
                _vm.OnColumnFocused(pane);
            }
        }

        // ------------------------------------------------------------------ opening

        /// <summary>
        /// Opens an item from a list: folders navigate, files go to their application. In Columns a folder becomes the
        /// new root (selecting it already showed its contents in the next column).
        /// </summary>
        private async Task OpenFromListAsync(ListBox list, FileListItemViewModel item)
        {
            FileListViewModel vm = _vm;
            if (vm == null || item == null)
            {
                return;
            }

            try
            {
                if (item.IsDirectory && KindOf(list) == ListKind.Column)
                {
                    await vm.LoadFolderAsync(item.FullPath);
                }
                else
                {
                    await vm.OpenItemAsync(item);
                }
            }
            catch (Exception ex)
            {
                // The view model reports expected failures itself (OpenItemFailed); this is the top of an async chain.
                Log.Logger.Error(ex, "Unexpected error opening {Path}", item.FullPath);
            }
        }

        // ------------------------------------------------------------------ This PC and Home

        private async void OnThisPcDriveClick(object sender, RoutedEventArgs e)
        {
            if (_vm == null || !(sender is Control control) || !(control.DataContext is ThisPcDriveItem drive))
            {
                return;
            }

            e.Handled = true;
            try
            {
                await _vm.OpenThisPcDriveAsync(drive);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "This PC drive open failed for {Path}", drive.RootPath);
            }
        }

        private async void OnThisPcFolderClick(object sender, RoutedEventArgs e)
        {
            string path = sender is Control control ? ResolveQuickFolder(control.Tag as string) : null;
            if (_vm == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            e.Handled = true;
            try
            {
                await _vm.LoadFolderAsync(path);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "This PC folder open failed for {Path}", path);
            }
        }

        private async void OnThisPcRecentClick(object sender, RoutedEventArgs e)
        {
            if (_vm == null || !(sender is Control control) || !(control.DataContext is ThisPcRecentItem item))
            {
                return;
            }

            e.Handled = true;
            try
            {
                await _vm.OpenThisPcRecentAsync(item);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "This PC recent open failed for {Path}", item.FullPath);
            }
        }

        private async void OnHomeFolderClick(object sender, RoutedEventArgs e)
        {
            if (_vm == null || !(sender is Control control) || !(control.DataContext is FavoriteFolderRowViewModel folder) ||
                string.IsNullOrEmpty(folder.FullPath))
            {
                return;
            }

            e.Handled = true;
            try
            {
                await _vm.LoadFolderAsync(folder.FullPath);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Home folder open failed for {Path}", folder.FullPath);
            }
        }

        private void OnHomeRecentFileClick(object sender, RoutedEventArgs e)
        {
            if (_vm == null || !(sender is Control control) || !(control.DataContext is RecentFileRowViewModel file))
            {
                return;
            }

            e.Handled = true;
            _vm.OpenRecentFile(file.FullPath, file.Name);
        }

        /// <summary>The This PC "Folders" tiles name their folder in Tag.</summary>
        private static string ResolveQuickFolder(string key)
        {
            switch (key)
            {
                case "Desktop": return SpecialFolders.Desktop;
                case "Documents": return SpecialFolders.Documents;
                case "Downloads": return SpecialFolders.Downloads;
                case "Pictures": return SpecialFolders.Pictures;
                case "Music": return SpecialFolders.Music;
                case "Videos": return SpecialFolders.Videos;
                default: return null;
            }
        }
    }
}
