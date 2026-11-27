// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    public enum FileOperationKind
    {
        Copy,
        Move,
        Delete,

        /// <summary>Reversing an earlier operation.</summary>
        Undo
    }
}
