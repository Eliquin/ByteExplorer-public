// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Infrastructure;
using ByteExplorer.Views.Dialogs;

namespace ByteExplorer
{
    /// <summary>The window shows the "Rename items" dialog for its panes (F2 with several items selected).</summary>
    public abstract partial class MainWindowBase : IBulkRenameDialogService
    {
        public async Task<IReadOnlyList<BulkRenameMove>> PromptBulkRenameAsync(IReadOnlyList<BulkRenameItem> items, Func<string, string, bool> existsInFolder) =>
            await new BulkRenameDialog(items, existsInFolder).ShowDialog<IReadOnlyList<BulkRenameMove>>(this);
    }
}
