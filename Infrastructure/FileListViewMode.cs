// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The underlying layout engine. Several presets (Small/Medium/Large/
    /// Extra large icons) map to Icons; Columns and Nexus are dedicated modes.
    /// </summary>
    public enum FileListViewMode
    {
        Details,
        List,
        Icons,
        Tiles,
        Timeline,
        Columns,
        Nexus
    }
}
