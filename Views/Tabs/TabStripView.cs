// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Tabs;
using Serilog;

namespace ByteExplorer.Views.Tabs
{
    /// <summary>
    /// The tab bar (DataContext: <see cref="TabsViewModel"/>): a row above the file list, or a vertical list beside it
    /// (Settings › Tabs, or the bar's own menu), with what people expect from Edge, Chrome and Windows Explorer:
    /// <list type="bullet">
    /// <item>Click selects (on press, as browsers do); drag reorders, the other tabs sliding out of the way; pinned and other
    /// tabs keep to their own side.</item>
    /// <item>The close button, a middle click, or Ctrl+W closes; closing several with the mouse keeps the tabs' width until
    /// the pointer leaves the bar, so the next close button is under the pointer.</item>
    /// <item>Double click (or middle click) on the bar's empty part, or +, opens a new tab.</item>
    /// <item>Right click: the tab's menu (duplicate, pin, close others/to one side, reopen closed, the bar's position).</item>
    /// <item>Files dragged over a tab: after a moment the tab opens (spring-loaded, as folders do); dropped on it, they're
    /// copied or moved into its folder. Folders dropped on + or the bar's empty part open in new tabs.</item>
    /// <item>Horizontal: tabs shrink to a minimum, then the row scrolls (wheel); an "all tabs" menu appears.
    /// Vertical: resized by its edge, shrunk to icons by its button.</item>
    /// </list>
    /// </summary>
    public sealed class TabStripView : UserControl
    {
        private const double CollapsedWidth = 52;
        private static readonly TimeSpan SpringDelay = TimeSpan.FromMilliseconds(650);

        private readonly Dictionary<TabViewModel, TabItemView> _views = new Dictionary<TabViewModel, TabItemView>();
        private readonly DispatcherTimer _springTimer;
        private TabsViewModel _vm;
        private TabStripPanel _panel;
        private Border _root;
        private Button _addButton;
        private Button _overflowButton;
        private Button _newTabRow;
        private Button _collapseButton;
        private TextBlock _header;
        private ScrollViewer _verticalScroller;
        private bool _builtVertical;

        // Pointer gesture state.
        private TabItemView _pressed;
        private TabItemView _middlePressed;
        private TabItemView _rightPressed;
        private Point _pressPoint;
        private double _grabOffset;
        private bool _dragging;
        private bool _resizing;
        private double _resizeStartX;
        private double _resizeStartWidth;
        private TabViewModel _springTab;
        private TabItemView _hovered;

        public TabStripView()
        {
            Focusable = false;
            _springTimer = new DispatcherTimer { Interval = SpringDelay };
            _springTimer.Tick += (_, _) =>
            {
                _springTimer.Stop();
                if (_springTab != null && !_springTab.IsClosed)
                {
                    _vm?.Select(_springTab);
                }
            };

            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(PointerPressedEvent, OnPointerPressedHandler, RoutingStrategies.Bubble);
            AddHandler(PointerMovedEvent, OnPointerMovedHandler, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnPointerReleasedHandler, RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, (_, _) => EndGesture(commitDrag: true), RoutingStrategies.Bubble);
            PointerExited += (_, _) => Unfreeze();
            PointerWheelChanged += OnWheel;
        }

        /// <summary>The window's view model: drops on a tab go through its operations queue.</summary>
        public MainViewModel Main { get; set; }

        /// <summary>The window can put the tabs in its title bar (the Modern layout): the bar's menu offers it.</summary>
        public bool SupportsTitleBar { get; set; }

        /// <summary>
        /// The bar sits in the window's title bar: it takes the title bar's height and color, the selected tab the address
        /// bar's (so it reads as the front of the address bar below, as in Windows 11), and its empty parts let the
        /// pointer through to the title bar, which moves the window.
        /// </summary>
        public bool IsInTitleBar
        {
            get => _isInTitleBar;
            set
            {
                if (_isInTitleBar == value)
                {
                    return;
                }

                _isInTitleBar = value;
                HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                ApplyPlacementClasses();
            }
        }

        private bool _isInTitleBar;

        /// <summary>A tab was chosen with the pointer: the window gives the file list the keyboard.</summary>
        public event EventHandler ContentFocusRequested;

        /// <summary>Tabs slide when they move (off in tests, which look at the layout right after a change).</summary>
        public bool AnimateMoves
        {
            get => _panel?.AnimateMoves ?? true;
            set
            {
                _animate = value;
                if (_panel != null)
                {
                    _panel.AnimateMoves = value;
                }
            }
        }

        private bool _animate = true;

        internal TabStripPanel Panel => _panel;

        internal IReadOnlyList<TabItemView> ItemViews => _panel?.Children.OfType<TabItemView>().ToList() ?? new List<TabItemView>();

        // ------------------------------------------------------------------ view model

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Attach(DataContext as TabsViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _springTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        private void Attach(TabsViewModel viewModel)
        {
            if (ReferenceEquals(viewModel, _vm))
            {
                return;
            }

            if (_vm != null)
            {
                ((INotifyCollectionChanged)_vm.Tabs).CollectionChanged -= OnTabsChanged;
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            foreach (TabItemView view in _views.Values)
            {
                view.Detach();
            }

            _views.Clear();
            _vm = viewModel;
            if (_vm != null)
            {
                ((INotifyCollectionChanged)_vm.Tabs).CollectionChanged += OnTabsChanged;
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }

            Build();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TabsViewModel.IsVertical):
                case nameof(TabsViewModel.Placement):
                    if (_vm.IsVertical != _builtVertical)
                    {
                        Build();
                    }
                    else
                    {
                        ApplyPlacementClasses();
                    }

                    break;
                case nameof(TabsViewModel.IsCollapsed):
                case nameof(TabsViewModel.VerticalWidth):
                    ApplyVerticalSize();
                    break;
                case nameof(TabsViewModel.IsStripVisible):
                    IsVisible = _vm.IsStripVisible;
                    break;
                case nameof(TabsViewModel.SelectedTab):
                    UpdateNeighbourClasses();
                    BringSelectedIntoView();
                    break;
                case nameof(TabsViewModel.Count):
                    if (_header != null)
                    {
                        _header.Text = "Tabs (" + _vm.Count + ")";
                    }

                    break;
            }
        }

        private void OnTabsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            SyncChildren();
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                BringSelectedIntoView();
            }
        }

        // ------------------------------------------------------------------ building

        private void Build()
        {
            EndGesture(commitDrag: false);
            _panel?.Children.Clear();
            if (_vm == null)
            {
                Content = null;
                return;
            }

            _builtVertical = _vm.IsVertical;
            _panel = new TabStripPanel
            {
                Orientation = _builtVertical ? Orientation.Vertical : Orientation.Horizontal,
                IsPinnedSelector = c => c is TabItemView view && view.Tab.IsPinned,
                AnimateMoves = _animate
            };
            _panel.OverflowChanged += (_, _) => UpdateOverflowButton();

            _root = _builtVertical ? BuildVertical() : BuildHorizontal();
            Content = _root;
            IsVisible = _vm.IsStripVisible;
            ApplyPlacementClasses();
            ApplyVerticalSize();
            SyncChildren();
        }

        private Border BuildHorizontal()
        {
            _addButton = new Button { Classes = { "cmd", "tabBarButton" }, Content = new SymbolIcon { Symbol = Symbol.Add, FontSize = 15 }, Focusable = false, Margin = new Thickness(4, 0, 0, 2) };
            ToolTip.SetTip(_addButton, "New tab (" + Modifier + "+T)");
            _addButton.Click += (_, _) => _vm?.NewTabCommand.Execute(null);

            _overflowButton = new Button { Classes = { "cmd", "tabBarButton" }, Content = new SymbolIcon { Symbol = Symbol.ChevronDown, FontSize = 13 }, Focusable = false, IsVisible = false, Margin = new Thickness(2, 0, 0, 2) };
            ToolTip.SetTip(_overflowButton, "All tabs");
            _overflowButton.Click += (_, _) => ShowAllTabsMenu(_overflowButton);

            _newTabRow = null;
            _groupLine = null;
            _resizer = null;
            _collapseButton = null;
            _header = null;
            _verticalScroller = null;
            return new Border
            {
                Classes = { "tabBar", "horizontal" },
                Child = new TabRowPanel { Children = { _panel, _addButton, _overflowButton } }
            };
        }

        private Border BuildVertical()
        {
            _collapseButton = new Button { Classes = { "cmd", "tabBarButton" }, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
            _collapseButton.Click += (_, _) => _vm?.ToggleCollapsedCommand.Execute(null);
            _header = new TextBlock { Classes = { "tabBarHeader" }, Text = "Tabs (" + _vm.Count + ")", Margin = new Thickness(6, 0, 0, 0) };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 8, 8, 4), Height = 32 };
            header.Children.Add(_collapseButton);
            Grid.SetColumn(_header, 1);
            header.Children.Add(_header);

            _newTabRow = new Button { Classes = { "cmd", "newTabRow" }, Focusable = false };
            ToolTip.SetTip(_newTabRow, "New tab (" + Modifier + "+T)");
            _newTabRow.Click += (_, _) => _vm?.NewTabCommand.Execute(null);

            _groupLine = new Border { Classes = { "tabGroupLine" }, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, IsVisible = false };
            _verticalScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { new Panel { Children = { _panel, _groupLine } }, _newTabRow }, Margin = new Thickness(0, 0, 0, 8) }
            };

            var resizer = new Border { Classes = { "tabResizer" } };
            ToolTip.SetTip(resizer, "Drag to resize");
            resizer.PointerPressed += OnResizerPressed;
            resizer.PointerMoved += OnResizerMoved;
            resizer.PointerReleased += OnResizerReleased;
            resizer.PointerCaptureLost += (_, _) => _resizing = false;
            _resizer = resizer;

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            grid.Children.Add(header);
            Grid.SetRow(_verticalScroller, 1);
            grid.Children.Add(_verticalScroller);
            grid.Children.Add(resizer);
            Grid.SetRowSpan(resizer, 2);

            var root = new Border { Classes = { "tabBar", "vertical" }, Child = grid };
            root.Transitions = new Transitions { new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(140) } };
            return root;
        }

        private Border _resizer;
        private Border _groupLine;

        private static string Modifier => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

        private void ApplyPlacementClasses()
        {
            if (_root == null || _vm == null)
            {
                return;
            }

            _root.Classes.Set("left", _vm.IsLeft);
            _root.Classes.Set("right", _vm.IsRight);
            _root.Classes.Set("titleBar", _isInTitleBar && !_builtVertical);
            if (_resizer != null)
            {
                _resizer.HorizontalAlignment = _vm.IsRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            }

            ApplyVerticalSize();
        }

        private void ApplyVerticalSize()
        {
            if (_root == null || _vm == null || !_builtVertical)
            {
                return;
            }

            bool collapsed = _vm.IsCollapsed;
            _root.Width = collapsed ? CollapsedWidth : _vm.VerticalWidth;
            if (_resizer != null)
            {
                _resizer.IsVisible = !collapsed;
            }

            if (_collapseButton != null)
            {
                bool right = _vm.IsRight;
                _collapseButton.Content = new SymbolIcon
                {
                    Symbol = collapsed
                        ? (right ? Symbol.PanelRightExpand : Symbol.PanelLeftExpand)
                        : (right ? Symbol.PanelRightContract : Symbol.PanelLeftContract),
                    FontSize = 16
                };
                ToolTip.SetTip(_collapseButton, collapsed ? "Expand the tab bar" : "Collapse the tab bar to icons");
                _collapseButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }

            if (_header != null)
            {
                _header.IsVisible = !collapsed;
                if (_header.Parent is Grid header)
                {
                    header.Margin = collapsed ? new Thickness(0, 8, 0, 4) : new Thickness(8, 8, 8, 4);
                    Grid.SetColumnSpan(_collapseButton, collapsed ? 2 : 1);
                }
            }

            if (_newTabRow != null)
            {
                _newTabRow.Classes.Set("collapsed", collapsed);
                var icon = new SymbolIcon { Symbol = Symbol.Add, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                _newTabRow.Content = collapsed
                    ? icon
                    : new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = "New tab", Margin = new Thickness(10, 0, 0, 0), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center } } };
            }

            foreach (TabItemView view in _views.Values)
            {
                view.SetMode(vertical: true, collapsed);
            }
        }

        /// <summary>Puts the bar's tab views in the tabs' order, making views for new tabs and dropping those of closed ones.</summary>
        private void SyncChildren()
        {
            if (_panel == null || _vm == null)
            {
                return;
            }

            var alive = new HashSet<TabViewModel>(_vm.Tabs);
            foreach (TabViewModel gone in _views.Keys.Where(t => !alive.Contains(t)).ToList())
            {
                TabItemView view = _views[gone];
                view.Detach();
                _panel.Children.Remove(view);
                _views.Remove(gone);
                if (ReferenceEquals(_hovered, view))
                {
                    _hovered = null;
                }
            }

            Avalonia.Controls.Controls children = _panel.Children;
            for (int i = 0; i < _vm.Tabs.Count; i++)
            {
                TabViewModel tab = _vm.Tabs[i];
                if (!_views.TryGetValue(tab, out TabItemView view))
                {
                    view = new TabItemView(tab);
                    view.CloseButton.Click += (_, _) => CloseWithPointer(view.Tab);
                    view.PointerEntered += (_, _) => SetHovered(view);
                    view.PinnedChanged += (_, _) =>
                    {
                        _panel?.InvalidateMeasure();
                        UpdateGroupLine();
                    };
                    view.PointerExited += (_, _) => SetHovered(ReferenceEquals(_hovered, view) ? null : _hovered);
                    _views[tab] = view;
                }

                view.SetMode(_builtVertical, _builtVertical && _vm.IsCollapsed);
                int at = children.IndexOf(view);
                if (at == i)
                {
                    continue;
                }

                if (at >= 0)
                {
                    children.Move(at, i);
                }
                else
                {
                    children.Insert(i, view);
                }
            }

            while (children.Count > _vm.Tabs.Count)
            {
                children.RemoveAt(children.Count - 1);
            }

            _panel.InvalidateMeasure();
            UpdateNeighbourClasses();
            UpdateGroupLine();
        }

        /// <summary>Vertical: the line between pinned and other tabs (when there are both).</summary>
        private void UpdateGroupLine()
        {
            if (_groupLine == null || _panel == null)
            {
                return;
            }

            double y = _panel.GroupLineY;
            _groupLine.IsVisible = !double.IsNaN(y);
            if (_groupLine.IsVisible)
            {
                _groupLine.Margin = new Thickness(12, y, 12, 0);
            }
        }

        private void SetHovered(TabItemView view)
        {
            _hovered = view;
            UpdateNeighbourClasses();
        }

        /// <summary>The tab before the selected or pointed-at one hides its separator (no line next to a highlighted tab).</summary>
        private void UpdateNeighbourClasses()
        {
            if (_panel == null)
            {
                return;
            }

            Avalonia.Controls.Controls children = _panel.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is TabItemView view)
                {
                    TabItemView next = i + 1 < children.Count ? children[i + 1] as TabItemView : null;
                    view.Classes.Set("beforeSelected", next?.Tab.IsSelected == true);
                    view.Classes.Set("beforeHover", next != null && ReferenceEquals(next, _hovered));
                }
            }
        }

        private void BringSelectedIntoView()
        {
            if (_panel == null || _vm?.SelectedTab == null || !_views.TryGetValue(_vm.SelectedTab, out TabItemView view))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                int index = _panel.Children.IndexOf(view);
                if (index < 0)
                {
                    return;
                }

                if (_builtVertical)
                {
                    view.BringIntoView();
                }
                else
                {
                    _panel.BringIntoView(index);
                }
            }, DispatcherPriority.Loaded);
        }

        private void UpdateOverflowButton()
        {
            if (_overflowButton != null && _panel != null)
            {
                _overflowButton.IsVisible = _panel.IsOverflowing;
            }
        }

        // ------------------------------------------------------------------ pointer: select, drag, close, menus

        private TabItemView ItemFrom(object source)
        {
            for (Visual visual = source as Visual; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            {
                if (visual is TabItemView view)
                {
                    return view;
                }

                if (visual is Button && !(visual.GetVisualParent() is null) && ReferenceEquals(visual, _addButton))
                {
                    return null;
                }
            }

            return null;
        }

        private static bool IsInside(object source, Visual container)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (ReferenceEquals(visual, container))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether a pointer event is on the bar's empty part (not a tab, a button or the resize edge).</summary>
        private bool IsOnEmptyBar(object source)
        {
            for (Visual visual = source as Visual; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            {
                if (visual is TabItemView || visual is Button || visual is ScrollBar || ReferenceEquals(visual, _resizer))
                {
                    return false;
                }
            }

            return true;
        }

        private void OnPointerPressedHandler(object sender, PointerPressedEventArgs e)
        {
            if (_vm == null || _resizing)
            {
                return;
            }

            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
            TabItemView item = ItemFrom(e.Source);
            if (item != null && IsInside(e.Source, item.CloseButton))
            {
                return; // the close button's own click
            }

            if (item == null)
            {
                if (!IsOnEmptyBar(e.Source))
                {
                    return;
                }

                if ((properties.IsLeftButtonPressed && e.ClickCount == 2) || properties.IsMiddleButtonPressed)
                {
                    _vm.NewTabCommand.Execute(null); // double click (or middle click) on the empty bar: a new tab, as in browsers
                    e.Handled = true;
                }
                else if (properties.IsRightButtonPressed)
                {
                    e.Handled = true;
                    ShowBarMenu(this);
                }

                return;
            }

            e.Handled = true;
            if (properties.IsLeftButtonPressed)
            {
                bool wasSelected = item.Tab.IsSelected;
                _vm.Select(item.Tab); // on press, as browsers do: the tab shows while the button is still down
                _pressed = item;
                _pressPoint = e.GetPosition(this);
                (double start, _) = _panel.SlotOf(_panel.Children.IndexOf(item));
                _grabOffset = AlongAxis(e.GetPosition(_panel)) + (_builtVertical ? 0 : _panel.ScrollOffset) - start;
                e.Pointer.Capture(this);
                if (!wasSelected || e.ClickCount == 1)
                {
                    ContentFocusRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (properties.IsMiddleButtonPressed)
            {
                _middlePressed = item;
            }
            else if (properties.IsRightButtonPressed)
            {
                _rightPressed = item;
            }
        }

        private double AlongAxis(Point point) => _builtVertical ? point.Y : point.X;

        private void OnPointerMovedHandler(object sender, PointerEventArgs e)
        {
            if (_pressed == null || _panel == null || _vm == null)
            {
                return;
            }

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndGesture(commitDrag: true);
                return;
            }

            if (!_dragging)
            {
                if (!FileDragDropHelper.HasMovedPastDragThreshold(_pressPoint, e.GetPosition(this)))
                {
                    return;
                }

                _dragging = true;
                _panel.DragChild = _pressed;
                _pressed.Classes.Add("dragging");
            }

            int index = _panel.Children.IndexOf(_pressed);
            if (index < 0)
            {
                EndGesture(commitDrag: false);
                return;
            }

            double scroll = _builtVertical ? 0 : _panel.ScrollOffset;
            (double _, double size) = _panel.SlotOf(index);
            (double lastStart, double lastSize) = _panel.SlotOf(_panel.Children.Count - 1);
            double position = Math.Clamp(AlongAxis(e.GetPosition(_panel)) + scroll - _grabOffset, 0, Math.Max(0, lastStart + lastSize - size));
            _panel.DragPosition = position;

            // Over the middle of a neighbour: it moves (the model keeps pinned and other tabs apart).
            int target = _panel.IndexAt(position + size / 2);
            if (target != index)
            {
                _vm.Move(_pressed.Tab, target);
            }

            // Near an end of a scrolled row: scroll along.
            if (!_builtVertical && _panel.IsOverflowing)
            {
                double x = e.GetPosition(_panel).X;
                if (x < 24)
                {
                    _panel.ScrollOffset -= 12;
                }
                else if (x > _panel.Bounds.Width - 24)
                {
                    _panel.ScrollOffset += 12;
                }
            }

            _panel.InvalidateArrange();
        }

        private void OnPointerReleasedHandler(object sender, PointerReleasedEventArgs e)
        {
            if (_vm == null)
            {
                return;
            }

            TabItemView item = ItemFrom(e.Source);
            if (e.InitialPressMouseButton == MouseButton.Middle && _middlePressed != null)
            {
                TabItemView pressed = _middlePressed;
                _middlePressed = null;
                if (ReferenceEquals(item, pressed))
                {
                    e.Handled = true;
                    CloseWithPointer(pressed.Tab); // middle click closes, as in every browser
                }

                return;
            }

            if (e.InitialPressMouseButton == MouseButton.Right && _rightPressed != null)
            {
                TabItemView pressed = _rightPressed;
                _rightPressed = null;
                if (ReferenceEquals(item, pressed))
                {
                    e.Handled = true;
                    ShowTabMenu(pressed);
                }

                return;
            }

            if (e.InitialPressMouseButton == MouseButton.Left && _pressed != null)
            {
                e.Pointer.Capture(null);
                EndGesture(commitDrag: true);
            }
        }

        private void EndGesture(bool commitDrag)
        {
            TabItemView pressed = _pressed;
            _pressed = null;
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (_panel != null)
            {
                _panel.DragChild = null; // it slides from where it was dropped into its slot
                _panel.InvalidateArrange();
            }

            pressed?.Classes.Remove("dragging");
        }

        /// <summary>Closes a tab with the pointer: a horizontal row keeps its tabs' width until the pointer leaves it.</summary>
        private void CloseWithPointer(TabViewModel tab)
        {
            if (_vm == null || tab == null)
            {
                return;
            }

            if (!_builtVertical && _panel != null && _vm.Count > 1 && !tab.IsPinned)
            {
                _panel.FreezeTabWidth = _panel.CurrentTabWidth;
            }

            _vm.CloseTab(tab);
        }

        private void Unfreeze()
        {
            if (_panel != null && !double.IsNaN(_panel.FreezeTabWidth))
            {
                _panel.FreezeTabWidth = double.NaN;
                _panel.InvalidateMeasure();
            }
        }

        private void OnWheel(object sender, PointerWheelEventArgs e)
        {
            if (_builtVertical || _panel == null || !_panel.IsOverflowing)
            {
                return;
            }

            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            _panel.ScrollOffset -= delta * 60;
            e.Handled = true;
        }

        // ------------------------------------------------------------------ vertical resize

        private void OnResizerPressed(object sender, PointerPressedEventArgs e)
        {
            if (_vm == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _resizing = true;
            _resizeStartX = e.GetPosition(TopLevel.GetTopLevel(this) as Visual ?? this).X;
            _resizeStartWidth = _root.Bounds.Width;
            _root.Transitions = null; // follow the pointer exactly
            e.Pointer.Capture((IInputElement)sender);
            e.Handled = true;
        }

        private void OnResizerMoved(object sender, PointerEventArgs e)
        {
            if (!_resizing || _vm == null)
            {
                return;
            }

            double dx = e.GetPosition(TopLevel.GetTopLevel(this) as Visual ?? this).X - _resizeStartX;
            double width = _resizeStartWidth + (_vm.IsRight ? -dx : dx);
            _root.Width = Math.Clamp(width, TabSettings.MinVerticalWidth, TabSettings.MaxVerticalWidth);
        }

        private void OnResizerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!_resizing)
            {
                return;
            }

            _resizing = false;
            e.Pointer.Capture(null);
            if (_vm != null)
            {
                _vm.VerticalWidth = _root.Width; // saved once, at the end of the drag
            }

            _root.Transitions = new Transitions { new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(140) } };
        }

        // ------------------------------------------------------------------ menus

        private void ShowTabMenu(TabItemView view)
        {
            TabViewModel tab = view.Tab;
            bool vertical = _builtVertical;
            var items = new List<Control>
            {
                Item("New tab", Symbol.TabAdd, _vm.NewTabCommand, null, Modifier + "+T"),
                new Separator(),
                Item("Duplicate tab", Symbol.TabDesktopCopy, _vm.DuplicateTabCommand, tab),
                Item(tab.PinMenuText, tab.IsPinned ? Symbol.PinOff : Symbol.Pin, _vm.TogglePinCommand, tab),
                Item(vertical ? "Move tab up" : "Move tab left", vertical ? Symbol.ArrowUp : Symbol.ArrowLeft, _vm.MoveTabBackCommand, tab, Modifier + "+Shift+PageUp"),
                Item(vertical ? "Move tab down" : "Move tab right", vertical ? Symbol.ArrowDown : Symbol.ArrowRight, _vm.MoveTabForwardCommand, tab, Modifier + "+Shift+PageDown"),
                new Separator(),
                Item("Close tab", Symbol.Dismiss, _vm.CloseTabCommand, tab, Modifier + "+W"),
                Item("Close other tabs", Symbol.TabDesktopMultiple, _vm.CloseOtherTabsCommand, tab),
                Item(_vm.CloseAfterText, null, _vm.CloseTabsAfterCommand, tab),
                Item(_vm.CloseBeforeText, null, _vm.CloseTabsBeforeCommand, tab),
                new Separator(),
                Item("Reopen closed tab", Symbol.TabDesktopArrowClockwise, _vm.ReopenClosedTabCommand, null, Modifier + "+Shift+T"),
                new Separator(),
                PlacementMenu()
            };
            OpenMenu(view, items);
        }

        private void ShowBarMenu(Control anchor)
        {
            var items = new List<Control>
            {
                Item("New tab", Symbol.TabAdd, _vm.NewTabCommand, null, Modifier + "+T"),
                Item("Reopen closed tab", Symbol.TabDesktopArrowClockwise, _vm.ReopenClosedTabCommand, null, Modifier + "+Shift+T"),
                new Separator(),
                PlacementMenu()
            };
            if (_builtVertical)
            {
                items.Add(Item(_vm.IsCollapsed ? "Expand the tab bar" : "Collapse the tab bar to icons",
                    _vm.IsCollapsed ? Symbol.PanelLeftExpand : Symbol.PanelLeftContract, _vm.ToggleCollapsedCommand, null));
            }

            OpenMenu(anchor, items);
        }

        /// <summary>"Tab bar position": Title bar (where the window has one for tabs), Top, Left, Right (the current one checked).</summary>
        private MenuItem PlacementMenu()
        {
            var menu = new MenuItem { Header = "Tab bar position", Icon = new SymbolIcon { Symbol = Symbol.TabDesktop, FontSize = 16 } };
            var items = new List<MenuItem>();
            if (SupportsTitleBar)
            {
                items.Add(PlacementItem("Title bar", TabSettings.PlacementTitleBar, Symbol.Window));
            }

            items.Add(PlacementItem("Top", TabSettings.PlacementTop, Symbol.PanelTopExpand));
            items.Add(PlacementItem("Left", TabSettings.PlacementLeft, Symbol.PanelLeftExpand));
            items.Add(PlacementItem("Right", TabSettings.PlacementRight, Symbol.PanelRightExpand));
            menu.ItemsSource = items;
            return menu;
        }

        private MenuItem PlacementItem(string header, string placement, Symbol symbol)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 },
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _vm.Placement == placement,
                Command = _vm.SetPlacementCommand,
                CommandParameter = placement
            };
            return item;
        }

        private void ShowAllTabsMenu(Control anchor)
        {
            var items = new List<Control>();
            foreach (TabViewModel tab in _vm.Tabs)
            {
                TabViewModel target = tab;
                var item = new MenuItem
                {
                    Header = tab.Title,
                    Icon = new SymbolIcon { Symbol = tab.Icon, FontSize = 16 },
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = tab.IsSelected
                };
                ToolTip.SetTip(item, tab.ToolTip);
                item.Click += (_, _) =>
                {
                    _vm?.Select(target);
                    ContentFocusRequested?.Invoke(this, EventArgs.Empty);
                };
                items.Add(item);
            }

            var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight, MaxHeight = 520 };
            menu.Open(anchor);
        }

        private static MenuItem Item(string header, Symbol? symbol, System.Windows.Input.ICommand command, object parameter, string gesture = null)
        {
            var item = new MenuItem
            {
                Header = header,
                Command = command,
                CommandParameter = parameter,
                Icon = symbol is Symbol s ? new SymbolIcon { Symbol = s, FontSize = 16 } : null
            };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture.Replace("Cmd", "Meta", StringComparison.Ordinal)); // display only: the window binds the keys
                }
                catch (ArgumentException)
                {
                }
            }

            return item;
        }

        private static void OpenMenu(Control anchor, List<Control> items)
        {
            CommandManager.InvalidateRequerySuggested();
            var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
            menu.Open(anchor);
        }

        // ------------------------------------------------------------------ files dragged over the bar

        private enum DropKind
        {
            None,
            IntoTab,
            OpenInNewTabs
        }

        private (DropKind Kind, TabItemView Tab, string Folder, IReadOnlyList<string> Paths) DropTargetOf(DragEventArgs e)
        {
            if (_vm == null || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths) || paths.Count == 0)
            {
                return (DropKind.None, null, null, null);
            }

            TabItemView view = ItemFrom(e.Source);
            if (view != null)
            {
                string folder = view.Tab.Location;
                bool isFolder = folder != null && !TabLocation.IsPage(folder) && !Core.Services.ArchiveBrowser.IsInsideArchive(folder);
                return (DropKind.IntoTab, view, isFolder ? folder : null, paths);
            }

            // On + or the empty bar: folders open in new tabs (files don't open in tabs).
            bool allFolders = paths.All(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
            return allFolders && (IsOnEmptyBar(e.Source) || IsInside(e.Source, _addButton) || IsInside(e.Source, _newTabRow))
                ? (DropKind.OpenInNewTabs, null, null, paths)
                : (DropKind.None, null, null, paths);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            (DropKind kind, TabItemView view, string folder, IReadOnlyList<string> paths) = DropTargetOf(e);
            SetDropTarget(kind == DropKind.IntoTab ? view?.Tab : null);
            SetButtonDrop(kind == DropKind.OpenInNewTabs);
            switch (kind)
            {
                case DropKind.IntoTab:
                    e.DragEffects = folder != null ? FileDragDropHelper.ResolveEffect(e, folder, paths) : DragDropEffects.None;
                    ArmSpring(view.Tab);
                    break;
                case DropKind.OpenInNewTabs:
                    e.DragEffects = DragDropEffects.Link;
                    ArmSpring(null);
                    break;
                default:
                    e.DragEffects = DragDropEffects.None;
                    ArmSpring(null);
                    break;
            }

            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            SetDropTarget(null);
            SetButtonDrop(false);
            ArmSpring(null);
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            try
            {
                (DropKind kind, TabItemView view, string folder, IReadOnlyList<string> paths) = DropTargetOf(e);
                SetDropTarget(null);
                SetButtonDrop(false);
                ArmSpring(null);
                e.Handled = true;
                if (kind == DropKind.OpenInNewTabs && Main != null)
                {
                    e.DragEffects = DragDropEffects.Link;
                    await Main.OpenFoldersInNewTabsAsync(paths, showFirst: true);
                    return;
                }

                if (kind != DropKind.IntoTab || folder == null || Main?.FileListViewModel == null)
                {
                    e.DragEffects = DragDropEffects.None;
                    return;
                }

                await Menus.DropChoice.RunAsync(view, e, folder, paths, Main.FileListViewModel);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "A drop on the tab bar failed");
            }
        }

        private void SetDropTarget(TabViewModel tab)
        {
            foreach (TabViewModel each in _views.Keys)
            {
                each.IsDropTarget = ReferenceEquals(each, tab);
            }
        }

        private void SetButtonDrop(bool on)
        {
            _addButton?.Classes.Set("drop", on);
            _newTabRow?.Classes.Set("drop", on);
        }

        /// <summary>Files held over a tab for a moment open it (spring-loaded), so they can be dropped into its folder's subfolders.</summary>
        private void ArmSpring(TabViewModel tab)
        {
            if (tab != null && tab.IsSelected)
            {
                tab = null;
            }

            if (ReferenceEquals(tab, _springTab))
            {
                return;
            }

            _springTab = tab;
            _springTimer.Stop();
            if (tab != null)
            {
                _springTimer.Start();
            }
        }

        /// <summary>
        /// Lays out the horizontal bar: the tabs, then + right after the last tab (not at the far end), then the "all tabs"
        /// button. The tabs get the room the buttons leave.
        /// </summary>
        private sealed class TabRowPanel : Avalonia.Controls.Panel
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double buttons = 0;
                double height = 0;
                for (int i = 1; i < Children.Count; i++)
                {
                    Children[i].Measure(availableSize);
                    if (Children[i].IsVisible)
                    {
                        buttons += Children[i].DesiredSize.Width;
                        height = Math.Max(height, Children[i].DesiredSize.Height);
                    }
                }

                Control tabs = Children[0];
                double available = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - buttons);
                tabs.Measure(new Size(available, availableSize.Height));
                return new Size(tabs.DesiredSize.Width + buttons, Math.Max(height, tabs.DesiredSize.Height));
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                double buttons = 0;
                for (int i = 1; i < Children.Count; i++)
                {
                    buttons += Children[i].IsVisible ? Children[i].DesiredSize.Width : 0;
                }

                Control tabs = Children[0];
                double tabsWidth = Math.Min(tabs.DesiredSize.Width, Math.Max(0, finalSize.Width - buttons));
                tabs.Arrange(new Rect(0, 0, tabsWidth, finalSize.Height));
                double x = tabsWidth;
                for (int i = 1; i < Children.Count; i++)
                {
                    Control child = Children[i];
                    double width = child.IsVisible ? child.DesiredSize.Width : 0;
                    child.Arrange(new Rect(x, 0, width, finalSize.Height));
                    x += width;
                }

                return finalSize;
            }
        }
    }
}
