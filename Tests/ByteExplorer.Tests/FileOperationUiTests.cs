// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.FileOperations;
using ByteExplorer.Views;
using ByteExplorer.Views.Dialogs;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// File operations through the real window: a clashing drop asks with the conflict dialog, the answer is applied,
    /// Ctrl+Z undoes it, and the operations panel lists a slow job with pause and resume.
    /// </summary>
    public sealed class FileOperationUiTests : IDisposable
    {
        private readonly string _root;

        public FileOperationUiTests()
        {
            // Private trash: undo of the replace restores from there.
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "be-ui-trash"));
            _root = Path.Combine(Path.GetTempPath(), "be-opsui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "dst"));
        }

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

        [AvaloniaFact]
        public async Task ClashingDrop_AsksReplace_ThenCtrlZUndoesIt()
        {
            string src = Path.Combine(_root, "src", "report.txt");
            string dst = Path.Combine(_root, "dst", "report.txt");
            File.WriteAllText(src, "new version");
            File.WriteAllText(dst, "old version");

            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(Path.Combine(_root, "dst"));
                await Pump(5);

                Task drop = pane.ExecuteDropAsync(new[] { src }, Path.Combine(_root, "dst"), isMove: false);
                ConflictDialog dialog = await WaitForDialog(window);
                Assert.NotNull(dialog);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    dialog.CaptureRenderedFrame()?.Save(Path.Combine(directory, "conflict-dialog.png"));
                }

                Button replace = dialog.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Replace");
                Click(dialog, replace);
                await drop.WaitAsync(TimeSpan.FromSeconds(10));
                await Pump(5);

                Assert.Equal("new version", File.ReadAllText(dst));
                Assert.True(window.ViewModel.Operations.CanUndo);
                Assert.StartsWith("Undo Copy", window.ViewModel.Operations.UndoText);

                // Ctrl+Z with the file list focused (not a text box).
                pane.SelectedPreset = FileListViewPreset.Details;
                window.Focus();
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                // Undo removes the new file, then restores the old one from the trash: between the two the file is absent.
                for (int i = 0; i < 200 && (!File.Exists(dst) || File.ReadAllText(dst) != "old version"); i++)
                {
                    await Pump(1);
                }

                Assert.Equal("old version", File.ReadAllText(dst));
                Assert.False(window.ViewModel.Operations.CanUndo);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task SlowJob_AppearsInThePanel_WithPauseAndResume()
        {
            string big = Path.Combine(_root, "src", "big.bin");
            File.WriteAllBytes(big, new byte[4 * 1024 * 1024]);

            var window = new NemoMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileOperationQueue queue = window.ViewModel.Operations;

                // Start a copy and pause it at once: it stays running long enough to be shown.
                Task copy = queue.TransferAsync(Core.Models.FileOperationKind.Copy, new[] { big }, Path.Combine(_root, "dst"));
                FileOperationJob job = queue.Jobs.Single();
                job.Pause();
                for (int i = 0; i < 100 && !job.IsShown; i++)
                {
                    await Pump(1);
                }

                Assert.True(job.IsShown);
                Assert.True(queue.HasShownJobs);
                Assert.True(job.IsPaused);
                Assert.Equal("Paused", job.DetailText);

                var panel = window.GetVisualDescendants().OfType<FileOperationsPanel>().Single();
                Assert.True(panel.IsVisible);
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "operations-panel.png"));
                }

                job.Resume();
                await copy.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(FileOperationJobState.Completed, job.State);
                Assert.Equal(4 * 1024 * 1024, new FileInfo(Path.Combine(_root, "dst", "big.bin")).Length);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task RenameAndNewFolder_AreUndoneNewestFirst()
        {
            string folder = Path.Combine(_root, "dst");
            File.WriteAllText(Path.Combine(folder, "draft.txt"), "text");
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(folder);
                await Pump(5);

                FileListItemViewModel draft = pane.Items.OfType<FileListItemViewModel>().Single(item => item.Name == "draft.txt");
                await pane.RenameItemAsync(draft, "final.txt");
                Assert.True(File.Exists(Path.Combine(folder, "final.txt")));

                pane.NewFolderCommand.Execute(null);
                for (int i = 0; i < 100 && !Directory.Exists(Path.Combine(folder, "New folder")); i++)
                {
                    await Pump(1);
                }

                Assert.True(Directory.Exists(Path.Combine(folder, "New folder")));
                Assert.Equal("Undo New folder", window.ViewModel.Operations.UndoText);

                await window.ViewModel.Operations.UndoLastAsync();
                Assert.False(Directory.Exists(Path.Combine(folder, "New folder")));
                Assert.StartsWith("Undo Rename", window.ViewModel.Operations.UndoText);

                await window.ViewModel.Operations.UndoLastAsync();
                Assert.True(File.Exists(Path.Combine(folder, "draft.txt")));
                Assert.False(File.Exists(Path.Combine(folder, "final.txt")));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task AtStartup_AnInterruptedOperationIsCleanedUp_AndThePersonIsTold()
        {
            string folder = Path.Combine(_root, "dst");
            string partial = Path.Combine(folder, ".movie.mkv.byteexplorer-0123456789abcdef0123456789abcdef.partial");
            File.WriteAllText(partial, "half a movie");
            Core.Services.FileOperations.OperationJournal journal = Core.Services.FileOperations.OperationJournal.Start(
                MainViewModel.OperationJournalFolder, Core.Models.FileOperationKind.Copy, new[] { "/media/movie.mkv" }, folder);
            journal.TemporaryCreated(partial);
            journal.AbandonForTests(); // ... and the app was killed

            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                MessageDialog message = null;
                for (int i = 0; i < 250 && message == null; i++)
                {
                    await Pump(1);
                    message = window.OwnedWindows.OfType<MessageDialog>().FirstOrDefault(d => d.IsVisible);
                }

                Assert.NotNull(message);
                string text = string.Join(" ", message.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("closed while a file operation was running", text);
                Assert.Contains("Copy \"movie.mkv\" to \"dst\"", text);
                Assert.Contains("1 unfinished temporary file was removed", text);
                Assert.False(File.Exists(partial));
                Assert.False(File.Exists(journal.Path));
                message.Close();
            }
            finally
            {
                window.CloseWithoutAsking();
            }
        }

        private static async Task<ConflictDialog> WaitForDialog(Window owner)
        {
            for (int i = 0; i < 250; i++)
            {
                ConflictDialog dialog = owner.OwnedWindows.OfType<ConflictDialog>().FirstOrDefault();
                if (dialog != null && dialog.IsVisible)
                {
                    await Pump(3);
                    return dialog;
                }

                await Pump(1);
            }

            return null;
        }

        private static void Click(TopLevel top, Visual target)
        {
            Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top) ?? default;
            top.MouseDown(center, MouseButton.Left);
            top.MouseUp(center, MouseButton.Left);
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
