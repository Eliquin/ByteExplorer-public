// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ByteExplorer.ViewModels
{
    public sealed partial class FileListViewModel
    {
        /// <summary>The most items Enter opens at once: a runaway "open 5,000 files" would start 5,000 programs.</summary>
        private const int MaxItemsOpenedAtOnce = 32;

        /// <summary>
        /// Enter on the selection, as in Explorer: one item opens as a double-click would; with several, every file goes
        /// to its program and the folders open in new tabs (Explorer opens them in new windows), or, without tabs, the
        /// first folder opens here.
        /// </summary>
        public async Task OpenSelectionAsync(FileListItemViewModel focused)
        {
            List<FileListItemViewModel> selected = SelectedItems.ToList();
            if (selected.Count <= 1)
            {
                FileListItemViewModel item = selected.Count == 1 ? selected[0] : focused;
                if (item != null)
                {
                    await OpenItemAsync(item).ConfigureAwait(true);
                }

                return;
            }

            foreach (FileListItemViewModel file in selected.Where(i => !i.IsDirectory).Take(MaxItemsOpenedAtOnce))
            {
                await OpenItemAsync(file).ConfigureAwait(true);
            }

            List<string> folders = selected.Where(i => i.IsDirectory).Select(i => i.FullPath).Take(MaxItemsOpenedAtOnce).ToList();
            if (folders.Count == 0)
            {
                return;
            }

            if (OpenInNewTabs != null)
            {
                await OpenInNewTabs(folders, false).ConfigureAwait(true);
            }
            else
            {
                await LoadFolderAsync(folders[0]).ConfigureAwait(true);
            }
        }
    }
}
