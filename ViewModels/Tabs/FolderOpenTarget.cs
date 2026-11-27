// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.ViewModels.Tabs
{
    /// <summary>Where a folder chosen in the sidebar, the tree or a menu opens.</summary>
    public enum FolderOpenTarget
    {
        /// <summary>The active pane (a click).</summary>
        ActivePane,

        /// <summary>The other pane, turning split view on if needed ("Open in the other pane").</summary>
        OtherPane,

        /// <summary>A new tab next to this one, shown ("Open in new tab").</summary>
        NewTab,

        /// <summary>What a middle click does (Settings › Tabs): a new tab behind this one, or the other pane.</summary>
        MiddleClick
    }
}
