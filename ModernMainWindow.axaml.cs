// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using ByteExplorer.ViewModels;

namespace ByteExplorer
{
    /// <summary>
    /// The Modern window layout (called "Dark" in the WPF build, before it had a light theme): title bar, navigation and
    /// address bar, command toolbar, body (sidebar | file list(s) or search results | details), status bar. Everything
    /// it does is in <see cref="MainWindowBase"/>; this class only names its controls.
    /// </summary>
    public partial class ModernMainWindow : MainWindowBase
    {
        public ModernMainWindow()
        {
            InitializeComponent();
            AttachParts(new MainWindowParts
            {
                DragRegions = new[] { TitleBar },
                CaptionButtons = CaptionButtons,
                MaximizeButton = MaximizeButton,
                MaximizeIcon = MaximizeIcon,
                FrameBorder = FrameBorder,
                ResizeGrips = ResizeGrips,
                TitleBarHeight = 32,
                Omnibar = Omnibar,
                GlobalSearchBox = GlobalSearchBox,
                SearchResults = SearchResultsPanel,
                Sidebar = LeftPanel,
                SidebarColumn = BodyGrid.ColumnDefinitions[0],
                SidebarSplitter = LeftSplitter,
                SidebarDefaultWidth = 220,
                SidebarMinWidth = 120,
                SidebarMaxWidth = 600,
                DetailsColumn = BodyGrid.ColumnDefinitions[4],
                DetailsSplitter = DetailsSplitter,
                DetailsPanel = DetailsPanel,
                DetailsDefaultWidth = 240,
                DetailsMinWidth = 160,
                StatusBarRow = RootGrid.RowDefinitions[4],
                Toolbar = Toolbar,
                ToolbarRow = RootGrid.RowDefinitions[2],
                StatusBar = StatusBar,
                PaneGrid = PaneGrid,
                TabStrip = TabStrip,
                TabbedArea = TabbedArea,
                TitleBarTabHost = TitleBarTabHost,
                TitleText = TitleText,
                TitleBarRow = RootGrid.RowDefinitions[0],
                LeftTabHost = LeftTabHost,
                PaneSplitter = PaneSplitter,
                PrimaryPane = PrimaryPane,
                SecondaryPane = SecondaryPane,
                ToastBorder = ToastBorder,
                ToastText = ToastText,
                OperationsPanel = OperationsPanel,
                Areas = new[]
                {
                    new Views.AreaPart(Core.Models.AreaAppearance.AddressBar, AddressBar, "NavBarBrush", "FieldBrush") { Row = RootGrid.RowDefinitions[1] },
                    new Views.AreaPart(Core.Models.AreaAppearance.Toolbar, Toolbar, "CommandBarBrush") { Row = RootGrid.RowDefinitions[2] },
                    new Views.AreaPart(Core.Models.AreaAppearance.LeftPanel, LeftPanel, "LeftPaneBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.MainView, MainViewHost, "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.DetailsPanel, DetailsPanel, "LeftPaneBrush", "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.StatusBar, StatusBar, "StatusBarBrush") { Row = RootGrid.RowDefinitions[4] },
                },
                // One stack (the tree scrolls with the panel), so every section moves, the tree included.
                SidebarSectionsAbove = SidebarSections,
                SidebarDesignHost = LeftPanel
            });
        }

        /// <summary>The title (icon and name) starts after the traffic lights.</summary>
        protected override void MakeRoomForTrafficLights()
        {
            TitleContent.Margin = new Avalonia.Thickness(78, 0, 0, 0);
            TitleBarTabHost.Margin = new Avalonia.Thickness(102, 0, 72, 0); // tabs in the title bar start after the icon too
        }
    }
}
