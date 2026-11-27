// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.FileOperations;
using ByteExplorer.ViewModels.Tabs;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The trash view: reading a freedesktop.org trash (home and per-volume, records without items, percent-encoded
    /// paths), restoring (also onto a taken name and into a deleted folder), deleting permanently (links are removed, not
    /// followed), emptying, the Windows $I size field, and the surface in a real window (sidebar pin, tab, breadcrumb,
    /// rows, Select all, Delete, Restore with undo). The freedesktop parts run on Linux only; the Windows Recycle Bin
    /// listing itself can't run here.
    /// </summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class TrashViewTests : IDisposable
    {
        private readonly string _root;
        private readonly string _trash;

        public TrashViewTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "be-trashview-" + Guid.NewGuid().ToString("N"));
            _trash = Path.Combine(_root, "Trash");
            Directory.CreateDirectory(Path.Combine(_trash, "files"));
            Directory.CreateDirectory(Path.Combine(_trash, "info"));
            Directory.CreateDirectory(Path.Combine(_root, "docs"));
        }

        public void Dispose()
        {
            Trash.TrashDirectoriesOverride = null;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------------ core (Linux)

        [Fact]
        public void ParseTrashInfo_DecodesThePathAndReadsTheLocalDate()
        {
            Trash.ParseTrashInfo("[Trash Info]\nPath=/home/a/My%20Files/caf%C3%A9.txt\nDeletionDate=2024-03-05T14:30:00\n",
                out string path, out DateTime deletedUtc);
            Assert.Equal("/home/a/My Files/café.txt", path);
            Assert.Equal(new DateTime(2024, 3, 5, 14, 30, 0, DateTimeKind.Local).ToUniversalTime(), deletedUtc);

            // Keys outside the [Trash Info] group, and a malformed date, are ignored.
            Trash.ParseTrashInfo("[Other]\nPath=/wrong\n[Trash Info]\nPath=/right\nDeletionDate=yesterday\n", out path, out deletedUtc);
            Assert.Equal("/right", path);
            Assert.Equal(DateTime.MinValue, deletedUtc);
        }

        [Fact]
        public void ListFreedesktop_ListsItemsWithTheirOriginalPlaceDateAndSize()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string original = Path.Combine(_root, "docs", "report final.txt");
            AddToTrash("report final.txt", original, "2024-01-02T03:04:05", content: "12345");
            string folderOriginal = Path.Combine(_root, "docs", "Photos");
            AddFolderToTrash("Photos", folderOriginal, "2024-02-01T10:00:00");
            File.WriteAllText(Path.Combine(_trash, "info", "orphan.txt.trashinfo"), "[Trash Info]\nPath=/x/orphan.txt\nDeletionDate=2024-01-01T00:00:00\n");
            File.WriteAllText(Path.Combine(_trash, "files", "no-record.txt"), "x"); // an item without a record is not listed

            // A volume's trash records paths relative to its top folder.
            string volume = Path.Combine(_root, "volume");
            string volumeTrash = Path.Combine(volume, ".Trash-1000");
            Directory.CreateDirectory(Path.Combine(volumeTrash, "files"));
            Directory.CreateDirectory(Path.Combine(volumeTrash, "info"));
            File.WriteAllText(Path.Combine(volumeTrash, "files", "song.mp3"), "abc");
            File.WriteAllText(Path.Combine(volumeTrash, "info", "song.mp3.trashinfo"), "[Trash Info]\nPath=Music/song.mp3\nDeletionDate=2024-05-05T05:05:05\n");

            List<TrashEntry> entries = Trash.ListFreedesktop(new[] { (_trash, (string)null), (volumeTrash, volume) }, CancellationToken.None);

            Assert.Equal(new[] { "Photos", "report final.txt", "song.mp3" }, entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            TrashEntry file = entries.Single(e => e.Name == "report final.txt");
            Assert.Equal(original, file.OriginalPath);
            Assert.Equal(Path.Combine(_root, "docs"), file.OriginalFolder);
            Assert.Equal(5, file.Size);
            Assert.False(file.IsDirectory);
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local).ToUniversalTime(), file.DeletedUtc);
            TrashEntry folder = entries.Single(e => e.Name == "Photos");
            Assert.True(folder.IsDirectory);
            Assert.Equal(-1, folder.Size);
            Assert.Equal(Path.Combine(volume, "Music", "song.mp3"), entries.Single(e => e.Name == "song.mp3").OriginalPath);
        }

        [Fact]
        public void Restore_PutsTheItemBackAndRemovesItsRecord_EvenIntoAFolderDeletedSince()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string original = Path.Combine(_root, "gone", "deeper", "notes.txt");
            AddToTrash("notes.txt", original, "2024-01-02T03:04:05", content: "kept");
            TrashEntry entry = Single();

            string restored = Trash.Restore(entry);

            Assert.Equal(original, restored);
            Assert.Equal("kept", File.ReadAllText(original));
            Assert.False(File.Exists(entry.TrashPath));
            Assert.False(File.Exists(entry.InfoPath));
            Assert.Empty(Trash.ListFreedesktop(new[] { (_trash, (string)null) }, CancellationToken.None));
        }

        [Fact]
        public void Restore_OntoATakenName_KeepsBoth()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string original = Path.Combine(_root, "docs", "plan.txt");
            File.WriteAllText(original, "the new one");
            AddToTrash("plan.txt", original, "2024-01-02T03:04:05", content: "the old one");

            string restored = Trash.Restore(Single());

            Assert.Equal(Path.Combine(_root, "docs", "plan (2).txt"), restored);
            Assert.Equal("the new one", File.ReadAllText(original));
            Assert.Equal("the old one", File.ReadAllText(restored));
        }

        [Fact]
        public void Restore_WithoutAnOriginalPlace_Throws()
        {
            var entry = new TrashEntry("x.txt", null, Path.Combine(_trash, "files", "x.txt"), null, DateTime.MinValue, 1, false);
            Assert.False(entry.CanRestore);
            Assert.Throws<IOException>(() => Trash.Restore(entry));
        }

        [Fact]
        public void DeletePermanently_RemovesTheTreeAndItsRecord_ButNotWhatALinkPointsTo()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
            string folder = AddFolderToTrash("Project", Path.Combine(_root, "docs", "Project"), "2024-01-01T00:00:00");
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "sub", "a.txt"), "a");
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);

            TrashEntry entry = Single();
            Trash.DeletePermanently(entry);

            Assert.False(Directory.Exists(entry.TrashPath));
            Assert.False(File.Exists(entry.InfoPath));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        }

        [Fact]
        public void EmptyFreedesktop_RemovesEverythingButKeepsTheTrashFolders()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            AddToTrash("a.txt", Path.Combine(_root, "docs", "a.txt"), "2024-01-01T00:00:00", content: "a");
            string folder = AddFolderToTrash("B", Path.Combine(_root, "docs", "B"), "2024-01-01T00:00:00");
            File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
            File.WriteAllText(Path.Combine(_trash, "files", "orphan"), "x");
            File.WriteAllText(Path.Combine(_trash, "directorysizes"), "4096 1700000000 B\n");

            Trash.EmptyFreedesktop(new[] { _trash }, CancellationToken.None);

            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_trash, "files")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_trash, "info")));
            Assert.False(File.Exists(Path.Combine(_trash, "directorysizes")));
        }

        [Fact]
        public void RecycleBinRecord_GivesItsSizeAndPath()
        {
            // A Windows 10 $I record (version 2): version, size, FILETIME, length in characters with the null, the path.
            string path = @"C:\Users\a\Documents\budget.xlsx";
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(2L));
            bytes.AddRange(BitConverter.GetBytes(123456L));
            bytes.AddRange(BitConverter.GetBytes(new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()));
            bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
            bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
            byte[] data = bytes.ToArray();

            Assert.True(RecycleBin.TryParseInfoFile(data, out string parsed, out DateTime deletedUtc));
            Assert.Equal(path, parsed);
            Assert.Equal(new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc), deletedUtc);
            Assert.Equal(123456L, RecycleBin.SizeFromInfoFile(data));
            Assert.Equal(-1L, RecycleBin.SizeFromInfoFile(new byte[4]));
        }

        [Fact]
        public void TrashIsAPageLocation()
        {
            Assert.True(TabLocation.IsValid(TabLocation.Trash));
            Assert.True(TabLocation.IsPage(TabLocation.Trash));
            Assert.Equal(Trash.DisplayName, HistoryEntry.Trash.DisplayName);
            Assert.NotEqual(HistoryEntry.ThisPc, HistoryEntry.Trash);
        }

        // ------------------------------------------------------------------ the operation queue

        [AvaloniaFact]
        public async Task QueueRestore_IsRecorded_AndUndoSendsItBackToTheTrash()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string original = Path.Combine(_root, "docs", "undo-me.txt");
            AddToTrash("undo-me.txt", original, "2024-01-02T03:04:05", content: "u");
            var queue = new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null);

            FileOperationOutcome outcome = await queue.RestoreFromTrashAsync(new[] { Single() });

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.True(File.Exists(original));
            Assert.True(queue.CanUndo);
            Assert.Equal("Undo Restore \"undo-me.txt\"", queue.UndoText);

            await queue.UndoLastAsync();
            Assert.False(File.Exists(original)); // in a trash again (whichever this machine's is)
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task TheSidebarTrash_OpensTheSurface_WithRowsMenusAndKeys()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string report = Path.Combine(_root, "docs", "report.txt");
            string old = Path.Combine(_root, "docs", "old.log");
            AddToTrash("report.txt", report, "2024-01-02T03:04:05", content: "r");
            AddToTrash("old.log", old, "2024-03-02T03:04:05", content: "old log");
            Trash.TrashDirectoriesOverride = new[] { (_trash, (string)null) };

            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                var dialogs = new ConfirmingDialogs(window);
                pane.DialogService = dialogs;

                // The sidebar's trash pin opens the view in the active pane.
                // (Another test may have unpinned it from the shared settings: the same pin as the sidebar's, then.)
                ByteExplorer.ViewModels.Sidebar.PinnedItemViewModel pin = window.ViewModel.Pins.Items.FirstOrDefault(p => p.IsTrash)
                    ?? new ByteExplorer.ViewModels.Sidebar.PinnedItemViewModel(ByteExplorer.Core.Models.PinColors.TrashKey, null, null);
                await window.OpenPinAsync(pin, FolderOpenTarget.ActivePane);
                await WaitUntil(() => pane.IsTrashView && !pane.IsLoading && pane.TrashItems.Count == 2);
                await Pump(5);

                TrashView view = window.GetVisualDescendants().OfType<TrashView>().Single(v => ReferenceEquals(v.DataContext, pane));
                Assert.True(view.IsEffectivelyVisible);
                Assert.Equal(2, view.List.ItemCount);
                Assert.Equal(new[] { "old.log", "report.txt" }, pane.TrashItems.Select(i => i.Name)); // newest deleted first
                Assert.Equal(Path.Combine(_root, "docs"), pane.TrashItems[0].OriginalLocation);
                Assert.Equal("7 bytes", pane.TrashItems[0].SizeDisplay);
                Assert.Equal(new[] { "Trash" }, window.ViewModel.Omnibar.Crumbs.Select(c => c.Name));
                Assert.Equal(ByteExplorer.Icons.Symbol.Delete, window.ViewModel.Omnibar.LocationSymbol);
                Assert.Equal("Trash", window.ViewModel.Tabs.SelectedTab.Title);
                Assert.Equal(TabLocation.Trash, TabLocation.Of(pane));
                Assert.True(pane.EmptyTrashCommand.CanExecute(null));
                SaveShot(window, "trash-view.png");

                pane.SortTrashBy(TrashSortColumn.Name);
                Assert.Equal(new[] { "old.log", "report.txt" }, pane.TrashItems.Select(i => i.Name));
                pane.SortTrashBy(TrashSortColumn.Name);
                Assert.Equal(new[] { "report.txt", "old.log" }, pane.TrashItems.Select(i => i.Name));

                // Select all (Ctrl+A goes through the pane's command) selects the trash's rows.
                pane.SelectAllCommand.Execute(null);
                await Pump(2);
                Assert.Equal(2, view.SelectedItems.Count);

                // Restore one: back in its folder, gone from the list, and undoable.
                TrashItemViewModel reportRow = pane.TrashItems.Single(i => i.Name == "report.txt");
                await pane.RestoreFromTrashAsync(new[] { reportRow });
                await WaitUntil(() => pane.TrashItems.Count == 1 && !pane.IsLoading);
                Assert.Equal("r", File.ReadAllText(report));
                Assert.True(window.ViewModel.Operations.CanUndo);

                // Delete (the key, on the focused list) deletes permanently after asking.
                await Pump(20); // the queue's own refresh of the panes showing the trash lands too
                view.List.Selection.Select(0);
                view.List.ContainerFromIndex(0)?.Focus();
                await Pump(2);
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                await WaitUntil(() => pane.TrashItems.Count == 0 && !pane.IsLoading);
                Assert.Contains("Delete permanently", dialogs.Confirmations);
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_trash, "files")));
                Assert.False(File.Exists(old));
                Assert.False(pane.EmptyTrashCommand.CanExecute(null));

                // Back leaves the trash for where the pane was before.
                await pane.NavigateHistoryAsync(-1);
                await Pump(5);
                Assert.False(pane.IsTrashView);
                await pane.NavigateHistoryAsync(1);
                await WaitUntil(() => pane.IsTrashView && !pane.IsLoading);
            }
            finally
            {
                window.CloseWithoutAsking();
                await Pump(3);
            }
        }

        [AvaloniaFact]
        public async Task EmptyTrash_AsksThenEmptiesEverything()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            AddToTrash("a.txt", Path.Combine(_root, "docs", "a.txt"), "2024-01-01T00:00:00", content: "a");
            AddFolderToTrash("B", Path.Combine(_root, "docs", "B"), "2024-01-01T00:00:00");
            Trash.TrashDirectoriesOverride = new[] { (_trash, (string)null) };

            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                var dialogs = new ConfirmingDialogs(window) { Answer = false };
                pane.DialogService = dialogs;
                await pane.LoadTrashAsync();
                await WaitUntil(() => pane.TrashItems.Count == 2 && !pane.IsLoading);
                await WaitUntil(() => pane.TrashItems.Single(i => i.Name == "B").SizeBytes == 0); // the empty folder, counted after the listing

                await pane.EmptyTrashAsync(); // "no": nothing happens
                Assert.Equal(2, pane.TrashItems.Count);

                dialogs.Answer = true;
                await pane.EmptyTrashAsync();
                await WaitUntil(() => pane.TrashItems.Count == 0 && !pane.IsLoading);
                Assert.Equal(new[] { "Empty Trash", "Empty Trash" }, dialogs.Confirmations);
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_trash, "files")));
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_trash, "info")));
            }
            finally
            {
                window.CloseWithoutAsking();
                await Pump(3);
            }
        }

        // ------------------------------------------------------------------ helpers

        private TrashEntry Single() => Trash.ListFreedesktop(new[] { (_trash, (string)null) }, CancellationToken.None).Single();

        private void AddToTrash(string name, string originalPath, string date, string content)
        {
            File.WriteAllText(Path.Combine(_trash, "files", name), content);
            WriteInfo(name, originalPath, date);
        }

        private string AddFolderToTrash(string name, string originalPath, string date)
        {
            string folder = Path.Combine(_trash, "files", name);
            Directory.CreateDirectory(folder);
            WriteInfo(name, originalPath, date);
            return folder;
        }

        private void WriteInfo(string name, string originalPath, string date) =>
            File.WriteAllText(Path.Combine(_trash, "info", name + ".trashinfo"),
                "[Trash Info]\nPath=" + Uri.EscapeDataString(originalPath).Replace("%2F", "/") + "\nDeletionDate=" + date + "\n");

        private static void SaveShot(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(watch.ElapsedMilliseconds < timeoutMs, "timed out waiting");
                await Pump(1);
            }
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>Answers every confirmation with <see cref="Answer"/> and remembers the titles; the rest goes to the window.</summary>
        private sealed class ConfirmingDialogs : IDialogService
        {
            private readonly IDialogService _inner;

            public ConfirmingDialogs(IDialogService inner) => _inner = inner;

            public bool Answer { get; set; } = true;

            public List<string> Confirmations { get; } = new List<string>();

            public Task<bool> ConfirmAsync(string title, string message, string confirmText)
            {
                Confirmations.Add(title);
                return Task.FromResult(Answer);
            }

            public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, bool permanent) => Task.FromResult(Answer);
            public Task<string> PromptRenameAsync(string currentName, bool isDirectory) => _inner.PromptRenameAsync(currentName, isDirectory);
            public void ShowProperties(IReadOnlyList<string> paths) => _inner.ShowProperties(paths);
            public Task<string> PromptTextAsync(string title, string message, string initialText, string placeholder, string okText, Func<string, string> validate) =>
                _inner.PromptTextAsync(title, message, initialText, placeholder, okText, validate);
            public Task<PasswordAnswer> PromptPasswordAsync(PasswordRequest request) => _inner.PromptPasswordAsync(request);
            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public Task<ConflictDecision> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => _inner.ResolveConflictAsync(conflict, cancellationToken);
            public Task<ErrorChoice> ResolveErrorAsync(ErrorInfo error, CancellationToken cancellationToken) => _inner.ResolveErrorAsync(error, cancellationToken);
        }
    }
}
