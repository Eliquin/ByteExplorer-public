// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Category-based filter for the Filter menu (see FilterSelector) —
    /// deliberately distinct from FileListViewModel.FilterText, which
    /// filters by name substring. This filters by what kind of item it
    /// is, a genuinely different, complementary axis, not a duplicate
    /// of the existing text filter.
    /// </summary>
    public enum FileCategoryFilter
    {
        All,
        FoldersOnly,
        FilesOnly,
        Images,
        Documents,
        Videos,
        Music,
        Archives
    }
}
