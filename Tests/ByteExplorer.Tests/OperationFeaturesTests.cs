// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The smaller file operation features: the delete question names the items; Ctrl+drag duplicates in place.</summary>
    public sealed class OperationFeaturesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-features-" + Guid.NewGuid().ToString("N"));

        public OperationFeaturesTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void TheDeleteQuestion_NamesWhatGoes()
        {
            string file = Path.Combine(_root, "report.pdf");
            File.WriteAllText(file, "x");
            string folder = Directory.CreateDirectory(Path.Combine(_root, "Photos")).FullName;

            Assert.Equal("Move \"report.pdf\" to the Trash?", MainWindowBase.DescribeForDelete(new[] { file }, permanent: false, "the Trash"));
            Assert.Equal("Permanently delete the folder \"Photos\" and everything in it?\n\nThis can't be undone.",
                MainWindowBase.DescribeForDelete(new[] { folder }, permanent: true, "the Trash"));

            string[] many = Enumerable.Range(1, 8).Select(i => Path.Combine(_root, $"file{i}.txt")).Prepend(folder).ToArray();
            string text = MainWindowBase.DescribeForDelete(many, permanent: false, "the Recycle Bin");
            Assert.StartsWith("Move these 9 items to the Recycle Bin?", text);
            Assert.Contains("•  Photos  (folder)", text);
            Assert.Contains("•  file4.txt", text);
            Assert.DoesNotContain("file5.txt", text); // the first five, then a count
            Assert.EndsWith("…and 4 more", text);
        }

        [Fact]
        public async Task ReplaceIfNewer_UpdatesOnlyTheOlderFiles()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Directory.CreateDirectory(Path.Combine(_root, "trash-home")).FullName);
            string src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
            string dst = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
            var now = DateTime.UtcNow;
            Write(src, "edited.txt", "new edit", now);            // newer than the copy at the destination
            Write(dst, "edited.txt", "old copy", now.AddHours(-2));
            Write(src, "stale.txt", "older version", now.AddHours(-3)); // older than what the destination has
            Write(dst, "stale.txt", "kept", now.AddHours(-1));

            var resolver = new OnceResolver(ConflictChoice.ReplaceIfNewer);
            FileOperationOutcome outcome = await new FileOperationService(null).TransferAsync(FileOperationKind.Copy,
                new[] { Path.Combine(src, "edited.txt"), Path.Combine(src, "stale.txt") }, dst, new FileOperationContext { ConflictResolver = resolver });

            Assert.Equal(1, resolver.Calls); // asked once, applied to the rest
            Assert.Equal("new edit", File.ReadAllText(Path.Combine(dst, "edited.txt")));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(dst, "stale.txt")));
            Assert.Equal(1, outcome.Result.SkippedCount);
        }

        private static void Write(string folder, string name, string text, DateTime modifiedUtc)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, modifiedUtc);
        }

        private sealed class OnceResolver : IConflictResolver
        {
            private readonly ConflictChoice _choice;

            public OnceResolver(ConflictChoice choice) => _choice = choice;

            public int Calls { get; private set; }

            public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new ConflictDecision(_choice, applyToAll: true));
            }
        }

        [Avalonia.Headless.XUnit.AvaloniaFact]
        public async Task CutItems_AreDrawnDimmed_UntilTheCutIsGone()
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, "cut")).FullName;
            string file = Path.Combine(folder, "letter.txt");
            File.WriteAllText(file, "x");
            var window = new ModernMainWindow { Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                var pane = window.ViewModel.PrimaryFileListViewModel;
                pane.SelectedPreset = FileListViewPreset.LargeIcons;
                await pane.LoadFolderAsync(folder);
                await Pump(10);
                var item = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ByteExplorer.Controls.FileItemView>()
                    .First(v => v.IsEffectivelyVisible && v.DataContext is ByteExplorer.ViewModels.FileListItemViewModel i && i.FullPath == file);
                int normal = Ink(window, item);

                ClipboardFileService.SetFilesForCut(new[] { file });
                Assert.True(ClipboardFileService.IsCut(file));
                await Pump(5);
                int dimmed = Ink(window, item);
                Assert.True(dimmed < normal * 0.8, $"{dimmed} vs {normal}"); // the icon and name are paler

                ClipboardFileService.ClearFiles();
                Assert.False(ClipboardFileService.IsCut(file));
                await Pump(5);
                Assert.Equal(normal, Ink(window, item), 0.05 * normal);
            }
            finally
            {
                ClipboardFileService.ClearFiles();
                window.CloseWithoutAsking();
            }
        }

        /// <summary>How much the item draws (contrast of its icon and name with its background).</summary>
        private static unsafe int Ink(Avalonia.Controls.Window window, Avalonia.Visual item)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = (Avalonia.Media.Imaging.WriteableBitmap)window.CaptureRenderedFrame();
            using var buffer = frame.Lock();
            Avalonia.Point origin = item.TranslatePoint(default, window) ?? default;
            // Contrast with the item's background (its corner pixel): what the icon and name draw, in either theme.
            byte* corner = (byte*)buffer.Address + (int)origin.Y * buffer.RowBytes + (int)origin.X * 4;
            int background = (corner[0] + corner[1] + corner[2]) / 3;
            int ink = 0;
            for (int y = (int)origin.Y; y < (int)(origin.Y + item.Bounds.Height) && y < buffer.Size.Height; y++)
            {
                byte* line = (byte*)buffer.Address + y * buffer.RowBytes;
                for (int x = (int)origin.X; x < (int)(origin.X + item.Bounds.Width) && x < buffer.Size.Width; x++)
                {
                    ink += Math.Abs((line[x * 4] + line[x * 4 + 1] + line[x * 4 + 2]) / 3 - background);
                }
            }

            return ink;
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }

        [Fact]
        public void DroppingIntoTheItemsOwnFolder_IsOnlyValidAsADuplicate()
        {
            string file = Path.Combine(_root, "a.txt");
            File.WriteAllText(file, "x");
            Assert.False(FileDragDropHelper.IsValidDropTarget(_root, new[] { file })); // a plain drag there does nothing
            Assert.True(FileDragDropHelper.IsValidDropTarget(_root, new[] { file }, allowSameFolder: true)); // Ctrl: a copy
            Assert.False(FileDragDropHelper.IsValidDropTarget(_root, new[] { _root }, allowSameFolder: true)); // never into itself
        }
    }
}
