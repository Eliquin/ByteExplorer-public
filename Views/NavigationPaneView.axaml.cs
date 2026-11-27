// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Menus;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Code-behind of the virtualized navigation tree (see the .axaml).
    /// <list type="bullet">
    /// <item>Navigation happens once per intent: a click navigates at once, a click on the already-selected row
    /// navigates again (re-opening it after browsing elsewhere); a selection made with the keyboard (arrows, type-to-find)
    /// navigates after <see cref="KeyboardActivationDelay"/>, so arrowing past ten folders opens one, not ten (Enter
    /// opens at once). The second click of a double-click and a click on a chevron do not navigate.</item>
    /// <item>The tree follows the active pane: whatever opened a folder (the file list, the address bar, Quick access,
    /// history), the tree expands down to it and selects its row without navigating again
    /// (NavigationSettings.ExpandTreeToCurrentFolder; off, it selects the row only when already visible).</item>
    /// <item>Middle click or Ctrl+Enter opens a folder in the other pane. Right-click opens the tree's menu (expand,
    /// other pane, refresh, collapse all) over the file menu, without selecting the row.</item>
    /// <item>Keys act on the tree's folder, not on the file list's hidden selection: Delete, Shift+Delete, F2, Ctrl+C,
    /// Ctrl+X, Ctrl+V, Ctrl+Shift+C, Alt+Enter; numpad +/- expand and collapse; F5 reloads the folder (and the list).
    /// Typing letters jumps to the next visible folder starting with them.</item>
    /// <item>Rows accept file drops; hovering a drag over a closed folder for <see cref="SpringLoadDelay"/> opens it, and
    /// dragging near the top or bottom edge scrolls.</item>
    /// </list>
    /// </summary>
    public partial class NavigationPaneView : UserControl, IFileKeyScope
    {
        /// <summary>How long a keyboard selection rests before the folder opens (Explorer waits about as long).</summary>
        public static readonly TimeSpan KeyboardActivationDelay = TimeSpan.FromMilliseconds(350);

        /// <summary>How long a drag hovers over a closed folder before it opens.</summary>
        public static readonly TimeSpan SpringLoadDelay = TimeSpan.FromMilliseconds(700);

        private const string ContextTargetClass = "contextTarget";
        private const string DropTargetClass = "dropTarget";
        private const double DragScrollEdge = 28;
        private const double DragScrollStep = 30;

        private readonly DispatcherTimer _activationTimer;
        private readonly DispatcherTimer _springTimer;
        private readonly TypeAheadSearch _typeAhead = new TypeAheadSearch();
        private bool _selectionChangedDuringGesture;
        private bool _pointerSelecting;
        private bool _syncingSelection;
        private int _gestureClickCount;
        private NavigationNodeViewModel _pendingActivation;
        private NavigationTreeRow _middlePressRow;
        private ListBoxItem _dropTarget;
        private ListBoxItem _springTarget;
        private long _lastDragScrollTicks;
        private int _lastDriveCount = -1;
        private TopLevel _topLevel;
        private MainViewModel _main;
        private FileListViewModel _observedPane;
        private NavigationViewModel _observedTree;
        private CancellationTokenSource _syncCts;

        /// <summary>
        /// The tree scrolls with the panel it is in (the Modern left panel's one scroller) instead of having a scroller of
        /// its own. It is still a virtualizing list: Avalonia's VirtualizingStackPanel creates rows from its effective
        /// viewport, which is the part the outer scroller shows, so a tree of 100,000 rows still has only the visible ones.
        /// </summary>
        public static readonly StyledProperty<bool> ScrollsWithPanelProperty =
            AvaloniaProperty.Register<NavigationPaneView, bool>(nameof(ScrollsWithPanel));

        public bool ScrollsWithPanel
        {
            get => GetValue(ScrollsWithPanelProperty);
            set => SetValue(ScrollsWithPanelProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ScrollsWithPanelProperty)
            {
                // Disabled: the list's own scroller measures it at full height (the rows' estimate), so the panel scrolls it.
                ScrollViewer.SetVerticalScrollBarVisibility(Rows, ScrollsWithPanel ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            }
        }

        /// <summary>The scroller that moves the rows: the panel's when the tree scrolls with it, else the list's own.</summary>
        private ScrollViewer RowsScroller => ScrollsWithPanel ? this.FindAncestorOfType<ScrollViewer>() : Rows.Scroll as ScrollViewer;

        /// <summary>
        /// Shows row <paramref name="index"/>. In the panel's scroller the list can't scroll itself, so once the row exists
        /// (the list creates it for ScrollIntoView) the panel is asked to show it. When the tree only follows the folder the
        /// file list opened (<paramref name="followingPane"/>), the panel moves only when that navigation didn't start in
        /// the panel (the pointer is elsewhere, as for a double-click in the file list) and the tree is in sight: a click on
        /// a Quick access pin or a lens keeps those rows under the pointer, and a panel scrolled up to them stays there.
        /// </summary>
        private void RevealRow(int index, bool followingPane = false)
        {
            if (ScrollsWithPanel && followingPane && (RowsScroller?.IsPointerOver == true || !TreeInSight()))
            {
                return;
            }

            Rows.ScrollIntoView(index);
            if (ScrollsWithPanel && RowsScroller is ScrollViewer panel && Rows.ContainerFromIndex(index) is Control row &&
                row.TranslatePoint(default, panel) is Point top)
            {
                double y = panel.Offset.Y;
                if (top.Y < 0)
                {
                    y += top.Y;
                }
                else if (top.Y + row.Bounds.Height > panel.Viewport.Height)
                {
                    y += top.Y + row.Bounds.Height - panel.Viewport.Height;
                }

                panel.Offset = new Vector(panel.Offset.X, Math.Clamp(y, 0, Math.Max(0, panel.Extent.Height - panel.Viewport.Height)));
            }
        }

        /// <summary>Whether some of the tree shows in the panel's viewport.</summary>
        private bool TreeInSight()
        {
            if (RowsScroller is not ScrollViewer panel || this.TranslatePoint(default, panel) is not Point top)
            {
                return true;
            }

            return top.Y < panel.Viewport.Height && top.Y + Bounds.Height > 0;
        }

        public NavigationPaneView()
        {
            InitializeComponent();

            _activationTimer = new DispatcherTimer { Interval = KeyboardActivationDelay };
            _activationTimer.Tick += OnActivationTimerTick;
            _springTimer = new DispatcherTimer { Interval = SpringLoadDelay };
            _springTimer.Tick += OnSpringTimerTick;

            Rows.SelectionChanged += OnSelectionChanged;
            Rows.ContextRequested += OnContextRequested;
            Rows.AddHandler(KeyDownEvent, OnRowsKeyDown, RoutingStrategies.Tunnel);
            Rows.AddHandler(TextInputEvent, OnRowsTextInput, RoutingStrategies.Tunnel);
            Rows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            Rows.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            Rows.AddHandler(PointerCaptureLostEvent, (_, _) => _pointerSelecting = false, RoutingStrategies.Bubble, handledEventsToo: true);
            Rows.AddHandler(DragDrop.DragOverEvent, OnRowsDragOver);
            Rows.AddHandler(DragDrop.DragLeaveEvent, OnRowsDragLeave);
            Rows.AddHandler(DragDrop.DropEvent, OnRowsDrop);
        }

        private NavigationViewModel ViewModel => DataContext as NavigationViewModel;

        private FileListViewModel ActiveFileList => _main?.FileListViewModel ?? (TopLevel.GetTopLevel(this)?.DataContext as MainViewModel)?.FileListViewModel;

        // ------------------------------------------------------------------ following the active pane

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel != null)
            {
                _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
            }

            ObserveMain(_topLevel?.DataContext as MainViewModel);
            ObserveTree(ViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_topLevel != null)
            {
                _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
                _topLevel = null;
            }

            ObserveMain(null);
            ObserveTree(null);
            _activationTimer.Stop();
            _springTimer.Stop();
            _syncCts?.Cancel();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_topLevel != null)
            {
                ObserveTree(ViewModel);
                ScheduleSync();
            }
        }

        private void OnTopLevelPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == DataContextProperty)
            {
                ObserveMain(_topLevel?.DataContext as MainViewModel);
            }
        }

        private void ObserveMain(MainViewModel main)
        {
            if (ReferenceEquals(_main, main))
            {
                return;
            }

            if (_main != null)
            {
                _main.PropertyChanged -= OnMainPropertyChanged;
            }

            _main = main;
            if (_main != null)
            {
                _main.PropertyChanged += OnMainPropertyChanged;
            }

            ObservePane(_main?.FileListViewModel);
        }

        private void ObservePane(FileListViewModel pane)
        {
            if (ReferenceEquals(_observedPane, pane))
            {
                return;
            }

            if (_observedPane != null)
            {
                _observedPane.PropertyChanged -= OnPanePropertyChanged;
            }

            _observedPane = pane;
            if (_observedPane != null)
            {
                _observedPane.PropertyChanged += OnPanePropertyChanged;
            }

            ScheduleSync();
        }

        private void ObserveTree(NavigationViewModel tree)
        {
            if (ReferenceEquals(_observedTree, tree))
            {
                return;
            }

            if (_observedTree != null)
            {
                _observedTree.DrivesRefreshed -= OnDrivesRefreshed;
                _observedTree.FollowSettingsChanged -= OnFollowSettingsChanged;
            }

            _observedTree = tree;
            if (_observedTree != null)
            {
                _observedTree.DrivesRefreshed += OnDrivesRefreshed;
                _observedTree.FollowSettingsChanged += OnFollowSettingsChanged;
            }
        }

        private void OnMainPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.FileListViewModel))
            {
                ObservePane(_main?.FileListViewModel); // the other pane took focus: follow it
            }
        }

        private void OnPanePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileListViewModel.CurrentPath) || e.PropertyName == nameof(FileListViewModel.IsThisPcView) ||
                e.PropertyName == nameof(FileListViewModel.IsHomeView))
            {
                ScheduleSync();
            }
        }

        /// <summary>
        /// The drive list arrives after startup's first navigation (and changes with a USB stick): sync when it changes.
        /// Not on every periodic refresh: for a folder the tree cannot show (inside a ZIP file), that would re-expand
        /// every few seconds the folders the person collapsed.
        /// </summary>
        private void OnDrivesRefreshed(object sender, IReadOnlyList<DriveNodeInfo> drives)
        {
            int count = drives?.Count ?? 0;
            if (count == _lastDriveCount)
            {
                return;
            }

            _lastDriveCount = count;
            if (Rows.SelectedItem == null && _observedPane != null && (_observedPane.CurrentPath != null || _observedPane.IsThisPcView))
            {
                ScheduleSync();
            }
        }

        private void OnFollowSettingsChanged(object sender, EventArgs e) => ScheduleSync();

        private void ScheduleSync()
        {
            _syncCts?.Cancel();
            var cts = new CancellationTokenSource();
            _syncCts = cts;
            SyncWithPaneAsync(cts).Forget("NavigationPaneView.SyncWithPaneAsync");
        }

        /// <summary>Selects (expanding to it, per the setting) the row of the place the active pane shows, without navigating.</summary>
        private async Task SyncWithPaneAsync(CancellationTokenSource cts)
        {
            // One sync per burst of property changes (a navigation raises several), after they have all landed.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            NavigationViewModel tree = ViewModel;
            FileListViewModel pane = _observedPane;
            if (cts.IsCancellationRequested || tree == null || pane == null || _activationTimer.IsEnabled || !tree.FollowCurrentFolder)
            {
                return; // superseded, the person is moving through the tree with the keyboard, or following is off
            }

            NavigationNodeViewModel node = null;
            if (pane.IsThisPcView)
            {
                node = tree.ThisPcNode;
            }
            else if (!pane.IsHomeView && !string.IsNullOrEmpty(pane.CurrentPath))
            {
                node = await tree.RevealAsync(pane.CurrentPath, tree.ExpandToCurrentFolder, cts.Token);
            }

            if (!cts.IsCancellationRequested && !_activationTimer.IsEnabled && ReferenceEquals(tree, ViewModel))
            {
                SelectWithoutActivating(node);
            }
        }

        private void SelectWithoutActivating(NavigationNodeViewModel node)
        {
            NavigationViewModel tree = ViewModel;
            if (tree == null)
            {
                return;
            }

            int index = node == null ? -1 : tree.VisibleRows.IndexOf(node);
            _syncingSelection = true;
            try
            {
                if (index < 0)
                {
                    Rows.SelectedItem = null;
                    tree.SelectedNode = null;
                }
                else
                {
                    if (Rows.SelectedIndex != index)
                    {
                        Rows.SelectedIndex = index;
                    }

                    tree.SelectedNode = node;
                    RevealRow(index, followingPane: true);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        // ------------------------------------------------------------------ selection and navigation

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NavigationViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            NavigationNodeViewModel node = (Rows.SelectedItem as NavigationTreeRow)?.Node;
            viewModel.SelectedNode = node;
            if (_syncingSelection)
            {
                return;
            }

            _selectionChangedDuringGesture = true;
            _activationTimer.Stop();

            // "Loading..." / "Timed out" rows are not places; selecting one (arrow keys pass over them) navigates nowhere.
            if (!IsNavigable(node))
            {
                return;
            }

            if (_pointerSelecting)
            {
                viewModel.NotifyNodeActivated(node);
            }
            else
            {
                _pendingActivation = node;
                _activationTimer.Start();
            }
        }

        private void OnActivationTimerTick(object sender, EventArgs e)
        {
            _activationTimer.Stop();
            NavigationNodeViewModel node = _pendingActivation;
            _pendingActivation = null;
            if (ViewModel is NavigationViewModel viewModel && node != null && ReferenceEquals(viewModel.SelectedNode, node) && IsNavigable(node))
            {
                viewModel.NotifyNodeActivated(node);
            }
        }

        /// <summary>Opens the selected row now (Enter), dropping a keyboard selection's pending delay.</summary>
        private void ActivateNow(NavigationNodeViewModel node)
        {
            _activationTimer.Stop();
            _pendingActivation = null;
            if (IsNavigable(node))
            {
                ViewModel?.NotifyNodeActivated(node);
            }
        }

        private void OnRowsPointerPressed(object sender, PointerPressedEventArgs e)
        {
            _selectionChangedDuringGesture = false;
            _gestureClickCount = e.ClickCount;
            _middlePressRow = null;

            PointerPointProperties properties = e.GetCurrentPoint(Rows).Properties;
            _pointerSelecting = properties.IsLeftButtonPressed;
            ListBoxItem item = ItemFromSource(e.Source);
            if (properties.IsRightButtonPressed && item != null)
            {
                e.Handled = true; // right-click must not select (and so navigate)
            }
            else if (properties.IsMiddleButtonPressed && item?.DataContext is NavigationTreeRow middleRow)
            {
                _middlePressRow = middleRow;
                e.Handled = true;
            }
            else if (properties.IsLeftButtonPressed && e.ClickCount == 2 && !IsInsideExpander(e.Source) &&
                     item?.DataContext is NavigationTreeRow row && row.HasChildren)
            {
                // Double-click toggles, as in a tree.
                row.Node.IsExpanded = !row.Node.IsExpanded;
                e.Handled = true;
            }
        }

        private void OnRowsPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _pointerSelecting = false;
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                NavigationTreeRow pressed = _middlePressRow;
                _middlePressRow = null;
                if (pressed != null && ReferenceEquals(ItemFromSource(e.Source)?.DataContext, pressed))
                {
                    OpenElsewhere(pressed.Node, ViewModels.Tabs.FolderOpenTarget.MiddleClick);
                    e.Handled = true;
                }

                return;
            }

            if (e.InitialPressMouseButton != MouseButton.Left || _gestureClickCount != 1 || _selectionChangedDuringGesture ||
                IsInsideExpander(e.Source))
            {
                return;
            }

            if (ItemFromSource(e.Source)?.DataContext is NavigationTreeRow row && ViewModel is NavigationViewModel viewModel &&
                ReferenceEquals(viewModel.SelectedNode, row.Node))
            {
                ActivateNow(row.Node);
            }
        }

        private void OpenInOtherPane(NavigationNodeViewModel node) => OpenElsewhere(node, ViewModels.Tabs.FolderOpenTarget.OtherPane);

        /// <summary>Opens a row's folder in the other pane, a new tab, or where a middle click opens it (Settings › Tabs).</summary>
        private void OpenElsewhere(NavigationNodeViewModel node, ViewModels.Tabs.FolderOpenTarget target)
        {
            if (node == null || node.NodeType == NavigationNodeType.Placeholder || _main == null)
            {
                return;
            }

            if (node.FullPath == null)
            {
                // This PC: a page, not a folder; it opens in a tab too.
                if (node.NodeType == NavigationNodeType.Computer && target != ViewModels.Tabs.FolderOpenTarget.OtherPane)
                {
                    _main.OpenInNewTabAsync(ViewModels.Tabs.TabLocation.ThisPc, target == ViewModels.Tabs.FolderOpenTarget.NewTab ? true : null)
                        .Forget("open This PC in a new tab");
                }

                return;
            }

            _main.OpenFolderAsync(node.FullPath, target).Forget("open a tree folder elsewhere");
        }

        // ------------------------------------------------------------------ keyboard

        /// <summary>
        /// Tree keys on a flat list: Right expands (or moves to the first child), Left collapses (or moves to the parent),
        /// Enter opens now, Ctrl+Enter opens in the other pane, numpad +/- expand and collapse. The file keys act on the
        /// selected folder (the window's own bindings would act on the file list's selection, which is not what has the
        /// keyboard here). Up/Down/Home/End/PageUp/PageDown are the list's own.
        /// </summary>
        /// <summary>
        /// Up/Down/Page Up/Page Down/Home/End when the tree scrolls with the panel: the list's own handling pages by its
        /// scroller, which is the whole tree's height there, and can't make the panel follow. A page is what the panel shows.
        /// </summary>
        private bool MoveInPanel(Key key, NavigationTreeRows rows)
        {
            int current = Rows.SelectedIndex;
            if (current < 0 || rows.Count == 0)
            {
                return false;
            }

            const double RowHeight = 30;
            int page = Math.Max(1, (int)((RowsScroller?.Viewport.Height ?? Rows.Bounds.Height) / RowHeight) - 1);
            int target = key switch
            {
                Key.Up => current - 1,
                Key.Down => current + 1,
                Key.PageUp => current - page,
                Key.PageDown => current + page,
                Key.Home => 0,
                Key.End => rows.Count - 1,
                _ => int.MinValue
            };
            if (target == int.MinValue)
            {
                return false;
            }

            SelectRow(Math.Clamp(target, 0, rows.Count - 1));
            return true;
        }

        private void OnRowsKeyDown(object sender, KeyEventArgs e)
        {
            if (!(Rows.SelectedItem is NavigationTreeRow row) || ViewModel is not NavigationViewModel viewModel)
            {
                return;
            }

            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            KeyModifiers modifiers = e.KeyModifiers;
            NavigationTreeRows rows = viewModel.VisibleRows;
            NavigationNodeViewModel node = row.Node;
            bool isFolder = node.NodeType == NavigationNodeType.Folder && node.FullPath != null;

            if (modifiers == KeyModifiers.None && ScrollsWithPanel && MoveInPanel(e.Key, rows))
            {
                e.Handled = true;
                return;
            }

            if (modifiers == KeyModifiers.None)
            {
                switch (e.Key)
                {
                    case Key.Right:
                        if (row.HasChildren && !node.IsExpanded)
                        {
                            node.IsExpanded = true;
                        }
                        else if (node.IsExpanded)
                        {
                            int index = rows.IndexOf(node);
                            if (index >= 0 && index + 1 < rows.Count && rows[index + 1].Depth > row.Depth)
                            {
                                SelectRow(index + 1);
                            }
                        }

                        e.Handled = true;
                        return;

                    case Key.Left:
                        if (node.IsExpanded)
                        {
                            node.IsExpanded = false;
                        }
                        else if (rows.ParentOf(row) is NavigationTreeRow parent)
                        {
                            SelectRow(rows.IndexOf(parent.Node));
                        }

                        e.Handled = true;
                        return;

                    case Key.Add:
                        node.IsExpanded = row.HasChildren || node.IsExpanded;
                        e.Handled = true;
                        return;

                    case Key.Subtract:
                        node.IsExpanded = false;
                        e.Handled = true;
                        return;

                    case Key.Enter:
                        ActivateNow(node);
                        e.Handled = true;
                        return;

                    case Key.Escape:
                        _activationTimer.Stop(); // stay where the pane is
                        _pendingActivation = null;
                        ScheduleSync();
                        e.Handled = true;
                        return;

                    case Key.F5:
                        Refresh(node);
                        return; // not handled: the window's F5 refreshes the file list too

                    case Key.F2:
                        if (isFolder)
                        {
                            ActiveFileList?.PromptRenamePathAsync(node.FullPath).Forget("PromptRenamePathAsync");
                        }

                        e.Handled = true;
                        return;

                    case Key.Delete:
                        DeleteFolder(node, permanent: false);
                        e.Handled = true;
                        return;
                }
            }
            else if (modifiers == KeyModifiers.Shift && e.Key == Key.Delete)
            {
                DeleteFolder(node, permanent: true);
                e.Handled = true;
            }
            else if (modifiers == command && e.Key == Key.Enter)
            {
                OpenInOtherPane(node);
                e.Handled = true;
            }
            else if (modifiers == command && (e.Key == Key.C || e.Key == Key.X))
            {
                if (node.FullPath != null && node.NodeType != NavigationNodeType.Placeholder)
                {
                    if (e.Key == Key.C)
                    {
                        ClipboardFileService.SetFilesForCopy(new[] { node.FullPath });
                    }
                    else if (isFolder)
                    {
                        ClipboardFileService.SetFilesForCut(new[] { node.FullPath });
                    }
                }

                e.Handled = true;
            }
            else if (modifiers == command && e.Key == Key.V)
            {
                if (node.FullPath != null && !node.IsUnreachable && ActiveFileList is FileListViewModel fileList)
                {
                    FileContextMenu.PasteIntoAsync(fileList, node.FullPath).Forget("PasteIntoAsync");
                }

                e.Handled = true;
            }
            else if (modifiers == (command | KeyModifiers.Shift) && e.Key == Key.C)
            {
                if (node.FullPath != null)
                {
                    ClipboardFileService.SetText(node.FullPath);
                }

                e.Handled = true;
            }
            else if (modifiers == KeyModifiers.Alt && e.Key == Key.Enter)
            {
                if (node.FullPath != null && ActiveFileList?.DialogService is IDialogService dialogs)
                {
                    dialogs.ShowProperties(new[] { node.FullPath });
                }

                e.Handled = true;
            }
        }

        /// <summary>Type-to-find over the visible rows (the rules are <see cref="TypeAheadSearch"/>'s).</summary>
        private void OnRowsTextInput(object sender, TextInputEventArgs e)
        {
            NavigationViewModel viewModel = ViewModel;
            string text = e.Text;
            long now = DateTime.UtcNow.Ticks;
            if (viewModel == null || string.IsNullOrEmpty(text) || char.IsControl(text[0]) ||
                (text == " " && !_typeAhead.IsActive(now)))
            {
                return;
            }

            NavigationTreeRows rows = viewModel.VisibleRows;
            int index = _typeAhead.Next(text, Rows.SelectedIndex, rows.Count,
                i => rows[i].Node.NodeType == NavigationNodeType.Placeholder ? null : rows[i].Node.Name, now);
            e.Handled = true;
            if (index >= 0)
            {
                SelectRow(index);
            }
        }

        private void SelectRow(int index)
        {
            if (index < 0)
            {
                return;
            }

            Rows.SelectedIndex = index;
            RevealRow(index);
            (Rows.ContainerFromIndex(index) as Control)?.Focus(NavigationMethod.Directional);
        }

        private void Refresh(NavigationNodeViewModel node)
        {
            if (node.NodeType == NavigationNodeType.Computer || IsDriveLevelNode(node.NodeType))
            {
                ViewModel?.RequestImmediateDriveRefresh();
            }

            if (node.FullPath != null)
            {
                node.RequestReload();
            }
        }

        private void DeleteFolder(NavigationNodeViewModel node, bool permanent)
        {
            if (node.NodeType != NavigationNodeType.Folder || node.FullPath == null || ActiveFileList is not FileListViewModel fileList)
            {
                return; // drives and This PC are not deleted from here
            }

            fileList.DeletePathsAsync(new[] { node.FullPath }, permanent).Forget("DeletePathsAsync");
        }

        // ------------------------------------------------------------------ context menu

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            ListBoxItem item = ItemFromSource(e.Source);
            if (item?.DataContext is not NavigationTreeRow row || !IsNavigable(row.Node))
            {
                return;
            }

            NavigationNodeViewModel node = row.Node;
            e.Handled = true;
            bool atPointer = e.TryGetPosition(item, out _);

            if (!node.IsUnreachable && node.FullPath != null && ActiveFileList is FileListViewModel shellPane &&
                FileContextMenu.TryShowShellMenu(item, shellPane, new[] { node.FullPath }, pathsAreSelection: false))
            {
                return; // Right-click menu: Windows (or Shift+right-click in Hybrid)
            }

            var items = new List<Control>(BuildTreeItems(row));
            if (node.IsUnreachable)
            {
                items.Insert(0, MenuItemFor("Retry", Symbol.ArrowClockwise, () => Refresh(node)));
            }
            else if (node.FullPath != null && ActiveFileList is FileListViewModel fileList)
            {
                ContextMenu fileMenu = FileContextMenu.ForItems(item, fileList, new[] { node.FullPath }, pathsAreSelection: false);
                if (fileMenu?.ItemsSource is IEnumerable<Control> fileItems)
                {
                    items.Add(new Separator());
                    items.AddRange(fileItems.ToList());
                    fileMenu.ItemsSource = null;
                }
            }

            OpenMenu(new ContextMenu { ItemsSource = items }, item, atPointer);
        }

        /// <summary>The tree's own commands, above the file menu.</summary>
        private IEnumerable<Control> BuildTreeItems(NavigationTreeRow row)
        {
            NavigationNodeViewModel node = row.Node;
            if (row.HasChildren)
            {
                yield return node.IsExpanded
                    ? MenuItemFor("Collapse", Symbol.ChevronUp, () => node.IsExpanded = false, "Left")
                    : MenuItemFor("Expand", Symbol.ChevronDown, () => node.IsExpanded = true, "Right");
            }

            if (node.FullPath != null && _main != null)
            {
                yield return MenuItemFor("Open in new tab", Symbol.TabAdd, () => OpenElsewhere(node, ViewModels.Tabs.FolderOpenTarget.NewTab),
                    Infrastructure.TabOptions.MiddleClickOpensOtherPane ? null : "Middle-click");
                yield return MenuItemFor("Open in other pane", Symbol.SplitVertical, () => OpenInOtherPane(node),
                    OperatingSystem.IsMacOS() ? "Cmd+Enter" : "Ctrl+Enter");
            }

            yield return MenuItemFor(node.NodeType == NavigationNodeType.Computer ? "Refresh drives" : "Refresh", Symbol.ArrowClockwise,
                () => Refresh(node), "F5");
            yield return MenuItemFor("Collapse all folders", Symbol.ArrowUp, () =>
            {
                ViewModel?.CollapseAll();
                ScheduleSync();
            });
        }

        private static MenuItem MenuItemFor(string header, Symbol symbol, Action action, string gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture);
                }
                catch (ArgumentException)
                {
                    ToolTip.SetTip(item, gesture); // "Middle-click" isn't a key gesture: shown as a tip instead
                }
            }

            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "A navigation tree menu command failed");
                }
            };
            return item;
        }

        private static void OpenMenu(ContextMenu menu, ListBoxItem item, bool atPointer)
        {
            menu.Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementTarget = item;
            item.Classes.Add(ContextTargetClass);
            menu.Closed += (_, _) =>
            {
                item.Classes.Remove(ContextTargetClass);
                menu.ItemsSource = null;
            };

            try
            {
                menu.Open(item);
            }
            catch (Exception ex)
            {
                item.Classes.Remove(ContextTargetClass);
                Log.Logger.Warning(ex, "Failed to open the navigation tree's context menu");
            }
        }

        // ------------------------------------------------------------------ drag and drop

        private void OnRowsDragOver(object sender, DragEventArgs e)
        {
            ScrollNearEdge(e.GetPosition(RowsScroller ?? (Visual)Rows));
            ListBoxItem item = ItemFromSource(e.Source);
            SpringLoad(item);
            if (!TryGetDropFolder(item, out string folder) || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
            {
                SetDropTarget(null);
                return;
            }

            e.Handled = true;
            e.DragEffects = FileDragDropHelper.ResolveEffect(e, folder, paths);
            SetDropTarget(e.DragEffects != DragDropEffects.None ? item : null);
        }

        private void OnRowsDragLeave(object sender, DragEventArgs e)
        {
            // Leave also bubbles up from the parts of a row the pointer moves between; only leaving the list ends the hover.
            Point position = e.GetPosition(Rows);
            if (position.X < 0 || position.Y < 0 || position.X >= Rows.Bounds.Width || position.Y >= Rows.Bounds.Height)
            {
                SetDropTarget(null);
                SpringLoad(null);
            }
        }

        private async void OnRowsDrop(object sender, DragEventArgs e)
        {
            try
            {
                SetDropTarget(null);
                SpringLoad(null);
                ListBoxItem item = ItemFromSource(e.Source);
                if (!TryGetDropFolder(item, out string folder) || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
                {
                    return;
                }

                e.Handled = true;
                FileListViewModel fileList = ActiveFileList;
                if (fileList == null)
                {
                    e.DragEffects = DragDropEffects.None;
                    return;
                }

                await Menus.DropChoice.RunAsync(Rows, e, folder, paths, fileList);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Dropping files on a tree node failed");
            }
        }

        /// <summary>Starts (or keeps) the hover timer of a closed folder under a drag; anything else stops it.</summary>
        private void SpringLoad(ListBoxItem item)
        {
            bool canOpen = item?.DataContext is NavigationTreeRow row && row.HasChildren && !row.Node.IsExpanded && !row.Node.IsUnreachable;
            if (!canOpen)
            {
                _springTimer.Stop();
                _springTarget = null;
                return;
            }

            if (!ReferenceEquals(_springTarget, item))
            {
                _springTarget = item;
                _springTimer.Stop();
                _springTimer.Start();
            }
        }

        private void OnSpringTimerTick(object sender, EventArgs e)
        {
            _springTimer.Stop();
            if (_springTarget?.DataContext is NavigationTreeRow row && row.HasChildren)
            {
                row.Node.IsExpanded = true;
            }

            _springTarget = null;
        }

        /// <summary>Scrolls the tree while a drag rests near its top or bottom edge (at most every 60 ms).</summary>
        private void ScrollNearEdge(Point position)
        {
            if (RowsScroller is not ScrollViewer scroller)
            {
                return;
            }

            double delta = position.Y < DragScrollEdge ? -DragScrollStep
                : position.Y > scroller.Bounds.Height - DragScrollEdge ? DragScrollStep
                : 0;
            long now = Environment.TickCount64;
            if (delta == 0 || now - _lastDragScrollTicks < 60)
            {
                return;
            }

            _lastDragScrollTicks = now;
            double maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(scroller.Offset.Y + delta, 0, maxY));
        }

        private void SetDropTarget(ListBoxItem item)
        {
            if (ReferenceEquals(_dropTarget, item))
            {
                return;
            }

            _dropTarget?.Classes.Remove(DropTargetClass);
            _dropTarget = item;
            _dropTarget?.Classes.Add(DropTargetClass);
        }

        private static bool TryGetDropFolder(ListBoxItem item, out string folder)
        {
            folder = null;
            if (item?.DataContext is not NavigationTreeRow row || !IsNavigable(row.Node) || row.Node.IsUnreachable || row.Node.FullPath == null)
            {
                return false;
            }

            folder = row.Node.FullPath;
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static bool IsNavigable(NavigationNodeViewModel node) =>
            node != null && node.NodeType != NavigationNodeType.Placeholder &&
            (node.FullPath != null || node.NodeType == NavigationNodeType.Computer);

        private static bool IsDriveLevelNode(NavigationNodeType nodeType) =>
            nodeType == NavigationNodeType.DriveFixed
            || nodeType == NavigationNodeType.DriveRemovable
            || nodeType == NavigationNodeType.DriveNetwork
            || nodeType == NavigationNodeType.DriveOptical
            || nodeType == NavigationNodeType.DriveUnknown
            || nodeType == NavigationNodeType.Phone;

        private static ListBoxItem ItemFromSource(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is ListBoxItem item)
                {
                    return item;
                }

                if (visual is ListBox)
                {
                    return null;
                }
            }

            return null;
        }

        private static bool IsInsideExpander(object source)
        {
            for (Visual visual = source as Visual; visual != null && visual is not ListBoxItem; visual = visual.GetVisualParent())
            {
                if (visual is ToggleButton)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
