// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Security;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Dialogs;
using ByteExplorer.Views.Menus;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Password protection (.bxlock): the format (round trips, wrong passwords, tampering, truncation), protecting files
    /// and folders (verified before the original goes), browsing an unlocked item like a ZIP file, locking, removing the
    /// protection, changing the password, the dialog and the right-click menu.
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class PasswordProtectionTests : IDisposable
    {
        /// <summary>The fewest rounds a file may claim: keeps the tests quick (the real default is 600,000).</summary>
        private const int TestIterations = ProtectedFormat.MinIterations;
        private const int SmallChunk = ProtectedFormat.MinChunkSize;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-lock-" + Guid.NewGuid().ToString("N"));

        public PasswordProtectionTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            ProtectedItems.LockAll();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------------ format

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(SmallChunk - 1)]
        [InlineData(SmallChunk)]
        [InlineData(SmallChunk + 1)]
        [InlineData(SmallChunk * 3)]
        [InlineData(SmallChunk * 3 + 777)]
        public void Streams_RoundTrip_AnySize_WithSeeking(int size)
        {
            byte[] data = RandomBytes(size, seed: size);
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, data);

            using var stream = new DecryptingStream(new MemoryStream(file), key);
            Assert.Equal(size, stream.Length);
            Assert.Equal(data, ReadAll(stream));

            // Random access, as a ZIP reader does.
            var random = new Random(size);
            for (int i = 0; i < 20 && size > 0; i++)
            {
                int offset = random.Next(size);
                int count = Math.Min(random.Next(1, SmallChunk * 2), size - offset);
                stream.Position = offset;
                byte[] part = new byte[count];
                stream.ReadExactly(part);
                Assert.Equal(data.AsSpan(offset, count).ToArray(), part);
            }
        }

        [Fact]
        public void TheRightPassword_Unlocks_AWrongOne_IsRefused()
        {
            using ProtectedKey key = ProtectedKey.Create("correct horse", true, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, Encoding.UTF8.GetBytes("secret"));
            ProtectedHeader header = ProtectedFormat.ReadHeader(new MemoryStream(file));
            Assert.True(header.IsFolder);
            Assert.Equal(TestIterations, header.Iterations);

            using (ProtectedKey again = ProtectedKey.Unlock(header, "correct horse"))
            {
                Assert.True(again.Matches(header));
            }

            Assert.Throws<WrongPasswordException>(() => ProtectedKey.Unlock(header, "Correct horse"));
            Assert.Throws<WrongPasswordException>(() => ProtectedKey.Unlock(header, "correct horse "));
        }

        [Fact]
        public void TheSamePassword_GivesEveryFileADifferentKeyAndCiphertext()
        {
            byte[] data = RandomBytes(5000, 1);
            using ProtectedKey a = ProtectedKey.Create("same password", false, TestIterations, SmallChunk);
            using ProtectedKey b = ProtectedKey.Create("same password", false, TestIterations, SmallChunk);
            byte[] fileA = Encrypt(a, data);
            byte[] fileB = Encrypt(b, data);
            Assert.NotEqual(fileA.AsSpan(16, 24).ToArray(), fileB.AsSpan(16, 24).ToArray()); // salt and nonce prefix
            Assert.NotEqual(fileA.AsSpan(ProtectedFormat.HeaderLength, 64).ToArray(), fileB.AsSpan(ProtectedFormat.HeaderLength, 64).ToArray());
            Assert.Throws<WrongPasswordException>(() => new DecryptingStream(new MemoryStream(fileB), a));
        }

        [Fact]
        public void AChangedByte_AnywhereInTheContent_IsDetected()
        {
            byte[] data = RandomBytes(SmallChunk * 3 + 100, 2);
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, data);
            foreach (int offset in new[] { ProtectedFormat.HeaderLength, ProtectedFormat.HeaderLength + SmallChunk + 20, file.Length - 1 })
            {
                byte[] tampered = (byte[])file.Clone();
                tampered[offset] ^= 0x01;
                using var stream = new DecryptingStream(new MemoryStream(tampered), key);
                Assert.Throws<InvalidDataException>(() => ReadAll(stream));
            }
        }

        [Fact]
        public void AChangedHeader_IsTakenForAWrongPassword()
        {
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, RandomBytes(100, 3));
            file[20] ^= 0x01; // the salt
            ProtectedHeader header = ProtectedFormat.ReadHeader(new MemoryStream(file));
            Assert.Throws<WrongPasswordException>(() => ProtectedKey.Unlock(header, "correct horse"));

            file[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(() => ProtectedFormat.ReadHeader(new MemoryStream(file)));
        }

        [Fact]
        public void AFileCutShort_OrSwappedChunks_OrNeverCompleted_DoNotDecrypt()
        {
            byte[] data = RandomBytes(SmallChunk * 3, 4);
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, data);
            int record = SmallChunk + ProtectedFormat.TagLength;

            // The last chunk removed, exactly at a chunk boundary: the new last chunk wasn't sealed as the last.
            byte[] cut = file.AsSpan(0, file.Length - record).ToArray();
            using (var stream = new DecryptingStream(new MemoryStream(cut), key))
            {
                Assert.Throws<InvalidDataException>(() => ReadAll(stream));
            }

            // Two chunks swapped.
            byte[] swapped = (byte[])file.Clone();
            file.AsSpan(ProtectedFormat.HeaderLength, record).CopyTo(swapped.AsSpan(ProtectedFormat.HeaderLength + record));
            file.AsSpan(ProtectedFormat.HeaderLength + record, record).CopyTo(swapped.AsSpan(ProtectedFormat.HeaderLength));
            using (var stream = new DecryptingStream(new MemoryStream(swapped), key))
            {
                Assert.Throws<InvalidDataException>(() => ReadAll(stream));
            }

            // Written but never completed (an interrupted write).
            var unfinished = new MemoryStream();
            using (var writer = new EncryptingStream(unfinished, key, leaveOpen: true))
            {
                writer.Write(data);
            }

            using (var stream = new DecryptingStream(new MemoryStream(unfinished.ToArray()), key))
            {
                Assert.Throws<InvalidDataException>(() => ReadAll(stream));
            }

            // Only a header.
            Assert.Throws<InvalidDataException>(() => new DecryptingStream(new MemoryStream(file.AsSpan(0, ProtectedFormat.HeaderLength).ToArray()), key));
        }

        [Fact]
        public void CraftedHeaders_WithAbsurdCosts_AreRefused()
        {
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            byte[] file = Encrypt(key, RandomBytes(10, 5));
            byte[] slow = (byte[])file.Clone();
            BitConverter.GetBytes(int.MaxValue).CopyTo(slow, 12); // iterations: would hang the check
            Assert.Throws<InvalidDataException>(() => ProtectedFormat.ReadHeader(new MemoryStream(slow)));
            byte[] huge = (byte[])file.Clone();
            BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 40); // chunk size: would allocate gigabytes
            Assert.Throws<InvalidDataException>(() => ProtectedFormat.ReadHeader(new MemoryStream(huge)));
        }

        [Fact]
        public void PlacesInsideProtectedItems_AreRecognised_ForKeepingThemOutOfSavedHistory()
        {
            string locked = Path.Combine(_root, "Photos.bxlock");
            Assert.True(ProtectedFormat.IsProtectedPath(locked));
            Assert.True(ProtectedFormat.IsProtectedPath(Path.Combine(locked, "Photos", "2024")));
            Assert.False(ProtectedFormat.IsProtectedPath(Path.Combine(_root, "Photos.bxlocked")));
            Assert.False(ProtectedFormat.IsProtectedPath(Path.Combine(_root, "a.bxlock.txt")));
            Assert.Equal(_root, ProtectedFormat.OutsideProtected(Path.Combine(locked, "Photos", "2024")));
            Assert.Equal(_root, ProtectedFormat.OutsideProtected(_root));
        }

        [Fact]
        public void PasswordRules_AndStrength()
        {
            Assert.NotNull(ProtectedItems.PasswordProblem(""));
            Assert.NotNull(ProtectedItems.PasswordProblem("short"));
            Assert.NotNull(ProtectedItems.PasswordProblem("        "));
            Assert.Null(ProtectedItems.PasswordProblem("long enough"));
            Assert.Equal("Weak", ProtectedItems.Strength("aaaaaaaa"));
            Assert.Equal("Strong", ProtectedItems.Strength("Tr0ub4dor & horse battery"));
        }

        // ------------------------------------------------------------------ protect, browse, lock, remove, change

        [Fact]
        public void ProtectingAFolder_KeepsEverything_DeletesTheOriginal_AndRemovingTheProtectionGivesItBack()
        {
            string folder = MakeTree("Photos");
            Dictionary<string, string> before = Snapshot(folder);
            DateTime nestedTime = new DateTime(2020, 5, 6, 7, 8, 10);
            File.SetLastWriteTime(Path.Combine(folder, "2024", "beach.jpg"), nestedTime);

            ProtectResult result;
            using (ProtectedKey key = ProtectedKey.Create("correct horse", true, TestIterations, SmallChunk))
            {
                result = ProtectedItems.Protect(folder, key, null, null, CancellationToken.None);
            }

            Assert.Equal(Path.Combine(_root, "Photos.bxlock"), result.ProtectedPath);
            Assert.Empty(result.NotDeleted);
            Assert.False(Directory.Exists(folder));
            Assert.False(File.Exists(result.ProtectedPath + ".partial"));
            Assert.Throws<WrongPasswordException>(() => ProtectedItems.Unlock(result.ProtectedPath, "wrong password"));

            // Nothing of the content is readable in the file.
            byte[] raw = File.ReadAllBytes(result.ProtectedPath);
            Assert.DoesNotContain("readme", Encoding.ASCII.GetString(raw));

            using (ProtectedKey key = ProtectedItems.Unlock(result.ProtectedPath, "correct horse"))
            {
                string restored = ProtectedItems.RemoveProtection(result.ProtectedPath, key, null, null, CancellationToken.None);
                Assert.Equal(folder, restored);
            }

            Assert.False(File.Exists(result.ProtectedPath));
            Assert.Equal(before, Snapshot(folder));
            Assert.True(Directory.Exists(Path.Combine(folder, "empty")));
            Assert.Equal(nestedTime, File.GetLastWriteTime(Path.Combine(folder, "2024", "beach.jpg")));
            Assert.Empty(Directory.GetDirectories(_root, ".bxlock-restore-*"));
        }

        [Fact]
        public void ProtectingAFile_AndRestoringItBesideATakenName()
        {
            string file = Path.Combine(_root, "report.docx");
            byte[] content = RandomBytes(SmallChunk * 300 + 5, 6); // over a megabyte: more than one real chunk
            File.WriteAllBytes(file, content);
            string locked;
            using (ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations))
            {
                locked = ProtectedItems.Protect(file, key, null, null, CancellationToken.None).ProtectedPath;
            }

            Assert.Equal(file + ".bxlock", locked);
            Assert.False(File.Exists(file));
            File.WriteAllText(file, "a new file with the old name");

            using (ProtectedKey key = ProtectedItems.Unlock(locked, "correct horse"))
            {
                string restored = ProtectedItems.RemoveProtection(locked, key, null, null, CancellationToken.None);
                Assert.Equal(Path.Combine(_root, "report (2).docx"), restored);
                Assert.Equal(content, File.ReadAllBytes(restored));
            }

            Assert.Equal("a new file with the old name", File.ReadAllText(file));
        }

        [Fact]
        public void ProtectingBesideATakenName_UsesAFreeOne()
        {
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "notes");
            File.WriteAllText(Path.Combine(_root, "notes.txt.bxlock"), "someone else's");
            using ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk);
            string locked = ProtectedItems.Protect(Path.Combine(_root, "notes.txt"), key, null, null, CancellationToken.None).ProtectedPath;
            Assert.Equal(Path.Combine(_root, "notes.txt (2).bxlock"), locked);
            Assert.Equal("someone else's", File.ReadAllText(Path.Combine(_root, "notes.txt.bxlock")));
        }

        [Fact]
        public void ACancelledProtection_KeepsTheOriginal_AndLeavesNothingBehind()
        {
            string folder = MakeTree("Work");
            Dictionary<string, string> before = Snapshot(folder);
            using var cts = new CancellationTokenSource();
            int reports = 0;
            using ProtectedKey key = ProtectedKey.Create("correct horse", true, TestIterations, SmallChunk);
            Assert.ThrowsAny<OperationCanceledException>(() => ProtectedItems.Protect(folder, key, _ =>
            {
                if (++reports == 3)
                {
                    cts.Cancel();
                }
            }, null, cts.Token));

            Assert.Equal(before, Snapshot(folder));
            Assert.Empty(Directory.GetFiles(_root, "*.bxlock*"));
        }

        [Fact]
        public void WhatCantBeProtected_IsRefusedWithAReason()
        {
            Assert.NotNull(ProtectedItems.CannotProtectReason(Path.GetPathRoot(_root)));
            Assert.NotNull(ProtectedItems.CannotProtectReason(Path.Combine(_root, "missing")));
            File.WriteAllText(Path.Combine(_root, "a.bxlock"), "x");
            Assert.NotNull(ProtectedItems.CannotProtectReason(Path.Combine(_root, "a.bxlock")));

            if (!OperatingSystem.IsWindows())
            {
                string folder = MakeTree("Linked");
                File.CreateSymbolicLink(Path.Combine(folder, "link"), "/etc/hostname");
                Assert.Null(ProtectedItems.CannotProtectReason(folder));
                using ProtectedKey key = ProtectedKey.Create("correct horse", true, TestIterations, SmallChunk);
                IOException error = Assert.Throws<IOException>(() => ProtectedItems.Protect(folder, key, null, null, CancellationToken.None));
                Assert.Contains("link", error.Message);
                Assert.True(Directory.Exists(folder));
                Assert.Empty(Directory.GetFiles(_root, "Linked*"));
            }
        }

        [Fact]
        public void AnUnlockedItem_IsBrowsedLikeAZip_AndLockingForgetsItAndItsTemporaryFiles()
        {
            string folder = MakeTree("Album");
            string locked;
            using (ProtectedKey key = ProtectedKey.Create("correct horse", true, TestIterations, SmallChunk))
            {
                locked = ProtectedItems.Protect(folder, key, null, null, CancellationToken.None).ProtectedPath;
            }

            Assert.False(ArchiveBrowser.IsInsideArchive(locked)); // locked: an ordinary file
            using (ProtectedKey key = ProtectedItems.Unlock(locked, "correct horse"))
            {
                ProtectedItems.Remember(locked, key);
            }

            Assert.True(ArchiveBrowser.TryResolve(Path.Combine(locked, "Album", "2024"), out string archive, out string inner));
            Assert.Equal(locked, archive);
            Assert.Equal("Album/2024", inner);
            DirectoryIndex top = ArchiveBrowser.List(locked, string.Empty, locked);
            Assert.False(top.HasError);
            Assert.Equal(new[] { "Album" }, top.Entries.Select(e => e.Name));
            DirectoryIndex year = ArchiveBrowser.List(locked, "Album/2024", Path.Combine(locked, "Album", "2024"));
            Assert.Contains(year.Entries, e => e.Name == "beach.jpg" && !e.IsDirectory);

            IReadOnlyList<string> extracted = ArchiveBrowser.ExtractToTemp(new[] { Path.Combine(locked, "Album", "readme.txt") }, null, CancellationToken.None);
            Assert.Equal("hello", File.ReadAllText(extracted[0]));
            Assert.StartsWith(ProtectedItems.TempRoot, extracted[0]);

            string lockedEvent = null;
            EventHandler<string> listener = (_, path) => lockedEvent = path;
            ProtectedItems.Locked += listener;
            try
            {
                ProtectedItems.Lock(locked);
            }
            finally
            {
                ProtectedItems.Locked -= listener;
            }

            Assert.Equal(locked, lockedEvent);
            Assert.False(File.Exists(extracted[0]));
            Assert.False(ProtectedItems.IsUnlocked(locked));
            Assert.False(ArchiveBrowser.IsInsideArchive(Path.Combine(locked, "Album")));
            Assert.Null(ProtectedItems.KeyFor(locked));
        }

        [Fact]
        public void ItemsUnlockedUntilLeft_LockOnceNoPaneIsInside()
        {
            string file = Path.Combine(_root, "diary.txt");
            File.WriteAllText(file, "dear diary");
            string locked;
            using (ProtectedKey key = ProtectedKey.Create("correct horse", false, TestIterations, SmallChunk))
            {
                locked = ProtectedItems.Protect(file, key, null, null, CancellationToken.None).ProtectedPath;
                ProtectedItems.Remember(locked, key, lockWhenLeft: true);
            }

            ProtectedItems.LockLeft(new[] { locked, _root });
            Assert.True(ProtectedItems.IsUnlocked(locked));
            ProtectedItems.LockLeft(new[] { _root });
            Assert.False(ProtectedItems.IsUnlocked(locked));
        }

        [Fact]
        public void ChangingThePassword_ReEncrypts_TheOldOneStopsWorking()
        {
            string folder = MakeTree("Taxes");
            Dictionary<string, string> before = Snapshot(folder);
            string locked;
            using (ProtectedKey key = ProtectedKey.Create("old password", true, TestIterations, SmallChunk))
            {
                locked = ProtectedItems.Protect(folder, key, null, null, CancellationToken.None).ProtectedPath;
                ProtectedItems.Remember(locked, key);
            }

            using (ProtectedKey oldKey = ProtectedItems.Unlock(locked, "old password"))
            using (ProtectedKey newKey = ProtectedKey.Create("new password", true, TestIterations, SmallChunk))
            {
                ProtectedItems.ChangePassword(locked, oldKey, newKey, null, null, CancellationToken.None);
            }

            Assert.False(File.Exists(locked + ".partial"));
            Assert.Throws<WrongPasswordException>(() => ProtectedItems.Unlock(locked, "old password"));
            using (ProtectedKey session = ProtectedItems.KeyFor(locked))
            {
                Assert.NotNull(session); // the session's key follows the change
            }

            using (ProtectedKey key = ProtectedItems.Unlock(locked, "new password"))
            {
                ProtectedItems.RemoveProtection(locked, key, null, null, CancellationToken.None);
            }

            Assert.Equal(before, Snapshot(folder));
        }

        // ------------------------------------------------------------------ UI

        [AvaloniaFact]
        public void TheDialog_ForANewPassword_WantsItTwice_AndShowsWhatIsWrong()
        {
            var dialog = new PasswordDialog(new PasswordRequest { Title = "Protect with password", IsNew = true, Warning = "Forget it and it's gone." });
            dialog.Show();
            try
            {
                TextBox password = Find<TextBox>(dialog, "PasswordBox");
                TextBox confirm = Find<TextBox>(dialog, "ConfirmBox");
                Button ok = Find<Button>(dialog, "OkButton");
                TextBlock problem = Find<TextBlock>(dialog, "ProblemText");
                Assert.NotEqual('\0', password.PasswordChar);
                Assert.NotNull(Find<Border>(dialog, "WarningBox"));
                Assert.False(ok.IsEnabled);

                password.Text = "short";
                Dispatcher.UIThread.RunJobs();
                Assert.False(ok.IsEnabled);
                Assert.Contains("at least", problem.Text);

                password.Text = "long enough phrase";
                confirm.Text = "long enough phrasf";
                Dispatcher.UIThread.RunJobs();
                Assert.False(ok.IsEnabled);
                Assert.Contains("don't match", problem.Text);

                confirm.Text = "long enough phrase";
                Dispatcher.UIThread.RunJobs();
                Assert.True(ok.IsEnabled);
                Assert.False(problem.IsVisible);

                Find<Button>(dialog, "RevealButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(password.RevealPassword);
                Assert.True(confirm.RevealPassword);
                SaveScreenshot(dialog, "password-dialog-new.png");
            }
            finally
            {
                dialog.Close();
            }
        }

        [AvaloniaFact]
        public async Task TheDialog_ChecksThePassword_AndStaysOpenWhenItIsWrong()
        {
            var tries = new List<string>();
            var dialog = new PasswordDialog(new PasswordRequest
            {
                Title = "Open protected item",
                OfferRemember = true,
                Check = (password, _) =>
                {
                    tries.Add(password);
                    return Task.FromResult(password == "right password" ? null : "That password is wrong.");
                }
            });
            dialog.Show();
            TextBox box = Find<TextBox>(dialog, "PasswordBox");
            Button ok = Find<Button>(dialog, "OkButton");
            Assert.True(Find<CheckBox>(dialog, "RememberBox").IsVisible);

            box.Text = "wrong password";
            Dispatcher.UIThread.RunJobs();
            ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Pump(3);
            Assert.True(dialog.IsVisible);
            Assert.Equal("That password is wrong.", Find<TextBlock>(dialog, "ProblemText").Text);
            SaveScreenshot(dialog, "password-dialog-wrong.png");

            box.Text = "right password";
            Dispatcher.UIThread.RunJobs();
            ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Pump(3);
            Assert.False(dialog.IsVisible);
            Assert.Equal(new[] { "wrong password", "right password" }, tries);
        }

        [AvaloniaFact]
        public async Task ThePane_ProtectsOpensLocksAndRestores_ThroughTheMenu()
        {
            string folder = MakeTree("Holiday");
            Dictionary<string, string> before = Snapshot(folder);
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                var dialogs = new ScriptedDialogs(window);
                pane.DialogService = dialogs;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items.OfType<FileListItemViewModel>().Any(i => i.Name == "Holiday"));

                // An ordinary folder: "Protect with password…".
                MenuItem menu = FileContextMenu.BuildPasswordMenu(pane, new[] { folder });
                Assert.Equal(new[] { "Protect with password…" }, Headers(menu));

                dialogs.Answers.Enqueue("correct horse");
                await pane.ProtectAsync(new[] { folder });
                string locked = Path.Combine(_root, "Holiday.bxlock");
                await WaitUntil(() => File.Exists(locked) && !Directory.Exists(folder));
                await WaitUntil(() => pane.Items.OfType<FileListItemViewModel>().Any(i => i.Name == "Holiday.bxlock"));
                FileListItemViewModel lockedItem = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "Holiday.bxlock");
                Assert.Equal("Password-protected item", lockedItem.TypeDisplay);
                Assert.Equal(Icons.Symbol.LockClosed, FileIconGlyphs.For(lockedItem.Name, false));
                Assert.True(dialogs.Requests[0].IsNew);
                Assert.Contains("can't be recovered", dialogs.Requests[0].Warning);

                // A protected item: open, change, remove (no Lock while locked).
                Assert.Equal(new[] { "Open with password…", "Change password…", "Remove protection…" }, Headers(FileContextMenu.BuildPasswordMenu(pane, new[] { locked })));

                // Opening it (double-click): a wrong password first, then the right one.
                dialogs.Answers.Enqueue("wrong password");
                dialogs.Answers.Enqueue("correct horse");
                await pane.OpenItemAsync(lockedItem);
                await WaitUntil(() => pane.CurrentPath == locked);
                Assert.Equal("That password is wrong. Check Caps Lock and the keyboard layout, and try again.", dialogs.LastCheckError);
                Assert.True(pane.IsInsideArchive);
                Assert.Contains(pane.Items.OfType<FileListItemViewModel>(), i => i.Name == "Holiday" && i.IsDirectory);
                SaveScreenshot(window, "inside-protected-item.png");

                Assert.Contains("Lock now", Headers(FileContextMenu.BuildPasswordMenu(pane, new[] { locked })));

                // Locking sends the pane back to the folder.
                pane.LockProtected(locked);
                await WaitUntil(() => pane.CurrentPath == _root);
                Assert.False(ProtectedItems.IsUnlocked(locked));

                // Remove protection: asks the password again (locked), gives the folder back.
                dialogs.Answers.Enqueue("correct horse");
                await pane.RemoveProtectionAsync(locked);
                await WaitUntil(() => Directory.Exists(folder) && !File.Exists(locked));
                Assert.Equal(before, Snapshot(folder));
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private string MakeTree(string name)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(folder, "2024", "summer"));
            Directory.CreateDirectory(Path.Combine(folder, "empty"));
            File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");
            File.WriteAllBytes(Path.Combine(folder, "2024", "beach.jpg"), RandomBytes(SmallChunk * 5 + 3, 7));
            File.WriteAllBytes(Path.Combine(folder, "2024", "summer", "sea.raw"), RandomBytes(100_000, 8));
            File.WriteAllBytes(Path.Combine(folder, "zero.bin"), Array.Empty<byte>());
            return folder;
        }

        /// <summary>Every file (relative path → SHA-256) and folder (relative path → "dir") under a folder.</summary>
        private static Dictionary<string, string> Snapshot(string folder)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, entry);
                map[relative] = Directory.Exists(entry) ? "dir" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
            }

            return map;
        }

        private static byte[] Encrypt(ProtectedKey key, byte[] data)
        {
            var output = new MemoryStream();
            using (var writer = new EncryptingStream(output, key, leaveOpen: true))
            {
                // Odd-sized writes, as a ZIP writer makes.
                for (int offset = 0; offset < data.Length;)
                {
                    int count = Math.Min(1000 + offset % 3333, data.Length - offset);
                    writer.Write(data, offset, count);
                    offset += count;
                }

                writer.Complete();
            }

            return output.ToArray();
        }

        private static byte[] ReadAll(Stream stream)
        {
            stream.Position = 0;
            var copy = new MemoryStream();
            stream.CopyTo(copy, 777);
            return copy.ToArray();
        }

        private static byte[] RandomBytes(int size, int seed)
        {
            byte[] data = new byte[size];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static string[] Headers(MenuItem menu) =>
            menu.ItemsSource.OfType<MenuItem>().Select(i => i.Header as string).ToArray();

        private static T Find<T>(Control root, string name) where T : Control =>
            root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        private static void SaveScreenshot(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 500)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition());
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>The window's dialogs, except passwords: answered from a queue, each checked as the real dialog does.</summary>
        private sealed class ScriptedDialogs : IDialogService
        {
            private readonly IDialogService _inner;

            public ScriptedDialogs(IDialogService inner) => _inner = inner;

            public Queue<string> Answers { get; } = new Queue<string>();
            public List<PasswordRequest> Requests { get; } = new List<PasswordRequest>();
            public string LastCheckError { get; private set; }

            public async Task<PasswordAnswer> PromptPasswordAsync(PasswordRequest request)
            {
                Requests.Add(request);
                while (Answers.Count > 0)
                {
                    string password = Answers.Dequeue();
                    string error = request.Check == null ? null : await request.Check(password, CancellationToken.None);
                    if (error == null)
                    {
                        return new PasswordAnswer { Password = password, Remember = request.Remember };
                    }

                    LastCheckError = error;
                }

                return null;
            }

            public Task<string> PromptRenameAsync(string currentName, bool isDirectory) => _inner.PromptRenameAsync(currentName, isDirectory);
            public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, bool permanent) => _inner.ConfirmDeleteAsync(paths, permanent);
            public Task<bool> ConfirmAsync(string title, string message, string confirmText) => _inner.ConfirmAsync(title, message, confirmText);
            public void ShowProperties(IReadOnlyList<string> paths) => _inner.ShowProperties(paths);
            public Task<string> PromptTextAsync(string title, string message, string initialText, string placeholder, string okText, Func<string, string> validate) =>
                _inner.PromptTextAsync(title, message, initialText, placeholder, okText, validate);
            public Task ShowMessageAsync(string title, string message) => _inner.ShowMessageAsync(title, message);
            public Task<ConflictDecision> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => _inner.ResolveConflictAsync(conflict, cancellationToken);
            public Task<ErrorChoice> ResolveErrorAsync(ErrorInfo error, CancellationToken cancellationToken) => _inner.ResolveErrorAsync(error, cancellationToken);
        }
    }
}
