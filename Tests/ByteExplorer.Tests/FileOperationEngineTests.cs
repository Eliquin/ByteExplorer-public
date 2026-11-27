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
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.ViewModels.FileOperations;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The file operations engine on the real file system: name clash choices, apply-to-all, folder merges, moves,
    /// deletes, cancellation, progress, pause, and undoing each. The trash is redirected to a private folder (Linux and
    /// macOS read XDG_DATA_HOME / the home trash; the tests run on Linux in CI).
    /// </summary>
    public sealed class FileOperationEngineTests : IDisposable
    {
        private static readonly string TrashHome = InitTrash();
        private readonly string _root;
        private readonly FileOperationService _engine = new FileOperationService(null);

        public FileOperationEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "be-ops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        private static string InitTrash()
        {
            string home = Path.Combine(Path.GetTempPath(), "be-trash-" + Guid.NewGuid().ToString("N"));
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

        // ------------------------------------------------------------------ clashes

        [Fact]
        public async Task Copy_NewItems_ThenUndo_RemovesTheCopies()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "alpha");
            string tree = Folder("src/tree");
            Write(tree, "b.txt", "beta");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { file, tree }, dst, new FileOperationContext());

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal("alpha", Read(dst, "a.txt"));
            Assert.Equal("beta", Read(Path.Combine(dst, "tree"), "b.txt"));
            Assert.NotNull(outcome.Record);

            FileOperationOutcome undo = await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.False(undo.Result.HasFailures);
            Assert.False(File.Exists(Path.Combine(dst, "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(dst, "tree")));
            Assert.True(File.Exists(file)); // the source is untouched
        }

        [Fact]
        public async Task Clash_Replace_TrashesTheOldFile_AndUndoBringsItBack()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "new content");
            Write(dst, "a.txt", "old content");
            var resolver = new FixedResolver(ConflictChoice.Replace);

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst, new FileOperationContext { ConflictResolver = resolver });

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal(1, resolver.Calls);
            Assert.Equal(ConflictKind.FileOverFile, resolver.Last.Kind);
            Assert.Equal("new content", Read(dst, "a.txt"));
            Assert.Empty(Directory.GetFiles(dst, "*.partial", SearchOption.AllDirectories)); // no temporary left

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.Equal("old content", Read(dst, "a.txt"));
            Assert.Single(Directory.GetFiles(dst));
        }

        [Fact]
        public async Task Clash_Skip_LeavesTheDestination_AndCountsTheSkip()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "new");
            Write(dst, "a.txt", "old");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst,
                new FileOperationContext { ConflictResolver = new FixedResolver(ConflictChoice.Skip) });

            Assert.Equal("old", Read(dst, "a.txt"));
            Assert.Equal(1, outcome.Result.SkippedCount);
            Assert.Equal(0, outcome.Result.SuccessCount);
            Assert.False(outcome.Result.HasFailures);
            Assert.Null(outcome.Record); // nothing to undo
        }

        [Fact]
        public async Task Clash_KeepBoth_GivesTheIncomingItemAFreeName()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "new");
            Write(dst, "a.txt", "old");

            await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst,
                new FileOperationContext { ConflictResolver = new FixedResolver(ConflictChoice.KeepBoth) });

            Assert.Equal("old", Read(dst, "a.txt"));
            Assert.Equal("new", Read(dst, "a (2).txt"));
        }

        [Fact]
        public async Task Clash_WithoutAResolver_NeverOverwrites()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "new");
            Write(dst, "a.txt", "old");

            await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst, new FileOperationContext());

            Assert.Equal("old", Read(dst, "a.txt"));
            Assert.Equal("new", Read(dst, "a (2).txt"));
        }

        [Fact]
        public async Task ApplyToAll_AsksOnce()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            var files = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                files.Add(Write(src, $"f{i}.txt", "new" + i));
                Write(dst, $"f{i}.txt", "old" + i);
            }

            var resolver = new FixedResolver(ConflictChoice.Replace, applyToAll: true);
            await _engine.TransferAsync(FileOperationKind.Copy, files, dst, new FileOperationContext { ConflictResolver = resolver });

            Assert.Equal(1, resolver.Calls);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("new" + i, Read(dst, $"f{i}.txt"));
            }
        }

        [Fact]
        public async Task CopyIntoItsOwnFolder_MakesASecondCopyWithoutAsking()
        {
            string src = Folder("src");
            string file = Write(src, "a.txt", "x");
            var resolver = new FixedResolver(ConflictChoice.Skip);

            await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, src, new FileOperationContext { ConflictResolver = resolver });

            Assert.Equal(0, resolver.Calls);
            Assert.True(File.Exists(Path.Combine(src, "a - Copy.txt"))); // Explorer's name for a duplicate

            await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, src, new FileOperationContext { ConflictResolver = resolver });
            Assert.True(File.Exists(Path.Combine(src, "a - Copy (2).txt")));

            string tree = Folder("src/Photos");
            await _engine.TransferAsync(FileOperationKind.Copy, new[] { tree }, src, new FileOperationContext());
            Assert.True(Directory.Exists(Path.Combine(src, "Photos - Copy")));
        }

        [Fact]
        public async Task FolderIntoItself_IsRefused()
        {
            string src = Folder("src");
            string inner = Folder("src/inner");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { src }, inner, new FileOperationContext());

            Assert.Single(outcome.Result.Failures);
            Assert.Empty(Directory.GetFileSystemEntries(inner));
        }

        [Fact]
        public async Task MergeFolders_AsksForClashingFilesOnly_AndUndoRemovesOnlyWhatWasAdded()
        {
            string srcTree = Folder("src/docs");
            string dstTree = Folder("dst/docs");
            Write(srcTree, "same.txt", "incoming");
            Write(srcTree, "new.txt", "added");
            Write(dstTree, "same.txt", "existing");
            Write(dstTree, "keep.txt", "untouched");

            var resolver = new ScriptedResolver(ConflictChoice.Replace /* merge the folder */, ConflictChoice.Skip /* same.txt */);
            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { srcTree }, Path.Combine(_root, "dst"),
                new FileOperationContext { ConflictResolver = resolver });

            Assert.Equal(new[] { ConflictKind.FolderOverFolder, ConflictKind.FileOverFile }, resolver.Kinds);
            Assert.Equal("existing", Read(dstTree, "same.txt"));
            Assert.Equal("added", Read(dstTree, "new.txt"));
            Assert.Equal("untouched", Read(dstTree, "keep.txt"));

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.False(File.Exists(Path.Combine(dstTree, "new.txt")));
            Assert.Equal("existing", Read(dstTree, "same.txt"));
            Assert.Equal("untouched", Read(dstTree, "keep.txt"));
        }

        [Fact]
        public async Task TypeMismatch_OffersNoReplace()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "thing", "file");
            Folder("dst/thing");
            var resolver = new FixedResolver(ConflictChoice.Replace);

            await _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst, new FileOperationContext { ConflictResolver = resolver });

            Assert.Equal(ConflictKind.TypeMismatch, resolver.Last.Kind);
            Assert.True(Directory.Exists(Path.Combine(dst, "thing")));      // the folder is still there
            Assert.True(File.Exists(Path.Combine(dst, "thing (2)")));       // Replace became Keep both
        }

        // ------------------------------------------------------------------ move

        [Fact]
        public async Task Move_SameVolume_ThenUndo_PutsItBack()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "x");
            string tree = Folder("src/tree");
            Write(tree, "b.txt", "y");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Move, new[] { file, tree }, dst, new FileOperationContext());
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(tree));
            Assert.Equal("y", Read(Path.Combine(dst, "tree"), "b.txt"));

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.Equal("x", Read(src, "a.txt"));
            Assert.Equal("y", Read(tree, "b.txt"));
            Assert.Empty(Directory.GetFileSystemEntries(dst));
        }

        [Fact]
        public async Task MoveMerge_RemovesTheEmptiedSource_AndUndoRecreatesIt()
        {
            string srcTree = Folder("src/docs");
            string dstTree = Folder("dst/docs");
            Write(srcTree, "one.txt", "1");
            Write(dstTree, "two.txt", "2");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Move, new[] { srcTree }, Path.Combine(_root, "dst"),
                new FileOperationContext { ConflictResolver = new FixedResolver(ConflictChoice.Replace) });

            Assert.False(Directory.Exists(srcTree));
            Assert.Equal("1", Read(dstTree, "one.txt"));

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.Equal("1", Read(srcTree, "one.txt"));
            Assert.False(File.Exists(Path.Combine(dstTree, "one.txt")));
            Assert.Equal("2", Read(dstTree, "two.txt"));
        }

        [Fact]
        public async Task Move_AcrossVolumes_CopiesVerifiesAndDeletesTheSource_AndUndoMovesBack()
        {
            if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
            {
                return;
            }

            string other = Path.Combine("/dev/shm", "be-ops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(other);
            try
            {
                string src = Folder("src");
                string file = Write(src, "big.bin", new string('z', 300_000));
                FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Move, new[] { file }, other, new FileOperationContext());

                Assert.True(outcome.Result.IsFullSuccess);
                Assert.False(File.Exists(file));
                Assert.Equal(300_000, new FileInfo(Path.Combine(other, "big.bin")).Length);
                Assert.Empty(Directory.GetFiles(other, "*.partial"));

                await _engine.UndoAsync(outcome.Record, new FileOperationContext());
                Assert.True(File.Exists(file));
                Assert.False(File.Exists(Path.Combine(other, "big.bin")));
            }
            finally
            {
                Directory.Delete(other, true);
            }
        }

        // ------------------------------------------------------------------ delete

        [Fact]
        public async Task DeleteToTrash_ThenUndo_RestoresEverything()
        {
            string src = Folder("src");
            string file = Write(src, "a.txt", "keep me");
            string tree = Folder("src/tree");
            Write(tree, "b.txt", "and me");

            FileOperationOutcome outcome = await _engine.DeleteAsync(new[] { file, tree }, permanent: false, new FileOperationContext());
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(tree));
            Assert.Equal(2, outcome.Record.Actions.Count);
            Assert.All(outcome.Record.Actions, action => Assert.IsType<TrashedAction>(action));

            FileOperationOutcome undo = await _engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.False(undo.Result.HasFailures);
            Assert.Equal("keep me", Read(src, "a.txt"));
            Assert.Equal("and me", Read(tree, "b.txt"));
        }

        [Fact]
        public async Task PermanentDelete_HasNothingToUndo()
        {
            string file = Write(Folder("src"), "a.txt", "gone");
            FileOperationOutcome outcome = await _engine.DeleteAsync(new[] { file }, permanent: true, new FileOperationContext());

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.False(File.Exists(file));
            Assert.Null(outcome.Record);
        }

        [Fact]
        public async Task UndoRestore_WhenThePlaceIsTaken_KeepsBoth()
        {
            string src = Folder("src");
            string file = Write(src, "a.txt", "original");
            FileOperationOutcome outcome = await _engine.DeleteAsync(new[] { file }, permanent: false, new FileOperationContext());
            Write(src, "a.txt", "newcomer");

            await _engine.UndoAsync(outcome.Record, new FileOperationContext());

            Assert.Equal("newcomer", Read(src, "a.txt"));
            Assert.Equal("original", Read(src, "a (2).txt"));
        }

        // ------------------------------------------------------------------ cancel, progress, pause

        [Fact]
        public async Task CancelAtAClash_StopsButKeepsTheRecordOfWhatWasDone()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string first = Write(src, "1.txt", "one");
            string second = Write(src, "2.txt", "two");
            string third = Write(src, "3.txt", "three");
            Write(dst, "2.txt", "existing");

            FileOperationOutcome outcome = await _engine.TransferAsync(FileOperationKind.Copy, new[] { first, second, third }, dst,
                new FileOperationContext { ConflictResolver = new FixedResolver(ConflictChoice.Cancel) });

            Assert.True(outcome.Result.WasCancelled);
            Assert.True(File.Exists(Path.Combine(dst, "1.txt")));
            Assert.False(File.Exists(Path.Combine(dst, "3.txt")));
            Assert.Single(outcome.Record.Actions);
        }

        [Fact]
        public async Task Progress_ReachesTheTotal()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            var files = Enumerable.Range(0, 5).Select(i => Write(src, $"f{i}.bin", new string('x', 50_000 * (i + 1)))).ToList();
            var snapshots = new List<FileOperationProgress>();

            await _engine.TransferAsync(FileOperationKind.Copy, files, dst, new FileOperationContext
            {
                Progress = p =>
                {
                    lock (snapshots)
                    {
                        snapshots.Add(p);
                    }
                }
            });

            FileOperationProgress last = snapshots.Last();
            Assert.Equal(750_000, last.TotalBytes);
            Assert.Equal(last.TotalBytes, last.CompletedBytes);
            Assert.Equal(5, last.CompletedItems);
            Assert.Equal(1.0, last.Fraction);
            Assert.Contains(snapshots, s => s.Phase == FileOperationPhase.Preparing);
        }

        [Fact]
        public async Task Pause_HoldsTheWorkUntilResumed()
        {
            string src = Folder("src");
            string dst = Folder("dst");
            string file = Write(src, "a.txt", "x");
            using var gate = new PauseGate();
            gate.Pause();

            Task<FileOperationOutcome> running = _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst, new FileOperationContext { Pause = gate });
            await Task.Delay(300);
            Assert.False(running.IsCompleted);
            Assert.False(File.Exists(Path.Combine(dst, "a.txt")));

            gate.Resume();
            FileOperationOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Result.IsFullSuccess);
        }

        [Fact]
        public async Task CancelWhilePaused_Stops()
        {
            string file = Write(Folder("src"), "a.txt", "x");
            string dst = Folder("dst");
            using var gate = new PauseGate();
            using var cts = new CancellationTokenSource();
            gate.Pause();

            Task<FileOperationOutcome> running = _engine.TransferAsync(FileOperationKind.Copy, new[] { file }, dst,
                new FileOperationContext { Pause = gate, CancellationToken = cts.Token });
            await Task.Delay(100);
            cts.Cancel();

            FileOperationOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Result.WasCancelled);
            Assert.False(File.Exists(Path.Combine(dst, "a.txt")));
        }

        // ------------------------------------------------------------------ pieces

        [Fact]
        public void RecycleBinRecords_ParseBothVersions()
        {
            const string original = @"C:\Users\me\Documents\report.pdf";
            DateTime deleted = new DateTime(2026, 11, 2, 21, 30, 0, DateTimeKind.Utc);

            byte[] v2 = BuildInfoFile(2, original, deleted);
            Assert.True(RecycleBin.TryParseInfoFile(v2, out string path2, out DateTime when2));
            Assert.Equal(original, path2);
            Assert.Equal(deleted, when2);

            byte[] v1 = BuildInfoFile(1, original, deleted);
            Assert.True(RecycleBin.TryParseInfoFile(v1, out string path1, out _));
            Assert.Equal(original, path1);

            Assert.False(RecycleBin.TryParseInfoFile(new byte[10], out _, out _));
            Assert.False(RecycleBin.TryParseInfoFile(BuildInfoFile(7, original, deleted), out _, out _));
        }

        [Theory]
        [InlineData(2, "A few seconds left")]
        [InlineData(42, "About 45 seconds left")]
        [InlineData(70, "About a minute left")]
        [InlineData(600, "About 10 minutes left")]
        [InlineData(3600, "About 1 hour left")]
        [InlineData(4800, "About 1 hour 20 minutes left")]
        public void TimeLeft_ReadsNaturally(double seconds, string expected)
        {
            Assert.Equal(expected, FileOperationJob.FormatRemaining(seconds));
        }

        private static byte[] BuildInfoFile(long version, string path, DateTime deletedUtc)
        {
            var bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(version));
            bytes.AddRange(BitConverter.GetBytes(1234L));
            bytes.AddRange(BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()));
            if (version == 1)
            {
                byte[] field = new byte[520];
                Encoding.Unicode.GetBytes(path).CopyTo(field, 0);
                bytes.AddRange(field);
            }
            else
            {
                bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
                bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
            }

            return bytes.ToArray();
        }

        private string Folder(string relative)
        {
            string path = Path.Combine(_root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Write(string folder, string name, string content)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static string Read(string folder, string name) => File.ReadAllText(Path.Combine(folder, name));

        private sealed class FixedResolver : IConflictResolver
        {
            private readonly ConflictChoice _choice;
            private readonly bool _applyToAll;

            public FixedResolver(ConflictChoice choice, bool applyToAll = false)
            {
                _choice = choice;
                _applyToAll = applyToAll;
            }

            public int Calls { get; private set; }
            public ConflictInfo Last { get; private set; }

            public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken)
            {
                Calls++;
                Last = conflict;
                return Task.FromResult(new ConflictDecision(_choice, _applyToAll));
            }
        }

        private sealed class ScriptedResolver : IConflictResolver
        {
            private readonly Queue<ConflictChoice> _choices;

            public ScriptedResolver(params ConflictChoice[] choices) => _choices = new Queue<ConflictChoice>(choices);

            public List<ConflictKind> Kinds { get; } = new List<ConflictKind>();

            public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken)
            {
                Kinds.Add(conflict.Kind);
                return Task.FromResult(new ConflictDecision(_choices.Count > 0 ? _choices.Dequeue() : ConflictChoice.Skip, false));
            }
        }
    }
}
