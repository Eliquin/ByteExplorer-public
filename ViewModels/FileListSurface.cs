// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Which of the center pane's mutually-exclusive surfaces is
    /// currently shown. Single source of truth for FileListViewModel's
    /// IsStandardFolderView / IsThisPcView / IsHomeView — see
    /// FileListViewModel.SetSurface. Kept as one enum rather than
    /// independently-settable bools specifically so it's structurally
    /// impossible for two surfaces to be marked visible at once (a
    /// real risk with separate flags: three different Load*Async
    /// methods would each need to remember to reset every OTHER
    /// surface's flag, and missing one anywhere silently shows two
    /// surfaces overlapping).
    /// </summary>
    public enum FileListSurface
    {
        StandardFolder,
        ThisPc,
        Home,

        /// <summary>The trash / Recycle Bin's items (see FileListViewModel.Trash.cs).</summary>
        Trash
    }
}
