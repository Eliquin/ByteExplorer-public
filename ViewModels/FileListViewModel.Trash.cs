// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>The trash view's sortable columns.</summary>
    public enum TrashSortColumn
    {
        Name,
        OriginalLocation,
        DateDeleted,
        Size,
        Type
    }

    /// <summary>
    /// The trash / Recycle Bin surface: everything in the system's trash, listed by <see cref="Trash.List"/>, with
    /// Restore (through the operation queue, so it shows as a job and Undo sends the items back), Delete permanently and
    /// Empty (both asked first: neither can be undone). It is a page like This PC: no folder path, its own history entry
    /// and tab token (<see cref="Tabs.TabLocation.Trash"/>). Opening an item is deliberately not offered: a file in the
    /// trash is restored first, as in Explorer and Files, so nothing is ever edited inside the trash.
    /// </summary>
    public sealed partial class FileListViewModel
    {
        private ObservableCollection<TrashItemViewModel> _trashItems = new ObservableCollection<TrashItemViewModel>();
        private TrashSortColumn _trashSortColumn = TrashSortColumn.DateDeleted;
        private bool _trashSortAscending;
        private AsyncRelayCommand _emptyTrashCommand;

        /// <summary>The items shown, in the current sort order.</summary>
        public ObservableCollection<TrashItemViewModel> TrashItems
        {
            get => _trashItems;
            private set
            {
                if (SetProperty(ref _trashItems, value))
                {
                    RaisePropertyChanged(nameof(HasTrashItems));
                    _emptyTrashCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasTrashItems => _trashItems.Count > 0;

        /// <summary>"Recycle Bin" or "Trash", the surface's heading.</summary>
        public string TrashTitle => Trash.DisplayName;

        /// <summary>The Empty button's text.</summary>
        public string EmptyTrashText => OperatingSystem.IsWindows() ? "Empty Recycle Bin" : "Empty Trash";

        public TrashSortColumn TrashSortColumn => _trashSortColumn;

        public bool TrashSortAscending => _trashSortAscending;

        /// <summary>Empties the trash after asking (the surface's header button and the background menu).</summary>
        public AsyncRelayCommand EmptyTrashCommand =>
            _emptyTrashCommand ??= new AsyncRelayCommand(_ => EmptyTrashAsync(), _ => IsTrashView && HasTrashItems && !_isLoading);

        /// <summary>
        /// Sorts by a column: the same column again reverses the order. New columns start in the order people expect:
        /// newest first for the date, largest first for the size, A to Z otherwise.
        /// </summary>
        public void SortTrashBy(TrashSortColumn column)
        {
            if (column == _trashSortColumn)
            {
                _trashSortAscending = !_trashSortAscending;
            }
            else
            {
                _trashSortColumn = column;
                _trashSortAscending = column != TrashSortColumn.DateDeleted && column != TrashSortColumn.Size;
            }

            RaisePropertyChanged(nameof(TrashSortColumn));
            RaisePropertyChanged(nameof(TrashSortAscending));
            TrashItems = new ObservableCollection<TrashItemViewModel>(SortTrash(_trashItems));
        }

        /// <summary>Shows the trash in this pane (a navigation: recorded in the history, like This PC).</summary>
        public Task LoadTrashAsync()
        {
            NavigationRequested?.Invoke(this, EventArgs.Empty);
            HasStartedNavigation = true;
            StopAllLiveWatching();
            CancelFolderSizes();

            SetSurface(FileListSurface.Trash);
            RecordArrival(HistoryEntry.Trash);
            ErrorMessage = null;
            CurrentPath = null;
            Items = null;
            TimelineGroups = null;
            ColumnPanes = null;
            NexusClusters = null;
            SelectedItem = null;
            SelectedItems.Clear();
            return ReloadTrashAsync();
        }

        /// <summary>Lists the trash again (F5, and after anything that may have changed it); nothing when another surface is shown.</summary>
        public async Task ReloadTrashAsync()
        {
            if (!IsTrashView)
            {
                return;
            }

            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            IsLoading = true;
            StatusText = $"Loading the {Trash.DisplayName}…";
            try
            {
                IReadOnlyList<TrashEntry> entries = await Task.Run(() => Trash.List(cts.Token), cts.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_loadCts, cts) || !IsTrashView)
                {
                    return; // superseded
                }

                var items = new List<TrashItemViewModel>(entries.Count);
                foreach (TrashEntry entry in entries)
                {
                    items.Add(new TrashItemViewModel(entry));
                }

                TrashItems = new ObservableCollection<TrashItemViewModel>(SortTrash(items));
                UpdateTrashStatusText();
                CountTrashFolderSizesAsync(items, cts.Token).Forget("count the trash's folder sizes");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list the trash");
                ErrorMessage = $"Couldn't open the {Trash.DisplayName}.";
                StatusText = string.Empty;
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    IsLoading = false;
                    _emptyTrashCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// Puts the items back where they came from, as one job of the operation queue (Undo sends them back to the trash).
        /// Items whose place isn't known (macOS) are left, with a message.
        /// </summary>
        public async Task RestoreFromTrashAsync(IReadOnlyList<TrashItemViewModel> items)
        {
            if (items == null || items.Count == 0 || Operations == null)
            {
                return;
            }

            List<TrashEntry> restorable = items.Where(i => i.CanRestore).Select(i => i.Entry).ToList();
            if (restorable.Count == 0)
            {
                OperationFailed?.Invoke(this, $"The {Trash.DisplayName} doesn't say where {(items.Count == 1 ? "this item" : "these items")} came from, so {(items.Count == 1 ? "it" : "they")} can't be put back. Drag {(items.Count == 1 ? "it" : "them")} out of the {Trash.DisplayName} instead.");
                return;
            }

            FileOperationOutcome outcome = await Operations.RestoreFromTrashAsync(restorable).ConfigureAwait(true);
            FileOperationResult result = outcome.Result;
            int renamed = outcome.Record?.Actions.OfType<CreatedAction>()
                .Count(a => restorable.All(e => !Core.PathUtil.AreSame(e.OriginalPath, a.Path))) ?? 0;
            StatusText = $"Restored {result.SuccessCount:N0} item{(result.SuccessCount == 1 ? string.Empty : "s")}" +
                         (renamed > 0 ? $" ({renamed:N0} under a new name: the original place was taken)" : string.Empty);
            if (result.HasFailures)
            {
                OperationFailed?.Invoke(this, "Some items couldn't be restored:\n" +
                    string.Join("\n", result.Failures.Take(10).Select(f => $"- {Core.PathUtil.GetName(f.SourcePath)}: {f.Message}")));
            }

            await ReloadTrashAsync().ConfigureAwait(true);
        }

        /// <summary>Deletes the items from the trash for good, after asking.</summary>
        public async Task DeleteFromTrashPermanentlyAsync(IReadOnlyList<TrashItemViewModel> items)
        {
            if (items == null || items.Count == 0 || DialogService == null)
            {
                return;
            }

            string what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count:N0} items";
            bool confirmed = await DialogService.ConfirmAsync("Delete permanently",
                $"Permanently delete {what}? This can't be undone.", "Delete").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            List<TrashEntry> entries = items.Select(i => i.Entry).ToList();
            List<string> failures = await RunTrashWorkAsync($"Deleting {(items.Count == 1 ? what : $"{items.Count:N0} items")} from the {Trash.DisplayName}", token =>
            {
                var failed = new List<string>();
                foreach (TrashEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        Trash.DeletePermanently(entry, token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                    {
                        _logger.Warning(ex, "Deleting {Name} from the trash failed", entry.Name);
                        failed.Add($"- {entry.Name}: {ex.Message}");
                    }
                }

                return failed;
            }).ConfigureAwait(true);

            int deleted = entries.Count - failures.Count;
            StatusText = $"Deleted {deleted:N0} item{(deleted == 1 ? string.Empty : "s")} permanently";
            if (failures.Count > 0)
            {
                OperationFailed?.Invoke(this, "Some items couldn't be deleted:\n" + string.Join("\n", failures.Take(10)));
            }

            await ReloadTrashAsync().ConfigureAwait(true);
        }

        /// <summary>Empties the trash after asking.</summary>
        public async Task EmptyTrashAsync()
        {
            if (DialogService == null)
            {
                return;
            }

            int count = _trashItems.Count;
            string what = count > 0 ? $"all {count:N0} item{(count == 1 ? string.Empty : "s")} in the {Trash.DisplayName}" : $"everything in the {Trash.DisplayName}";
            bool confirmed = await DialogService.ConfirmAsync(OperatingSystem.IsWindows() ? "Empty Recycle Bin" : "Empty Trash",
                $"Permanently delete {what}? This can't be undone.", "Empty").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            List<string> failures = await RunTrashWorkAsync($"Emptying the {Trash.DisplayName}", token =>
            {
                try
                {
                    Trash.Empty(token);
                    return new List<string>();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    _logger.Warning(ex, "Emptying the trash failed");
                    return new List<string> { ex.Message };
                }
            }).ConfigureAwait(true);

            StatusText = failures.Count == 0 ? $"The {Trash.DisplayName} is empty" : string.Empty;
            if (failures.Count > 0)
            {
                OperationFailed?.Invoke(this, string.Join("\n", failures));
            }

            await ReloadTrashAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Runs permanent deletion work off the UI thread: as a job of the operation queue when there is one (it appears in
        /// the operations panel if it takes a while, and can be cancelled), else directly. Cancelled work returns no failures.
        /// </summary>
        private async Task<List<string>> RunTrashWorkAsync(string title, Func<CancellationToken, List<string>> work)
        {
            try
            {
                return Operations != null
                    ? await Operations.RunBackgroundAsync(title, (_, token) => Task.Run(() => work(token), token)).ConfigureAwait(true)
                    : await Task.Run(() => work(CancellationToken.None)).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return new List<string>();
            }
        }

        private void UpdateTrashStatusText()
        {
            int count = _trashItems.Count;
            long known = _trashItems.Where(i => i.SizeBytes > 0).Sum(i => i.SizeBytes);
            StatusText = count == 0
                ? $"The {Trash.DisplayName} is empty"
                : $"{count:N0} item{(count == 1 ? string.Empty : "s")} · {FolderSizes.FormatBytes(known)}";
        }

        /// <summary>
        /// Counts the folders whose size the trash doesn't record (Linux, macOS), one after another in the background, so
        /// the list shows at once and the Size column fills in. Stops when the pane shows something else.
        /// </summary>
        private async Task CountTrashFolderSizesAsync(IReadOnlyList<TrashItemViewModel> items, CancellationToken token)
        {
            foreach (TrashItemViewModel item in items)
            {
                if (!item.IsDirectory || item.SizeBytes >= 0)
                {
                    continue;
                }

                try
                {
                    FolderSizeInfo info = await Task.Run(() => FolderSizes.Calculate(item.Entry.TrashPath, token), token).ConfigureAwait(true);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    item.SizeBytes = info.Bytes;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    _logger.Debug(ex, "Could not count the size of {Path}", item.Entry.TrashPath);
                }
            }

            if (!token.IsCancellationRequested && IsTrashView)
            {
                UpdateTrashStatusText();
                if (_trashSortColumn == TrashSortColumn.Size)
                {
                    TrashItems = new ObservableCollection<TrashItemViewModel>(SortTrash(_trashItems));
                }
            }
        }

        private IEnumerable<TrashItemViewModel> SortTrash(IEnumerable<TrashItemViewModel> items)
        {
            StringComparer names = StringComparer.CurrentCultureIgnoreCase;
            IOrderedEnumerable<TrashItemViewModel> ordered = _trashSortColumn switch
            {
                TrashSortColumn.OriginalLocation => Order(items, i => i.OriginalLocation, names),
                TrashSortColumn.DateDeleted => Order(items, i => i.DeletedUtc, Comparer<DateTime>.Default),
                TrashSortColumn.Size => Order(items, i => i.SizeBytes, Comparer<long>.Default),
                TrashSortColumn.Type => Order(items, i => i.TypeDisplay, names),
                _ => Order(items, i => i.Name, names)
            };

            // Ties (same folder, same second) by name, A to Z, so the order is stable between reloads.
            return ordered.ThenBy(i => i.Name, names);
        }

        private IOrderedEnumerable<TrashItemViewModel> Order<TKey>(IEnumerable<TrashItemViewModel> items, Func<TrashItemViewModel, TKey> key, IComparer<TKey> comparer) =>
            _trashSortAscending ? items.OrderBy(key, comparer) : items.OrderByDescending(key, comparer);
    }
}
