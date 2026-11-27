// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services.FileOperations;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// The file operations engine: copy, move, delete, and undoing any of them.
    /// <para>
    /// <b>Name clashes</b> are the person's choice (<see cref="IConflictResolver"/>): Replace (the existing file goes to
    /// the trash, so it can be restored), Skip, or Keep both ("name (2).ext"); for a folder onto a folder, Replace
    /// merges the two, asking again for the files that clash inside. "Apply to all" remembers the choice per kind of
    /// clash for the rest of the operation. Without a resolver every clash keeps both, never overwriting.
    /// </para>
    /// <para>
    /// <b>Safety</b>: a file is never written over in place. A replacement is first copied (or, on the same volume,
    /// renamed) to a hidden temporary sibling, then the existing file goes to the trash, then the temporary file takes
    /// its name; if that last step fails the trashed file is put back. A cross-volume move copies, verifies (the
    /// VerificationMode setting) and commits before deleting the source, so the only good copy is never destroyed
    /// first; if only the source cleanup fails, the item is reported as a partial move, not a failure.
    /// </para>
    /// <para>
    /// <b>Progress</b> is reported in bytes (scanned before the work starts, so there is a percentage, speed and time
    /// left) or, for same-volume moves (renames, no bytes copied), in items. <b>Pause</b> blocks between files and
    /// between the buffers of the file being copied.
    /// </para>
    /// <para>
    /// <b>Undo</b>: every reversible change is recorded (<see cref="FileOperationRecord"/>): items created, moved,
    /// trashed (deleted or replaced), emptied source folders removed. <see cref="UndoAsync"/> reverses them last to
    /// first: copies go to the trash, moves go back, trashed items are restored. A permanent delete records nothing.
    /// </para>
    /// <para>
    /// Every operation continues past individual failures and reports them at the end, runs off the UI thread, and
    /// retries transient errors (a file briefly locked) with backoff.
    /// </para>
    /// </summary>
    public sealed partial class FileOperationService : IFileOperationService
    {
        /// <summary>Guards against junction and symlink loops while walking a tree.</summary>
        private const int MaxTreeDepth = 64;

        private const int DeleteBatchSize = 200;
        private const int MaxShellPath = 260;

        /// <summary>
        /// Tests only: called after each buffer a copy writes (the file being written, bytes so far), on the worker thread,
        /// so a test can stop a copy half way and look at the disk. Null in the app.
        /// </summary>
        internal static Action<string, long> AfterBufferForTests;

        /// <summary>Tests only: the free space reported for a destination folder (null in the app).</summary>
        internal static Func<string, long> FreeSpaceForTests;

        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;

        /// <summary>
        /// Where running copies and moves keep their journal (OperationJournal), for OperationRecovery at the next start.
        /// Null: no journal (tests that don't exercise recovery).
        /// </summary>
        public string JournalFolder { get; init; }

        public FileOperationService(ISettingsService settingsService, ILogger logger = null)
        {
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<FileOperationService>();
        }

        // ================================================================== copy / move

        public Task<FileOperationOutcome> TransferAsync(FileOperationKind kind, IReadOnlyList<string> sourcePaths, string destinationFolder, FileOperationContext context)
        {
            if (kind == FileOperationKind.Delete)
            {
                throw new ArgumentException("Use DeleteAsync for deletes.", nameof(kind));
            }

            if (sourcePaths == null || sourcePaths.Count == 0 || string.IsNullOrEmpty(destinationFolder))
            {
                return Task.FromResult(new FileOperationOutcome(new FileOperationResult(kind, 0, Array.Empty<FileOperationFailure>(), false), null));
            }

            if (InvolvesPhone(sourcePaths, destinationFolder))
            {
                // A phone has no renames into place, no flushing and no recycle bin: its own route (FileOperationService.Phone.cs).
                return Task.Run(() =>
                {
                    using var phoneRun = new PhoneTransferRun(this, kind, sourcePaths, destinationFolder, context ?? new FileOperationContext());
                    return phoneRun.Execute();
                });
            }

            return Task.Run(() =>
            {
                using var run = new TransferRun(this, kind, sourcePaths, destinationFolder, context ?? new FileOperationContext());
                return run.Execute();
            });
        }

        /// <summary>One copy or move: its state lives here, so the engine itself holds none and runs jobs in parallel.</summary>
        private sealed class TransferRun : IDisposable
        {
            private readonly OperationJournal _journal;
            private readonly FileOperationService _owner;
            private readonly FileOperationKind _kind;
            private readonly IReadOnlyList<string> _sources;
            private readonly string _destinationFolder;
            private readonly FileOperationContext _context;
            private readonly CancellationToken _token;
            private readonly ProgressTracker _progress;
            private readonly List<UndoAction> _undo = new List<UndoAction>();
            private readonly List<FileOperationFailure> _failures = new List<FileOperationFailure>();
            private readonly List<FileOperationFailure> _partialMoves = new List<FileOperationFailure>();
            private readonly Dictionary<ConflictKind, ConflictChoice> _remembered = new Dictionary<ConflictKind, ConflictChoice>();
            private readonly VerificationMode _verification;
            private readonly HashSet<string> _affected = new HashSet<string>(PathUtil.Comparer);
            private int _skipped;

            public TransferRun(FileOperationService owner, FileOperationKind kind, IReadOnlyList<string> sources, string destinationFolder, FileOperationContext context)
            {
                _owner = owner;
                _kind = kind;
                _sources = sources;
                _destinationFolder = destinationFolder;
                _context = context;
                _token = context.CancellationToken;
                _progress = new ProgressTracker(context.Progress);
                // Every transfer that writes bytes is checked (copies too: a copy is often the backup someone relies on).
                _verification = ParseVerificationMode(owner._settingsService?.Current?.FileList?.VerificationMode);
                _journal = OperationJournal.Start(owner.JournalFolder, kind, sources, destinationFolder);
            }

            private bool _skipAllErrors;

            /// <summary>
            /// Runs one item's step. A failure (not a cancellation or a full destination) is asked about, when the setting
            /// says to ask: Retry runs the step again, Skip and Skip all record it as failed (with where it was going, for
            /// "Retry failed"), Cancel stops the operation. True when the step succeeded.
            /// </summary>
            private bool Attempt(string path, string destinationFolder, Action step)
            {
                while (true)
                {
                    try
                    {
                        step();
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (DestinationFullException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (NativeFileOperations.IsDiskFull(ex))
                    {
                        throw DestinationFullException.During(destinationFolder, ex);
                    }
                    catch (Exception ex)
                    {
                        switch (_owner.AskAboutError(_context, _progress, _kind, path, ex, ref _skipAllErrors))
                        {
                            case ErrorChoice.Retry:
                                _owner._logger.Information("{Verb}: retrying {Path} after: {Message}", Verb, path, ex.Message);
                                continue;
                            case ErrorChoice.Cancel:
                                throw new OperationCanceledException(_token);
                            default:
                                _owner._logger.Warning(ex, "{Verb} failed for {Path}", Verb, path);
                                _failures.Add(new FileOperationFailure(path, ex.Message) { RetryDestination = destinationFolder });
                                return false;
                        }
                    }
                }
            }

            /// <summary>The run ended (finished, failed or cancelled, not crashed): its journal goes.</summary>
            public void Dispose() => _journal?.Dispose();

            /// <summary>A hidden temporary file is about to be written (journaled so a crash's leftover is removed at the next start).</summary>
            private string NewTemporary(string destination)
            {
                string temporary = CreateTemporarySiblingPath(destination);
                _journal?.TemporaryCreated(temporary);
                return temporary;
            }

            /// <summary>The temporary file is committed or deleted (only journaled as gone once it really is).</summary>
            private void TemporaryDone(string temporary)
            {
                if (!File.Exists(temporary))
                {
                    _journal?.TemporaryGone(temporary);
                }
            }

            private bool IsMove => _kind == FileOperationKind.Move;

            private string Verb => IsMove ? "Move" : "Copy";

            public FileOperationOutcome Execute()
            {
                _owner._logger.Information("{Verb} starting: {Count} item(s) into {Destination}", Verb, _sources.Count, _destinationFolder);
                _affected.Add(_destinationFolder);
                int successCount = 0;
                bool cancelled = false;

                try
                {
                    Plan();
                    _progress.SetPhase(FileOperationPhase.Working);

                    foreach (string source in _sources)
                    {
                        _token.ThrowIfCancellationRequested();
                        string parent = Path.GetDirectoryName(source);
                        if (IsMove && !string.IsNullOrEmpty(parent))
                        {
                            _affected.Add(parent);
                        }

                        try
                        {
                            bool transferred = false;
                            if (Attempt(source, _destinationFolder, () => transferred = TransferTopLevel(source)) && transferred)
                            {
                                successCount++;
                            }
                        }
                        finally
                        {
                            _progress.CompleteTopLevelItem();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (DestinationFullException full)
                {
                    // Everything after this would fail the same way: stop with one clear failure instead.
                    _owner._logger.Warning("{Verb} stopped: {Message}", Verb, full.Message);
                    _failures.Add(new FileOperationFailure(_destinationFolder, full.Message));
                }

                _progress.SetPhase(FileOperationPhase.Finishing);
                _owner._logger.Information("{Verb} finished: {Success} succeeded, {Skipped} skipped, {Partial} partial, {Failed} failed, cancelled={Cancelled}",
                    Verb, successCount, _skipped, _partialMoves.Count, _failures.Count, cancelled);

                var result = new FileOperationResult(_kind, successCount, _failures, cancelled, _partialMoves, _skipped);
                string description = $"{Verb} {Describe(_sources)} to {PathUtil.GetName(_destinationFolder)}";
                FileOperationRecord record = _undo.Count > 0 ? new FileOperationRecord(description, _undo.ToArray(), new List<string>(_affected)) : null;
                return new FileOperationOutcome(result, record);
            }

            /// <summary>
            /// Counts what will be transferred, for a percentage and time left: bytes of everything that is copied, and
            /// top-level items. Same-volume moves copy nothing (they are renames), so they count only as items.
            /// </summary>
            private void Plan()
            {
                long totalBytes = 0;
                foreach (string source in _sources)
                {
                    _token.ThrowIfCancellationRequested();
                    _progress.SetCurrent(PathUtil.GetName(source));
                    if (IsMove && IsSameVolume(source, _destinationFolder))
                    {
                        continue;
                    }

                    try
                    {
                        totalBytes += Directory.Exists(source) ? ScanTreeBytes(source) : new FileInfo(source).Length;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Reported when the item itself is transferred.
                    }
                }

                _progress.SetTotals(totalBytes, _sources.Count);
                CheckFreeSpace(totalBytes);
            }

            /// <summary>
            /// Refuses to start what can't fit: the bytes to write (moves within a volume write none) against the
            /// destination's free space. Unknown free space (a network share that won't say) lets it start.
            /// </summary>
            private void CheckFreeSpace(long bytesToWrite)
            {
                long free = FreeSpace(_destinationFolder);
                if (bytesToWrite > 0 && free >= 0 && bytesToWrite > free)
                {
                    throw new DestinationFullException(
                        $"There isn't enough space in \"{PathUtil.GetName(_destinationFolder)}\": this needs {FormatBytes(bytesToWrite)}, " +
                        $"and {FormatBytes(free)} is free. Nothing was {(IsMove ? "moved" : "copied")}.");
                }
            }

            private long ScanTreeBytes(string root)
            {
                long total = 0;
                var pending = new Stack<(string Path, int Depth)>();
                pending.Push((root, 0));
                while (pending.Count > 0)
                {
                    _token.ThrowIfCancellationRequested();
                    (string folder, int depth) = pending.Pop();
                    foreach (DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(folder, _token))
                    {
                        if (IsLinkEntry(entry, folder))
                        {
                            continue; // a link is transferred as a link: nothing of its target is copied
                        }

                        if (entry.IsDirectory)
                        {
                            if (depth + 1 <= MaxTreeDepth)
                            {
                                pending.Push((Path.Combine(folder, entry.Name), depth + 1));
                            }
                        }
                        else
                        {
                            total += entry.SizeBytes;
                        }
                    }
                }

                return total;
            }

            /// <summary>One selected item; true if it was transferred (fully or partly), false if skipped.</summary>
            private bool TransferTopLevel(string source)
            {
                // A link (symbolic link, junction) is transferred as a link, never as the folder it points to.
                bool isDirectory = Directory.Exists(source) && !IsLink(source);
                if (!isDirectory && !File.Exists(source) && !IsLink(source))
                {
                    throw new FileNotFoundException($"\"{PathUtil.GetName(source)}\" no longer exists.", source);
                }

                // A folder into itself or one of its subfolders would recurse forever (copy) or corrupt the source (move).
                if (isDirectory && IsSameOrSubPath(source, _destinationFolder))
                {
                    throw new IOException("Can't copy or move a folder into itself or one of its own subfolders.");
                }

                string destination = Path.Combine(_destinationFolder, PathUtil.GetName(source));
                _progress.SetCurrent(PathUtil.GetName(source));

                if (PathUtil.AreSame(source, destination))
                {
                    if (IsMove)
                    {
                        return true; // already there: nothing to do
                    }

                    // Copy into its own folder: always a second copy beside it, named as Explorer names it ("name - Copy").
                    TransferNew(source, DuplicateName(destination), isDirectory, record: true);
                    return true;
                }

                return TransferInto(source, destination, isDirectory, record: true);
            }

            /// <summary>
            /// Transfers <paramref name="source"/> to <paramref name="destination"/>, resolving a clash there. record: whether
            /// to record the changes for undo (false inside a folder this operation created, which is undone as a whole).
            /// Returns false when skipped.
            /// </summary>
            private bool TransferInto(string source, string destination, bool isDirectory, bool record)
            {
                bool destinationIsDirectory = Directory.Exists(destination);
                bool destinationIsFile = !destinationIsDirectory && File.Exists(destination);
                if (!destinationIsDirectory && !destinationIsFile)
                {
                    TransferNew(source, destination, isDirectory, record);
                    return true;
                }

                ConflictKind kind = isDirectory && destinationIsDirectory
                    ? ConflictKind.FolderOverFolder
                    : !isDirectory && destinationIsFile ? ConflictKind.FileOverFile : ConflictKind.TypeMismatch;

                switch (Decide(kind, source, destination, isDirectory, destinationIsDirectory))
                {
                    case ConflictChoice.Skip:
                        _skipped++;
                        bool rename = IsMove && IsSameVolume(source, destination);
                        _progress.Skip(rename ? 0 : isDirectory ? ScanTreeBytes(source) : SafeLength(source), rename);
                        return false;
                    case ConflictChoice.KeepBoth:
                        TransferNew(source, FreeName(destination), isDirectory, record);
                        return true;
                    default: // Replace
                        if (kind == ConflictKind.FolderOverFolder)
                        {
                            MergeFolder(source, destination, record);
                        }
                        else
                        {
                            ReplaceFile(source, destination, record);
                        }

                        return true;
                }
            }

            /// <summary>Asks (or uses a remembered "apply to all" answer) what to do about a clash.</summary>
            private ConflictChoice Decide(ConflictKind kind, string source, string destination, bool sourceIsDirectory, bool destinationIsDirectory)
            {
                if (_remembered.TryGetValue(kind, out ConflictChoice remembered))
                {
                    return Resolve(remembered, source, destination);
                }

                IConflictResolver resolver = _context.ConflictResolver;
                if (resolver == null)
                {
                    return ConflictChoice.KeepBoth; // never overwrite without being asked
                }

                var info = new ConflictInfo(kind, source, destination,
                    sourceIsDirectory ? -1 : SafeLength(source), SafeModified(source),
                    destinationIsDirectory ? -1 : SafeLength(destination), SafeModified(destination), IsMove);

                FileOperationPhase previous = _progress.Phase;
                _progress.SetPhase(FileOperationPhase.WaitingForDecision);
                ConflictDecision decision;
                try
                {
                    // The worker thread waits for the person; it is this operation's own thread, never the UI thread.
                    decision = resolver.ResolveAsync(info, _token).GetAwaiter().GetResult();
                }
                finally
                {
                    _progress.SetPhase(previous);
                }

                ConflictChoice choice = decision.Choice;
                if (choice == ConflictChoice.Cancel)
                {
                    throw new OperationCanceledException(_token);
                }

                if (kind == ConflictKind.TypeMismatch && (choice == ConflictChoice.Replace || choice == ConflictChoice.ReplaceIfNewer))
                {
                    choice = ConflictChoice.KeepBoth; // a file can't replace a folder (or the reverse)
                }

                if (decision.ApplyToAll)
                {
                    _remembered[kind] = choice;
                }

                return Resolve(choice, source, destination);
            }

            /// <summary>
            /// "Replace if newer" decided per file: the incoming file replaces the existing one only when it was modified
            /// later (to the second: file systems keep times at different precisions), otherwise it is skipped.
            /// </summary>
            private static ConflictChoice Resolve(ConflictChoice choice, string source, string destination)
            {
                if (choice != ConflictChoice.ReplaceIfNewer)
                {
                    return choice;
                }

                DateTime incoming = SafeModifiedUtc(source), existing = SafeModifiedUtc(destination);
                return incoming > existing.AddSeconds(1) ? ConflictChoice.Replace : ConflictChoice.Skip;
            }

            private static DateTime SafeModifiedUtc(string path)
            {
                try
                {
                    return File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return DateTime.MinValue;
                }
            }

            /// <summary>Transfers an item to a path where nothing is.</summary>
            private void TransferNew(string source, string destination, bool isDirectory, bool record)
            {
                _context.Pause?.WaitWhilePaused(_token);
                _token.ThrowIfCancellationRequested();

                if (IsLink(source))
                {
                    TransferLink(source, destination, record);
                    return;
                }

                if (!isDirectory)
                {
                    if (!IsMove)
                    {
                        string copied = CopyFileAtomically(source, destination);
                        Record(record, new CreatedAction(copied));
                    }
                    else if (MoveFile(source, destination))
                    {
                        Record(record, new MovedAction(source, destination));
                    }
                    else
                    {
                        Record(record, new CreatedAction(destination)); // partial: the source could not be removed
                    }

                    return;
                }

                if (IsMove && IsSameVolume(source, destination))
                {
                    try
                    {
                        // A rename: instant whatever the folder's size.
                        _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(source, destination, writeThrough: false), _token);
                        Record(record, new MovedAction(source, destination));
                        return;
                    }
                    catch (Exception ex) when (NativeFileOperations.IsCrossDeviceError(ex))
                    {
                        // Another volume mounted inside this one: copy and delete instead.
                    }
                }

                // The folder's date and (Windows) Hidden/System marks, read before its contents leave it; set on the new folder
                // after its contents are in, since writing them changes its date.
                (DateTime? written, FileAttributes marks) = FolderMetadata(source);
                Directory.CreateDirectory(destination);
                Record(record, IsMove ? new MovedAction(source, destination) : new CreatedAction(destination));

                // Inside a folder this operation created, nothing clashes and the top entry undoes it all.
                MergeContents(source, destination, record: false, depth: 1);
                ApplyFolderMetadata(destination, written, marks);
            }

            private static (DateTime? Written, FileAttributes Marks) FolderMetadata(string folder)
            {
                try
                {
                    var info = new DirectoryInfo(folder);
                    FileAttributes marks = OperatingSystem.IsWindows() ? info.Attributes & (FileAttributes.Hidden | FileAttributes.System) : 0;
                    return (info.LastWriteTimeUtc, marks);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return (null, 0);
                }
            }

            /// <summary>Best effort: a file system that refuses folder dates (some network shares) is not a failure.</summary>
            private void ApplyFolderMetadata(string folder, DateTime? written, FileAttributes marks)
            {
                try
                {
                    if (written is DateTime when && when.Year > 1980)
                    {
                        Directory.SetLastWriteTimeUtc(folder, when);
                    }

                    if (marks != 0)
                    {
                        File.SetAttributes(folder, File.GetAttributes(folder) | marks);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _owner._logger.Debug(ex, "Could not keep the date of folder {Folder}", folder);
                }
            }

            /// <summary>Folder onto an existing folder: its entries go in one by one, clashes asked about.</summary>
            private void MergeFolder(string source, string destination, bool record)
            {
                MergeContents(source, destination, record, depth: 1);
            }

            /// <summary>
            /// Transfers every entry of <paramref name="sourceFolder"/> into <paramref name="destinationFolder"/>. For a move,
            /// the source folder is removed if it ended up empty (skipped or failed entries keep it). Failures are recorded
            /// per entry and the rest continue.
            /// </summary>
            private void MergeContents(string sourceFolder, string destinationFolder, bool record, int depth)
            {
                if (depth > MaxTreeDepth)
                {
                    throw new IOException($"\"{sourceFolder}\" is nested more than {MaxTreeDepth} levels deep (possibly a link loop).");
                }

                // Listed up front: the folder changes while its entries are moved out of it.
                var entries = new List<DirectoryIndexEntry>(NativeFileEnumerator.Enumerate(sourceFolder, _token));
                bool everythingLeft = true;
                foreach (DirectoryIndexEntry entry in entries)
                {
                    _token.ThrowIfCancellationRequested();
                    string source = Path.Combine(sourceFolder, entry.Name);
                    string destination = Path.Combine(destinationFolder, entry.Name);
                    bool isDirectory = entry.IsDirectory && !IsLinkEntry(entry, sourceFolder);
                    bool succeeded = Attempt(source, destinationFolder, () =>
                    {
                        if (isDirectory && Directory.Exists(destination) && !IsLink(destination))
                        {
                            // Folders inside a merge merge silently, as in Explorer; the files in them are asked about.
                            MergeContents(source, destination, record, depth + 1);
                            if (IsMove && Directory.Exists(source))
                            {
                                everythingLeft = false;
                            }
                        }
                        else if (!TransferInto(source, destination, isDirectory, record))
                        {
                            everythingLeft = false;
                        }
                    });
                    if (!succeeded)
                    {
                        everythingLeft = false;
                    }
                }

                if (IsMove && everythingLeft)
                {
                    try
                    {
                        _owner.RetryWithBackoff(() => Directory.Delete(sourceFolder, recursive: false), _token);
                        Record(record, new RemovedFolderAction(sourceFolder));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Something appeared in it meanwhile, or it is in use: the moved content is safe; leave the folder.
                        _owner._logger.Debug(ex, "Could not remove the emptied folder {Path}", sourceFolder);
                    }
                }
            }

            /// <summary>
            /// Replaces an existing file without ever being without one: the new content goes to a hidden temporary
            /// sibling first, then the old file goes to the trash, then the temporary file takes the name. If that last
            /// rename fails, the old file is put back.
            /// </summary>
            private void ReplaceFile(string source, string destination, bool record)
            {
                _context.Pause?.WaitWhilePaused(_token);
                if (IsLink(source))
                {
                    ReplaceWithLink(source, destination, record);
                    return;
                }

                if (IsMove && IsSameVolume(source, destination))
                {
                    ReplaceByRename(source, destination, record);
                    return;
                }

                // The new content goes to a hidden sibling first (the source stays untouched), checked, and for a move put
                // on the disk before anything else changes.
                string temporary = NewTemporary(destination);
                try
                {
                    // Always on the disk before the old item is touched: after a crash past this point the recovery puts this
                    // copy in place, and it must be whole.
                    CopyFileBytes(source, temporary, flushToDisk: true);
                    FileVerification.Verify(_verification, source, temporary, SafeLength(source), _token);
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    TemporaryDone(temporary);
                    throw;
                }

                TrashedItem replaced;
                try
                {
                    replaced = _owner.TrashForReplace(destination);
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    TemporaryDone(temporary);
                    throw;
                }

                _journal?.Replacing(destination, replaced, temporary);
                try
                {
                    _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(temporary, destination, writeThrough: true), _token);
                    TemporaryDone(temporary);
                    _journal?.Replaced(destination);
                }
                catch
                {
                    if (replaced != null)
                    {
                        try
                        {
                            Trash.Restore(replaced);
                        }
                        catch (Exception restoreEx)
                        {
                            _owner._logger.Error(restoreEx, "Could not put back {Path} after a failed replace", destination);
                        }
                    }

                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    TemporaryDone(temporary);
                    _journal?.Replaced(destination);
                    throw;
                }

                if (replaced != null)
                {
                    Record(record, new TrashedAction(replaced));
                }

                if (!IsMove)
                {
                    Record(record, new CreatedAction(destination));
                    return;
                }

                NativeFileOperations.SyncDirectory(Path.GetDirectoryName(destination)); // the new name is on the disk
                if (DeleteMovedSource(source, destination))
                {
                    Record(record, new MovedAction(source, destination));
                }
                else
                {
                    Record(record, new CreatedAction(destination));
                }
            }

            /// <summary>
            /// A symbolic link or junction: a same-volume move renames the link itself; otherwise the link is recreated at
            /// the destination pointing where it pointed (relative targets stay relative), and a move then removes the old
            /// link. Never follows it: copying or moving a folder that holds a link to ~/Photos must not touch ~/Photos.
            /// </summary>
            private void TransferLink(string source, string destination, bool record)
            {
                if (IsMove && IsSameVolume(source, destination))
                {
                    try
                    {
                        _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(source, destination, writeThrough: true), _token);
                        _progress.CompleteRename();
                        Record(record, new MovedAction(source, destination));
                        return;
                    }
                    catch (Exception ex) when (NativeFileOperations.IsCrossDeviceError(ex))
                    {
                        // Another volume mounted inside this one: recreate and remove instead.
                    }
                }

                NativeFileOperations.CopyLink(source, destination);
                if (!IsMove)
                {
                    Record(record, new CreatedAction(destination));
                    return;
                }

                try
                {
                    _owner.RetryWithBackoff(() => NativeFileOperations.DeleteLink(source), _token);
                    Record(record, new MovedAction(source, destination));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _owner._logger.Warning(ex, "Moved the link {Source} but could not remove the original", source);
                    _partialMoves.Add(new FileOperationFailure(source, $"Moved to \"{destination}\", but the original link couldn't be removed."));
                    Record(record, new CreatedAction(destination));
                }
            }

            /// <summary>Replace by a link: the old item goes to the trash, the link takes its place (the old one comes back on failure).</summary>
            private void ReplaceWithLink(string source, string destination, bool record)
            {
                TrashedItem replaced = _owner.TrashForReplace(destination);
                _journal?.Replacing(destination, replaced, null);
                try
                {
                    TransferLink(source, destination, record);
                    _journal?.Replaced(destination);
                }
                catch
                {
                    if (replaced != null)
                    {
                        try
                        {
                            Trash.Restore(replaced);
                        }
                        catch (Exception restoreEx)
                        {
                            _owner._logger.Error(restoreEx, "Could not put back {Path} after a failed replace", destination);
                        }
                    }

                    _journal?.Replaced(destination);
                    throw;
                }

                if (replaced != null)
                {
                    Record(record, new TrashedAction(replaced));
                }
            }

            /// <summary>
            /// Same-volume Replace by a move: the old file goes to the trash, then the source is renamed onto its name (one
            /// atomic rename, no copy). At no point is the source anywhere but at its own name or the destination: a crash
            /// in between leaves the old file in the trash and the source where it was. A failed rename puts the old back.
            /// </summary>
            private void ReplaceByRename(string source, string destination, bool record)
            {
                TrashedItem replaced = _owner.TrashForReplace(destination);
                _journal?.Replacing(destination, replaced, null);
                try
                {
                    _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(source, destination, writeThrough: true), _token);
                    _progress.CompleteRename();
                    _journal?.Replaced(destination);
                }
                catch
                {
                    if (replaced != null)
                    {
                        try
                        {
                            Trash.Restore(replaced);
                        }
                        catch (Exception restoreEx)
                        {
                            _owner._logger.Error(restoreEx, "Could not put back {Path} after a failed replace", destination);
                        }
                    }

                    _journal?.Replaced(destination);
                    throw;
                }

                if (replaced != null)
                {
                    Record(record, new TrashedAction(replaced));
                }

                Record(record, new MovedAction(source, destination));
            }

            /// <summary>
            /// A copy to a free name, committed only when complete: the bytes go to a hidden sibling, are checked (the
            /// verification setting), and the sibling is renamed to the name. A failure, a cancellation or a crash never
            /// leaves a truncated file under the real name. If something took the name meanwhile, the copy is kept under a
            /// free name beside it. Returns where the copy ended up.
            /// </summary>
            private string CopyFileAtomically(string source, string destination)
            {
                string temporary = NewTemporary(destination);
                try
                {
                    CopyFileBytes(source, temporary, flushToDisk: _verification == VerificationMode.Sha256);
                    FileVerification.Verify(_verification, source, temporary, SafeLength(source), _token);
                    return CommitTemporary(temporary, destination);
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    throw;
                }
                finally
                {
                    TemporaryDone(temporary);
                }
            }

            /// <summary>Renames a finished temporary file to its name (never over anything); a name taken meanwhile gets a free one.</summary>
            private string CommitTemporary(string temporary, string destination)
            {
                try
                {
                    _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(temporary, destination, writeThrough: true), _token);
                    return destination;
                }
                catch (Exception) when (Exists(destination) && !_token.IsCancellationRequested)
                {
                    string free = FreeName(destination);
                    _owner._logger.Information("{Destination} appeared during the copy; keeping the copy as {Free}", destination, free);
                    _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(temporary, free, writeThrough: true), _token);
                    return free;
                }
            }

            /// <summary>
            /// Moves one file to a free path. Same volume: a rename. Across volumes: copy to a temporary sibling, verify,
            /// commit, then delete the source. False when everything worked but the source could not be removed.
            /// </summary>
            private bool MoveFile(string source, string destination)
            {
                if (IsSameVolume(source, destination))
                {
                    try
                    {
                        _owner.RetryWithBackoff(() => NativeFileOperations.MoveFile(source, destination, writeThrough: true), _token);
                        _progress.CompleteRename();
                        return true;
                    }
                    catch (Exception ex) when (NativeFileOperations.IsCrossDeviceError(ex))
                    {
                        // A volume mounted inside this one: fall through to copy and delete.
                    }
                }

                // Copied to a hidden sibling, checked, put on the disk, renamed to its name, and the rename put on the disk:
                // only then is the source deleted, so a crash or power cut at any point leaves at least one whole copy.
                string temporary = NewTemporary(destination);
                try
                {
                    CopyFileBytes(source, temporary, flushToDisk: true);
                    FileVerification.Verify(_verification, source, temporary, SafeLength(source), _token);
                    destination = CommitTemporary(temporary, destination);
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    throw;
                }
                finally
                {
                    TemporaryDone(temporary);
                }

                NativeFileOperations.SyncDirectory(Path.GetDirectoryName(destination));
                return DeleteMovedSource(source, destination);
            }

            /// <summary>
            /// After a verified cross-volume copy: removes the source permanently (it was relocated, not deleted, so no
            /// trash entry). A failure leaves both copies and is reported as a partial move.
            /// </summary>
            private bool DeleteMovedSource(string source, string destination)
            {
                try
                {
                    _owner.RetryWithBackoff(() => NativeFileOperations.DeleteFilePermanently(source), _token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _owner._logger.Warning(ex, "Moved {Source} but could not remove the original", source);
                    _partialMoves.Add(new FileOperationFailure(source,
                        $"Moved to \"{destination}\", but the original couldn't be removed (it may be open in another program). " +
                        "Both copies exist; delete the original once it's free."));
                    return false;
                }
            }

            /// <summary>Copies the bytes with progress and pause; a retry first removes what the failed attempt left.</summary>
            private void CopyFileBytes(string source, string destination, bool flushToDisk)
            {
                long size = SafeLength(source);
                long before = _progress.CompletedBytes;
                bool attempted = false;
                try
                {
                _owner.RetryWithBackoff(() =>
                {
                    if (attempted)
                    {
                        NativeFileOperations.TryDeleteTemporaryFile(destination);
                        _progress.SetCompletedBytes(before);
                    }

                    attempted = true;
                    NativeFileOperations.CopyFile(source, destination, (transferred, _) =>
                    {
                        _context.Pause?.WaitWhilePaused(_token);
                        _progress.SetCompletedBytes(before + transferred);
                        AfterBufferForTests?.Invoke(destination, transferred);
                    }, _token, size >= NativeFileOperations.UnbufferedCopyThreshold, flushToDisk);
                }, _token);
                }
                catch
                {
                    _progress.SetCompletedBytes(before); // a failed attempt's bytes don't count (a retry adds them again)
                    throw;
                }

                _progress.SetCompletedBytes(before + size);
            }

            /// <summary>Undo of a move: the item goes back to where nothing is now.</summary>
            public void MoveBack(string from, string to, bool isDirectory) => TransferNew(from, to, isDirectory, record: false);

            /// <summary>
            /// Undo of a move whose old place is occupied again (a folder only partly moved, or something new there):
            /// a folder merges back; anything else comes back beside it under a free name. Clashes keep both.
            /// </summary>
            public void MergeBack(string from, string to, bool isDirectory)
            {
                if (isDirectory && Directory.Exists(to))
                {
                    MergeContents(from, to, record: false, depth: 1);
                }
                else
                {
                    TransferNew(from, FreeName(to), isDirectory, record: false);
                }
            }

            public void ThrowIfFailed()
            {
                if (_failures.Count > 0)
                {
                    string more = _failures.Count > 1 ? $" (and {_failures.Count - 1:N0} more)" : string.Empty;
                    throw new IOException(_failures[0].Message + more);
                }
            }

            private void Record(bool record, UndoAction action)
            {
                if (record)
                {
                    _undo.Add(action);
                }
            }
        }

        // ================================================================== delete

        public Task<FileOperationOutcome> DeleteAsync(IReadOnlyList<string> paths, bool permanent, FileOperationContext context)
        {
            if (paths == null || paths.Count == 0)
            {
                return Task.FromResult(new FileOperationOutcome(new FileOperationResult(FileOperationKind.Delete, 0, Array.Empty<FileOperationFailure>(), false), null));
            }

            context ??= new FileOperationContext();
            if (paths.Any(Platform.Phones.Phones.IsPhonePath))
            {
                return Task.Run(() => DeletePhoneItemsCore(paths, permanent, context));
            }

            return Task.Run(() => DeleteCore(paths, permanent, context));
        }

        private FileOperationOutcome DeleteCore(IReadOnlyList<string> paths, bool permanent, FileOperationContext context)
        {
            CancellationToken token = context.CancellationToken;
            var progress = new ProgressTracker(context.Progress);
            progress.SetTotals(0, paths.Count);
            progress.SetPhase(FileOperationPhase.Working);
            var failures = new List<FileOperationFailure>();
            var undo = new List<UndoAction>();
            var affected = new HashSet<string>(PathUtil.Comparer);
            int successCount = 0;
            bool cancelled = false;

            _logger.Information("Delete starting: {Count} item(s), permanent={Permanent}", paths.Count, permanent);

            try
            {
                if (!permanent && OperatingSystem.IsWindows())
                {
                    successCount = RecycleOnWindows(paths, context, progress, failures, undo);
                }
                else
                {
                    bool skipAll = false;
                    foreach (string path in paths)
                    {
                        context.Pause?.WaitWhilePaused(token);
                        token.ThrowIfCancellationRequested();
                        progress.SetCurrent(PathUtil.GetName(path));
                        try
                        {
                            while (true)
                            {
                                try
                                {
                                    if (!Exists(path))
                                    {
                                        successCount++; // already gone
                                    }
                                    else if (permanent)
                                    {
                                        DeletePermanently(path, token);
                                        successCount++;
                                    }
                                    else
                                    {
                                        TrashedItem trashed = null;
                                        RetryWithBackoff(() => trashed = Trash.MoveToTrash(path), token);
                                        undo.Add(new TrashedAction(trashed));
                                        successCount++;
                                    }

                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    ErrorChoice choice = AskAboutError(context, progress, FileOperationKind.Delete, path, ex, ref skipAll);
                                    if (choice == ErrorChoice.Retry)
                                    {
                                        continue;
                                    }

                                    if (choice == ErrorChoice.Cancel)
                                    {
                                        throw new OperationCanceledException(token);
                                    }

                                    _logger.Warning(ex, "Delete failed for {Path}", path);
                                    failures.Add(new FileOperationFailure(path, ex.Message));
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            progress.CompleteTopLevelItem();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            foreach (string path in paths)
            {
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    affected.Add(parent);
                }
            }

            _logger.Information("Delete finished: {Success} succeeded, {Failed} failed, cancelled={Cancelled}", successCount, failures.Count, cancelled);
            var result = new FileOperationResult(FileOperationKind.Delete, successCount, failures, cancelled);
            FileOperationRecord record = undo.Count > 0
                ? new FileOperationRecord($"Delete {Describe(paths)}", undo.ToArray(), new List<string>(affected))
                : null;
            return new FileOperationOutcome(result, record);
        }

        /// <summary>
        /// Windows: recycling goes to the shell in batches (one SHFileOperation per item made a large selection take
        /// seconds); whatever a batch left is retried one by one, which pinpoints the failures. Each batch's items are
        /// then looked up in the Recycle Bin, so the delete can be undone.
        /// </summary>
        private int RecycleOnWindows(IReadOnlyList<string> paths, FileOperationContext context, ProgressTracker progress, List<FileOperationFailure> failures, List<UndoAction> undo)
        {
            CancellationToken token = context.CancellationToken;
            int successCount = 0;
            bool skipAllRecycle = false;
            for (int start = 0; start < paths.Count; start += DeleteBatchSize)
            {
                context.Pause?.WaitWhilePaused(token);
                token.ThrowIfCancellationRequested();
                int end = Math.Min(paths.Count, start + DeleteBatchSize);
                var batch = new List<string>(end - start);
                var single = new List<string>();
                for (int i = start; i < end; i++)
                {
                    string path = Path.GetFullPath(paths[i]);
                    if (!Exists(path))
                    {
                        successCount++;
                        progress.CompleteTopLevelItem();
                        continue;
                    }

                    (path.Length >= MaxShellPath ? single : batch).Add(path);
                }

                DateTime startedUtc = DateTime.UtcNow;
                var recycled = new List<string>();
                if (batch.Count > 0)
                {
                    progress.SetCurrent(batch.Count == 1 ? PathUtil.GetName(batch[0]) : $"{batch.Count:N0} items");
                    try
                    {
                        NativeFileOperations.TryRecycle(batch);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Batched recycle failed; retrying the items one by one");
                    }

                    foreach (string path in batch)
                    {
                        if (Exists(path))
                        {
                            single.Add(path);
                        }
                        else
                        {
                            recycled.Add(path);
                            successCount++;
                            progress.CompleteTopLevelItem();
                        }
                    }
                }

                foreach (string path in single)
                {
                    token.ThrowIfCancellationRequested();
                    progress.SetCurrent(PathUtil.GetName(path));
                    try
                    {
                        while (true)
                        {
                            try
                            {
                                RetryWithBackoff(() =>
                                {
                                    if (!NativeFileOperations.TryRecycle(new[] { path }) && Exists(path))
                                    {
                                        throw new IOException($"\"{PathUtil.GetName(path)}\" couldn't be moved to the Recycle Bin.");
                                    }
                                }, token);
                                recycled.Add(path);
                                successCount++;
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                ErrorChoice choice = AskAboutError(context, progress, FileOperationKind.Delete, path, ex, ref skipAllRecycle);
                                if (choice == ErrorChoice.Retry)
                                {
                                    continue;
                                }

                                if (choice == ErrorChoice.Cancel)
                                {
                                    throw new OperationCanceledException(token);
                                }

                                _logger.Warning(ex, "Recycle failed for {Path}", path);
                                failures.Add(new FileOperationFailure(path, ex.Message));
                                break;
                            }
                        }
                    }
                    finally
                    {
                        progress.CompleteTopLevelItem();
                    }
                }

                if (recycled.Count > 0 && OperatingSystem.IsWindows())
                {
                    Dictionary<string, TrashedItem> found = RecycleBin.FindRecycled(recycled, startedUtc);
                    foreach (string path in recycled)
                    {
                        if (found.TryGetValue(path, out TrashedItem item))
                        {
                            undo.Add(new TrashedAction(item));
                        }
                        else
                        {
                            _logger.Information("Recycled item not found in the Recycle Bin (can't be undone): {Path}", path);
                        }
                    }
                }
            }

            return successCount;
        }

        private void DeletePermanently(string path, CancellationToken token)
        {
            bool isLink = IsLink(path);
            bool isDirectory = Directory.Exists(path) && !isLink;
            RetryWithBackoff(() =>
            {
                if (isLink)
                {
                    NativeFileOperations.DeleteLink(path); // the link only, never what it points to
                }
                else if (isDirectory)
                {
                    NativeFileOperations.DeleteDirectoryTreePermanently(path, token);
                }
                else
                {
                    NativeFileOperations.DeleteFilePermanently(path);
                }
            }, token);
        }

        /// <summary>
        /// The existing file a Replace displaces: to the trash, so the replace can be undone. Where the volume has no
        /// trash (a removable drive on Windows deletes instead, some Linux mounts have none) it is deleted permanently,
        /// as an overwrite would; the replace then can't restore it on undo. Null in that case.
        /// </summary>
        private TrashedItem TrashForReplace(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string full = Path.GetFullPath(path);
                    DateTime startedUtc = DateTime.UtcNow;
                    if (full.Length < MaxShellPath && NativeFileOperations.TryRecycle(new[] { full }) && !Exists(full))
                    {
                        return RecycleBin.FindRecycled(new[] { full }, startedUtc).TryGetValue(full, out TrashedItem item) ? item : null;
                    }
                }
                else
                {
                    return Trash.MoveToTrash(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Information(ex, "No trash for the replaced file {Path}; deleting it", path);
            }

            if (Exists(path))
            {
                RetryWithBackoff(() => NativeFileOperations.DeleteFilePermanently(path), CancellationToken.None);
            }

            return null;
        }

        // ================================================================== undo

        public Task<FileOperationOutcome> UndoAsync(FileOperationRecord record, FileOperationContext context)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            context ??= new FileOperationContext();
            return Task.Run(() => UndoCore(record, context));
        }

        /// <summary>
        /// Reverses a record last to first. Each step checks the item is still where the operation left it; anything
        /// changed since (deleted, moved, its old place taken) is reported and the rest continue. A restored item whose
        /// place is taken comes back beside it under a free name.
        /// </summary>
        private FileOperationOutcome UndoCore(FileOperationRecord record, FileOperationContext context)
        {
            CancellationToken token = context.CancellationToken;
            var progress = new ProgressTracker(context.Progress);
            progress.SetTotals(0, record.Actions.Count);
            progress.SetPhase(FileOperationPhase.Working);
            var failures = new List<FileOperationFailure>();
            int successCount = 0;
            bool cancelled = false;
            int notReached = 0; // actions [0, notReached) were not attempted (cancelled before them)

            _logger.Information("Undo starting: {Description} ({Count} change(s))", record.Description, record.Actions.Count);
            try
            {
                for (int i = record.Actions.Count - 1; i >= 0; i--)
                {
                    notReached = i + 1;
                    context.Pause?.WaitWhilePaused(token);
                    token.ThrowIfCancellationRequested();
                    UndoAction action = record.Actions[i];
                    progress.SetCurrent(PathUtil.GetName(action.DisplayPath));
                    try
                    {
                        UndoOne(action, context);
                        successCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Undo step failed for {Path}", action.DisplayPath);
                        failures.Add(new FileOperationFailure(action.DisplayPath, ex.Message));
                    }
                    finally
                    {
                        progress.CompleteTopLevelItem();
                    }

                    notReached = i;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            _logger.Information("Undo finished: {Success} reversed, {Failed} failed, cancelled={Cancelled}", successCount, failures.Count, cancelled);

            // Cancelled part-way: the steps never reached are handed back as a record, so they can still be undone.
            FileOperationRecord rest = cancelled && notReached > 0
                ? new FileOperationRecord(record.Description, record.Actions.Take(notReached).ToArray(), record.AffectedFolders)
                : null;
            return new FileOperationOutcome(new FileOperationResult(FileOperationKind.Undo, successCount, failures, cancelled), rest);
        }

        private void UndoOne(UndoAction action, FileOperationContext context)
        {
            CancellationToken token = context.CancellationToken;
            switch (action)
            {
                case CreatedAction created:
                    if (!Exists(created.Path))
                    {
                        return; // already gone: nothing to undo
                    }

                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            string full = Path.GetFullPath(created.Path);
                            if (full.Length >= MaxShellPath || !NativeFileOperations.TryRecycle(new[] { full }) || Exists(full))
                            {
                                throw new IOException("The Recycle Bin did not take it.");
                            }
                        }
                        else
                        {
                            Trash.MoveToTrash(created.Path);
                        }
                    }
                    catch (IOException ex)
                    {
                        // No trash here: deleting for good is only safe for what is still exactly what the operation made
                        // (a file of the same size and time, an empty folder). Anything else, such as a new folder that
                        // has since been filled from another program, is left in place.
                        if (!IsUnchanged(created))
                        {
                            throw new IOException($"\"{PathUtil.GetName(created.Path)}\" couldn't go to the trash (this drive has none), and it isn't empty or has changed since; it was left in place so nothing is lost. Delete it yourself if you don't need it.", ex);
                        }

                        _logger.Information(ex, "No trash for {Path}; deleting the unchanged item", created.Path);
                        DeletePermanently(created.Path, token);
                    }

                    return;

                case MovedAction moved:
                    if (!Exists(moved.To))
                    {
                        throw new IOException($"\"{PathUtil.GetName(moved.To)}\" is no longer at \"{moved.To}\".");
                    }

                    if (string.Equals(moved.From, moved.To, StringComparison.OrdinalIgnoreCase) && !string.Equals(moved.From, moved.To, StringComparison.Ordinal))
                    {
                        // A change of letter case only: on a case-insensitive file system the old name "exists" (it is the
                        // same item), so rename back through a temporary name instead of keeping both.
                        string temporary = CreateTemporarySiblingPath(moved.From);
                        NativeFileOperations.MoveFile(moved.To, temporary, writeThrough: false);
                        NativeFileOperations.MoveFile(temporary, moved.From, writeThrough: false);
                        return;
                    }

                    string parent = Path.GetDirectoryName(moved.From);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    // Moving back is a move like any other (a rename, or copy-verify-delete across volumes); if the old
                    // place is taken (a folder only partly moved), the item is merged back, keeping both on a clash.
                    using (var back = new TransferRun(this, FileOperationKind.Move, new[] { moved.To }, parent ?? moved.From,
                        new FileOperationContext { Pause = context.Pause, CancellationToken = token }))
                    {
                        bool isDirectory = Directory.Exists(moved.To) && !IsLink(moved.To);
                        if (!Exists(moved.From))
                        {
                            back.MoveBack(moved.To, moved.From, isDirectory);
                        }
                        else
                        {
                            back.MergeBack(moved.To, moved.From, isDirectory);
                        }

                        back.ThrowIfFailed();
                    }

                    return;

                case TrashedAction trashed:
                    Trash.Restore(trashed.Item);
                    return;

                case RemovedFolderAction removed:
                    Directory.CreateDirectory(removed.Path);
                    return;
            }
        }

        // ================================================================== helpers

        /// <summary>
        /// The error question for a failed item, from the worker thread: Skip without asking when the setting says so,
        /// there is no one to ask, or the person chose Skip all. The job shows "waiting for your decision" meanwhile.
        /// </summary>
        private ErrorChoice AskAboutError(FileOperationContext context, ProgressTracker progress, FileOperationKind kind, string path, Exception error, ref bool skipAll)
        {
            bool ask = _settingsService?.Current?.FileList?.AskOnFileOperationErrors ?? true;
            if (skipAll || !ask || context.ErrorResolver == null)
            {
                return ErrorChoice.Skip;
            }

            FileOperationPhase previous = progress.Phase;
            progress.SetPhase(FileOperationPhase.WaitingForDecision);
            ErrorChoice choice;
            try
            {
                choice = context.ErrorResolver.ResolveAsync(new ErrorInfo(kind, path, error.Message), context.CancellationToken).GetAwaiter().GetResult();
            }
            finally
            {
                progress.SetPhase(previous);
            }

            if (choice == ErrorChoice.SkipAll)
            {
                skipAll = true;
                return ErrorChoice.Skip;
            }

            return choice;
        }

        /// <summary>Whether a created item is still what the operation made: the same file (size, time) or an empty folder.</summary>
        private static bool IsUnchanged(CreatedAction created)
        {
            try
            {
                if (Directory.Exists(created.Path) && !IsLink(created.Path))
                {
                    return !Directory.EnumerateFileSystemEntries(created.Path).Any();
                }

                var info = new FileInfo(created.Path);
                return created.Length >= 0 && info.Exists && info.Length == created.Length && info.LastWriteTimeUtc == created.LastWriteUtc;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void RetryWithBackoff(Action action, CancellationToken cancellationToken)
        {
            FileListSettings settings = _settingsService?.Current?.FileList;
            int maxRetries = settings?.MaxRetries ?? 3;
            int delayMs = settings?.InitialRetryDelayMs ?? 250;
            int maxDelayMs = settings?.MaxRetryDelayMs ?? 5000;

            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    action();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!NativeFileOperations.IsTransientError(ex) || attempt >= maxRetries)
                    {
                        throw;
                    }

                    _logger.Debug(ex, "Transient error, retrying (attempt {Attempt} of {MaxRetries})", attempt + 1, maxRetries);
                    cancellationToken.WaitHandle.WaitOne(delayMs);
                    delayMs = Math.Min(delayMs * 2, maxDelayMs);
                }
            }
        }

        /// <summary>Free bytes for the current user where <paramref name="folder"/> is, or -1 when it can't be told.</summary>
        internal static long FreeSpace(string folder)
        {
            if (FreeSpaceForTests != null)
            {
                return FreeSpaceForTests(folder);
            }

            try
            {
                string root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(folder)) : MountPoints.Of(folder);
                return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return -1;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} bytes" : $"{value:0.#} {units[unit]}";
        }

        /// <summary>The destination is full: the operation stops (what was done stays done).</summary>
        private sealed class DestinationFullException : IOException
        {
            public DestinationFullException(string message, Exception inner = null)
                : base(message, inner)
            {
            }

            public static DestinationFullException During(string folder, Exception inner) =>
                new DestinationFullException($"\"{PathUtil.GetName(folder)}\" ran out of space. The operation stopped; what was already done is complete, " +
                                             "and nothing was left half-written.", inner);
        }

        private static bool Exists(string path) =>
            File.Exists(path) || Directory.Exists(path) || IsLink(path);

        /// <summary>A symbolic link or junction (it has a link target), dangling or not; not a cloud placeholder.</summary>
        private static bool IsLink(string path) => NativeFileOperations.IsLink(path);

        /// <summary>An enumerated entry that is a link: only reparse points are checked further (the common case costs nothing).</summary>
        private static bool IsLinkEntry(DirectoryIndexEntry entry, string folder) =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(Path.Combine(folder, entry.Name));

        private static long SafeLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static DateTime SafeModified(string path)
        {
            try
            {
                return File.GetLastWriteTime(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return default;
            }
        }

        /// <summary>"report.pdf", or "3 items".</summary>
        private static string Describe(IReadOnlyList<string> paths) =>
            paths.Count == 1 ? $"\"{PathUtil.GetName(paths[0])}\"" : $"{paths.Count:N0} items";

        /// <summary>
        /// Explorer's name for a duplicate in the same folder: "report - Copy.pdf", then "report - Copy (2).pdf", …; a folder
        /// "Photos - Copy".
        /// </summary>
        internal static string DuplicateName(string path)
        {
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            bool isFolder = Directory.Exists(path) && !IsLink(path);
            string name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            string extension = isFolder ? string.Empty : Path.GetExtension(path);
            for (int counter = 1; counter < 10000; counter++)
            {
                string suffix = counter == 1 ? " - Copy" : $" - Copy ({counter})";
                string candidate = Path.Combine(folder, name + suffix + extension);
                if (!Exists(candidate))
                {
                    return candidate;
                }
            }

            return FreeName(path);
        }

        /// <summary>Explorer's "name (2).ext" convention for keeping both.</summary>
        private static string FreeName(string path) => Trash.FreeName(path);

        private static VerificationMode ParseVerificationMode(string value)
        {
            if (string.Equals(value, "Sha256", StringComparison.OrdinalIgnoreCase))
            {
                return VerificationMode.Sha256;
            }

            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
            {
                return VerificationMode.None;
            }

            return VerificationMode.Size; // default and fallback for an unrecognized value
        }

        /// <summary>
        /// A temporary file beside the final destination: the same folder is the same volume, so committing it is an
        /// atomic rename, never a second copy. Hidden (leading dot) and clearly named in case a crash leaves it behind.
        /// </summary>
        private static string CreateTemporarySiblingPath(string destinationPath)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = ".";
            }

            return Path.Combine(directory, $".{Path.GetFileName(destinationPath)}{OperationTemporaries.Marker}{Guid.NewGuid():N}{OperationTemporaries.Suffix}");
        }

        private static bool IsSameOrSubPath(string basePath, string candidatePath)
        {
            try
            {
                string normalizedBase = PathUtil.WithTrailingSeparator(Path.GetFullPath(basePath));
                string normalizedCandidate = PathUtil.WithTrailingSeparator(Path.GetFullPath(candidatePath));
                return normalizedCandidate.StartsWith(normalizedBase, PathUtil.Comparison);
            }
            catch (Exception)
            {
                // A malformed path is itself a red flag: refuse rather than risk it.
                return true;
            }
        }

        internal static bool IsSameVolume(string sourcePath, string destinationPath)
        {
            if (Platform.Phones.Phones.IsPhonePath(sourcePath) || Platform.Phones.Phones.IsPhonePath(destinationPath))
            {
                return false; // nothing on a phone is renamed across folders by the file system
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Every Unix path has the root "/": compare the mount points holding the two paths instead.
                    return string.Equals(MountPoints.Of(sourcePath), MountPoints.Of(destinationPath), StringComparison.Ordinal);
                }

                string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
                string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationPath));
                return string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Unknown: treat as cross-volume, the slower but rigorous (copy, verify, commit) path.
                return false;
            }
        }

        /// <summary>
        /// Progress state of one operation, reported at most every 100 ms (and at phase changes): a copy reports per
        /// buffer, far more often than anyone can read.
        /// </summary>
        private sealed class ProgressTracker
        {
            private const long ReportIntervalTicks = 100 * TimeSpan.TicksPerMillisecond;
            private readonly Action<FileOperationProgress> _sink;
            private long _totalBytes;
            private int _totalItems;
            private int _completedItems;
            private string _current;
            private long _lastReport;

            public ProgressTracker(Action<FileOperationProgress> sink) => _sink = sink;

            public FileOperationPhase Phase { get; private set; } = FileOperationPhase.Preparing;

            public long CompletedBytes { get; private set; }

            public void SetTotals(long totalBytes, int totalItems)
            {
                _totalBytes = totalBytes;
                _totalItems = totalItems;
                Report(force: true);
            }

            public void SetPhase(FileOperationPhase phase)
            {
                Phase = phase;
                Report(force: true);
            }

            public void SetCurrent(string name)
            {
                _current = name;
                Report(force: false);
            }

            public void SetCompletedBytes(long bytes)
            {
                CompletedBytes = Math.Max(0, bytes);
                Report(force: false);
            }

            /// <summary>A rename copies nothing; nothing to add to the bytes.</summary>
            public void CompleteRename() => Report(force: false);

            /// <summary>A skipped file's bytes count as done, so the percentage still reaches 100.</summary>
            public void Skip(long bytes, bool wasRename)
            {
                if (bytes > 0 && !wasRename)
                {
                    CompletedBytes += bytes;
                }

                Report(force: false);
            }

            public void CompleteTopLevelItem()
            {
                _completedItems++;
                Report(force: _completedItems >= _totalItems);
            }

            private void Report(bool force)
            {
                if (_sink == null)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                if (!force && now - _lastReport < ReportIntervalTicks)
                {
                    return;
                }

                _lastReport = now;
                _sink(new FileOperationProgress(Phase, _totalBytes, Math.Min(CompletedBytes, Math.Max(_totalBytes, CompletedBytes)),
                    _totalItems, Math.Min(_completedItems, _totalItems), _current));
            }
        }
    }
}
