// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// What kind of thing a navigation tree node represents. Drives get
    /// their own values (rather than a generic "Drive" + a DriveType
    /// property) because it's checked on every icon lookup and a switch
    /// over an enum is faster and clearer than branching on DriveType at
    /// the view-model layer.
    /// </summary>
    public enum NavigationNodeType
    {
        Computer,
        DriveFixed,
        DriveRemovable,
        DriveNetwork,
        DriveOptical,
        DriveUnknown,
        Phone,
        Folder,
        Placeholder
    }
}
