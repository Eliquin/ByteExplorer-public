// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Renaming several items at once (F2, Rename with 2+ items selected): the "Rename items" dialog computes the new names
    /// (Core/BulkRename) and the renames run as one operation with one undo record, so a single Ctrl+Z puts the whole
    /// batch back. Phones are left out: their providers rename one item at a time with no undo, and a half-renamed batch
    /// there could not be put back.
    /// </summary>
    public sealed partial class FileListViewModel
    {
        /// <summary>Raised after a bulk rename with the new names of the renamed items in the shown folder, for the view to select them.</summary>
        public event EventHandler<IReadOnlyList<string>> SelectNamesRequested;

        /// <summary>The selected items in the order the list shows them (the current sort), which numbering follows.</summary>
        internal List<FileListItemViewModel> SelectedItemsInListOrder()
        {
            var selected = SelectedItems.ToList();
            if (Items is VirtualizingFileList list)
            {
                // IndexOf is O(1) here (each item knows its row). Other lists (Timeline groups) keep the selection order.
                var order = selected.Select((item, i) => (Item: item, Index: list.IndexOf(item), Position: i)).ToList();
                order.Sort((a, b) =>
                {
                    int x = a.Index < 0 ? int.MaxValue : a.Index;
                    int y = b.Index < 0 ? int.MaxValue : b.Index;
                    return x != y ? x.CompareTo(y) : a.Position.CompareTo(b.Position);
                });
                selected = order.Select(o => o.Item).ToList();
            }

            return selected;
        }

        private async Task RenameSeveralAsync()
        {
            List<FileListItemViewModel> items = SelectedItemsInListOrder();
            if (items.Count < 2 || IsInsideArchive)
            {
                return;
            }

            if (items.Any(item => Phones.IsPhonePath(item.FullPath)))
            {
                OperationFailed?.Invoke(this, "Several items can't be renamed at once on a phone. Rename them one at a time.");
                return;
            }

            if (!(DialogService is IBulkRenameDialogService dialogs))
            {
                return;
            }

            var renameItems = items.Select(item => new BulkRenameItem(item.FullPath, item.IsDirectory, item.LastWriteTimeUtc.ToLocalTime())).ToList();
            Func<string, string, bool> exists = await Task.Run(() => ReadFolderNames(renameItems)).ConfigureAwait(true);
            IReadOnlyList<BulkRenameMove> moves = await dialogs.PromptBulkRenameAsync(renameItems, exists).ConfigureAwait(true);
            await ApplyBulkRenameAsync(moves).ConfigureAwait(true);
        }

        /// <summary>
        /// The names in the items' folders, read from disk (hidden items included, which the list may not show), so the
        /// preview can say which new names are taken.
        /// </summary>
        private Func<string, string, bool> ReadFolderNames(IEnumerable<BulkRenameItem> items)
        {
            var byFolder = new Dictionary<string, HashSet<string>>(PathUtil.Comparer);
            foreach (string folder in items.Select(i => i.Folder).Distinct(PathUtil.Comparer))
            {
                var names = new HashSet<string>(PathUtil.Comparer);
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        names.Add(Path.GetFileName(path));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.Warning(ex, "Couldn't list {Folder} for the bulk rename preview", folder);
                }

                byFolder[folder] = names;
            }

            return (folder, name) => byFolder.TryGetValue(folder ?? string.Empty, out HashSet<string> names) && names.Contains(name);
        }

        /// <summary>
        /// Renames the items as one operation (see <see cref="BulkRenameRunner"/> for chains, cycles and case-only renames)
        /// and records one undo step for all of it. Items that fail are reported by name; the others stay renamed, and the
        /// undo puts back exactly what was done. Afterwards the renamed items are selected.
        /// </summary>
        public async Task ApplyBulkRenameAsync(IReadOnlyList<BulkRenameMove> moves)
        {
            if (moves == null || moves.Count == 0)
            {
                return;
            }

            if (moves.Any(m => Phones.IsPhonePath(m.From)))
            {
                OperationFailed?.Invoke(this, "Several items can't be renamed at once on a phone. Rename them one at a time.");
                return;
            }

            bool caseInsensitive = PathUtil.Comparison == StringComparison.OrdinalIgnoreCase;
            BulkRenameResult result = await Task.Run(() => BulkRenameRunner.Run(moves, caseInsensitive)).ConfigureAwait(true);
            string[] folders = moves.Select(m => Path.GetDirectoryName(m.From) ?? string.Empty).Distinct(PathUtil.Comparer).ToArray();

            if (result.Steps.Count > 0)
            {
                List<BulkRenameMove> done = result.Renamed.ToList();
                string description = done.Count == 1
                    ? $"Rename \"{Path.GetFileName(done[0].From)}\" to \"{Path.GetFileName(done[0].To)}\""
                    : $"Rename {done.Count} items";
                Operations?.Record(new FileOperationRecord(description, result.Steps.ToArray(), folders)
                {
                    // Redo renames them again, with the same checks (an item whose new name was taken since fails alone).
                    Redo = () => ApplyBulkRenameAsync(done)
                });
            }

            foreach (string folder in folders)
            {
                _indexService.InvalidateCache(folder);
            }

            for (int i = 0; i < result.Renamed.Count + result.Failures.Count; i++)
            {
                _analyticsService.TrackItemRenamed(success: i < result.Renamed.Count);
            }

            string shown = CurrentPath;
            if (shown != null && folders.Any(f => PathUtil.AreSame(f, shown)))
            {
                await RefreshAfterFileOperationAsync(shown).ConfigureAwait(true);
            }
            else if (folders.Length > 0)
            {
                await RefreshAfterFileOperationAsync(folders[0]).ConfigureAwait(true);
            }

            string[] renamedHere = result.Renamed
                .Where(m => shown != null && PathUtil.AreSame(Path.GetDirectoryName(m.To), shown))
                .Select(m => Path.GetFileName(m.To))
                .ToArray();
            if (renamedHere.Length > 0)
            {
                SelectNamesRequested?.Invoke(this, renamedHere);
            }

            StatusText = result.Renamed.Count == 1 ? "Renamed 1 item" : $"Renamed {result.Renamed.Count} items";
            if (result.Failures.Count > 0)
            {
                _logger.Warning("Bulk rename: {Renamed} renamed, {Failed} failed", result.Renamed.Count, result.Failures.Count);
                OperationFailed?.Invoke(this, DescribeFailures(result));
            }
        }

        /// <summary>"3 of 12 items couldn't be renamed:" and the first few names with their reasons.</summary>
        internal static string DescribeFailures(BulkRenameResult result)
        {
            const int Listed = 8;
            int total = result.Failures.Count + result.Renamed.Count;
            var text = new StringBuilder();
            text.Append(result.Failures.Count == 1 ? "1 item" : $"{result.Failures.Count} items")
                .Append(total > result.Failures.Count ? $" of {total}" : string.Empty)
                .Append(" couldn't be renamed")
                .Append(result.Renamed.Count > 0 ? " (the others were):" : ":");
            foreach ((string name, string message) in result.Failures.Take(Listed))
            {
                text.Append("\n\n\"").Append(name).Append("\": ").Append(message);
            }

            if (result.Failures.Count > Listed)
            {
                text.Append($"\n\n…and {result.Failures.Count - Listed} more.");
            }

            return text.ToString();
        }
    }
}
