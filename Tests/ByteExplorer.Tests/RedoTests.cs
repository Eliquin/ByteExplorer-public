// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.FileOperations;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Redo: a fully undone operation can be done again (newest undone first), the redo is itself undoable, a new
    /// operation clears the redo list, and a partly undone operation is never offered for redo.
    /// </summary>
    public sealed class RedoTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-redo-" + Guid.NewGuid().ToString("N"));

        public RedoTests()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "be-redo-trash"));
            Directory.CreateDirectory(_root);
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
        public async Task ACopyUndone_IsRedone_AndTheRedoUndoesAgain()
        {
            string src = Dir("src");
            string dst = Dir("dst");
            string file = Path.Combine(src, "report.txt");
            File.WriteAllText(file, "numbers");
            FileOperationQueue queue = NewQueue();
            Assert.False(queue.CanRedo);
            Assert.Equal("Redo", queue.RedoText);

            await queue.TransferAsync(FileOperationKind.Copy, new[] { file }, dst);
            string copied = Path.Combine(dst, "report.txt");
            Assert.True(File.Exists(copied));

            await queue.UndoLastAsync();
            Assert.False(File.Exists(copied));
            Assert.True(queue.CanRedo);
            Assert.StartsWith("Redo Copy", queue.RedoText);

            await queue.RedoLastAsync();
            Assert.Equal("numbers", File.ReadAllText(copied));
            Assert.False(queue.CanRedo);
            Assert.StartsWith("Undo Copy", queue.UndoText);

            await queue.UndoLastAsync();
            Assert.False(File.Exists(copied));
            Assert.True(File.Exists(file));
        }

        [AvaloniaFact]
        public async Task AMoveAndATrashDelete_AreRedoneNewestUndoneFirst()
        {
            string src = Dir("src");
            string dst = Dir("dst");
            string moved = Path.Combine(src, "a.txt");
            string trashed = Path.Combine(src, "b.txt");
            File.WriteAllText(moved, "a");
            File.WriteAllText(trashed, "b");
            FileOperationQueue queue = NewQueue();

            await queue.TransferAsync(FileOperationKind.Move, new[] { moved }, dst);
            await queue.DeleteAsync(new[] { trashed }, permanent: false);
            Assert.False(File.Exists(trashed));

            await queue.UndoLastAsync(); // the delete
            await queue.UndoLastAsync(); // the move
            Assert.True(File.Exists(moved));
            Assert.True(File.Exists(trashed));
            Assert.StartsWith("Redo Move", queue.RedoText);

            await queue.RedoLastAsync(); // the move, undone last
            Assert.False(File.Exists(moved));
            Assert.True(File.Exists(Path.Combine(dst, "a.txt")));
            Assert.True(File.Exists(trashed));
            Assert.True(queue.CanRedo);

            await queue.RedoLastAsync(); // then the delete
            Assert.False(File.Exists(trashed));
            Assert.False(queue.CanRedo);
        }

        [AvaloniaFact]
        public async Task ANewOperation_ClearsTheRedoList()
        {
            string src = Dir("src");
            string dst = Dir("dst");
            string first = Path.Combine(src, "1.txt");
            string second = Path.Combine(src, "2.txt");
            File.WriteAllText(first, "1");
            File.WriteAllText(second, "2");
            FileOperationQueue queue = NewQueue();

            await queue.TransferAsync(FileOperationKind.Copy, new[] { first }, dst);
            await queue.UndoLastAsync();
            Assert.True(queue.CanRedo);

            await queue.TransferAsync(FileOperationKind.Copy, new[] { second }, dst);
            Assert.False(queue.CanRedo);
            Assert.Equal("Redo", queue.RedoText);
            Assert.False(queue.RedoCommand.CanExecute(null));
        }

        [AvaloniaFact]
        public async Task AnUndoThatCouldNotReverseEverything_IsNotOfferedForRedo()
        {
            string src = Dir("src");
            string dst = Dir("dst");
            string first = Path.Combine(src, "1.txt");
            string second = Path.Combine(src, "2.txt");
            File.WriteAllText(first, "1");
            File.WriteAllText(second, "2");
            FileOperationQueue queue = NewQueue();

            await queue.TransferAsync(FileOperationKind.Move, new[] { first, second }, dst);
            // One moved file was taken away since: it can't be moved back, so the undo is partial.
            File.Delete(Path.Combine(dst, "2.txt"));

            await queue.UndoLastAsync();
            Assert.True(File.Exists(first));
            Assert.False(queue.CanRedo); // redoing would move 1.txt again but not the lost 2.txt: half an operation
        }

        [AvaloniaFact]
        public async Task ASecondRedoWhileTheFirstStillRuns_WaitsItsTurn_InsteadOfBeingDropped()
        {
            string dir = Dir("work");
            FileOperationQueue queue = NewQueue();
            var order = new System.Collections.Generic.List<string>();
            var firstMayFinish = new TaskCompletionSource<bool>();
            foreach (string name in new[] { "two", "one" })
            {
                string file = Path.Combine(dir, name + ".txt");
                File.WriteAllText(file, name);
                string captured = name;
                queue.Record(new FileOperationRecord(name, new UndoAction[] { new CreatedAction(file) }, new[] { dir })
                {
                    Redo = async () =>
                    {
                        order.Add(captured + " started");
                        if (captured == "two")
                        {
                            await firstMayFinish.Task;
                        }

                        order.Add(captured + " done");
                    }
                });
            }

            await queue.UndoLastAsync(); // "one" (recorded last)
            await queue.UndoLastAsync(); // "two"
            Assert.StartsWith("Redo two", queue.RedoText); // undone last, redone first

            queue.RedoCommand.Execute(null); // runs "two", held open
            Assert.True(queue.RedoCommand.CanExecute(null));
            queue.RedoCommand.Execute(null); // pressed again at once
            await Pump(3);
            Assert.Equal(new[] { "two started" }, order);

            firstMayFinish.SetResult(true);
            await WaitUntil(() => order.Count == 4);
            Assert.Equal(new[] { "two started", "two done", "one started", "one done" }, order);
            Assert.False(queue.CanRedo);
        }

        [AvaloniaFact]
        public async Task RenameAndNewFolder_AreRedoneThroughTheWindowKeys()
        {
            string folder = Dir("work");
            File.WriteAllText(Path.Combine(folder, "draft.txt"), "text");
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                FileOperationQueue operations = window.ViewModel.Operations;
                await pane.LoadFolderAsync(folder);
                await Pump(5);

                FileListItemViewModel draft = pane.Items.OfType<FileListItemViewModel>().Single(item => item.Name == "draft.txt");
                await pane.RenameItemAsync(draft, "final.txt");
                pane.NewFolderCommand.Execute(null);
                await WaitUntil(() => operations.UndoText == "Undo New folder");

                await operations.UndoLastAsync();
                await operations.UndoLastAsync();
                Assert.False(Directory.Exists(Path.Combine(folder, "New folder")));
                Assert.True(File.Exists(Path.Combine(folder, "draft.txt")));
                Assert.StartsWith("Redo Rename", operations.RedoText);

                // Ctrl+Y redoes the rename (undone last), and Ctrl+Shift+Z, pressed at once while that still runs, the new
                // folder after it: the second press waits its turn instead of being dropped.
                window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
                window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
                window.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null);
                window.KeyRelease(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, null);
                await WaitUntil(() => File.Exists(Path.Combine(folder, "final.txt")));
                await WaitUntil(() => Directory.Exists(Path.Combine(folder, "New folder")));
                await WaitUntil(() => operations.UndoText == "Undo New folder"); // recorded just after it's created
                Assert.False(File.Exists(Path.Combine(folder, "draft.txt")));
                Assert.False(operations.CanRedo);

                // The redo is undoable like the original.
                await operations.UndoLastAsync();
                Assert.False(Directory.Exists(Path.Combine(folder, "New folder")));
            }
            finally
            {
                window.Close();
            }
        }

        private static FileOperationQueue NewQueue() =>
            new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null);

        private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 300 && !condition(); i++)
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
