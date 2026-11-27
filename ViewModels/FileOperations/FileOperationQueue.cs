// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels.FileOperations
{
    /// <summary>
    /// Every file operation of the app (both panes) runs through here: the jobs the operations panel shows, the order
    /// they run in, name clash questions, and the undo history.
    /// <para>
    /// <b>Order</b>: copies and moves run one at a time, in the order they were started (two big copies to the same
    /// disk at once are slower than one after the other, and a new paste no longer cancels the one running, as it did
    /// before). Deletes, undo and archive jobs run at once, beside them.
    /// </para>
    /// <para>
    /// <b>Undo</b>: each operation that changed something reversible pushes its record; Undo (Ctrl+Z) reverses the
    /// newest, as a job of its own. The history holds the last <see cref="MaxHistory"/> operations of this session.
    /// <b>Redo</b> (Ctrl+Y, Ctrl+Shift+Z) does a fully undone operation again, newest undone first; any new operation
    /// clears the redo list, as in every editor.
    /// </para>
    /// <para>All members are used on the UI thread; the engine runs on worker threads and reports through the jobs.</para>
    /// </summary>
    public sealed class FileOperationQueue : ViewModelBase
    {
        public const int MaxHistory = 50;

        /// <summary>Operations quicker than this never appear in the panel (no flash for a rename or a small delete).</summary>
        private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(600);

        /// <summary>How long a finished job stays listed: long enough to read, longer when something went wrong.</summary>
        private static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan KeepFinishedWithProblems = TimeSpan.FromSeconds(12);

        private readonly IFileOperationService _service;
        private readonly IUiDispatcher _dispatcher;
        private readonly Func<IDialogService> _dialogs;
        private readonly SemaphoreSlim _transferLane = new SemaphoreSlim(1, 1);
        private readonly List<FileOperationRecord> _history = new List<FileOperationRecord>();
        private readonly List<FileOperationRecord> _redo = new List<FileOperationRecord>();
        private readonly AsyncRelayCommand _undoCommand;
        private readonly AsyncRelayCommand _redoCommand;
        private int _redoing;
        private readonly SemaphoreSlim _redoTurn = new SemaphoreSlim(1, 1);
        private bool _hasShownJobs;

        public FileOperationQueue(IFileOperationService service, IUiDispatcher dispatcher, Func<IDialogService> dialogs)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _dispatcher = dispatcher;
            _dialogs = dialogs;
            Jobs.CollectionChanged += OnJobsChanged;
            _undoCommand = new AsyncRelayCommand(_ => UndoLastAsync(), _ => CanUndo);
            // Overlap allowed: a second Ctrl+Y while the first redo still runs (it refreshes the folder) is not dropped; the
            // presses queue in RedoLastAsync and run one after another, newest undone first.
            _redoCommand = new AsyncRelayCommand(_ => RedoLastAsync(), _ => CanRedo, allowOverlap: true);
            ClearFinishedCommand = new RelayCommand(_ => ClearFinished());
        }

        /// <summary>Running, waiting and recently finished jobs, oldest first.</summary>
        public ObservableCollection<FileOperationJob> Jobs { get; } = new ObservableCollection<FileOperationJob>();

        /// <summary>Whether the panel has anything to show.</summary>
        public bool HasShownJobs
        {
            get => _hasShownJobs;
            private set => SetProperty(ref _hasShownJobs, value);
        }

        public ICommand UndoCommand => _undoCommand;

        public ICommand RedoCommand => _redoCommand;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>"Redo Copy 3 items to Documents", or "Redo" when there is nothing to redo.</summary>
        public string RedoText => _redo.Count > 0 ? "Redo " + _redo[_redo.Count - 1].Description : "Redo";

        public ICommand ClearFinishedCommand { get; }

        public bool CanUndo => _history.Count > 0;

        /// <summary>"Undo Copy 3 items to Documents", or "Undo" when there is nothing to undo.</summary>
        public string UndoText => _history.Count > 0 ? "Undo " + _history[_history.Count - 1].Description : "Undo";

        /// <summary>Folders whose content an operation or an undo changed; the panes showing them refresh.</summary>
        public event EventHandler<IReadOnlyList<string>> FoldersChanged;

        /// <summary>An operation finished and its changes were recorded (for undo): moves and renames, created items.</summary>
        public event EventHandler<FileOperationRecord> Recorded;

        /// <summary>An operation's changes were undone (raised after the undo ran, successfully or partly).</summary>
        public event EventHandler<FileOperationRecord> Undone;

        /// <summary>
        /// A copy, move or delete finished (fully, partly or cancelled), however it was started: a paste, a drop, a delete,
        /// Redo or Retry failed. The one place usage statistics count operations.
        /// </summary>
        public event EventHandler<FileOperationResult> OperationFinished;

        // ------------------------------------------------------------------ operations

        public Task<FileOperationOutcome> TransferAsync(FileOperationKind kind, IReadOnlyList<string> paths, string destinationFolder)
        {
            string verb = kind == FileOperationKind.Move ? "Moving" : "Copying";
            string title = $"{verb} {Describe(paths)} to {PathUtil.GetName(destinationFolder)}";
            var job = new FileOperationJob(kind, title, _dispatcher);
            // Retry failed: each failed item goes again to where it was going (a file deep in a folder to its own subfolder).
            job.RetryFactory = result => () =>
            {
                foreach (IGrouping<string, FileOperationFailure> group in result.Failures
                             .Where(f => !string.IsNullOrEmpty(f.RetryDestination) && MayStillExist(f.SourcePath))
                             .GroupBy(f => f.RetryDestination, PathUtil.Comparer))
                {
                    TransferAsync(kind, group.Select(f => f.SourcePath).Distinct(PathUtil.Comparer).ToList(), group.Key).Forget("retry failed items");
                }
            };
            return RunAsync(job, useTransferLane: true,
                context => _service.TransferAsync(kind, paths, destinationFolder, context),
                // The sources, and what it writes in the destination (not the whole folder: deleting another file there need not wait).
                claims: paths.Concat(paths.Select(p => System.IO.Path.Combine(destinationFolder, PathUtil.GetName(p)))).ToArray(),
                touchedFolders: paths.Select(PathUtil.GetParent).Append(destinationFolder).ToArray(),
                redo: () => TransferAsync(kind, paths, destinationFolder), reportFinished: true);
        }

        public Task<FileOperationOutcome> DeleteAsync(IReadOnlyList<string> paths, bool permanent)
        {
            string title = (permanent ? "Deleting " : OperatingSystem.IsWindows() ? "Recycling " : "Moving to the Trash: ") + Describe(paths);
            var job = new FileOperationJob(FileOperationKind.Delete, title, _dispatcher);
            job.RetryFactory = result => () =>
            {
                List<string> failed = result.Failures.Select(f => f.SourcePath).Where(MayStillExist).ToList();
                if (failed.Count > 0)
                {
                    DeleteAsync(failed, permanent).Forget("retry failed deletes");
                }
            };
            return RunAsync(job, useTransferLane: false,
                context => _service.DeleteAsync(paths, permanent, context),
                claims: paths.ToArray(),
                touchedFolders: paths.Select(PathUtil.GetParent).ToArray(),
                // Only a delete to the Trash/Recycle Bin is recorded (and so undone): redoing it recycles them again.
                redo: () => DeleteAsync(paths, permanent), reportFinished: true);
        }

        /// <summary>
        /// Puts items from the trash back where they were deleted from (the trash view's Restore). An item whose place
        /// has been taken meanwhile is restored next to it under a free name ("name (2)"), so nothing is overwritten.
        /// Each restored item is recorded as created by the operation, so Undo sends it back to the trash, as Explorer's
        /// Undo Restore does. Not redoable: once undone, the item is a new trash entry the record doesn't know.
        /// </summary>
        public Task<FileOperationOutcome> RestoreFromTrashAsync(IReadOnlyList<Core.Platform.TrashEntry> entries)
        {
            List<string> originals = entries.Where(e => e.CanRestore).Select(e => e.OriginalPath).ToList();
            string description = $"Restore {Describe(originals)}";
            var job = new FileOperationJob(FileOperationKind.Move, "Restoring " + Describe(originals), _dispatcher) { SupportsPause = false, PastTenseText = "restored" };
            return RunAsync(job, useTransferLane: false, context => Task.Run(() =>
                {
                    var failures = new List<FileOperationFailure>();
                    var undo = new List<UndoAction>();
                    var folders = new List<string>();
                    bool cancelled = false;
                    foreach (Core.Platform.TrashEntry entry in entries)
                    {
                        if (context.CancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        try
                        {
                            string restored = Core.Platform.Trash.Restore(entry);
                            undo.Add(new CreatedAction(restored));
                            folders.Add(PathUtil.GetParent(restored));
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                        {
                            Log.Logger.Warning(ex, "Restoring {Name} from the trash failed", entry.Name);
                            failures.Add(new FileOperationFailure(entry.OriginalPath ?? entry.Name, ex.Message));
                        }
                    }

                    var result = new FileOperationResult(FileOperationKind.Move, undo.Count, failures, cancelled);
                    FileOperationRecord record = undo.Count > 0 ? new FileOperationRecord(description, undo, folders.Distinct(PathUtil.Comparer).ToList()) : null;
                    return new FileOperationOutcome(result, record);
                }),
                claims: originals.ToArray(),
                touchedFolders: originals.Select(PathUtil.GetParent).ToArray());
        }

        /// <summary>
        /// Whether a failed item is worth retrying: it still exists. A phone isn't asked here (it is slow and may be
        /// locked): its items are retried, and one that's gone is reported again.
        /// </summary>
        private static bool MayStillExist(string path) =>
            Core.Platform.Phones.Phones.IsPhonePath(path) || File.Exists(path) || Directory.Exists(path);

        /// <summary>
        /// Reverses the newest operation. Its record leaves the history first, so a second Ctrl+Z goes to the one
        /// before; if parts can't be reversed (changed since), the person is told which.
        /// </summary>
        public async Task UndoLastAsync()
        {
            if (_history.Count == 0)
            {
                return;
            }

            FileOperationRecord record = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);
            RaiseHistoryChanged();

            FileOperationOutcome outcome = await RunAsync(new FileOperationJob(FileOperationKind.Undo, "Undo: " + record.Description, _dispatcher),
                useTransferLane: false, context => _service.UndoAsync(record, context),
                // No touched folders here: the refresh comes below, after Undone, so the tags have moved back before the
                // folders are reconciled (reconciling first would set aside the tags of the paths the undo just left).
                claims: record.Actions.SelectMany(PathsOf).ToArray(), touchedFolders: null, recordResult: false).ConfigureAwait(true);

            // Cancelled part-way: what wasn't reached goes back on the history (Ctrl+Z again finishes it); listeners hear
            // only about what was undone.
            FileOperationRecord rest = outcome.Record;
            if (rest != null)
            {
                Record(rest);
            }

            IReadOnlyList<UndoAction> undone = rest == null ? record.Actions : record.Actions.Skip(rest.Actions.Count).ToArray();
            if (undone.Count > 0)
            {
                Undone?.Invoke(this, new FileOperationRecord(record.Description, undone, record.AffectedFolders));
            }

            // Only an operation undone completely can be redone: redoing half an undo would do part of it twice.
            if (rest == null && !outcome.Result.HasFailures && !outcome.Result.WasCancelled && record.Redo != null)
            {
                _redo.Add(record);
                RaiseHistoryChanged();
            }

            RaiseFoldersChanged(record.AffectedFolders);
            if (outcome.Result.HasFailures)
            {
                await ShowMessageAsync(FailureMessage("Some changes couldn't be undone, because the items changed since:", outcome.Result)).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Does the newest undone operation again. It runs as a new operation (conflicts are asked, failures listed) and
        /// records its own undo step, so Ctrl+Z undoes the redo.
        /// </summary>
        public async Task RedoLastAsync()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            // One redo at a time: a redone rename or new folder runs outside the job queue's path claims, and the next
            // redo may depend on it (rename A to B, then B to C).
            await _redoTurn.WaitAsync().ConfigureAwait(true);
            if (_redo.Count == 0)
            {
                _redoTurn.Release();
                return;
            }

            FileOperationRecord record = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            RaiseHistoryChanged();
            _redoing++;
            try
            {
                await record.Redo().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Log.Logger.Warning(ex, "Redo of {Description} failed", record.Description);
                await ShowMessageAsync("Couldn't redo \"" + record.Description + "\".\n\n" + ex.Message).ConfigureAwait(true);
            }
            finally
            {
                _redoing--;
                _redoTurn.Release();
            }
        }

        /// <summary>
        /// A job for work that reports text instead of bytes (compressing, extracting). The result's path is recorded for
        /// undo as created by the operation.
        /// </summary>
        public async Task<string> RunCreatingAsync(string title, string undoDescription, Func<IProgress<string>, CancellationToken, Task<string>> work)
        {
            string created = null;
            // Compressing and extracting run in one library call: they can't stop between files, so no Pause button.
            FileOperationJob job = new FileOperationJob(FileOperationKind.Copy, title, _dispatcher) { SupportsPause = false, PastTenseText = "completed" };
            await RunAsync(job, useTransferLane: false, async context =>
            {
                var progress = new Progress<string>(job.ReportText);
                created = await work(progress, context.CancellationToken).ConfigureAwait(false);
                var result = new FileOperationResult(FileOperationKind.Copy, 1, Array.Empty<FileOperationFailure>(), false);
                FileOperationRecord record = string.IsNullOrEmpty(created)
                    ? null
                    : new FileOperationRecord(undoDescription, new UndoAction[] { new CreatedAction(created) }, new[] { PathUtil.GetParent(created) });
                return new FileOperationOutcome(result, record);
            }, rethrow: true).ConfigureAwait(true);
            return created;
        }

        /// <summary>
        /// A job for background work that changes nothing the person would undo (extracting from an archive into a
        /// temporary folder to open or copy it). Throws what the work throws, including cancellation.
        /// </summary>
        public async Task<T> RunBackgroundAsync<T>(string title, Func<IProgress<string>, CancellationToken, Task<T>> work)
        {
            T value = default;
            FileOperationJob job = new FileOperationJob(FileOperationKind.Copy, title, _dispatcher) { SupportsPause = false, PastTenseText = "completed" };
            await RunAsync(job, useTransferLane: false, async context =>
            {
                value = await work(new Progress<string>(job.ReportText), context.CancellationToken).ConfigureAwait(false);
                return new FileOperationOutcome(new FileOperationResult(FileOperationKind.Copy, 1, Array.Empty<FileOperationFailure>(), false), null);
            }, rethrow: true).ConfigureAwait(true);
            return value;
        }

        /// <summary>
        /// A job for work that reports bytes and items and can be paused and cancelled, but changes nothing the history
        /// could put back (password protection: undoing it would delete the only copy of the data). Waits for earlier
        /// jobs on the same items (<paramref name="claims"/>) and runs in the transfer lane. Throws what the work throws,
        /// including cancellation.
        /// </summary>
        public async Task<T> RunUnrecordedAsync<T>(string title, string pastTense, IReadOnlyList<string> claims, Func<FileOperationContext, Task<T>> work)
        {
            T value = default;
            FileOperationJob job = new FileOperationJob(FileOperationKind.Copy, title, _dispatcher) { PastTenseText = pastTense };
            await RunAsync(job, useTransferLane: true, async context =>
            {
                value = await work(context).ConfigureAwait(false);
                return new FileOperationOutcome(new FileOperationResult(FileOperationKind.Copy, 1, Array.Empty<FileOperationFailure>(), false), null);
            }, rethrow: true, claims: claims?.ToArray(), touchedFolders: claims?.Select(PathUtil.GetParent).ToArray(), recordResult: false).ConfigureAwait(true);
            return value;
        }

        /// <summary>Records an operation done outside the queue (rename, new folder or file) so it can be undone.</summary>
        public void Record(FileOperationRecord record)
        {
            if (record == null || record.Actions.Count == 0)
            {
                return;
            }

            Recorded?.Invoke(this, record);
            if (_redoing == 0)
            {
                _redo.Clear(); // a new operation: what was undone before it no longer follows on
            }

            _history.Add(record);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }

            RaiseHistoryChanged();
        }

        /// <summary>Cancels every job that hasn't finished (Escape, or closing the app).</summary>
        public void CancelAll()
        {
            foreach (FileOperationJob job in Jobs)
            {
                job.Cancel();
            }
        }

        /// <summary>
        /// Cancels every job and waits (up to <paramref name="timeout"/>) until each has stopped at a safe point: its
        /// partial file removed, its journal closed. Used when the app closes; whatever is still running after the timeout
        /// is put right by the crash recovery at the next start.
        /// </summary>
        public async Task StopAllAsync(TimeSpan timeout)
        {
            CancelAll();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (HasActiveJobs && deadline.Elapsed < timeout)
            {
                try
                {
                    await _jobFinished.Task.WaitAsync(timeout - deadline.Elapsed).ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    break;
                }
            }
        }

        /// <summary>True while any job is waiting or running.</summary>
        public bool HasActiveJobs
        {
            get
            {
                foreach (FileOperationJob job in Jobs)
                {
                    if (!job.IsFinished)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// The items each queued or running job works on, in the order they were queued. A job waits until no job queued
        /// before it claims the same item or one inside the other (a delete never runs on a folder a move is still copying;
        /// an undo never runs under a copy of the same items). Jobs only wait for earlier ones, so they can't deadlock.
        /// Touched on the UI thread only.
        /// </summary>
        private readonly List<(FileOperationJob Job, string[] Paths)> _claims = new List<(FileOperationJob, string[])>();
        private TaskCompletionSource _jobFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private static IEnumerable<string> PathsOf(UndoAction action) => action switch
        {
            MovedAction moved => new[] { moved.From, moved.To },
            TrashedAction trashed => new[] { trashed.Item.OriginalPath },
            _ => new[] { action.DisplayPath },
        };

        private static bool Overlaps(string[] a, string[] b)
        {
            foreach (string x in a)
            {
                foreach (string y in b)
                {
                    if (!string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) && (IsSameOrInside(x, y) || IsSameOrInside(y, x)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsSameOrInside(string path, string folder)
        {
            string p = PathUtil.WithTrailingSeparator(path);
            string f = PathUtil.WithTrailingSeparator(folder);
            return p.StartsWith(f, PathUtil.Comparison);
        }

        private async Task WaitForEarlierClaimsAsync(FileOperationJob job, string[] paths)
        {
            while (true)
            {
                FileOperationJob blocker = null;
                foreach ((FileOperationJob other, string[] otherPaths) in _claims)
                {
                    if (ReferenceEquals(other, job))
                    {
                        break; // only jobs queued before this one count
                    }

                    if (!other.IsFinished && Overlaps(otherPaths, paths))
                    {
                        blocker = other;
                        break;
                    }
                }

                if (blocker == null)
                {
                    return;
                }

                job.MarkWaitingFor(blocker.Title);
                await _jobFinished.Task.WaitAsync(job.CancellationToken).ConfigureAwait(true);
            }
        }

        private async Task<FileOperationOutcome> RunAsync(FileOperationJob job, bool useTransferLane,
            Func<FileOperationContext, Task<FileOperationOutcome>> work, bool rethrow = false,
            string[] claims = null, IReadOnlyList<string> touchedFolders = null, bool recordResult = true, Func<Task> redo = null, bool reportFinished = false)
        {
            Jobs.Add(job);
            claims ??= Array.Empty<string>();
            _claims.Add((job, claims));
            ShowLaterAsync(job).Forget("show file operation");
            bool laneTaken = false;
            FileOperationOutcome outcome = null;
            try
            {
                await WaitForEarlierClaimsAsync(job, claims).ConfigureAwait(true);
                if (useTransferLane)
                {
                    await _transferLane.WaitAsync(job.CancellationToken).ConfigureAwait(true);
                    laneTaken = true;
                }

                job.MarkStarted();
                var context = new FileOperationContext
                {
                    ConflictResolver = new DialogConflictResolver(_dispatcher, _dialogs),
                    ErrorResolver = new DialogErrorResolver(_dispatcher, _dialogs),
                    Pause = job.PauseGate,
                    Progress = job.ReportProgress,
                    CancellationToken = job.CancellationToken
                };

                outcome = await work(context).ConfigureAwait(true);
                job.Complete(outcome.Result);
                if (outcome.Result.HasFailures && job.RetryFactory != null)
                {
                    job.RetryAction = job.RetryFactory(outcome.Result);
                }
                if (recordResult && outcome.Record != null)
                {
                    outcome.Record.Redo ??= redo;
                    Record(outcome.Record);
                }

                if (reportFinished)
                {
                    try
                    {
                        OperationFinished?.Invoke(this, outcome.Result);
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Warning(ex, "An operation-finished listener failed");
                    }
                }

                return outcome;
            }
            catch (OperationCanceledException)
            {
                job.MarkCancelled();
                outcome = new FileOperationOutcome(new FileOperationResult(job.Kind, 0, Array.Empty<FileOperationFailure>(), true), null);
                if (rethrow)
                {
                    throw;
                }

                return outcome;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "File operation failed: {Title}", job.Title);
                job.Fail(ex.Message);
                if (rethrow)
                {
                    throw;
                }

                return new FileOperationOutcome(new FileOperationResult(job.Kind, 0, new[] { new FileOperationFailure(job.Title, ex.Message) }, false), null);
            }
            finally
            {
                if (laneTaken)
                {
                    _transferLane.Release();
                }

                // Whatever happened (done, failed, cancelled, nothing recorded), the folders it touched are refreshed:
                // a permanent delete or a failed copy changes them too.
                RaiseFoldersChanged((outcome?.Record?.AffectedFolders ?? Array.Empty<string>())
                    .Concat(touchedFolders ?? Array.Empty<string>())
                    .Where(f => !string.IsNullOrEmpty(f)).Distinct(PathUtil.Comparer).ToList());
                _claims.RemoveAll(c => ReferenceEquals(c.Job, job));
                TaskCompletionSource finished = _jobFinished;
                _jobFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                finished.TrySetResult(); // jobs waiting for this one's items look again
                RemoveLaterAsync(job).Forget("remove finished file operation");
            }
        }

        private async Task ShowLaterAsync(FileOperationJob job)
        {
            await Task.Delay(ShowDelay).ConfigureAwait(true);
            if (!job.IsFinished)
            {
                job.IsShown = true;
                UpdateHasShownJobs();
            }
        }

        private async Task RemoveLaterAsync(FileOperationJob job)
        {
            if (job.IsShown)
            {
                await Task.Delay(job.HasProblems ? KeepFinishedWithProblems : KeepFinished).ConfigureAwait(true);
            }

            if (Jobs.Remove(job))
            {
                job.Dispose();
            }
        }

        private void ClearFinished()
        {
            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                if (Jobs[i].IsFinished)
                {
                    FileOperationJob job = Jobs[i];
                    Jobs.RemoveAt(i);
                    job.Dispose();
                }
            }
        }

        private void OnJobsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (FileOperationJob job in e.NewItems)
                {
                    job.PropertyChanged += OnJobPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (FileOperationJob job in e.OldItems)
                {
                    job.PropertyChanged -= OnJobPropertyChanged;
                }
            }

            UpdateHasShownJobs();
        }

        private void OnJobPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileOperationJob.IsShown))
            {
                UpdateHasShownJobs();
            }
        }

        private void UpdateHasShownJobs()
        {
            bool any = false;
            foreach (FileOperationJob job in Jobs)
            {
                if (job.IsShown)
                {
                    any = true;
                    break;
                }
            }

            HasShownJobs = any;
        }

        private void RaiseHistoryChanged()
        {
            RaisePropertyChanged(nameof(CanUndo));
            RaisePropertyChanged(nameof(UndoText));
            RaisePropertyChanged(nameof(CanRedo));
            RaisePropertyChanged(nameof(RedoText));
            _undoCommand.RaiseCanExecuteChanged();
            _redoCommand.RaiseCanExecuteChanged();
        }

        private void RaiseFoldersChanged(IReadOnlyList<string> folders)
        {
            if (folders != null && folders.Count > 0)
            {
                FoldersChanged?.Invoke(this, folders);
            }
        }

        private async Task ShowMessageAsync(string message)
        {
            IDialogService dialogs = _dialogs?.Invoke();
            if (dialogs != null)
            {
                await dialogs.ShowMessageAsync("ByteExplorer", message).ConfigureAwait(true);
            }
        }

        /// <summary>The first ten failures, one per line.</summary>
        internal static string FailureMessage(string heading, FileOperationResult result)
        {
            var builder = new System.Text.StringBuilder(heading);
            builder.AppendLine();
            int shown = 0;
            foreach (FileOperationFailure failure in result.Failures)
            {
                if (shown == 10)
                {
                    builder.AppendLine($"…and {result.Failures.Count - shown:N0} more.");
                    break;
                }

                builder.AppendLine($"- {PathUtil.GetName(failure.SourcePath)}: {failure.Message}");
                shown++;
            }

            return builder.ToString();
        }

        private static string Describe(IReadOnlyList<string> paths) =>
            paths == null || paths.Count == 0 ? "nothing" : paths.Count == 1 ? $"\"{PathUtil.GetName(paths[0])}\"" : $"{paths.Count:N0} items";
    }

    /// <summary>
    /// Asks about a name clash on the UI thread and hands the answer back to the waiting worker. If the operation is
    /// cancelled while the question is open, the dialog closes and the operation stops.
    /// </summary>
    /// <summary>Asks the error question on the UI thread for the worker thread waiting on it (no dialog service: skip).</summary>
    internal sealed class DialogErrorResolver : IErrorResolver
    {
        private readonly IUiDispatcher _dispatcher;
        private readonly Func<IDialogService> _dialogs;

        public DialogErrorResolver(IUiDispatcher dispatcher, Func<IDialogService> dialogs)
        {
            _dispatcher = dispatcher;
            _dialogs = dialogs;
        }

        public Task<ErrorChoice> ResolveAsync(ErrorInfo error, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<ErrorChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    IDialogService dialogs = _dialogs?.Invoke();
                    answer.TrySetResult(dialogs == null
                        ? ErrorChoice.Skip
                        : await dialogs.ResolveErrorAsync(error, cancellationToken).ConfigureAwait(true));
                }
                catch (OperationCanceledException)
                {
                    answer.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    answer.TrySetException(ex);
                }
            }).Forget("file operation error question");
            return answer.Task;
        }
    }

    internal sealed class DialogConflictResolver : IConflictResolver
    {
        private readonly IUiDispatcher _dispatcher;
        private readonly Func<IDialogService> _dialogs;

        public DialogConflictResolver(IUiDispatcher dispatcher, Func<IDialogService> dialogs)
        {
            _dispatcher = dispatcher;
            _dialogs = dialogs;
        }

        public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    IDialogService dialogs = _dialogs?.Invoke();
                    ConflictDecision decision = dialogs == null
                        ? new ConflictDecision(ConflictChoice.KeepBoth, false)
                        : await dialogs.ResolveConflictAsync(conflict, cancellationToken).ConfigureAwait(true);
                    answer.TrySetResult(decision);
                }
                catch (OperationCanceledException)
                {
                    answer.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    answer.TrySetException(ex);
                }
            }).Forget("name clash question");
            return answer.Task;
        }
    }
}
