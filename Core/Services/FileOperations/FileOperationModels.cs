// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;

namespace ByteExplorer.Core.Services.FileOperations
{
    /// <summary>What is in the way at the destination.</summary>
    public enum ConflictKind
    {
        /// <summary>A file onto an existing file: Replace, Skip or Keep both.</summary>
        FileOverFile,

        /// <summary>A folder onto an existing folder: Merge (Replace), Skip or Keep both.</summary>
        FolderOverFolder,

        /// <summary>A file onto a folder or a folder onto a file: only Skip or Keep both make sense.</summary>
        TypeMismatch
    }

    public enum ConflictChoice
    {
        /// <summary>Files: replace the existing one (it goes to the trash). Folders: merge into the existing one.</summary>
        Replace,

        Skip,

        /// <summary>Keep both: the incoming item gets a free name, "name (2).ext".</summary>
        KeepBoth,

        /// <summary>
        /// Files only: replace the existing file when the incoming one is newer (modified later), otherwise skip it. Mostly
        /// used with "apply to all", to bring a copy up to date.
        /// </summary>
        ReplaceIfNewer,

        /// <summary>Stop the whole operation.</summary>
        Cancel
    }

    /// <summary>One name clash, with what the person needs to decide: both items' size and date.</summary>
    public sealed class ConflictInfo
    {
        public ConflictInfo(ConflictKind kind, string sourcePath, string destinationPath, long sourceSize, DateTime sourceModified,
            long destinationSize, DateTime destinationModified, bool isMove)
        {
            Kind = kind;
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            SourceSize = sourceSize;
            SourceModified = sourceModified;
            DestinationSize = destinationSize;
            DestinationModified = destinationModified;
            IsMove = isMove;
        }

        public ConflictKind Kind { get; }
        public string SourcePath { get; }
        public string DestinationPath { get; }

        /// <summary>Bytes; -1 for a folder.</summary>
        public long SourceSize { get; }
        public DateTime SourceModified { get; }

        /// <summary>Bytes; -1 for a folder.</summary>
        public long DestinationSize { get; }
        public DateTime DestinationModified { get; }
        public bool IsMove { get; }
    }

    public readonly struct ConflictDecision
    {
        public ConflictDecision(ConflictChoice choice, bool applyToAll)
        {
            Choice = choice;
            ApplyToAll = applyToAll;
        }

        public ConflictChoice Choice { get; }

        /// <summary>Use the same choice for every later clash of the same kind in this operation.</summary>
        public bool ApplyToAll { get; }
    }

    /// <summary>Asks the person about a name clash. Called from the operation's worker thread.</summary>
    public interface IConflictResolver
    {
        Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken);
    }

    public enum FileOperationPhase
    {
        Preparing,
        Working,
        WaitingForDecision,
        Finishing
    }

    /// <summary>A progress snapshot. Byte counts are 0 when nothing is copied (same-volume moves are renames).</summary>
    public readonly struct FileOperationProgress
    {
        public FileOperationProgress(FileOperationPhase phase, long totalBytes, long completedBytes, int totalItems, int completedItems, string currentItem)
        {
            Phase = phase;
            TotalBytes = totalBytes;
            CompletedBytes = completedBytes;
            TotalItems = totalItems;
            CompletedItems = completedItems;
            CurrentItem = currentItem;
        }

        public FileOperationPhase Phase { get; }
        public long TotalBytes { get; }
        public long CompletedBytes { get; }
        public int TotalItems { get; }
        public int CompletedItems { get; }
        public string CurrentItem { get; }

        /// <summary>0..1: by bytes when there are bytes to copy, else by items.</summary>
        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1)
            : TotalItems > 0 ? Math.Clamp((double)CompletedItems / TotalItems, 0, 1) : 0;
    }

    /// <summary>
    /// Pause for a running operation: the worker blocks at the next check (between files, and between the buffers of a
    /// file being copied) until resumed or cancelled.
    /// </summary>
    public sealed class PauseGate : IDisposable
    {
        private readonly ManualResetEventSlim _running = new ManualResetEventSlim(true);

        public bool IsPaused => !_running.IsSet;

        public void Pause() => _running.Reset();

        public void Resume() => _running.Set();

        /// <summary>Returns at once when not paused; otherwise waits for Resume, or throws on cancellation.</summary>
        public void WaitWhilePaused(CancellationToken cancellationToken)
        {
            if (!_running.IsSet)
            {
                _running.Wait(cancellationToken);
            }
        }

        public void Dispose() => _running.Dispose();
    }

    /// <summary>What an operation needs from its caller: decisions, pause, progress and cancellation. All optional.</summary>
    /// <summary>What to do about an item that failed (Total Commander's error question).</summary>
    public enum ErrorChoice
    {
        /// <summary>Try the same item again (after closing the program that held it, freeing space, …).</summary>
        Retry,

        /// <summary>Leave this item out; it is listed as failed (and can be retried from the panel).</summary>
        Skip,

        /// <summary>Leave this and every later failing item out without asking again.</summary>
        SkipAll,

        /// <summary>Stop the whole operation.</summary>
        Cancel
    }

    /// <summary>An item that failed: what was being done to it and why it failed.</summary>
    public sealed class ErrorInfo
    {
        public ErrorInfo(FileOperationKind kind, string path, string message)
        {
            Kind = kind;
            Path = path;
            Message = message;
        }

        public FileOperationKind Kind { get; }

        public string Path { get; }

        public string Message { get; }
    }

    /// <summary>Asks the person about a failed item; called from the operation's worker thread.</summary>
    public interface IErrorResolver
    {
        Task<ErrorChoice> ResolveAsync(ErrorInfo error, CancellationToken cancellationToken);
    }

    public sealed class FileOperationContext
    {
        public IConflictResolver ConflictResolver { get; init; }

        /// <summary>Asks about failed items (when the setting says to ask); without one, failures are reported at the end.</summary>
        public IErrorResolver ErrorResolver { get; init; }
        public PauseGate Pause { get; init; }
        public Action<FileOperationProgress> Progress { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    // ------------------------------------------------------------------ undo

    /// <summary>One change an operation made, and how to reverse it.</summary>
    public abstract class UndoAction
    {
        /// <summary>The path the change left behind, for messages.</summary>
        public abstract string DisplayPath { get; }
    }

    /// <summary>The operation created this item (a copy, a new folder or file). Undo: move it to the trash.</summary>
    public sealed class CreatedAction : UndoAction
    {
        public CreatedAction(string path)
        {
            Path = path;
            try
            {
                // What the operation made, so an undo without a trash can tell whether it is still exactly that.
                var info = new System.IO.FileInfo(path);
                if (info.Exists)
                {
                    Length = info.Length;
                    LastWriteUtc = info.LastWriteTimeUtc;
                }
            }
            catch (System.Exception ex) when (ex is System.IO.IOException || ex is System.UnauthorizedAccessException)
            {
            }
        }

        public string Path { get; }

        /// <summary>The file's size when the operation made it; -1 for a folder (or unknown).</summary>
        public long Length { get; } = -1;

        public System.DateTime LastWriteUtc { get; }

        public override string DisplayPath => Path;
    }

    /// <summary>The item was moved (or renamed) from one path to another. Undo: move it back.</summary>
    public sealed class MovedAction : UndoAction
    {
        public MovedAction(string from, string to)
        {
            From = from;
            To = to;
        }

        public string From { get; }
        public string To { get; }
        public override string DisplayPath => To;
    }

    /// <summary>The item went to the trash (deleted, or replaced by another). Undo: restore it.</summary>
    public sealed class TrashedAction : UndoAction
    {
        public TrashedAction(TrashedItem item) => Item = item;
        public TrashedItem Item { get; }
        public override string DisplayPath => Item.OriginalPath;
    }

    /// <summary>An emptied source folder was removed after its contents were moved. Undo: create it again.</summary>
    public sealed class RemovedFolderAction : UndoAction
    {
        public RemovedFolderAction(string path) => Path = path;
        public string Path { get; }
        public override string DisplayPath => Path;
    }

    /// <summary>
    /// The changes one operation made, in order. Undo reverses them last to first. Only operations that changed
    /// something reversible produce a record (a permanent delete does not).
    /// </summary>
    public sealed class FileOperationRecord
    {
        public FileOperationRecord(string description, IReadOnlyList<UndoAction> actions, IReadOnlyList<string> affectedFolders)
        {
            Description = description;
            Actions = actions;
            AffectedFolders = affectedFolders;
            CreatedUtc = DateTime.UtcNow;
        }

        /// <summary>For the Undo menu and messages: "Copy 3 items to Documents".</summary>
        public string Description { get; }
        public IReadOnlyList<UndoAction> Actions { get; }

        /// <summary>Folders whose listing changed, refreshed after the operation and after undoing it.</summary>
        public IReadOnlyList<string> AffectedFolders { get; }
        public DateTime CreatedUtc { get; }

        /// <summary>
        /// Does the operation again after it was undone (Redo, Ctrl+Y), as a new operation with its own checks, conflicts
        /// and undo record. Null when it can't be redone (compressing, extracting).
        /// </summary>
        public Func<Task> Redo { get; set; }
    }

    /// <summary>An operation's result, plus its undo record (null when nothing reversible happened).</summary>
    public sealed class FileOperationOutcome
    {
        public FileOperationOutcome(FileOperationResult result, FileOperationRecord record)
        {
            Result = result;
            Record = record;
        }

        public FileOperationResult Result { get; }
        public FileOperationRecord Record { get; }
    }
}
