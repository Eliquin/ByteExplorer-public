// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Tabs (Settings › Tabs): where the tab bar sits, what a new tab opens, how folders open in tabs, and the tabs kept
    /// for the next start.
    /// </summary>
    public sealed class TabSettings
    {
        /// <summary>Tab bar positions for <see cref="Placement"/>.</summary>
        public const string PlacementTop = "Top";
        public const string PlacementLeft = "Left";
        public const string PlacementRight = "Right";

        /// <summary>In the window's title bar, as Windows 11's File Explorer (Modern layout; the others show them on top).</summary>
        public const string PlacementTitleBar = "TitleBar";

        /// <summary>What a new tab (Ctrl+T, the + button) shows, for <see cref="NewTabOpens"/>.</summary>
        public const string NewTabHome = "Home";
        public const string NewTabThisPc = "ThisPc";
        public const string NewTabCurrentFolder = "Current";

        /// <summary>What a middle click on a folder does, for <see cref="MiddleClickOpens"/>.</summary>
        public const string MiddleClickNewTab = "NewTab";
        public const string MiddleClickOtherPane = "OtherPane";

        public const double MinVerticalWidth = 150;
        public const double MaxVerticalWidth = 480;
        public const double DefaultVerticalWidth = 232;

        /// <summary>The most tabs a window holds (and restores): past this, opening one more is refused with a message.</summary>
        public const int MaxTabs = 100;

        /// <summary>
        /// Top (a horizontal row above the file list), TitleBar (in the title bar, Modern layout), or Left/Right (a vertical
        /// list: Left at the window's left edge before the sidebar in the Modern layout, beside the files in the others;
        /// Right beside the files).
        /// </summary>
        public string Placement { get; set; } = PlacementTitleBar;

        /// <summary>Vertical tabs shrunk to their icons (titles in the tips); the bar's toggle button switches it.</summary>
        public bool VerticalCollapsed { get; set; }

        /// <summary>The width of the vertical tab bar (dragged by its edge).</summary>
        public double VerticalWidth { get; set; } = DefaultVerticalWidth;

        /// <summary>Show the tab bar with one tab only. Off: it appears once a second tab is opened (as Firefox and Dolphin can).</summary>
        public bool ShowWithOneTab { get; set; } = true;

        /// <summary>Home, This PC, or the folder of the tab it was opened from.</summary>
        public string NewTabOpens { get; set; } = NewTabHome;

        /// <summary>A middle click on a folder (file list, folder tree, Quick access, Recent) opens it in a new tab, or in the other pane.</summary>
        public string MiddleClickOpens { get; set; } = MiddleClickNewTab;

        /// <summary>A folder opened in a new tab is shown at once (otherwise it opens behind, as a middle click in a browser).</summary>
        public bool SwitchToNewTab { get; set; }

        /// <summary>The tabs open at the last close (pinned tabs come back at every start; the rest with "open where I left off").</summary>
        public List<TabSessionEntry> SessionTabs { get; set; } = new List<TabSessionEntry>();

        /// <summary>The selected tab of <see cref="SessionTabs"/>.</summary>
        public int SessionSelectedTab { get; set; }

        public static bool IsPlacement(string value) =>
            value == PlacementTop || value == PlacementLeft || value == PlacementRight || value == PlacementTitleBar;

        public void Sanitize()
        {
            if (!IsPlacement(Placement))
            {
                Placement = PlacementTop;
            }

            if (NewTabOpens != NewTabHome && NewTabOpens != NewTabThisPc && NewTabOpens != NewTabCurrentFolder)
            {
                NewTabOpens = NewTabHome;
            }

            if (MiddleClickOpens != MiddleClickNewTab && MiddleClickOpens != MiddleClickOtherPane)
            {
                MiddleClickOpens = MiddleClickNewTab;
            }

            if (double.IsNaN(VerticalWidth) || double.IsInfinity(VerticalWidth))
            {
                VerticalWidth = DefaultVerticalWidth;
            }

            VerticalWidth = Math.Clamp(VerticalWidth, MinVerticalWidth, MaxVerticalWidth);

            SessionTabs ??= new List<TabSessionEntry>();
            SessionTabs.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Location));
            if (SessionTabs.Count > MaxTabs)
            {
                SessionTabs.RemoveRange(MaxTabs, SessionTabs.Count - MaxTabs);
            }

            foreach (TabSessionEntry tab in SessionTabs)
            {
                tab.SecondLocation ??= string.Empty;
                tab.Split &= tab.SecondLocation.Length > 0;
                tab.SecondFocused &= tab.Split;
            }

            SessionSelectedTab = SessionTabs.Count == 0 ? 0 : Math.Clamp(SessionSelectedTab, 0, SessionTabs.Count - 1);
        }
    }

    /// <summary>
    /// One tab, as kept in the settings: where its panes are (a folder path, or <c>::home</c> / <c>::thispc</c> / <c>::trash</c>), whether
    /// it is split and which pane has focus, and whether it is pinned.
    /// </summary>
    public sealed class TabSessionEntry
    {
        public string Location { get; set; } = string.Empty;
        public string SecondLocation { get; set; } = string.Empty;
        public bool Split { get; set; }
        public bool SecondFocused { get; set; }
        public bool Pinned { get; set; }
    }
}
