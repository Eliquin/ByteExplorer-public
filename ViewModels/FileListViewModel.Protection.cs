// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Security;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The right-click menu's Password submenu (<see cref="ProtectedItems"/>): protecting files and folders with a
    /// password, opening a protected item (it is then browsed like a ZIP file), locking it again, removing the protection
    /// and changing the password. The slow parts (deriving the key, encrypting, checking) run as jobs in the operations
    /// panel, with progress, pause and cancel; none is recorded for Undo, since undoing a protection would delete the only
    /// copy of the data.
    /// </summary>
    public sealed partial class FileListViewModel
    {
        private const string ForgottenWarning =
            "If you forget the password, the content can't be recovered: not by ByteExplorer, not by anyone.";

        private static readonly object PanesGate = new object();
        private static readonly List<WeakReference<FileListViewModel>> ProtectionPanes = new List<WeakReference<FileListViewModel>>();
        private static bool s_lockListenerAttached;
        private bool _registeredForProtection;

        /// <summary>Whether the Password submenu offers "Protect with password…" for these items.</summary>
        public bool CanProtect(IReadOnlyList<string> paths) =>
            paths != null && paths.Count > 0 && DialogService != null && paths.All(p => ProtectedItems.CannotProtectReason(p) == null);

        /// <summary>True for a .bxlock file on a drive (not inside an archive or on a phone): the protected-item rows apply.</summary>
        public static bool IsProtectedItem(string path) =>
            ProtectedFormat.IsProtectedFile(path) && !PhonePaths.IsPhonePath(path) && !ArchiveBrowser.IsInsideArchive(PathUtil.GetParent(path)) && File.Exists(path);

        /// <summary>
        /// Protect with password: one password for the selected items, each encrypted into its own "name.bxlock" beside
        /// it; the originals are deleted (permanently) once their encrypted copies have been checked.
        /// </summary>
        public async Task ProtectAsync(IReadOnlyList<string> paths)
        {
            if (!CanProtect(paths))
            {
                string reason = paths?.Select(ProtectedItems.CannotProtectReason).FirstOrDefault(r => r != null);
                if (reason != null)
                {
                    OperationFailed?.Invoke(this, reason);
                }

                return;
            }

            string what = paths.Count == 1 ? $"\"{PathUtil.GetName(paths[0])}\"" : $"These {paths.Count:N0} items";
            string into = paths.Count == 1 ? $"\"{PathUtil.GetName(paths[0])}{ProtectedFormat.Extension}\"" : $"one {ProtectedFormat.Extension} file each";
            PasswordAnswer answer = await DialogService.PromptPasswordAsync(new PasswordRequest
            {
                Title = "Protect with password",
                Message = $"{what} will be encrypted (AES-256) into {into}. Once the encrypted copy has been checked, the original is deleted.",
                OkText = "Protect",
                IsNew = true,
                Warning = ForgottenWarning + " The original is deleted permanently, not to the Recycle Bin; on SSDs, traces of it can remain on the drive until the space is reused."
            }).ConfigureAwait(true);
            if (answer == null)
            {
                return;
            }

            var problems = new List<string>();
            string lastCreated = null;
            foreach (string path in paths)
            {
                string name = PathUtil.GetName(path);
                try
                {
                    ProtectResult result = await RunProtectionJobAsync($"Protecting \"{name}\"", "protected", new[] { path }, context =>
                    {
                        bool isFolder = Directory.Exists(path);
                        using ProtectedKey key = ProtectedKey.Create(answer.Password, isFolder);
                        return ProtectedItems.Protect(path, key, context.Progress, context.Pause, context.CancellationToken);
                    }).ConfigureAwait(true);
                    lastCreated = result.ProtectedPath;
                    if (result.NotDeleted.Count > 0)
                    {
                        problems.Add($"\"{name}\" is protected, but these parts of the original couldn't be deleted (in use, or access denied); delete them yourself:\n  "
                            + string.Join("\n  ", result.NotDeleted.Take(8)) + (result.NotDeleted.Count > 8 ? $"\n  … and {result.NotDeleted.Count - 8:N0} more" : string.Empty));
                    }
                }
                catch (OperationCanceledException)
                {
                    StatusText = "Cancelled: the original was kept";
                    break;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    _logger.Warning(ex, "Protecting {Path} failed", path);
                    problems.Add($"\"{name}\" wasn't protected (the original was kept). {ex.Message}");
                }
            }

            if (lastCreated != null)
            {
                StatusText = paths.Count == 1 ? $"Protected as \"{PathUtil.GetName(lastCreated)}\"" : "Protected";
            }

            if (problems.Count > 0)
            {
                OperationFailed?.Invoke(this, string.Join("\n\n", problems));
            }
        }

        /// <summary>
        /// Opens a protected item: asks for its password (unless it is unlocked already) and shows what is inside, like a
        /// ZIP file. With "keep it unlocked" off, it locks again once no pane shows anything inside it.
        /// </summary>
        public async Task OpenProtectedAsync(string path)
        {
            if (!ProtectedItems.IsUnlocked(path) || ProtectedItems.KeyFor(path) is not ProtectedKey existing)
            {
                if (!await UnlockAsync(path, offerRemember: true).ConfigureAwait(true))
                {
                    return;
                }
            }
            else
            {
                existing.Dispose();
            }

            RegisterForProtection();
            await LoadFolderAsync(path, bypassCache: true).ConfigureAwait(true);
        }

        /// <summary>Asks for the password and keeps the item unlocked; false when cancelled or unreadable.</summary>
        private async Task<bool> UnlockAsync(string path, bool offerRemember)
        {
            if (DialogService == null)
            {
                return false;
            }

            ProtectedKey key = null;
            string name = PathUtil.GetName(path);
            PasswordAnswer answer = await DialogService.PromptPasswordAsync(new PasswordRequest
            {
                Title = "Open protected item",
                Message = $"Type the password of \"{name}\".",
                OkText = "Open",
                OfferRemember = offerRemember,
                Remember = true,
                Check = (password, token) => CheckPasswordAsync(path, password, token, k => key = k)
            }).ConfigureAwait(true);
            if (answer == null || key == null)
            {
                key?.Dispose();
                return false;
            }

            using (key)
            {
                ProtectedItems.Remember(path, key, lockWhenLeft: !answer.Remember);
            }

            return true;
        }

        /// <summary>The dialog's check: derives the key off the UI thread; a wrong password is said in the dialog.</summary>
        private static async Task<string> CheckPasswordAsync(string path, string password, CancellationToken token, Action<ProtectedKey> accepted)
        {
            try
            {
                ProtectedKey key = await Task.Run(() => ProtectedItems.Unlock(path, password), token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    key.Dispose();
                    return null;
                }

                accepted(key);
                return null;
            }
            catch (WrongPasswordException)
            {
                return "That password is wrong. Check Caps Lock and the keyboard layout, and try again.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is CryptographicException)
            {
                return ex.Message;
            }
        }

        /// <summary>Lock: forgets the key now; panes inside the item go back to its folder.</summary>
        public void LockProtected(string path)
        {
            ProtectedItems.Lock(path);
            StatusText = $"Locked \"{PathUtil.GetName(path)}\"";
        }

        /// <summary>
        /// Remove protection: decrypts the item back into the original beside it (under a free name if that one is taken)
        /// and deletes the protected file.
        /// </summary>
        public async Task RemoveProtectionAsync(string path)
        {
            ProtectedKey key = await KeyForOperationAsync(path, "Remove protection",
                $"Type the password of \"{PathUtil.GetName(path)}\". It is decrypted back into the original item beside it, and the protected file is deleted.",
                "Remove protection").ConfigureAwait(true);
            if (key == null)
            {
                return;
            }

            try
            {
                string restored = await RunProtectionJobAsync($"Removing the protection of \"{PathUtil.GetName(path)}\"", "decrypted", new[] { path },
                    context => ProtectedItems.RemoveProtection(path, key, context.Progress, context.Pause, context.CancellationToken)).ConfigureAwait(true);
                StatusText = $"Restored \"{PathUtil.GetName(restored)}\"";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled: the item is still protected";
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _logger.Warning(ex, "Removing the protection of {Path} failed", path);
                OperationFailed?.Invoke(this, $"The protection of \"{PathUtil.GetName(path)}\" wasn't removed (it is still protected). {ex.Message}");
            }
            finally
            {
                key.Dispose();
            }
        }

        /// <summary>Change password: the current one (unless unlocked), then the new one twice; the file is re-encrypted.</summary>
        public async Task ChangeProtectedPasswordAsync(string path)
        {
            string name = PathUtil.GetName(path);
            ProtectedKey oldKey = await KeyForOperationAsync(path, "Change password", $"Type the current password of \"{name}\".", "Next").ConfigureAwait(true);
            if (oldKey == null)
            {
                return;
            }

            try
            {
                PasswordAnswer answer = await DialogService.PromptPasswordAsync(new PasswordRequest
                {
                    Title = "Change password",
                    Message = $"Type the new password of \"{name}\". The old one stops working once the item has been re-encrypted.",
                    OkText = "Change",
                    IsNew = true,
                    Warning = ForgottenWarning
                }).ConfigureAwait(true);
                if (answer == null)
                {
                    return;
                }

                await RunProtectionJobAsync($"Changing the password of \"{name}\"", "re-encrypted", new[] { path }, context =>
                {
                    using ProtectedKey newKey = ProtectedKey.Create(answer.Password, oldKey.IsFolder);
                    ProtectedItems.ChangePassword(path, oldKey, newKey, context.Progress, context.Pause, context.CancellationToken);
                    return true;
                }).ConfigureAwait(true);
                StatusText = $"Changed the password of \"{name}\"";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled: the old password still works";
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _logger.Warning(ex, "Changing the password of {Path} failed", path);
                OperationFailed?.Invoke(this, $"The password of \"{name}\" wasn't changed (the old one still works). {ex.Message}");
            }
            finally
            {
                oldKey.Dispose();
            }
        }

        /// <summary>The key of an item for an operation: the session's when unlocked, else asked for (not kept).</summary>
        private async Task<ProtectedKey> KeyForOperationAsync(string path, string title, string message, string okText)
        {
            ProtectedKey key = ProtectedItems.KeyFor(path);
            if (key != null || DialogService == null)
            {
                return key;
            }

            PasswordAnswer answer = await DialogService.PromptPasswordAsync(new PasswordRequest
            {
                Title = title,
                Message = message,
                OkText = okText,
                Check = (password, token) => CheckPasswordAsync(path, password, token, k => key = k)
            }).ConfigureAwait(true);
            if (answer == null)
            {
                key?.Dispose();
                return null;
            }

            return key;
        }

        /// <summary>A job in the operations panel (or a plain worker when there is no panel, in tests).</summary>
        private async Task<T> RunProtectionJobAsync<T>(string title, string pastTense, IReadOnlyList<string> claims, Func<FileOperationContext, T> work)
        {
            if (Operations == null)
            {
                T value = await Task.Run(() => work(new FileOperationContext())).ConfigureAwait(true);
                await RefreshCurrentFolderAsync().ConfigureAwait(true);
                return value;
            }

            return await Operations.RunUnrecordedAsync(title, pastTense, claims, context => Task.Run(() => work(context), context.CancellationToken)).ConfigureAwait(true);
        }

        private static bool IsExpected(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is CryptographicException
            || ex is ArgumentException || ex is NotSupportedException;

        // ------------------------------------------------------------------ panes inside protected items

        /// <summary>
        /// Puts this pane on the list the lock rules look at: panes inside an item that is locked go back to its folder,
        /// and items unlocked "until I leave" lock once no listed pane is inside.
        /// </summary>
        private void RegisterForProtection()
        {
            lock (PanesGate)
            {
                if (!s_lockListenerAttached)
                {
                    s_lockListenerAttached = true;
                    ProtectedItems.Locked += OnProtectedItemLocked;
                }

                if (!_registeredForProtection)
                {
                    _registeredForProtection = true;
                    ProtectionPanes.Add(new WeakReference<FileListViewModel>(this));
                }
            }
        }

        private static List<FileListViewModel> LiveProtectionPanes()
        {
            var live = new List<FileListViewModel>();
            lock (PanesGate)
            {
                ProtectionPanes.RemoveAll(r => !r.TryGetTarget(out FileListViewModel pane) || pane._disposed);
                foreach (WeakReference<FileListViewModel> reference in ProtectionPanes)
                {
                    if (reference.TryGetTarget(out FileListViewModel pane))
                    {
                        live.Add(pane);
                    }
                }
            }

            return live;
        }

        /// <summary>Called when the pane's folder changes (and when it is closed): locks the items now left.</summary>
        private void OnPaneLocationChangedForProtection()
        {
            if (!_registeredForProtection && (_currentPath?.IndexOf(ProtectedFormat.Extension, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            {
                RegisterForProtection(); // inside a protected item, however it got there (address bar, history, a new tab)
            }

            if (!ProtectedItems.AnyLockWhenLeft)
            {
                return;
            }

            List<string> places = LiveProtectionPanes().Where(p => !p._disposed).Select(p => p._currentPath).Where(p => p != null).ToList();
            ProtectedItems.LockLeft(places);
        }

        private static void OnProtectedItemLocked(object sender, string lockedPath)
        {
            foreach (FileListViewModel pane in LiveProtectionPanes())
            {
                string current = pane._currentPath;
                if (!string.IsNullOrEmpty(current) && PathUtil.IsSameOrInside(PathUtil.Normalize(current), lockedPath))
                {
                    string folder = PathUtil.GetParent(lockedPath);
                    pane._dispatcher.InvokeAsync(() => pane.LoadFolderAsync(folder).Forget("leave a locked item")).Forget("leave a locked item");
                }
            }
        }
    }
}
