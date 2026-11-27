// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using ByteExplorer.Infrastructure;

namespace ByteExplorer
{
    /// <summary>
    /// The Nemo window layout: a full-height sidebar (places, pinned folders, drives) and one title bar. Everything it
    /// does is in <see cref="MainWindowBase"/>; this class names its controls and sets the Home row's folder.
    /// </summary>
    public partial class NemoMainWindow : MainWindowBase
    {
        private const double TitleBarHeight = 46;

        public NemoMainWindow()
        {
            InitializeComponent();

            HomeFolderRow.Tag = SpecialFolders.Home;

            AttachParts(new MainWindowParts
            {
                DragRegions = new Avalonia.Controls.Control[] { TitleBar, SidebarHeader },
                CaptionButtons = CaptionButtons,
                MaximizeButton = MaximizeButton,
                MaximizeIcon = MaximizeIcon,
                FrameBorder = FrameBorder,
                ResizeGrips = ResizeGrips,
                TitleBarHeight = TitleBarHeight,
                Omnibar = Omnibar,
                GlobalSearchBox = GlobalSearchBox,
                SearchResults = SearchResultsPanel,
                Sidebar = Sidebar,
                SidebarColumn = RootGrid.ColumnDefinitions[0],
                SidebarSplitter = SidebarSplitter,
                SidebarDefaultWidth = 220,
                SidebarMinWidth = 180,
                SidebarMaxWidth = 360,
                DetailsColumn = BodyGrid.ColumnDefinitions[1],
                DetailsSplitter = DetailsSplitter,
                DetailsPanel = DetailsPanel,
                DetailsDefaultWidth = 220,
                DetailsMinWidth = 140,
                StatusBarRow = MainGrid.RowDefinitions[2],
                StatusBar = StatusBar,
                PaneGrid = PaneGrid,
                TabStrip = TabStrip,
                TabbedArea = TabbedArea,
                PaneSplitter = PaneSplitter,
                PrimaryPane = PrimaryPane,
                SecondaryPane = SecondaryPane,
                ToastBorder = ToastBorder,
                ToastText = ToastText,
                OperationsPanel = OperationsPanel,
                // The address bar sits in the title bar, so the title bar's text size scales it too.
                Areas = new[]
                {
                    new Views.AreaPart(Core.Models.AreaAppearance.AddressBar, Omnibar, "FieldBrush") { Row = MainGrid.RowDefinitions[0] },
                    new Views.AreaPart(Core.Models.AreaAppearance.Toolbar, TitleBar, "NemoTitleBarBrush") { Row = MainGrid.RowDefinitions[0] },
                    new Views.AreaPart(Core.Models.AreaAppearance.LeftPanel, Sidebar, "NemoSidebarBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.MainView, MainViewHost, "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.DetailsPanel, DetailsPanel, "NemoSidebarBrush", "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.StatusBar, StatusBar, "NemoStatusBarBrush"),
                },
                SidebarSectionsAbove = SidebarSections
            });
        }

        /// <summary>The traffic lights sit over the sidebar's top-left corner: the header moves below them.</summary>
        protected override void MakeRoomForTrafficLights() => SidebarHeader.Margin = new Thickness(0, 24, 0, 0);
    }
}
