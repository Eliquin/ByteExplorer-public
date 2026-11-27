// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Folder sizes: the count (links not followed), the kept sizes and what forgets them, sorting folders by size, and the
    /// window (Calculate size, Alt+Shift+Enter, the Size column, the status bar, the details pane, the automatic setting).
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class FolderSizeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-sizes-" + Guid.NewGuid().ToString("N"));

        public FolderSizeTests()
        {
            Directory.CreateDirectory(_root);
            FolderSizes.ClearForTests();
            Write("Small/a.bin", 1_000);
            Write("Big/b.bin", 300_000);
            Write("Big/Deep/Deeper/c.bin", 200_000);
            Write("Medium/d.bin", 50_000);
            Write("Medium/e.bin", 50_000);
            Write("loose.txt", 10);
        }

        public void Dispose()
        {
            FolderSizes.ClearForTests();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private string Write(string relative, int bytes)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        [AvaloniaFact]
        public void TheCount_AddsEverythingInside_WithoutFollowingLinks()
        {
            FolderSizeInfo big = FolderSizes.Calculate(Path.Combine(_root, "Big"), CancellationToken.None);
            Assert.True(big.IsComplete);
            Assert.Equal(500_000, big.Bytes);
            Assert.Equal(2, big.Files);
            Assert.Equal(2, big.Folders); // Deep, Deeper

            if (!OperatingSystem.IsWindows())
            {
                // A link to a folder (here: to Big itself, a loop) counts as nothing and doesn't make the count run forever.
                Directory.CreateSymbolicLink(Path.Combine(_root, "Big", "loop"), Path.Combine(_root, "Big"));
                FolderSizeInfo again = FolderSizes.Calculate(Path.Combine(_root, "Big"), CancellationToken.None);
                Assert.Equal(500_000, again.Bytes);
                Assert.Equal(2, again.Folders);
            }

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => FolderSizes.Calculate(_root, cancel.Token));
        }

        [AvaloniaFact]
        public void KeptSizes_AreForgottenUpTheTree_WhenSomethingChangesInside()
        {
            string big = Path.Combine(_root, "Big");
            string deep = Path.Combine(big, "Deep");
            string deeper = Path.Combine(deep, "Deeper");
            foreach (string folder in new[] { _root, big, deep, deeper })
            {
                FolderSizes.Set(folder, new FolderSizeInfo(1, 1, 0, 0, isComplete: true));
            }

            long version = FolderSizes.Version;
            FolderSizes.Invalidate(deep); // something changed in Deep: Deep, Big and the root change; Deeper doesn't
            Assert.False(FolderSizes.TryGet(deep, out _));
            Assert.False(FolderSizes.TryGet(big, out _));
            Assert.False(FolderSizes.TryGet(_root, out _));
            Assert.True(FolderSizes.TryGet(deeper, out _));
            Assert.True(FolderSizes.Version > version);

            FolderSizes.Set(big, new FolderSizeInfo(5, 1, 0, 0, true));
            FolderSizes.ForgetChildren(_root);
            Assert.False(FolderSizes.TryGet(big, out _));
            Assert.True(FolderSizes.TryGet(deeper, out _));

            Assert.Equal("1.2 KB", FolderSizes.Format(new FolderSizeInfo(1234, 1, 0, 0, true)));
            Assert.Equal("≥ 1.2 KB", FolderSizes.Format(new FolderSizeInfo(1234, 1, 0, 2, true)));
            Assert.Equal("1.2 KB…", FolderSizes.Format(new FolderSizeInfo(1234, 1, 0, 0, false)));
        }

        [AvaloniaFact]
        public void SortingBySize_OrdersFoldersByTheirCountedSize()
        {
            DirectoryIndexEntry Folder(string name) => new DirectoryIndexEntry(name, true, 0, DateTime.UtcNow, FileAttributes.Directory);
            var entries = new[] { Folder("A"), Folder("B"), Folder("C"), new DirectoryIndexEntry("x.txt", false, 5, DateTime.UtcNow, FileAttributes.Normal) };
            long Size(DirectoryIndexEntry e) => e.Name switch { "A" => 300, "B" => -1, _ => 100 };

            DirectoryIndexEntry[] ascending = DirectoryEntrySorter.Sort(entries, EntryOrder.Size, ascending: true, folderSize: Size);
            Assert.Equal(new[] { "B", "C", "A", "x.txt" }, ascending.Select(e => e.Name)); // not counted = smallest
            DirectoryIndexEntry[] descending = DirectoryEntrySorter.Sort(entries, EntryOrder.Size, ascending: false, folderSize: Size);
            Assert.Equal(new[] { "A", "C", "B", "x.txt" }, descending.Select(e => e.Name));
        }

        [AvaloniaFact]
        public async Task InTheWindow_SizesAreCountedShownSortedAndForgotten()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                pane.SelectedPreset = FileListViewPreset.Details;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 4);
                FileListItemViewModel Item(string name) => pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == name);
                Assert.Equal(string.Empty, Item("Big").SizeDisplay); // not counted: nothing shown, as before

                // Alt+Shift+Enter: every folder here.
                window.KeyPress(Key.Enter, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.Enter, null);
                await WaitUntil(() => FolderSizes.BytesOrUnknown(Path.Combine(_root, "Small")) >= 0 &&
                                      FolderSizes.BytesOrUnknown(Path.Combine(_root, "Medium")) >= 0 &&
                                      FolderSizes.BytesOrUnknown(Path.Combine(_root, "Big")) >= 0);
                Assert.Equal("488.3 KB", Item("Big").SizeDisplay);
                Assert.Equal("97.7 KB", Item("Medium").SizeDisplay);
                Assert.StartsWith("3 folder sizes calculated", pane.StatusText);
                Assert.Contains("Size: 488.3 KB (2 files, 2 folders)", Item("Big").ToolTipText);

                // Sorting by size puts the folders in size order (still before the files).
                pane.SetSort(FileListSortColumn.Size, ascending: false);
                await WaitUntil(() => pane.Items.OfType<FileListItemViewModel>().First().Name == "Big");
                Assert.Equal(new[] { "Big", "Medium", "Small", "loose.txt" }, pane.Items.OfType<FileListItemViewModel>().Select(i => i.Name));

                // The status bar adds counted folders to a selection's size.
                pane.SelectedItems.Clear();
                pane.SelectedItems.Add(Item("Medium"));
                pane.SelectedItems.Add(Item("loose.txt"));
                await Pump(2);
                Assert.EndsWith("97.7 KB", pane.SelectionSummaryText);
                string shots = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(shots))
                {
                    Directory.CreateDirectory(shots);
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "folder-sizes.png"));
                }

                // A copy into Medium forgets its size (and the folders above it).
                string extra = Path.Combine(_root, "extra.bin");
                File.WriteAllBytes(extra, new byte[1000]);
                await window.ViewModel.Operations.TransferAsync(FileOperationKind.Copy, new[] { extra }, Path.Combine(_root, "Medium"));
                await WaitUntil(() => FolderSizes.BytesOrUnknown(Path.Combine(_root, "Medium")) < 0);
                Assert.True(FolderSizes.BytesOrUnknown(Path.Combine(_root, "Big")) >= 0); // untouched

                // "Calculate size" from the menu, for one folder; the details pane shows it.
                await pane.CalculateFolderSizesAsync(new[] { Path.Combine(_root, "Medium") }, announce: true);
                Assert.Equal(101_000, FolderSizes.BytesOrUnknown(Path.Combine(_root, "Medium")));
                pane.Preview.ShowItem(Item("Medium"));
                Assert.Contains(pane.Preview.Details, r => r.Label == "Size" && r.Value.StartsWith("98.6 KB (3 files", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task WithTheSetting_AFolderOpenedOnALocalDrive_HasItsFoldersCounted()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            FileListSettings settings = null;
            try
            {
                await Pump(30);
                settings = window.ViewModel.SettingsService.Current.FileList;
                settings.AutoCalculateFolderSizes = true;
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => FolderSizes.BytesOrUnknown(Path.Combine(_root, "Big")) == 500_000 &&
                                      FolderSizes.BytesOrUnknown(Path.Combine(_root, "Small")) == 1_000);

                // F5 counts them again.
                File.WriteAllBytes(Path.Combine(_root, "Small", "more.bin"), new byte[500]);
                pane.RefreshCommand.Execute(null);
                await WaitUntil(() => FolderSizes.BytesOrUnknown(Path.Combine(_root, "Small")) == 1_500);
            }
            finally
            {
                if (settings != null)
                {
                    settings.AutoCalculateFolderSizes = false;
                }

                window.Close();
            }
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 300)
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
    }
}
