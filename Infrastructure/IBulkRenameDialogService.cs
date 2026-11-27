// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The "Rename items" dialog (F2 with several items selected). A separate interface rather than a member of
    /// <see cref="IDialogService"/>, so dialog services that can't show it (test doubles, the tag editor's) need no change:
    /// the file list asks for it with a type test and does nothing when it is missing.
    /// </summary>
    public interface IBulkRenameDialogService
    {
        /// <summary>
        /// Asks how to rename <paramref name="items"/> (in list order) and returns the renames to perform, or null when
        /// cancelled. <paramref name="existsInFolder"/> says whether a name is taken in a folder (folder, name).
        /// </summary>
        Task<IReadOnlyList<BulkRenameMove>> PromptBulkRenameAsync(IReadOnlyList<BulkRenameItem> items, Func<string, string, bool> existsInFolder);
    }
}
