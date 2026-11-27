// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The application menu of both layouts (formerly the Nemo layout's tools menu). Rows run existing commands and
    /// two-way settings through bindings — the panel switches bind MainViewModel's panel properties, which persist and
    /// which the window applies, so the menu, the status bar buttons, the omnibar commands and the settings stay in
    /// step. The code here opens and closes the categories, closes the flyout after an action, and runs the few
    /// actions that are methods rather than commands (zoom steps, view presets, sort, Home, This PC, Settings).
    /// <para>
    /// Categories come in two styles (MainViewModel.IsMenuSubmenuHorizontal). Horizontal, the classic way: hovering or
    /// clicking a category opens its items in a submenu beside the menu, and moving to another row closes it after a
    /// short grace period, so the pointer can travel diagonally into the submenu without losing it. Vertical: clicking
    /// a category unfolds its items in place. Each category's items are one panel, moved into the submenu while it is
    /// open and back afterwards, so no row exists twice.
    /// </para>
    /// </summary>
    public partial class AppMenu : UserControl
    {
        /// <summary>Where the flyout opens relative to the button: the Modern layout's button sits at the left edge, Nemo's at the right.</summary>
        public static readonly StyledProperty<PlacementMode> PlacementProperty =
            AvaloniaProperty.Register<AppMenu, PlacementMode>(nameof(Placement), PlacementMode.BottomEdgeAlignedRight);

        /// <summary>Whether the layout has a command toolbar the "Toolbar" switch can hide (the Nemo layout has none).</summary>
        public static readonly StyledProperty<bool> HasToolbarProperty =
            AvaloniaProperty.Register<AppMenu, bool>(nameof(HasToolbar));

        /// <summary>Whether the layout can put the tabs in its title bar (Modern): the menu then offers that position.</summary>
        public static readonly StyledProperty<bool> HasTitleBarTabsProperty =
            AvaloniaProperty.Register<AppMenu, bool>(nameof(HasTitleBarTabs));

        public bool HasTitleBarTabs
        {
            get => GetValue(HasTitleBarTabsProperty);
            set => SetValue(HasTitleBarTabsProperty, value);
        }

        /// <summary>How long the pointer rests on a category before its submenu opens, or on another row before it closes.</summary>
        private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(380);

        private readonly MenuSection[] _sections;
        private readonly DispatcherTimer _hoverTimer;
        private MenuSection _openSubmenu;
        private MenuSection _pendingSubmenu;
        private bool _pendingClose;
        private bool _horizontal = true;

        public AppMenu()
        {
            InitializeComponent();
            _sections = new[]
            {
                new MenuSection(EditHeader, EditPanel, EditChevron),
                new MenuSection(PasswordHeader, PasswordPanel, PasswordChevron),
                new MenuSection(GoHeader, GoPanel, GoChevron),
                new MenuSection(ViewHeader, ViewOptionsPanel, ViewChevron),
                new MenuSection(SortHeader, SortPanel, SortChevron),
                new MenuSection(TreeHeader, TreePanel, TreeChevron),
                new MenuSection(IndexHeader, IndexPanel, IndexChevron),
            };

            _hoverTimer = new DispatcherTimer { Interval = HoverOpenDelay };
            _hoverTimer.Tick += OnHoverTimerTick;
            // PointerEntered is a direct event: each row of the menu reports itself (rows of categories do not, they are
            // nested panels and live in the submenu or under their header).
            foreach (Control row in MenuPanel.Children)
            {
                row.PointerEntered += OnMenuRowPointerEntered;
            }
        }

        /// <summary>A category: its header row, its items and the header's chevron.</summary>
        private sealed class MenuSection
        {
            public MenuSection(Button header, StackPanel panel, SymbolIcon chevron)
            {
                Header = header;
                Panel = panel;
                Chevron = chevron;
            }

            public Button Header { get; }

            public StackPanel Panel { get; }

            public SymbolIcon Chevron { get; }
        }

        /// <summary>Whether the open menu uses the horizontal (submenu) style; for tests.</summary>
        public bool IsHorizontal => _horizontal;

        /// <summary>The category whose submenu is open, by header name (null when none); for tests.</summary>
        public string OpenSubmenuName => _openSubmenu?.Header.Name;

        public PlacementMode Placement
        {
            get => GetValue(PlacementProperty);
            set => SetValue(PlacementProperty, value);
        }

        public bool HasToolbar
        {
            get => GetValue(HasToolbarProperty);
            set => SetValue(HasToolbarProperty, value);
        }

        private MainViewModel Main => DataContext as MainViewModel;

        private FileListViewModel ActivePane => Main?.FileListViewModel;

        /// <summary>Opens the menu (keyboard access and tests).</summary>
        public void Open() => TriggerButton.Flyout?.ShowAt(TriggerButton);

        /// <summary>The trigger's flyout (x:Name on a Flyout generates no field: it is not a control).</summary>
        private void HideMenu()
        {
            CloseSubmenu();
            TriggerButton.Flyout?.Hide();
        }

        private void OnFlyoutOpening(object sender, EventArgs e)
        {
            if (sender is PopupFlyoutBase flyout)
            {
                flyout.Placement = Placement;
            }

            BuildPasswordRows();

            // The style is read at each opening, so a change in the settings applies the next time the menu opens.
            _horizontal = Main?.IsMenuSubmenuHorizontal ?? true;
            foreach (MenuSection section in _sections)
            {
                section.Chevron.Symbol = _horizontal ? Symbol.ChevronRight : Symbol.ChevronDown;
            }
        }

        /// <summary>
        /// The Password category's rows for the active pane's selection: the right-click menu's Password submenu
        /// (FileContextMenu.BuildPasswordMenu), turned into this menu's rows, so every layout offers the same choices
        /// ("Protect with password…", or Open, Lock now, Change password, Remove protection for a protected item). With
        /// nothing selected, or a selection that can't be protected, one greyed row says why. Internal for the tests.
        /// </summary>
        internal void BuildPasswordRows()
        {
            PasswordPanel.Children.Clear();
            FileListViewModel pane = ActivePane;
            var paths = pane?.SelectedItems.Select(i => i.FullPath).ToList() ?? new List<string>();
            MenuItem built = paths.Count > 0 ? Menus.FileContextMenu.BuildPasswordMenu(pane, paths) : null;
            var source = (built?.ItemsSource as IEnumerable<Control>)?.ToList() ?? new List<Control>();
            foreach (Control entry in source)
            {
                if (entry is Separator)
                {
                    var divider = new Border();
                    divider.Classes.Add("menuDivider");
                    divider.Classes.Add("sub");
                    PasswordPanel.Children.Add(divider);
                }
                else if (entry is MenuItem item)
                {
                    var row = new Button { Content = item.Header, IsEnabled = item.IsEnabled };
                    row.Classes.Add("menuRow");
                    row.Classes.Add("sub");
                    ToolTip.SetTip(row, ToolTip.GetTip(item));
                    // The submenu's own action runs (one source for both menus), after this menu has closed.
                    row.Click += (_, _) =>
                    {
                        HideMenu();
                        Dispatcher.UIThread.Post(() => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), DispatcherPriority.Input);
                    };
                    PasswordPanel.Children.Add(row);
                }
            }

            if (PasswordPanel.Children.Count == 0)
            {
                var none = new Button
                {
                    Content = paths.Count == 0 ? "Select files or folders first" : "The selection can't be protected together",
                    IsEnabled = false,
                };
                none.Classes.Add("menuRow");
                none.Classes.Add("sub");
                PasswordPanel.Children.Add(none);
            }
        }

        private void OnFlyoutClosed(object sender, EventArgs e)
        {
            // The next opening starts with every category closed and scrolled to the top, as the WPF menu did.
            CloseSubmenu();
            foreach (MenuSection section in _sections)
            {
                section.Panel.IsVisible = false;
                section.Chevron.Symbol = _horizontal ? Symbol.ChevronRight : Symbol.ChevronDown;
            }

            MenuScroller.Offset = default;
        }

        private void OnSectionClick(object sender, RoutedEventArgs e)
        {
            MenuSection section = Array.Find(_sections, s => ReferenceEquals(s.Header, sender));
            if (section == null)
            {
                return;
            }

            if (_horizontal)
            {
                // A click opens at once (no hover delay) and, on the open category, closes it again.
                CancelHover();
                if (ReferenceEquals(_openSubmenu, section))
                {
                    CloseSubmenu();
                }
                else
                {
                    OpenSubmenu(section);
                }

                return;
            }

            bool expand = !section.Panel.IsVisible;
            section.Panel.IsVisible = expand;
            section.Chevron.Symbol = expand ? Symbol.ChevronUp : Symbol.ChevronDown;
            if (expand)
            {
                // Bring the opened category into view when it unfolds below the visible part of a scrolled menu.
                section.Panel.BringIntoView();
            }
        }

        // ------------------------------------------------------------------ horizontal submenus

        private void OpenSubmenu(MenuSection section)
        {
            if (ReferenceEquals(_openSubmenu, section))
            {
                return;
            }

            CloseSubmenu();
            if (section.Panel.Parent is Panel home)
            {
                home.Children.Remove(section.Panel);
            }

            section.Panel.Classes.Add("cascaded");
            section.Panel.IsVisible = true;
            SubmenuHost.Child = section.Panel;
            section.Header.Classes.Add("checked");
            _openSubmenu = section;
            SubmenuPopup.PlacementTarget = section.Header;
            SubmenuPopup.IsOpen = true;
        }

        private void CloseSubmenu()
        {
            MenuSection section = _openSubmenu;
            _openSubmenu = null;
            SubmenuPopup.IsOpen = false;
            if (section == null)
            {
                return;
            }

            // Back under its header, hidden, as the vertical style expects it.
            SubmenuHost.Child = null;
            section.Panel.Classes.Remove("cascaded");
            section.Panel.IsVisible = false;
            section.Header.Classes.Remove("checked");
            int index = MenuPanel.Children.IndexOf(section.Header);
            MenuPanel.Children.Insert(index + 1, section.Panel);
        }

        private void OnMenuRowPointerEntered(object sender, PointerEventArgs e)
        {
            if (!_horizontal)
            {
                return;
            }

            if (Array.Exists(_sections, s => ReferenceEquals(s.Panel, sender)))
            {
                // A category's items, shown in the submenu: the pointer is where it should be.
                CancelHover();
                return;
            }

            MenuSection section = Array.Find(_sections, s => ReferenceEquals(s.Header, sender));
            if (section != null)
            {
                if (!ReferenceEquals(section, _openSubmenu))
                {
                    ScheduleHover(section, close: false);
                }
                else
                {
                    CancelHover();
                }
            }
            else if (_openSubmenu != null)
            {
                ScheduleHover(null, close: true);
            }
        }

        private void OnSubmenuPointerEntered(object sender, PointerEventArgs e)
        {
            // Reaching the submenu keeps it open, even if the pointer crossed another category on its way.
            CancelHover();
        }

        private void ScheduleHover(MenuSection section, bool close)
        {
            _pendingSubmenu = section;
            _pendingClose = close;
            _hoverTimer.Stop();
            _hoverTimer.Interval = close || _openSubmenu != null ? HoverCloseDelay : HoverOpenDelay;
            _hoverTimer.Start();
        }

        private void CancelHover()
        {
            _hoverTimer.Stop();
            _pendingSubmenu = null;
            _pendingClose = false;
        }

        private void OnHoverTimerTick(object sender, EventArgs e)
        {
            _hoverTimer.Stop();
            MenuSection section = _pendingSubmenu;
            bool close = _pendingClose;
            CancelHover();
            if (!_horizontal || TriggerButton.Flyout?.IsOpen != true)
            {
                return;
            }

            if (section != null)
            {
                OpenSubmenu(section);
            }
            else if (close)
            {
                CloseSubmenu();
            }
        }

        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ActivePane?.AdjustZoom(-16);

        private void OnZoomInClick(object sender, RoutedEventArgs e) => ActivePane?.AdjustZoom(16);

        /// <summary>
        /// A command row was clicked: the menu closes once the command has run. Avalonia raises Click first and runs the
        /// command after it, reading Command then; closing at once moved a category's row out of its submenu and back
        /// under its header, which cut it off from the window for that moment, its binding lost the pane, and the command
        /// read was null: every command row in a category (Select all, Invert selection, New folder, Cut, Open in
        /// terminal…) did nothing. Closing is queued instead, behind the command.
        /// </summary>
        private void OnActionClick(object sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(HideMenu, DispatcherPriority.Input);

        private void OnViewOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.Tag is string name &&
                Enum.TryParse(name, out FileListViewPreset preset) && ActivePane is FileListViewModel pane)
            {
                pane.SelectedPreset = preset;
            }

            HideMenu();
        }

        /// <summary>Picks the column and keeps the current direction: a menu sets, it does not toggle (see FileListViewModel.SetSort).</summary>
        private void OnSortColumnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.Tag is string name &&
                Enum.TryParse(name, out FileListSortColumn column) && ActivePane is FileListViewModel pane)
            {
                pane.SetSort(column, pane.SortAscending);
            }

            HideMenu();
        }

        private void OnSortDirectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && ActivePane is FileListViewModel pane)
            {
                pane.SetSort(pane.SortColumn, string.Equals(control.Tag as string, "Ascending", StringComparison.Ordinal));
            }

            HideMenu();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            HideMenu();
            (TopLevel.GetTopLevel(this) as MainWindowBase)?.OpenSettings();
        }

        private void OnGoHomeClick(object sender, RoutedEventArgs e) => Go(pane => pane.LoadHomeAsync(), "AppMenu.Home");

        private void OnGoThisPcClick(object sender, RoutedEventArgs e) => Go(pane => pane.LoadThisPcAsync(), "AppMenu.ThisPc");

        private void Go(Func<FileListViewModel, Task> navigate, string context)
        {
            HideMenu();
            if (ActivePane is FileListViewModel pane)
            {
                // A search in progress covers the file list: end it so the place opened is what is shown.
                Main?.ClearSearch();
                navigate(pane).Forget(context);
            }
        }
    }
}
