// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// Calculating folder sizes (Total Commander's Space / Alt+Shift+Enter): for the selected folders, or every folder in
    /// the open folder, counted off the UI thread, two at a time across the app, each shown as it grows ("1.2 GB…") and
    /// kept (<see cref="FolderSizes"/>). Leaving the folder stops what is still counting there. With the setting on, the
    /// folders of each folder opened on a local drive are counted as it opens.
    /// </summary>
    public sealed partial class FileListViewModel
    {
        /// <summary>At most this many folders counted at once in the whole app: each count keeps a disk busy.</summary>
        private static readonly SemaphoreSlim SizeSlots = new SemaphoreSlim(2, 2);

        private CancellationTokenSource _sizeCts;
        private AsyncRelayCommand _calculateFolderSizesCommand;
        private AsyncRelayCommand _calculateAllFolderSizesCommand;

        /// <summary>The selected folders' sizes; with no folder selected, every folder's in the open folder.</summary>
        public ICommand CalculateFolderSizesCommand => _calculateFolderSizesCommand ??= new AsyncRelayCommand(
            _ => CalculateFolderSizesAsync(SelectedFolderPaths() is { Count: > 0 } selected ? selected : FolderPathsInView(), announce: true),
            _ => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive, allowOverlap: true);

        /// <summary>Every folder's size in the open folder (Alt+Shift+Enter).</summary>
        public ICommand CalculateAllFolderSizesCommand => _calculateAllFolderSizesCommand ??= new AsyncRelayCommand(
            _ => CalculateFolderSizesAsync(FolderPathsInView(), announce: true),
            _ => !string.IsNullOrEmpty(CurrentPath) && !IsInsideArchive, allowOverlap: true);

        private IReadOnlyList<string> SelectedFolderPaths() =>
            SelectedItems.Where(i => i.IsDirectory).Select(i => i.FullPath).ToList();

        /// <summary>The folders the open folder shows (the hidden-items settings apply), by their list paths.</summary>
        internal IReadOnlyList<string> FolderPathsInView()
        {
            DirectoryIndex index = _currentIndex;
            string folder = CurrentPath;
            if (index == null || string.IsNullOrEmpty(folder))
            {
                return Array.Empty<string>();
            }

            bool hidden = ShowHiddenItems;
            bool system = ShowSystemItems;
            return index.Entries
                .Where(e => e.IsDirectory && (hidden || !e.IsHidden) && (system || !e.IsSystem))
                .Select(e => PathUtil.Combine(folder, e.Name))
                .ToList();
        }

        /// <summary>
        /// Counts the folders' sizes (all of them, or with <paramref name="onlyUncounted"/> those not counted yet), shown
        /// in the views as they grow. <paramref name="announce"/>: progress and the total in the status bar.
        /// </summary>
        public async Task CalculateFolderSizesAsync(IReadOnlyList<string> folders, bool announce, bool onlyUncounted = false)
        {
            if (folders == null || folders.Count == 0)
            {
                if (announce)
                {
                    StatusText = "No folders here to measure";
                }

                return;
            }

            List<string> todo = folders
                .Where(f => !Phones.IsPhonePath(f) || Phones.For(f) is MountedPhoneProvider) // a Windows phone's tree: through its provider, not counted
                .Where(f => !onlyUncounted || !FolderSizes.TryGet(f, out _))
                .ToList();
            if (todo.Count == 0)
            {
                if (announce && folders.Count > 0)
                {
                    StatusText = "Folder sizes aren't available on this phone";
                }

                return;
            }

            _sizeCts ??= new CancellationTokenSource();
            CancellationToken token = _sizeCts.Token;
            int done = 0;
            long total = 0;
            if (announce)
            {
                StatusText = $"Calculating folder sizes… 0 of {todo.Count:N0}";
            }

            await Task.WhenAll(todo.Select(async folder =>
            {
                FolderSizeInfo? result = await CalculateOneAsync(folder, token).ConfigureAwait(true);
                done++;
                total += result?.Bytes ?? 0;
                if (announce && !token.IsCancellationRequested)
                {
                    StatusText = done < todo.Count
                        ? $"Calculating folder sizes… {done:N0} of {todo.Count:N0}"
                        : $"{todo.Count:N0} folder size{(todo.Count == 1 ? string.Empty : "s")} calculated: {FolderSizes.FormatBytes(total)} in all";
                }
            })).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            // Views built from templates show sizes when built, and a size sort orders by them: rebuild once at the end.
            if (_sortColumn == FileListSortColumn.Size || ViewMode == FileListViewMode.Timeline || ViewMode == FileListViewMode.Nexus)
            {
                RebuildDisplayedItemsDebounced();
            }
        }

        /// <summary>One folder: counted on a worker, its size published as it grows. Null when cancelled or unreadable.</summary>
        private async Task<FolderSizeInfo?> CalculateOneAsync(string folder, CancellationToken token)
        {
            try
            {
                await SizeSlots.WaitAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                Publish(folder, new FolderSizeInfo(0, 0, 0, 0, isComplete: false));
                FolderSizeInfo info = await Task.Run(() => FolderSizes.Calculate(folder, token, partial =>
                {
                    FolderSizes.Set(folder, partial);
                    _dispatcher?.InvokeAsync(() => FolderSizes.NotifyChanged(folder));
                }), token).ConfigureAwait(true);
                Publish(folder, info);
                return info;
            }
            catch (OperationCanceledException)
            {
                Forget(folder);
                return null;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                _logger.Debug(ex, "Couldn't count the size of {Folder}", folder);
                Forget(folder);
                return null;
            }
            finally
            {
                SizeSlots.Release();
            }
        }

        private void Publish(string folder, FolderSizeInfo info)
        {
            FolderSizes.Set(folder, info);
            FolderSizes.NotifyChanged(folder);
            if (info.IsComplete)
            {
                RaisePropertyChanged(nameof(SelectionSummaryText));
                Preview?.UpdateFolderSize(folder);
            }
        }

        private static void Forget(string folder)
        {
            if (FolderSizes.TryGet(folder, out FolderSizeInfo info) && !info.IsComplete)
            {
                FolderSizes.ForgetOnly(folder); // a partial count isn't a size
            }
        }

        /// <summary>Stops counting what this pane started (it left the folder).</summary>
        private void CancelFolderSizes()
        {
            CancellationTokenSource cts = _sizeCts;
            _sizeCts = null;
            cts?.Cancel();
        }

        /// <summary>F5 forgot the counted sizes: counted again once the refreshed listing is in (with the setting on).</summary>
        private void StartAutomaticFolderSizesAfterRefresh(string path)
        {
            _dispatcher?.InvokeAsync(() =>
            {
                if (string.Equals(CurrentPath, path, PathUtil.Comparison))
                {
                    StartAutomaticFolderSizes(path);
                }
            });
        }

        /// <summary>The setting was just turned on: the open folder's folders are counted (local drives only).</summary>
        internal void StartAutomaticFolderSizesNow() => StartAutomaticFolderSizes(CurrentPath);

        /// <summary>After a folder opened: with the setting on, its folders are counted (local drives only).</summary>
        private void StartAutomaticFolderSizes(string path)
        {
            if (!(_settingsService?.Current?.FileList?.AutoCalculateFolderSizes ?? false) || string.IsNullOrEmpty(path) ||
                PathClassifier.IsNetworkPath(path) || Phones.IsPhonePath(path) || ArchiveBrowser.IsInsideArchive(path))
            {
                return;
            }

            CalculateFolderSizesAsync(FolderPathsInView(), announce: false, onlyUncounted: true).Forget("automatic folder sizes");
        }
    }
}
