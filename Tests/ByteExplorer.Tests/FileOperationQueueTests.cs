// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.FileOperations;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The operations queue: jobs on the same items wait for each other (in the order they were queued), unrelated jobs
    /// don't; pause and name-clash states; an undo cut short keeps what it didn't reach.
    /// </summary>
    public sealed class FileOperationQueueTests : IDisposable
    {
        private static readonly string TrashHome = InitTrash();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-queue-" + Guid.NewGuid().ToString("N"));

        public FileOperationQueueTests() => Directory.CreateDirectory(_root);

        private static string InitTrash()
        {
            string home = Path.Combine(Path.GetTempPath(), "be-queue-trash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
            return home;
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
        public async Task ADeleteOfItemsBeingCopied_WaitsForTheCopy_AnUnrelatedDeleteDoesNot()
        {
            string folder = Dir("Project");
            var bytes = new byte[4 * 1024 * 1024];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(folder, "data.bin"), bytes);
            string unrelated = Path.Combine(Dir("Other"), "old.txt");
            File.WriteAllText(unrelated, "x");
            string destination = Dir("Backup");

            var queue = new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null);
            using var hold = new ManualResetEventSlim(false);
            int held = 0;
            Action<string, long> stop = (path, written) =>
            {
                if (path.StartsWith(destination, StringComparison.Ordinal) && Interlocked.Exchange(ref held, 1) == 0)
                {
                    hold.Wait(TimeSpan.FromSeconds(20)); // the copy stops half way
                }
            };
            FileOperationService.AfterBufferForTests += stop;
            try
            {
                Task<FileOperationOutcome> copy = queue.TransferAsync(FileOperationKind.Copy, new[] { folder }, destination);
                await WaitUntil(() => Volatile.Read(ref held) == 1);

                // Shift+Del on the folder being copied: it must wait, not delete the files still to be read.
                Task<FileOperationOutcome> delete = queue.DeleteAsync(new[] { folder }, permanent: true);
                // ... while deleting something else goes ahead at once.
                FileOperationOutcome other = await queue.DeleteAsync(new[] { unrelated }, permanent: true).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(other.Result.IsFullSuccess);
                Assert.False(File.Exists(unrelated));

                await Pump(10);
                Assert.False(delete.IsCompleted);
                Assert.True(Directory.Exists(folder));
                FileOperationJob waiting = queue.Jobs.Single(j => j.Kind == FileOperationKind.Delete && !j.IsFinished);
                Assert.Contains("to finish (same items)", waiting.DetailText);

                hold.Set();
                FileOperationOutcome copied = await copy.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(copied.Result.IsFullSuccess);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, "Project", "data.bin"))); // a whole copy
                FileOperationOutcome deleted = await delete.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(deleted.Result.IsFullSuccess); // and only then the delete
                Assert.False(Directory.Exists(folder));
            }
            finally
            {
                hold.Set();
                FileOperationService.AfterBufferForTests -= stop;
            }
        }

        [AvaloniaFact]
        public async Task AJobPausedWhileItsQuestionWasOpen_StaysPausedAfterTheAnswer()
        {
            var job = new FileOperationJob(FileOperationKind.Copy, "Copying", new UiDispatcher(Dispatcher.UIThread));
            job.MarkStarted();
            job.ReportProgress(new FileOperationProgress(FileOperationPhase.WaitingForDecision, 10, 0, 1, 0, "a.txt"));
            await Pump(3);
            Assert.True(job.NeedsDecision);
            job.Pause(); // can't pause while a question is open ...
            Assert.False(job.IsPaused);

            job.SetNeedsDecision(false);
            job.Pause();
            job.ReportProgress(new FileOperationProgress(FileOperationPhase.WaitingForDecision, 10, 0, 1, 0, "b.txt"));
            await Pump(3);
            job.ReportProgress(new FileOperationProgress(FileOperationPhase.Working, 10, 5, 1, 0, "b.txt"));
            await Pump(3);
            Assert.True(job.IsPaused); // answered while paused: still paused, and Resume is offered
            Assert.True(job.ResumeCommand.CanExecute(null));
            job.Resume();
            Assert.Equal(FileOperationJobState.Running, job.State);
            job.Dispose();
        }

        [AvaloniaFact]
        public void ResumingAJobThatHasNotStarted_PutsItBackInTheQueueState()
        {
            var job = new FileOperationJob(FileOperationKind.Copy, "Copying", new UiDispatcher(Dispatcher.UIThread));
            job.Pause();
            job.Resume();
            Assert.Equal(FileOperationJobState.Waiting, job.State);
            job.MarkStarted();
            job.Pause();
            job.Resume();
            Assert.Equal(FileOperationJobState.Running, job.State);
            Assert.False(new FileOperationJob(FileOperationKind.Copy, "Extracting", new UiDispatcher(Dispatcher.UIThread)) { SupportsPause = false }.CanPause);
            job.Dispose();
        }

        [AvaloniaFact]
        public async Task AnUndoCancelledPartWay_KeepsTheStepsItDidNotReach()
        {
            string src = Dir("src");
            string dst = Dir("dst");
            string[] files = Enumerable.Range(1, 3).Select(i =>
            {
                string path = Path.Combine(src, $"{i}.txt");
                File.WriteAllText(path, i.ToString());
                return path;
            }).ToArray();
            var engine = new FileOperationService(null);
            FileOperationOutcome copy = await engine.TransferAsync(FileOperationKind.Copy, files, dst, new FileOperationContext());
            Assert.Equal(3, copy.Record.Actions.Count);

            // Cancelled before it reached any step (as when the person cancels it while it waits its turn).
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            FileOperationOutcome undo = await engine.UndoAsync(copy.Record, new FileOperationContext { CancellationToken = cancel.Token });

            Assert.True(undo.Result.WasCancelled);
            Assert.NotNull(undo.Record);
            Assert.Equal(3, undo.Record.Actions.Count);
            Assert.Equal(3, Directory.GetFiles(dst).Length); // nothing was undone ...
            await engine.UndoAsync(undo.Record, new FileOperationContext()); // ... and the steps handed back still undo it all
            Assert.Empty(Directory.GetFiles(dst));
        }

        private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++)
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
