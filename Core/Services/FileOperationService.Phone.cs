// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Services.FileOperations;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Copies, moves and deletes that involve a phone. Everything the person sees works as for any other copy: the
    /// queue, progress, pause, cancel, name clash questions (Replace, Replace if newer, Skip, Keep both, for all), the
    /// error question (Retry, Skip, Skip all, Cancel) and "Retry failed". What differs is what a phone can do:
    /// <list type="bullet">
    /// <item>Into the PC, files still arrive the safe way: a hidden temporary file (journaled for crash cleanup), its size
    /// checked against the phone's, then renamed to its name.</item>
    /// <item>Onto a phone, a new file is created in one transfer of its exact size and only exists once complete (MTP);
    /// a cancelled or failed upload leaves nothing. A Replace uploads the new file under a temporary name first, and
    /// only then deletes the old one and renames the new one: the old file is never deleted before its replacement is
    /// safely on the phone.</item>
    /// <item>A move deletes the source only after the copy is complete and its size checked.</item>
    /// <item>Phone to phone goes through a temporary file on the PC (a phone can't read and write at the same time).</item>
    /// <item>Deleting on a phone is permanent (phones have no recycle bin); the confirmation says so.</item>
    /// <item>Links on the PC are not followed and can't be put on a phone: they are reported as failed.</item>
    /// <item>Undo: copies from a phone into the PC are undoable (the copies go to the trash); operations that change a
    /// phone are not (nothing on a phone can be restored).</item>
    /// </list>
    /// </summary>
    public sealed partial class FileOperationService
    {
        /// <summary>Temporary files of phone transfers and of phone files opened in an application.</summary>
        public static string PhoneTempRoot => PhoneTemp.Root;

        private const int PhoneBufferSize = 256 * 1024;

        private static bool InvolvesPhone(IReadOnlyList<string> sources, string destinationFolder) =>
            Phones.IsPhonePath(destinationFolder) || sources.Any(Phones.IsPhonePath);

        /// <summary>Deletes on a phone (permanent, whatever was asked) and anywhere else as asked.</summary>
        private FileOperationOutcome DeletePhoneItemsCore(IReadOnlyList<string> paths, bool permanent, FileOperationContext context)
        {
            var phonePaths = paths.Where(Phones.IsPhonePath).ToList();
            var otherPaths = paths.Where(p => !Phones.IsPhonePath(p)).ToList();
            FileOperationOutcome others = otherPaths.Count > 0 ? DeleteCore(otherPaths, permanent, context) : null;
            if (others?.Result.WasCancelled == true)
            {
                return others;
            }

            CancellationToken token = context.CancellationToken;
            var progress = new ProgressTracker(context.Progress);
            progress.SetTotals(0, phonePaths.Count);
            progress.SetPhase(FileOperationPhase.Working);
            var failures = new List<FileOperationFailure>(others?.Result.Failures ?? Array.Empty<FileOperationFailure>());
            int successCount = others?.Result.SuccessCount ?? 0;
            bool cancelled = false;
            bool skipAll = false;
            _logger.Information("Phone delete starting: {Count} item(s)", phonePaths.Count);

            try
            {
                foreach (string path in phonePaths)
                {
                    token.ThrowIfCancellationRequested();
                    context.Pause?.WaitWhilePaused(token);
                    progress.SetCurrent(PathUtil.GetName(path));
                    while (true)
                    {
                        try
                        {
                            Phones.Require(path).Delete(path);
                            successCount++;
                            break;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
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

                            _logger.Warning(ex, "Deleting {Path} from the phone failed", path);
                            failures.Add(new FileOperationFailure(path, ex.Message));
                            break;
                        }
                    }

                    progress.CompleteTopLevelItem();
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            progress.SetPhase(FileOperationPhase.Finishing);
            // Nothing deleted from a phone can be restored, so only the other items' undo steps (if any) are kept.
            return new FileOperationOutcome(new FileOperationResult(FileOperationKind.Delete, successCount, failures, cancelled), others?.Record);
        }

        /// <summary>One copy or move with a phone at one end or both (see the class remarks).</summary>
        private sealed class PhoneTransferRun : IDisposable
        {
            private readonly FileOperationService _owner;
            private readonly FileOperationKind _kind;
            private readonly IReadOnlyList<string> _sources;
            private readonly string _destinationFolder;
            private readonly FileOperationContext _context;
            private readonly CancellationToken _token;
            private readonly ProgressTracker _progress;
            private readonly OperationJournal _journal;
            private readonly List<FileOperationFailure> _failures = new List<FileOperationFailure>();
            private readonly List<UndoAction> _undo = new List<UndoAction>();
            private readonly HashSet<string> _affected = new HashSet<string>(PathUtil.Comparer);
            private readonly Dictionary<ConflictKind, ConflictChoice> _remembered = new Dictionary<ConflictKind, ConflictChoice>();
            private readonly byte[] _buffer = new byte[PhoneBufferSize];
            private bool _skipAllErrors;
            private int _skipped;
            private long _doneBytes;

            public PhoneTransferRun(FileOperationService owner, FileOperationKind kind, IReadOnlyList<string> sources, string destinationFolder, FileOperationContext context)
            {
                _owner = owner;
                _kind = kind;
                _sources = sources;
                _destinationFolder = PathUtil.TrimTrailingSeparator(destinationFolder);
                _context = context;
                _token = context.CancellationToken;
                _progress = new ProgressTracker(context.Progress);
                if (!Phones.IsPhonePath(_destinationFolder) && owner.JournalFolder != null)
                {
                    // Files arriving on the PC go through temporary files: journaled, so a crash's leftovers are removed.
                    _journal = OperationJournal.Start(owner.JournalFolder, kind, sources, destinationFolder);
                }
            }

            private bool IsMove => _kind == FileOperationKind.Move;
            private string Verb => IsMove ? "Move" : "Copy";

            /// <summary>Undo is offered only for copies into the PC: everything else changed a phone.</summary>
            private bool Undoable => !IsMove && !Phones.IsPhonePath(_destinationFolder);

            public void Dispose() => _journal?.Dispose();

            public FileOperationOutcome Execute()
            {
                int successCount = 0;
                bool cancelled = false;
                _owner._logger.Information("Phone {Verb} starting: {Count} item(s) to {Destination}", Verb, _sources.Count, _destinationFolder);
                try
                {
                    _progress.SetPhase(FileOperationPhase.Preparing);
                    _progress.SetTotals(ScanTotalBytes(), _sources.Count);
                    _progress.SetPhase(FileOperationPhase.Working);
                    _affected.Add(_destinationFolder);

                    foreach (string source in _sources)
                    {
                        _token.ThrowIfCancellationRequested();
                        _context.Pause?.WaitWhilePaused(_token);
                        bool done = false;
                        Attempt(source, _destinationFolder, () => done = TransferTopLevel(source));
                        if (done)
                        {
                            successCount++;
                            string parent = PathUtil.GetParent(source);
                            if (IsMove && parent != null)
                            {
                                _affected.Add(parent);
                            }
                        }

                        _progress.CompleteTopLevelItem();
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                _progress.SetPhase(FileOperationPhase.Finishing);
                _owner._logger.Information("Phone {Verb} finished: {Success} done, {Failed} failed, {Skipped} skipped, cancelled={Cancelled}",
                    Verb, successCount, _failures.Count, _skipped, cancelled);

                string description = $"{Verb} {Describe(_sources)} to {PathUtil.GetName(_destinationFolder)}";
                FileOperationRecord record = Undoable && _undo.Count > 0 ? new FileOperationRecord(description, _undo.ToArray(), _affected.ToList()) : null;
                return new FileOperationOutcome(new FileOperationResult(_kind, successCount, _failures, cancelled, skippedCount: _skipped), record);
            }

            /// <summary>Runs a step; a failure is asked about (Retry, Skip, Skip all, Cancel) like any other copy's.</summary>
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
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                    {
                        switch (_owner.AskAboutError(_context, _progress, _kind, path, ex, ref _skipAllErrors))
                        {
                            case ErrorChoice.Retry:
                                _owner._logger.Information("Phone {Verb}: retrying {Path} after: {Message}", Verb, path, ex.Message);
                                continue;
                            case ErrorChoice.Cancel:
                                throw new OperationCanceledException(_token);
                            default:
                                _owner._logger.Warning(ex, "Phone {Verb} failed for {Path}", Verb, path);
                                _failures.Add(new FileOperationFailure(path, ex.Message) { RetryDestination = destinationFolder });
                                return false;
                        }
                    }
                }
            }

            // ------------------------------------------------------------------ the walk

            private bool TransferTopLevel(string source)
            {
                ItemStat stat = Stat(source);
                if (!stat.Exists)
                {
                    throw new FileNotFoundException($"\"{PathUtil.GetName(source)}\" no longer exists.", source);
                }

                if (stat.IsLink)
                {
                    throw new IOException($"\"{PathUtil.GetName(source)}\" is a link; links can't be copied to or from a phone.");
                }

                if (stat.IsDirectory && PathUtil.IsSameOrInside(_destinationFolder, source))
                {
                    throw new IOException("Can't copy or move a folder into itself or one of its own subfolders.");
                }

                string name = PathUtil.GetName(source);
                string destination = PathUtil.Combine(_destinationFolder, name);
                _progress.SetCurrent(name);
                if (PathUtil.AreSame(source, destination))
                {
                    if (IsMove)
                    {
                        return true; // already there
                    }

                    string duplicate = DuplicateNameAt(destination, stat.IsDirectory);
                    TransferNew(source, duplicate, stat, topLevel: true);
                    return true;
                }

                return TransferInto(source, destination, stat, topLevel: true);
            }

            /// <summary>Transfers to <paramref name="destination"/>, resolving a clash there; false when skipped.</summary>
            private bool TransferInto(string source, string destination, ItemStat sourceStat, bool topLevel)
            {
                ItemStat existing = Stat(destination);
                if (!existing.Exists)
                {
                    TransferNew(source, destination, sourceStat, topLevel);
                    return true;
                }

                ConflictKind kind = sourceStat.IsDirectory && existing.IsDirectory
                    ? ConflictKind.FolderOverFolder
                    : !sourceStat.IsDirectory && !existing.IsDirectory ? ConflictKind.FileOverFile : ConflictKind.TypeMismatch;
                switch (Decide(kind, source, destination, sourceStat, existing))
                {
                    case ConflictChoice.Skip:
                        _skipped++;
                        Count(sourceStat.IsDirectory ? 0 : sourceStat.Size);
                        return false;
                    case ConflictChoice.KeepBoth:
                        TransferNew(source, FreeNameAt(destination), sourceStat, topLevel);
                        return true;
                    default:
                        if (kind == ConflictKind.FolderOverFolder)
                        {
                            MergeFolder(source, destination);
                        }
                        else
                        {
                            ReplaceFile(source, destination, sourceStat, existing);
                        }

                        return true;
                }
            }

            private void TransferNew(string source, string destination, ItemStat stat, bool topLevel)
            {
                if (stat.IsDirectory)
                {
                    string created = CreateFolder(PathUtil.GetParent(destination), PathUtil.GetName(destination));
                    if (topLevel && Undoable)
                    {
                        _undo.Add(new CreatedAction(created));
                    }

                    CopyFolderContents(source, created);
                    if (IsMove)
                    {
                        RemoveEmptySourceFolder(source);
                    }

                    return;
                }

                string written = WriteFile(source, PathUtil.GetParent(destination), PathUtil.GetName(destination), stat);
                if (topLevel && Undoable)
                {
                    _undo.Add(new CreatedAction(written));
                }

                if (IsMove)
                {
                    DeleteSource(source, isDirectory: false);
                }
            }

            /// <summary>Everything inside a folder this operation just created: no clashes possible, each item its own step.</summary>
            private void CopyFolderContents(string sourceFolder, string destinationFolder)
            {
                foreach (ChildItem child in ListChildren(sourceFolder))
                {
                    _token.ThrowIfCancellationRequested();
                    _context.Pause?.WaitWhilePaused(_token);
                    string childSource = PathUtil.Combine(sourceFolder, child.Name);
                    Attempt(childSource, destinationFolder, () =>
                    {
                        ItemStat stat = Stat(childSource);
                        if (stat.IsLink)
                        {
                            throw new IOException($"\"{child.Name}\" is a link; links can't be copied to or from a phone.");
                        }

                        if (stat.Exists)
                        {
                            TransferInto(childSource, PathUtil.Combine(destinationFolder, child.Name), stat, topLevel: false);
                        }
                    });
                }
            }

            /// <summary>Folder onto folder, "Merge": each item goes in, clashes inside asked about one by one.</summary>
            private void MergeFolder(string sourceFolder, string destinationFolder)
            {
                CopyFolderContents(sourceFolder, destinationFolder);
                if (IsMove)
                {
                    RemoveEmptySourceFolder(sourceFolder);
                }
            }

            /// <summary>
            /// A file over an existing file. The new file is complete at the destination (under a temporary name) before the
            /// old one is removed; on the PC the old one goes to the trash, as every Replace does.
            /// </summary>
            private void ReplaceFile(string source, string destination, ItemStat sourceStat, ItemStat existing)
            {
                string folder = PathUtil.GetParent(destination);
                string name = PathUtil.GetName(destination);
                string staged = WriteFile(source, folder, TemporaryName(name), sourceStat);
                if (!Phones.IsPhonePath(staged))
                {
                    _journal?.TemporaryCreated(staged); // a crash before it takes the old file's place removes it at the next start
                }

                try
                {
                    if (Phones.IsPhonePath(destination))
                    {
                        Phones.Require(destination).Delete(destination);
                        Phones.Require(staged).Rename(staged, name);
                    }
                    else
                    {
                        Trash.MoveToTrash(destination);
                        NativeFileOperations.MoveFile(staged, destination, writeThrough: true);
                        _journal?.TemporaryGone(staged);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException($"\"{name}\" was copied, but couldn't take the old file's place: the copy is kept as " +
                                          $"\"{PathUtil.GetName(staged)}\" beside it.\n\n{ex.Message}", ex);
                }

                if (IsMove)
                {
                    DeleteSource(source, isDirectory: false);
                }
            }

            // ------------------------------------------------------------------ one file

            /// <summary>
            /// Writes one file into <paramref name="folder"/> as <paramref name="name"/> and returns its path. Phone to
            /// phone goes through a temporary file on the PC.
            /// </summary>
            private string WriteFile(string source, string folder, string name, ItemStat stat)
            {
                bool fromPhone = Phones.IsPhonePath(source);
                bool toPhone = Phones.IsPhonePath(folder);
                if (fromPhone && toPhone)
                {
                    Directory.CreateDirectory(PhoneTempRoot);
                    string bridge = Path.Combine(PhoneTempRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                    long before = _doneBytes;
                    try
                    {
                        // The bytes cross twice: count each half as half the file.
                        DownloadToLocal(source, bridge, stat, progressScale: 0.5);
                        return Upload(bridge, folder, name, stat, progressScale: 0.5, progressBase: before + stat.Size / 2);
                    }
                    finally
                    {
                        NativeFileOperations.TryDeleteTemporaryFile(bridge);
                    }
                }

                if (toPhone)
                {
                    return Upload(source, folder, name, stat, progressScale: 1, progressBase: _doneBytes);
                }

                // Into the PC: a hidden temporary sibling, checked, then renamed to the name (a name taken meanwhile: kept both).
                string destination = Path.Combine(folder, name);
                string temporary = CreateTemporarySiblingPath(destination);
                _journal?.TemporaryCreated(temporary);
                try
                {
                    DownloadToLocal(source, temporary, stat, progressScale: 1);
                    string committed;
                    try
                    {
                        NativeFileOperations.MoveFile(temporary, destination, writeThrough: true);
                        committed = destination;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && (File.Exists(destination) || Directory.Exists(destination)))
                    {
                        committed = FreeName(destination);
                        NativeFileOperations.MoveFile(temporary, committed, writeThrough: true);
                    }

                    if (stat.ModifiedUtc != default)
                    {
                        TrySetModified(committed, stat.ModifiedUtc);
                    }

                    return committed;
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    throw;
                }
                finally
                {
                    if (!File.Exists(temporary))
                    {
                        _journal?.TemporaryGone(temporary);
                    }
                }
            }

            /// <summary>Copies a file (on a phone or on the PC) to a local file, then checks its size against the source's.</summary>
            private void DownloadToLocal(string source, string localPath, ItemStat stat, double progressScale)
            {
                long start = _doneBytes;
                using (Stream input = OpenRead(source))
                using (var output = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, PhoneBufferSize))
                {
                    Pump(input, output, stat.Size, start, progressScale);
                    output.Flush(flushToDisk: IsMove); // a moved file's source goes next: it must be on the disk first
                }

                long length = new FileInfo(localPath).Length;
                if (length != stat.Size)
                {
                    throw new IOException($"\"{PathUtil.GetName(source)}\" arrived incomplete ({length:N0} of {stat.Size:N0} bytes).");
                }
            }

            /// <summary>Copies a local file onto a phone; the phone's own record of its size is checked afterwards.</summary>
            private string Upload(string localSource, string phoneFolder, string name, ItemStat stat, double progressScale, long progressBase)
            {
                IPhoneProvider phone = Phones.Require(phoneFolder);
                string created;
                using (Stream input = OpenRead(localSource))
                using (PhoneWriteStream output = phone.CreateFile(phoneFolder, name, stat.Size, stat.ModifiedUtc))
                {
                    Pump(input, output, stat.Size, progressBase, progressScale);
                    created = output.Commit();
                }

                PhoneItemInfo info = phone.GetInfo(created);
                if (info != null && !info.IsDirectory && info.Size != stat.Size)
                {
                    throw new IOException($"\"{name}\" reached the phone incomplete ({info.Size:N0} of {stat.Size:N0} bytes).");
                }

                return created;
            }

            /// <summary>Moves bytes in buffers, reporting progress and honouring pause and cancel between buffers.</summary>
            private void Pump(Stream input, Stream output, long expected, long progressBase, double progressScale)
            {
                long copied = 0;
                while (true)
                {
                    _token.ThrowIfCancellationRequested();
                    _context.Pause?.WaitWhilePaused(_token);
                    int read = input.Read(_buffer, 0, _buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (copied + read > expected && expected >= 0 && output is PhoneWriteStream)
                    {
                        throw new IOException("The file changed while it was being copied; try again.");
                    }

                    output.Write(_buffer, 0, read);
                    copied += read;
                    _doneBytes = progressBase + (long)(copied * progressScale);
                    _progress.SetCompletedBytes(_doneBytes);
                }

                if (copied != expected && output is PhoneWriteStream)
                {
                    throw new IOException("The file changed while it was being copied; try again.");
                }
            }

            private void Count(long bytes)
            {
                _doneBytes += Math.Max(0, bytes);
                _progress.SetCompletedBytes(_doneBytes);
            }

            // ------------------------------------------------------------------ both kinds of place

            private readonly struct ItemStat
            {
                public ItemStat(bool exists, bool isDirectory, bool isLink, long size, DateTime modifiedUtc)
                {
                    Exists = exists;
                    IsDirectory = isDirectory;
                    IsLink = isLink;
                    Size = size;
                    ModifiedUtc = modifiedUtc;
                }

                public bool Exists { get; }
                public bool IsDirectory { get; }
                public bool IsLink { get; }
                public long Size { get; }
                public DateTime ModifiedUtc { get; }
            }

            private readonly struct ChildItem
            {
                public ChildItem(string name, bool isDirectory, long size)
                {
                    Name = name;
                    IsDirectory = isDirectory;
                    Size = size;
                }

                public string Name { get; }
                public bool IsDirectory { get; }
                public long Size { get; }
            }

            private static ItemStat Stat(string path)
            {
                if (Phones.IsPhonePath(path))
                {
                    PhoneItemInfo info = Phones.Require(path).GetInfo(path);
                    return info == null ? default : new ItemStat(true, info.IsDirectory, false, info.Size, info.ModifiedUtc);
                }

                if (IsLink(path))
                {
                    return new ItemStat(true, false, true, 0, default);
                }

                if (Directory.Exists(path))
                {
                    return new ItemStat(true, true, false, 0, Directory.GetLastWriteTimeUtc(path));
                }

                var file = new FileInfo(path);
                return file.Exists ? new ItemStat(true, false, false, file.Length, file.LastWriteTimeUtc) : default;
            }

            private IEnumerable<ChildItem> ListChildren(string folder)
            {
                if (Phones.IsPhonePath(folder))
                {
                    DirectoryIndex index = Phones.Require(folder).List(folder, _token);
                    if (index.HasError)
                    {
                        throw new IOException(index.ErrorMessage);
                    }

                    return index.Entries.Select(e => new ChildItem(e.Name, e.IsDirectory, e.SizeBytes)).ToList();
                }

                return new DirectoryInfo(folder).EnumerateFileSystemInfos()
                    .Select(i => new ChildItem(i.Name, (i.Attributes & FileAttributes.Directory) != 0 && !IsLink(i.FullName),
                        i is FileInfo f ? f.Length : 0))
                    .ToList();
            }

            private static Stream OpenRead(string path) =>
                Phones.IsPhonePath(path)
                    ? Phones.Require(path).OpenRead(path)
                    : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PhoneBufferSize, FileOptions.SequentialScan);

            private string CreateFolder(string parent, string name)
            {
                if (Phones.IsPhonePath(parent))
                {
                    return Phones.Require(parent).CreateFolder(parent, name);
                }

                string path = Path.Combine(parent, name);
                Directory.CreateDirectory(path);
                return path;
            }

            /// <summary>A moved item's source, once its copy is complete: deleted for good (from the phone, or the PC).</summary>
            private void DeleteSource(string source, bool isDirectory)
            {
                if (Phones.IsPhonePath(source))
                {
                    Phones.Require(source).Delete(source);
                }
                else if (isDirectory)
                {
                    Directory.Delete(source, recursive: false);
                }
                else
                {
                    NativeFileOperations.DeleteFilePermanently(source);
                }
            }

            /// <summary>After a folder's items were moved: the folder goes too, unless something was left in it (skipped, failed).</summary>
            private void RemoveEmptySourceFolder(string folder)
            {
                if (!ListChildren(folder).Any())
                {
                    DeleteSource(folder, isDirectory: true);
                }
            }

            private string FreeNameAt(string path)
            {
                if (!Phones.IsPhonePath(path))
                {
                    return FreeName(path);
                }

                string folder = PhonePaths.GetParent(path);
                string name = PhonePaths.GetName(path);
                string stem = Path.GetFileNameWithoutExtension(name);
                string extension = Path.GetExtension(name);
                IPhoneProvider phone = Phones.Require(path);
                for (int n = 2; n < 10000; n++)
                {
                    string candidate = PhonePaths.Combine(folder, $"{stem} ({n}){extension}");
                    if (phone.GetInfo(candidate) == null)
                    {
                        return candidate;
                    }
                }

                throw new IOException($"No free name was found for \"{name}\" on the phone.");
            }

            /// <summary>Explorer's "name - Copy.ext", then "name - Copy (2).ext", for a copy into its own folder (on a phone too).</summary>
            private static string DuplicateNameAt(string path, bool isDirectory)
            {
                if (!Phones.IsPhonePath(path))
                {
                    return DuplicateName(path);
                }

                string folder = PathUtil.GetParent(path);
                string name = PathUtil.GetName(path);
                string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
                string extension = isDirectory ? string.Empty : Path.GetExtension(name);
                IPhoneProvider phone = Phones.Require(path);
                for (int counter = 1; counter < 10000; counter++)
                {
                    string candidate = PathUtil.Combine(folder, stem + (counter == 1 ? " - Copy" : $" - Copy ({counter})") + extension);
                    if (phone.GetInfo(candidate) == null)
                    {
                        return candidate;
                    }
                }

                throw new IOException($"No free name was found for a copy of \"{name}\" on the phone.");
            }

            private static string TemporaryName(string name) => $".{name}{OperationTemporaries.Marker}{Guid.NewGuid():N}{OperationTemporaries.Suffix}";

            private static void TrySetModified(string path, DateTime modifiedUtc)
            {
                try
                {
                    File.SetLastWriteTimeUtc(path, modifiedUtc);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // The date is a courtesy; the file is what matters.
                }
            }

            private ConflictChoice Decide(ConflictKind kind, string source, string destination, ItemStat sourceStat, ItemStat existing)
            {
                if (!_remembered.TryGetValue(kind, out ConflictChoice choice))
                {
                    IConflictResolver resolver = _context.ConflictResolver;
                    if (resolver == null)
                    {
                        return ConflictChoice.KeepBoth; // never overwrite without being asked
                    }

                    var info = new ConflictInfo(kind, source, destination,
                        sourceStat.IsDirectory ? -1 : sourceStat.Size, sourceStat.ModifiedUtc.ToLocalTime(),
                        existing.IsDirectory ? -1 : existing.Size, existing.ModifiedUtc.ToLocalTime(), IsMove);
                    FileOperationPhase previous = _progress.Phase;
                    _progress.SetPhase(FileOperationPhase.WaitingForDecision);
                    ConflictDecision decision;
                    try
                    {
                        decision = resolver.ResolveAsync(info, _token).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        _progress.SetPhase(previous);
                    }

                    choice = decision.Choice;
                    if (choice == ConflictChoice.Cancel)
                    {
                        throw new OperationCanceledException(_token);
                    }

                    if (kind == ConflictKind.TypeMismatch && (choice == ConflictChoice.Replace || choice == ConflictChoice.ReplaceIfNewer))
                    {
                        choice = ConflictChoice.KeepBoth;
                    }

                    if (decision.ApplyToAll)
                    {
                        _remembered[kind] = choice;
                    }
                }

                if (choice == ConflictChoice.ReplaceIfNewer)
                {
                    // To the second: a phone keeps times less precisely than an NTFS disk.
                    bool newer = sourceStat.ModifiedUtc != default && existing.ModifiedUtc != default &&
                                 Math.Floor((sourceStat.ModifiedUtc - existing.ModifiedUtc).TotalSeconds) > 0;
                    return newer ? ConflictChoice.Replace : ConflictChoice.Skip;
                }

                return choice;
            }

            private long ScanTotalBytes()
            {
                long total = 0;
                foreach (string source in _sources)
                {
                    _token.ThrowIfCancellationRequested();
                    try
                    {
                        total += ScanBytes(source, Stat(source), depth: 0);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // It fails again, and is reported, when its turn comes.
                    }
                }

                return total;
            }

            private long ScanBytes(string path, ItemStat stat, int depth)
            {
                if (!stat.Exists || stat.IsLink)
                {
                    return 0;
                }

                if (!stat.IsDirectory)
                {
                    return stat.Size;
                }

                if (depth > 64)
                {
                    return 0;
                }

                long total = 0;
                foreach (ChildItem child in ListChildren(path))
                {
                    _token.ThrowIfCancellationRequested();
                    string childPath = PathUtil.Combine(path, child.Name);
                    total += child.IsDirectory ? ScanBytes(childPath, new ItemStat(true, true, false, 0, default), depth + 1) : Math.Max(0, child.Size);
                }

                return total;
            }

            private static string Describe(IReadOnlyList<string> paths) =>
                paths.Count == 1 ? $"\"{PathUtil.GetName(paths[0])}\"" : $"{paths.Count:N0} items";
        }
    }
}
