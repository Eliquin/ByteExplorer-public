// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One date bucket in Timeline view (Today, Yesterday, etc.).
    /// Holds already-hydrated row view models so the Timeline panel
    /// can bind directly without a second hydration pass.
    /// </summary>
    public sealed class TimelineGroup
    {
        public TimelineGroup(string header, IReadOnlyList<FileListItemViewModel> items)
        {
            Header = header;
            Items = items;
        }

        public string Header { get; }
        public IReadOnlyList<FileListItemViewModel> Items { get; }
        public int Count => Items.Count;
    }
}
