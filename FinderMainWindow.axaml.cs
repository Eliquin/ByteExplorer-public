// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ByteExplorer.Infrastructure;

namespace ByteExplorer
{
    /// <summary>
    /// The Finder window layout (after macOS Finder). Everything it does is in <see cref="MainWindowBase"/>; this class
    /// names its controls, sets the Home row's folder and handles the toolbar's view switcher.
    /// </summary>
    public partial class FinderMainWindow : MainWindowBase
    {
        private const double ToolbarHeight = 52;

        public FinderMainWindow()
        {
            InitializeComponent();

            HomeFolderRow.Tag = SpecialFolders.Home;

            AttachParts(new MainWindowParts
            {
                // The toolbar and the sidebar's top strip move the window, as Finder's unified title bar does.
                DragRegions = new Control[] { Toolbar, SidebarTop },
                CaptionButtons = CaptionButtons,
                MaximizeButton = MaximizeButton,
                MaximizeIcon = MaximizeIcon,
                FrameBorder = FrameBorder,
                ResizeGrips = ResizeGrips,
                TitleBarHeight = ToolbarHeight,
                Omnibar = Omnibar,
                GlobalSearchBox = GlobalSearchBox,
                SearchResults = SearchResultsPanel,
                Sidebar = Sidebar,
                SidebarColumn = RootGrid.ColumnDefinitions[0],
                SidebarSplitter = SidebarSplitter,
                SidebarDefaultWidth = 210,
                SidebarMinWidth = 160,
                SidebarMaxWidth = 360,
                DetailsColumn = BodyGrid.ColumnDefinitions[1],
                DetailsSplitter = DetailsSplitter,
                DetailsPanel = DetailsPanel,
                DetailsDefaultWidth = 240,
                DetailsMinWidth = 160,
                StatusBarRow = MainGrid.RowDefinitions[3],
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
                Areas = new[]
                {
                    new Views.AreaPart(Core.Models.AreaAppearance.AddressBar, PathBar, "ContentBrush", "FieldBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.Toolbar, Toolbar, "FinderToolbarBrush") { Row = MainGrid.RowDefinitions[0] },
                    new Views.AreaPart(Core.Models.AreaAppearance.LeftPanel, Sidebar, "FinderSidebarBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.MainView, MainViewHost, "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.DetailsPanel, DetailsPanel, "ContentBrush"),
                    new Views.AreaPart(Core.Models.AreaAppearance.StatusBar, StatusBar, "FinderToolbarBrush"),
                },
                SidebarSectionsAbove = SidebarSections
            });
        }

        /// <summary>The native buttons sit over the sidebar's top strip, which is already their height: nothing moves.</summary>
        protected override void MakeRoomForTrafficLights()
        {
        }

        /// <summary>The toolbar's segmented view control: Icons, List, Columns, Gallery (a view preset each).</summary>
        private void OnFinderViewClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.Tag is string name && Enum.TryParse(name, out FileListViewPreset preset) &&
                ViewModel?.FileListViewModel is ViewModels.FileListViewModel pane)
            {
                pane.SelectedPreset = preset;
            }
        }
    }
}
