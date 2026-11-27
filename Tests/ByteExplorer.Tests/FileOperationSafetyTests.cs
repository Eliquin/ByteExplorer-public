// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The file operations' data-safety guarantees, on the real file system (Linux; /dev/shm is a second volume):
    /// links are never followed, transfers are atomic and durable, a full disk is caught, interrupted operations are
    /// recovered. Each test is a scenario where a careless engine loses or corrupts data.
    /// </summary>
    public sealed class FileOperationSafetyTests : IDisposable
    {
        // The trash goes to a private folder (Linux reads XDG_DATA_HOME), never the person's real trash.
        private static readonly string TrashHome = InitTrash();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-safety-" + Guid.NewGuid().ToString("N"));
        private readonly string _other = Path.Combine("/dev/shm", "be-safety-" + Guid.NewGuid().ToString("N"));
        private readonly FileOperationService _engine = new FileOperationService(null);

        public FileOperationSafetyTests()
        {
            Directory.CreateDirectory(_root);
            if (CanCrossVolumes)
            {
                Directory.CreateDirectory(_other);
            }
        }

        private static string InitTrash()
        {
            string home = Path.Combine(Path.GetTempPath(), "be-safety-trash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
            return home;
        }

        private static bool CanCrossVolumes => OperatingSystem.IsLinux() && Directory.Exists("/dev/shm");

        public void Dispose()
        {
            foreach (string folder in new[] { _root, _other })
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        // ------------------------------------------------------------------ links

        [Fact]
        public async Task MovingAFolderAcrossVolumes_NeverTouchesWhatItsLinksPointTo()
        {
            if (!CanCrossVolumes)
            {
                return;
            }

            // ~/Photos with two pictures, and a folder holding a link to it: the folder is moved to another drive.
            string photos = Dir("Photos");
            File.WriteAllText(Path.Combine(photos, "a.jpg"), "picture a");
            File.WriteAllText(Path.Combine(photos, "b.jpg"), "picture b");
            string project = Dir("Project");
            File.WriteAllText(Path.Combine(project, "notes.txt"), "notes");
            Directory.CreateSymbolicLink(Path.Combine(project, "PhotosLink"), photos);

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Move, new[] { project }, _other, new FileOperationContext());

            Assert.True(outcome.Result.IsFullSuccess, string.Join("; ", outcome.Result.Failures.Select(f => f.Message)));
            Assert.Equal(new[] { "a.jpg", "b.jpg" }, Directory.GetFiles(photos).Select(Path.GetFileName).OrderBy(n => n)); // untouched
            string movedLink = Path.Combine(_other, "Project", "PhotosLink");
            Assert.Equal(photos, new DirectoryInfo(movedLink).LinkTarget); // the link moved, as a link
            Assert.Equal("notes", File.ReadAllText(Path.Combine(_other, "Project", "notes.txt")));
            Assert.False(Directory.Exists(project));
        }

        [Fact]
        public async Task CopyingAFolderWithALinkLoop_CopiesTheLink_NotEndlessNestedCopies()
        {
            string tree = Dir("Tree");
            File.WriteAllText(Path.Combine(tree, "file.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(tree, "Loop"), tree); // points back at its own folder
            string destination = Dir("Copies");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { tree }, destination, new FileOperationContext());

            Assert.True(outcome.Result.IsFullSuccess, string.Join("; ", outcome.Result.Failures.Select(f => f.Message)));
            string copiedLoop = Path.Combine(destination, "Tree", "Loop");
            Assert.Equal(tree, new DirectoryInfo(copiedLoop).LinkTarget);
            Assert.Equal(2, Directory.GetFileSystemEntries(Path.Combine(destination, "Tree")).Length);
        }

        [Fact]
        public async Task CopyingALink_RecreatesIt_DanglingOrNot_AndPermanentDeleteRemovesOnlyTheLink()
        {
            string target = Path.Combine(Dir("Data"), "keep.txt");
            File.WriteAllText(target, "precious");
            string links = Dir("Links");
            string fileLink = Path.Combine(links, "relative.txt");
            File.CreateSymbolicLink(fileLink, "../Data/keep.txt"); // relative target
            string dangling = Path.Combine(links, "gone.txt");
            File.CreateSymbolicLink(dangling, "/nowhere/at/all");
            string destination = Dir("Out");

            FileOperationOutcome copy = await _engine.TransferAsync(FileOperationKind.Copy, new[] { fileLink, dangling }, destination, new FileOperationContext());
            Assert.True(copy.Result.IsFullSuccess, string.Join("; ", copy.Result.Failures.Select(f => f.Message)));
            Assert.Equal("../Data/keep.txt", new FileInfo(Path.Combine(destination, "relative.txt")).LinkTarget); // still relative
            Assert.Equal("/nowhere/at/all", new FileInfo(Path.Combine(destination, "gone.txt")).LinkTarget);

            string folderLink = Path.Combine(links, "DataLink");
            Directory.CreateSymbolicLink(folderLink, Path.GetDirectoryName(target));
            FileOperationOutcome delete = await _engine.DeleteAsync(new[] { folderLink, fileLink }, permanent: true, new FileOperationContext());
            Assert.True(delete.Result.IsFullSuccess);
            Assert.Equal("precious", File.ReadAllText(target)); // what the links pointed to is untouched
            Assert.False(File.Exists(fileLink) || Directory.Exists(folderLink));
        }

        // ------------------------------------------------------------------ atomic copies

        [Fact]
        public async Task ACopyInProgress_NeverShowsAPartialFileUnderTheRealName_AndCommitsWhenComplete()
        {
            string source = Big(Dir("In"), "big.bin", 6);
            string destination = Dir("Out");
            using var halfway = new Halfway(destination);
            Task<FileOperationOutcome> copy = _engine.TransferAsync(FileOperationKind.Copy, new[] { source }, destination, new FileOperationContext());

            await halfway.Reached;
            string target = Path.Combine(destination, "big.bin");
            Assert.False(File.Exists(target), "a partial file appeared under the real name");
            Assert.Single(Directory.GetFiles(destination, "*.partial", Hidden));

            halfway.Release();
            FileOperationOutcome outcome = await copy.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(outcome.Result.IsFullSuccess, string.Join("; ", outcome.Result.Failures.Select(f => f.Message)));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
            Assert.Empty(Directory.GetFiles(destination, "*.partial", Hidden));
        }

        [Fact]
        public async Task ACancelledCopy_LeavesNothingBehind()
        {
            string source = Big(Dir("In"), "big.bin", 6);
            string destination = Dir("Out");
            using var halfway = new Halfway(destination);
            using var cancel = new System.Threading.CancellationTokenSource();
            Task<FileOperationOutcome> copy = _engine.TransferAsync(FileOperationKind.Copy, new[] { source }, destination,
                new FileOperationContext { CancellationToken = cancel.Token });

            await halfway.Reached;
            cancel.Cancel();
            halfway.Release();
            FileOperationOutcome outcome = await copy.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(outcome.Result.WasCancelled);
            Assert.Empty(Directory.GetFileSystemEntries(destination)); // no file, no partial file
        }

        [Fact]
        public async Task ANameTakenDuringTheCopy_KeepsBoth_NothingIsOverwritten()
        {
            string source = Big(Dir("In"), "big.bin", 6);
            string destination = Dir("Out");
            using var halfway = new Halfway(destination);
            Task<FileOperationOutcome> copy = _engine.TransferAsync(FileOperationKind.Copy, new[] { source }, destination, new FileOperationContext());

            await halfway.Reached;
            string target = Path.Combine(destination, "big.bin");
            File.WriteAllText(target, "someone else's file"); // another program saves a file with the same name
            halfway.Release();
            FileOperationOutcome outcome = await copy.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(outcome.Result.IsFullSuccess, string.Join("; ", outcome.Result.Failures.Select(f => f.Message)));
            Assert.Equal("someone else's file", File.ReadAllText(target));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(destination, "big (2).bin")));
        }

        [Fact]
        public async Task AMoveAcrossVolumes_KeepsTheSourceUntilTheCopyIsComplete()
        {
            if (!CanCrossVolumes)
            {
                return;
            }

            string source = Big(Dir("In"), "big.bin", 6);
            byte[] original = File.ReadAllBytes(source);
            using var halfway = new Halfway(_other);
            Task<FileOperationOutcome> move = _engine.TransferAsync(FileOperationKind.Move, new[] { source }, _other, new FileOperationContext());

            await halfway.Reached;
            Assert.Equal(original, File.ReadAllBytes(source)); // still whole while the copy is under way
            Assert.False(File.Exists(Path.Combine(_other, "big.bin")));

            halfway.Release();
            FileOperationOutcome outcome = await move.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.False(File.Exists(source));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_other, "big.bin")));
        }

        // ------------------------------------------------------------------ metadata

        [Fact]
        public async Task CopiedAndMovedFolders_KeepTheirDates_AndFilesKeepTheirs()
        {
            string tree = Dir("Album");
            string inner = Directory.CreateDirectory(Path.Combine(tree, "Disc 1")).FullName;
            string song = Path.Combine(inner, "track.flac");
            File.WriteAllText(song, "music");
            var old = new DateTime(2015, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(song, old);
            Directory.SetLastWriteTimeUtc(inner, old.AddDays(1));
            Directory.SetLastWriteTimeUtc(tree, old.AddDays(2));

            string copies = Dir("Copies");
            FileOperationOutcome copy = await _engine.TransferAsync(FileOperationKind.Copy, new[] { tree }, copies, new FileOperationContext());
            Assert.True(copy.Result.IsFullSuccess);
            Assert.Equal(old.AddDays(2), Directory.GetLastWriteTimeUtc(Path.Combine(copies, "Album")));
            Assert.Equal(old.AddDays(1), Directory.GetLastWriteTimeUtc(Path.Combine(copies, "Album", "Disc 1")));
            Assert.Equal(old, File.GetLastWriteTimeUtc(Path.Combine(copies, "Album", "Disc 1", "track.flac")));

            if (CanCrossVolumes)
            {
                FileOperationOutcome move = await _engine.TransferAsync(FileOperationKind.Move, new[] { tree }, _other, new FileOperationContext());
                Assert.True(move.Result.IsFullSuccess);
                Assert.Equal(old.AddDays(2), Directory.GetLastWriteTimeUtc(Path.Combine(_other, "Album")));
                Assert.Equal(old.AddDays(1), Directory.GetLastWriteTimeUtc(Path.Combine(_other, "Album", "Disc 1")));
            }
        }

        // ------------------------------------------------------------------ space

        [Fact]
        public async Task AnOperationThatCannotFit_IsRefusedBeforeAnythingIsWritten()
        {
            string source = Big(Dir("In"), "big.bin", 2);
            string destination = Dir("Small drive");
            FileOperationService.FreeSpaceForTests = folder => folder.StartsWith(_root, StringComparison.Ordinal) ? 1_000_000 : -1;
            try
            {
                FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { source }, destination, new FileOperationContext());
                FileOperationFailure failure = Assert.Single(outcome.Result.Failures);
                Assert.Contains("isn't enough space", failure.Message);
                Assert.Contains("2 MB", failure.Message);
                Assert.Empty(Directory.GetFileSystemEntries(destination)); // nothing written at all
            }
            finally
            {
                FileOperationService.FreeSpaceForTests = null;
            }
        }

        [Fact]
        public async Task ADiskFillingUp_StopsTheOperationOnce_WithNothingHalfWritten()
        {
            string input = Dir("In");
            string[] files = { Big(input, "1.bin", 2), Big(input, "2.bin", 2), Big(input, "3.bin", 2) };
            string destination = Dir("Out");
            Action<string, long> full = (path, bytes) =>
            {
                if (path.StartsWith(destination, StringComparison.Ordinal) && path.Contains(".2.bin.", StringComparison.Ordinal) && bytes > 0)
                {
                    throw new IOException("No space left on device", 28); // ENOSPC, as the system reports it
                }
            };
            FileOperationService.AfterBufferForTests += full;
            try
            {
                FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, files, destination, new FileOperationContext());

                FileOperationFailure failure = Assert.Single(outcome.Result.Failures); // one clear failure, not one per file
                Assert.Contains("ran out of space", failure.Message);
                Assert.Equal(new[] { "1.bin" }, Directory.GetFileSystemEntries(destination).Select(Path.GetFileName)); // done stays done; no partial
                Assert.Equal(File.ReadAllBytes(files[0]), File.ReadAllBytes(Path.Combine(destination, "1.bin")));
            }
            finally
            {
                FileOperationService.AfterBufferForTests -= full;
            }
        }

        // ------------------------------------------------------------------ verification

        [Fact]
        public async Task Sha256Verification_PassesForAGoodCopy_AndMove()
        {
            var settings = new SettingsService(Path.Combine(_root, "settings.json"));
            settings.Current.FileList.VerificationMode = "Sha256";
            var engine = new FileOperationService(settings);
            string source = Big(Dir("In"), "big.bin", 3);
            byte[] original = File.ReadAllBytes(source);

            FileOperationOutcome copy = await engine.TransferAsync(FileOperationKind.Copy, new[] { source }, Dir("Copied"), new FileOperationContext());
            Assert.True(copy.Result.IsFullSuccess, string.Join("; ", copy.Result.Failures.Select(f => f.Message)));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "Copied", "big.bin")));

            if (CanCrossVolumes)
            {
                FileOperationOutcome move = await engine.TransferAsync(FileOperationKind.Move, new[] { source }, _other, new FileOperationContext());
                Assert.True(move.Result.IsFullSuccess, string.Join("; ", move.Result.Failures.Select(f => f.Message)));
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(_other, "big.bin")));
                Assert.False(File.Exists(source));
            }
        }

        [Fact]
        public void Sha256Verification_CatchesASingleChangedByte_ReadFromTheDisk()
        {
            string a = Big(Dir("V"), "a.bin", 2);
            string b = Path.Combine(_root, "V", "b.bin");
            byte[] bytes = File.ReadAllBytes(a);
            bytes[1_234_567] ^= 0x01;
            File.WriteAllBytes(b, bytes);

            IOException failure = Assert.Throws<IOException>(() => FileVerification.Verify(VerificationMode.Sha256, a, b, bytes.Length, default));
            Assert.Contains("differs", failure.Message);
            File.Copy(a, b, overwrite: true);
            FileVerification.Verify(VerificationMode.Sha256, a, b, bytes.Length, default); // identical: passes
            Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                FileVerification.ComputeSha256(WriteText("abc.txt", "abc"), default));
        }

        private string WriteText(string name, string text)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, text);
            return path;
        }

        // ------------------------------------------------------------------ crash recovery

        [Fact]
        public async Task ANormalRun_LeavesNoJournal()
        {
            string journals = Dir("journals");
            var engine = new FileOperationService(null) { JournalFolder = journals };
            string src = Dir("A");
            string dst = Dir("B");
            File.WriteAllText(Path.Combine(src, "x.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "x.txt"), "old");
            File.WriteAllText(Path.Combine(src, "y.txt"), "y");

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Copy, new[] { Path.Combine(src, "x.txt"), Path.Combine(src, "y.txt") }, dst,
                new FileOperationContext { ConflictResolver = new ReplaceResolver() });

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Empty(Directory.GetFiles(journals));
            Assert.Empty(OperationRecovery.Recover(journals));
        }

        [Fact]
        public void Recovery_RemovesTheTemporaryFilesOfAnInterruptedCopy_AndNothingElse()
        {
            string journals = Dir("journals");
            string dst = Dir("Out");
            string done = Path.Combine(dst, "done.txt");
            File.WriteAllText(done, "finished before the crash");
            string partial = Path.Combine(dst, ".big.bin.byteexplorer-0123456789abcdef0123456789abcdef.partial");
            File.WriteAllText(partial, "half a file");
            string notOurs = Path.Combine(dst, "precious.txt");
            File.WriteAllText(notOurs, "never delete me");

            // The crash: the journal is never closed.
            OperationJournal journal = OperationJournal.Start(journals, FileOperationKind.Copy, new[] { "/src/big.bin", "/src/done.txt" }, dst);
            journal.TemporaryCreated(Path.Combine(dst, ".done.txt.byteexplorer-ffffffffffffffffffffffffffffffff.partial"));
            journal.TemporaryGone(Path.Combine(dst, ".done.txt.byteexplorer-ffffffffffffffffffffffffffffffff.partial"));
            journal.TemporaryCreated(partial);
            journal.TemporaryCreated(notOurs); // a damaged journal naming a file that isn't a temporary: never deleted
            journal.AbandonForTests();
            File.AppendAllText(journal.Path, "T\t/half/written/line"); // the crash cut the last line off

            RecoveredOperation recovered = Assert.Single(OperationRecovery.Recover(journals));
            Assert.Equal("Copy 2 items to \"Out\"", recovered.Description);
            Assert.Equal(1, recovered.TemporaryFilesRemoved);
            Assert.Empty(recovered.Problems);
            Assert.False(File.Exists(partial));
            Assert.Equal("never delete me", File.ReadAllText(notOurs));
            Assert.Equal("finished before the crash", File.ReadAllText(done));
            Assert.Empty(Directory.GetFiles(journals, "*.journal")); // handled: gone
        }

        [Fact]
        public void Recovery_FinishesAReplaceWhoseNewCopyWasComplete()
        {
            string journals = Dir("journals");
            string dst = Dir("Out");
            string target = Path.Combine(dst, "report.txt");
            File.WriteAllText(target, "old version");
            string temporary = Path.Combine(dst, ".report.txt.byteexplorer-0123456789abcdef0123456789abcdef.partial");
            File.WriteAllText(temporary, "new version");

            OperationJournal journal = OperationJournal.Start(journals, FileOperationKind.Copy, new[] { "/src/report.txt" }, dst);
            journal.TemporaryCreated(temporary);
            var trashed = ByteExplorer.Core.Platform.Trash.MoveToTrash(target); // the old version is in the trash ...
            journal.Replacing(target, trashed, temporary);                      // ... and then the process died
            journal.AbandonForTests();

            RecoveredOperation recovered = Assert.Single(OperationRecovery.Recover(journals));
            Assert.Equal(1, recovered.ReplacesCompleted);
            Assert.Equal("new version", File.ReadAllText(target));
            Assert.False(File.Exists(temporary));
            Assert.True(File.Exists(trashed.TrashPath)); // the old version stays in the trash, restorable
        }

        [Fact]
        public void Recovery_PutsBackTheOldItemWhenTheReplaceHadNoCompleteCopy()
        {
            string journals = Dir("journals");
            string src = Path.Combine(Dir("In"), "report.txt");
            File.WriteAllText(src, "new version"); // a same-volume move: the source is still at its place
            string dst = Dir("Out");
            string target = Path.Combine(dst, "report.txt");
            File.WriteAllText(target, "old version");

            OperationJournal journal = OperationJournal.Start(journals, FileOperationKind.Move, new[] { src }, dst);
            var trashed = ByteExplorer.Core.Platform.Trash.MoveToTrash(target);
            journal.Replacing(target, trashed, null); // died before the rename
            journal.AbandonForTests();

            RecoveredOperation recovered = Assert.Single(OperationRecovery.Recover(journals));
            Assert.Equal(1, recovered.ReplacesRolledBack);
            Assert.Equal("old version", File.ReadAllText(target));
            Assert.Equal("new version", File.ReadAllText(src));
        }

        [Fact]
        public void Recovery_NeverTouchesTheJournalOfARunningOperation()
        {
            string journals = Dir("journals");
            string dst = Dir("Out");
            string partial = Path.Combine(dst, ".big.bin.byteexplorer-0123456789abcdef0123456789abcdef.partial");
            File.WriteAllText(partial, "being written right now");
            using OperationJournal running = OperationJournal.Start(journals, FileOperationKind.Copy, new[] { "/src/big.bin" }, dst);
            running.TemporaryCreated(partial); // a second window starts while this operation runs

            Assert.Empty(OperationRecovery.Recover(journals));
            Assert.True(File.Exists(partial));
            Assert.True(File.Exists(running.Path));
        }

        private static readonly EnumerationOptions Hidden = new EnumerationOptions { AttributesToSkip = 0 };

        /// <summary>A file of <paramref name="megabytes"/> MB with varied bytes (a copy that mixes them up shows).</summary>
        private static string Big(string folder, string name, int megabytes)
        {
            string path = Path.Combine(folder, name);
            var bytes = new byte[megabytes * 1024 * 1024];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>
        /// Stops the first copy writing under <paramref name="folder"/> after its first buffer, until released: the test
        /// looks at the disk in the middle of the copy. Only copies into this test's own folder are stopped.
        /// </summary>
        private sealed class Halfway : IDisposable
        {
            private readonly string _folder;
            private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly System.Threading.ManualResetEventSlim _release = new(false);
            private readonly Action<string, long> _hook;
            private int _stopped;

            public Halfway(string folder)
            {
                _folder = folder;
                _hook = (path, bytes) =>
                {
                    if (bytes > 0 && path.StartsWith(_folder, StringComparison.Ordinal) && System.Threading.Interlocked.Exchange(ref _stopped, 1) == 0)
                    {
                        _reached.TrySetResult();
                        _release.Wait(TimeSpan.FromSeconds(20));
                    }
                };
                FileOperationService.AfterBufferForTests += _hook;
            }

            public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

            public void Release() => _release.Set();

            public void Dispose()
            {
                _release.Set();
                FileOperationService.AfterBufferForTests -= _hook;
            }
        }

        [Fact]
        public async Task ReplacingByAMoveOnTheSameVolume_LeavesNoHiddenTemporaryFile_AndUndoBringsBothBack()
        {
            string src = Dir("A");
            string dst = Dir("B");
            File.WriteAllText(Path.Combine(src, "doc.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "doc.txt"), "old");

            var replaceAll = new ReplaceResolver();
            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Move, new[] { Path.Combine(src, "doc.txt") }, dst,
                new FileOperationContext { ConflictResolver = replaceAll });

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "doc.txt")));
            Assert.False(File.Exists(Path.Combine(src, "doc.txt")));
            Assert.Empty(Directory.GetFiles(_root, "*.partial", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }));

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.Equal("new", File.ReadAllText(Path.Combine(src, "doc.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "doc.txt")));
        }

        private sealed class ReplaceResolver : IConflictResolver
        {
            public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, System.Threading.CancellationToken cancellationToken) =>
                Task.FromResult(new ConflictDecision(ConflictChoice.Replace, true));
        }

        private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;
    }
}
