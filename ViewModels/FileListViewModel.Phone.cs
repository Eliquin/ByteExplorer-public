// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The file list's commands in a phone folder, where the file system can't do them: opening a Windows phone's file
    /// (copied to the PC first), creating a folder or a text file, and renaming. They go through the phone's provider
    /// off the UI thread and are not undoable (nothing on a phone can be put back); copying, moving and deleting go
    /// through the operations queue like everywhere else (FileOperationService.Phone.cs).
    /// </summary>
    public sealed partial class FileListViewModel
    {
        /// <summary>
        /// Opens a file of a Windows phone: an application can only open a file on the PC, so it is copied to a temporary
        /// folder first (a job in the operations panel), as Explorer does. Changes made to it stay on the PC.
        /// </summary>
        private async Task OpenFromPhoneAsync(FileListItemViewModel item)
        {
            try
            {
                string local = Operations == null
                    ? await Task.Run(() => PhoneTemp.Download(item.FullPath, null, default)).ConfigureAwait(true)
                    : await Operations.RunBackgroundAsync($"Opening \"{item.Name}\" from the phone",
                        (progress, token) => Task.Run(() => PhoneTemp.Download(item.FullPath, progress, token), token)).ConfigureAwait(true);
                ShellLauncher.Open(local);
                StatusText = $"Opened a copy of \"{item.Name}\" (changes stay on this PC; copy it back to keep them on the phone)";
                _analyticsService.TrackFileOpened(item.FullPath);
                _soundFeedbackService?.PlayFileOpened();
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open {Path} from the phone", item.FullPath);
                OpenItemFailed?.Invoke(this, $"Couldn't open \"{item.Name}\" from the phone. {ex.Message}");
            }
        }

        /// <summary>"New folder" / "New text document" in a phone folder: the first free name, created on the phone.</summary>
        private async Task CreateOnPhoneAsync(string folder, bool isFolder)
        {
            string baseName = isFolder ? "New folder" : "New text document";
            string extension = isFolder ? string.Empty : ".txt";
            try
            {
                string created = await Task.Run(() =>
                {
                    IPhoneProvider phone = Phones.Require(folder);
                    if (phone.GetInfo(folder) is { IsDirectory: false } || PhonePaths.IsPhonePath(folder) && PhonePaths.Segments(folder).Length < 2)
                    {
                        throw new IOException("Open one of the phone's storages first; new items go inside it.");
                    }

                    for (int n = 1; n < 10000; n++)
                    {
                        string name = n == 1 ? baseName + extension : $"{baseName} ({n}){extension}";
                        if (phone.GetInfo(PathUtil.Combine(folder, name)) != null)
                        {
                            continue;
                        }

                        if (isFolder)
                        {
                            return phone.CreateFolder(folder, name);
                        }

                        using PhoneWriteStream stream = phone.CreateFile(folder, name, 0, DateTime.UtcNow);
                        return stream.Commit();
                    }

                    throw new IOException("No free name was found.");
                }).ConfigureAwait(true);

                _indexService.InvalidateCache(folder);
                await RefreshAfterFileOperationAsync(folder).ConfigureAwait(true);
                StatusText = isFolder ? "New folder created on the phone" : "New text document created on the phone";
                _logger.Information("Created {Path} on the phone", created);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Creating an item in {Folder} failed", folder);
                OperationFailed?.Invoke(this, $"Couldn't create the {(isFolder ? "folder" : "file")} on the phone.\n\n{ex.Message}");
            }
        }

        /// <summary>Renames an item on a phone (the name was validated by the caller); refused if the name is taken.</summary>
        private async Task RenameOnPhoneAsync(string fullPath, string oldName, string newName)
        {
            string parent = PathUtil.GetParent(fullPath);
            try
            {
                string renamed = await Task.Run(() =>
                {
                    IPhoneProvider phone = Phones.Require(fullPath);
                    bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
                    if (!caseOnly && phone.GetInfo(PathUtil.Combine(parent, newName)) != null)
                    {
                        throw new IOException("A file or folder with that name already exists.");
                    }

                    return phone.Rename(fullPath, newName);
                }).ConfigureAwait(true);

                _indexService.InvalidateCache(parent);
                string shown = CurrentPath;
                if (shown != null && PathUtil.IsSameOrInside(shown, fullPath) && !PathUtil.AreSame(shown, parent))
                {
                    await LoadFolderAsync(renamed + shown.Substring(PathUtil.TrimTrailingSeparator(fullPath).Length)).ConfigureAwait(true);
                }
                else
                {
                    await RefreshAfterFileOperationAsync(parent).ConfigureAwait(true);
                }

                StatusText = "Renamed on the phone";
                _analyticsService.TrackItemRenamed(success: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Rename on the phone failed for {Path}", fullPath);
                _analyticsService.TrackItemRenamed(success: false);
                OperationFailed?.Invoke(this, "Couldn't rename \"" + oldName + "\".\n\n" + ex.Message);
            }
        }
    }
}
