// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The details panel's file preview surface — see FilePreviewViewModel
    /// for the full picture. Constructed once in the main constructor
    /// (FileListViewModel.cs) and disposed alongside everything else in
    /// Dispose(); refreshed on every SelectedItem change via that
    /// property's own setter (also in FileListViewModel.cs).
    /// </summary>
    public sealed partial class FileListViewModel
    {
        public FilePreviewViewModel Preview { get; }
    }
}
