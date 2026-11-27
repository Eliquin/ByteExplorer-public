// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Input;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.FileOperations
{
    public enum FileOperationJobState
    {
        /// <summary>Queued behind another transfer.</summary>
        Waiting,
        Running,
        Paused,

        /// <summary>A name clash is waiting for the person's answer.</summary>
        NeedsDecision,
        Completed,
        CompletedWithProblems,
        Cancelled,
        Failed
    }

    /// <summary>
    /// One operation in the queue as the person sees it: title, current item, progress, speed and time left, with pause,
    /// resume and cancel. Progress arrives on the operation's worker thread at up to ten snapshots a second; only the
    /// newest is kept and it is applied on the UI thread in one batch (no queue of stale updates builds up).
    /// <para>
    /// Speed is measured over the last few seconds (a sliding window), so it follows real changes (a slow file, a busy
    /// disk) without jumping on every buffer; time left is the remaining bytes at that speed. Both are hidden while
    /// paused or waiting for a decision, and the window restarts afterwards, so the pause doesn't drag the speed down.
    /// </para>
    /// </summary>
    public sealed class FileOperationJob : ViewModelBase, IDisposable
    {
        private const double SpeedWindowSeconds = 3.0;
        private const int SampleCount = 64;

        private readonly IUiDispatcher _dispatcher;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly (double Seconds, long Bytes)[] _samples = new (double, long)[SampleCount];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly RelayCommand _pauseCommand;
        private readonly RelayCommand _resumeCommand;
        private readonly RelayCommand _cancelCommand;
        private int _sampleStart;
        private int _sampleLength;

        // Written by the worker thread, read on the UI thread.
        // Written by the worker thread, read on the UI thread: a struct of several fields, so under a lock (a torn read could
        // show "12 of 10" or a wrong speed sample).
        private readonly object _latestLock = new object();
        private FileOperationProgress _latest;
        private int _updatePending;
        private bool _started;

        private FileOperationJobState _state = FileOperationJobState.Waiting;
        private double _fraction;
        private bool _isIndeterminate = true;
        private string _currentItem;
        private string _detailText = "Waiting…";
        private string _speedText;
        private string _remainingText;
        private string _resultText;
        private bool _isShown;

        public FileOperationJob(FileOperationKind kind, string title, IUiDispatcher dispatcher)
        {
            Kind = kind;
            Title = title;
            _dispatcher = dispatcher;
            _pauseCommand = new RelayCommand(_ => Pause(), _ => CanPause);
            RetryCommand = new RelayCommand(_ => Retry(), _ => CanRetry);
            _resumeCommand = new RelayCommand(_ => Resume(), _ => State == FileOperationJobState.Paused);
            _cancelCommand = new RelayCommand(_ => Cancel(), _ => !IsFinished);
        }

        public FileOperationKind Kind { get; }

        /// <summary>"Copying 3 items to Documents".</summary>
        public string Title { get; }

        public PauseGate PauseGate { get; } = new PauseGate();

        public CancellationToken CancellationToken => _cts.Token;

        public ICommand PauseCommand => _pauseCommand;
        public ICommand ResumeCommand => _resumeCommand;
        public ICommand CancelCommand => _cancelCommand;

        public FileOperationJobState State
        {
            get => _state;
            private set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(IsFinished));
                    RaisePropertyChanged(nameof(IsPaused));
                    RaisePropertyChanged(nameof(CanPause));
                    RaisePropertyChanged(nameof(NeedsDecision));
                    RaisePropertyChanged(nameof(HasProblems));
                    _pauseCommand.RaiseCanExecuteChanged();
                    _resumeCommand.RaiseCanExecuteChanged();
                    _cancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsFinished => State >= FileOperationJobState.Completed;
        public bool IsPaused => State == FileOperationJobState.Paused;
        public bool CanPause => SupportsPause && (State == FileOperationJobState.Running || State == FileOperationJobState.Waiting);

        private Action _retry;

        /// <summary>
        /// Set by the queue when the job finished with failed items: queues them again (each where it was going). Offered
        /// once, as the panel's "Retry failed" button.
        /// </summary>
        internal Action RetryAction
        {
            get => _retry;
            set
            {
                _retry = value;
                RaisePropertyChanged(nameof(CanRetry));
                (RetryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Set by the queue: builds the retry of the failed items from the job's result.</summary>
        internal Func<FileOperationResult, Action> RetryFactory { get; set; }

        /// <summary>Finished with failures that can be tried again.</summary>
        public bool CanRetry => IsFinished && HasProblems && _retry != null;

        public System.Windows.Input.ICommand RetryCommand { get; private set; }

        /// <summary>Queues the failed items again; the finished job then leaves the panel.</summary>
        public void Retry()
        {
            Action retry = _retry;
            if (!CanRetry || retry == null)
            {
                return;
            }

            RetryAction = null;
            IsShown = false; // its retry job takes its place
            retry();
        }

        /// <summary>False for work that can't stop between steps (compressing, extracting): no Pause button for it.</summary>
        public bool SupportsPause { get; init; } = true;

        /// <summary>The verb of "3 couldn't be …" when the kind's own does not fit ("extracted", "compressed").</summary>
        public string PastTenseText { get; init; }
        public bool NeedsDecision => State == FileOperationJobState.NeedsDecision;
        public bool HasProblems => State == FileOperationJobState.CompletedWithProblems || State == FileOperationJobState.Failed;

        /// <summary>0..1.</summary>
        public double Fraction
        {
            get => _fraction;
            private set => SetProperty(ref _fraction, value);
        }

        /// <summary>True until the work's size is known (counting files), and for operations with no measurable size.</summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set => SetProperty(ref _isIndeterminate, value);
        }

        public string CurrentItem
        {
            get => _currentItem;
            private set => SetProperty(ref _currentItem, value);
        }

        /// <summary>"1.2 GB of 3.4 GB" or "12 of 40 items", or the state ("Paused", "Waiting…").</summary>
        public string DetailText
        {
            get => _detailText;
            private set => SetProperty(ref _detailText, value);
        }

        /// <summary>"34.2 MB/s", empty when not measured.</summary>
        public string SpeedText
        {
            get => _speedText;
            private set => SetProperty(ref _speedText, value);
        }

        /// <summary>"About 2 minutes left", empty when not measured.</summary>
        public string RemainingText
        {
            get => _remainingText;
            private set => SetProperty(ref _remainingText, value);
        }

        /// <summary>The outcome once finished: "Done", "Cancelled", "2 failed".</summary>
        public string ResultText
        {
            get => _resultText;
            private set => SetProperty(ref _resultText, value);
        }

        /// <summary>
        /// Whether the panel lists this job. Quick operations finish before they are shown, so a rename or a small
        /// delete never flashes a panel; set by the queue after a short delay, or at once when a decision is needed.
        /// </summary>
        public bool IsShown
        {
            get => _isShown;
            set => SetProperty(ref _isShown, value);
        }

        public void Pause()
        {
            if (!CanPause)
            {
                return;
            }

            PauseGate.Pause();
            State = FileOperationJobState.Paused;
            ResetSpeed();
            DetailText = "Paused";
        }

        public void Resume()
        {
            if (State != FileOperationJobState.Paused)
            {
                return;
            }

            ResetSpeed();
            // Back to what it was doing: running once it has started, still waiting for its turn otherwise.
            State = _started ? FileOperationJobState.Running : FileOperationJobState.Waiting;
            PauseGate.Resume();
            ApplyLatest();
        }

        public void Cancel()
        {
            if (IsFinished)
            {
                return;
            }

            _cts.Cancel();
            PauseGate.Resume(); // a paused worker must wake up to see the cancellation
            DetailText = "Cancelling…";
        }

        /// <summary>Called by the queue when the job's turn comes (UI thread).</summary>
        internal void MarkStarted()
        {
            _started = true;
            if (State == FileOperationJobState.Waiting)
            {
                State = FileOperationJobState.Running;
                DetailText = "Preparing…";
            }
        }

        /// <summary>Waiting for an earlier operation on the same items to finish first (UI thread).</summary>
        internal void MarkWaitingFor(string otherTitle)
        {
            if (State == FileOperationJobState.Waiting)
            {
                DetailText = $"Waiting for \"{otherTitle}\" to finish (same items)";
            }
        }

        /// <summary>The progress sink handed to the engine: worker thread, coalesced onto the UI thread.</summary>
        internal void ReportProgress(FileOperationProgress progress)
        {
            lock (_latestLock)
            {
                _latest = progress;
            }

            if (Interlocked.Exchange(ref _updatePending, 1) == 0)
            {
                _dispatcher.InvokeAsync(() =>
                {
                    Interlocked.Exchange(ref _updatePending, 0);
                    ApplyLatest();
                }).Forget("file operation progress");
            }
        }

        /// <summary>Text progress for operations without byte counts (archives).</summary>
        internal void ReportText(string text)
        {
            _dispatcher.InvokeAsync(() =>
            {
                if (!IsFinished && State != FileOperationJobState.Paused)
                {
                    CurrentItem = text;
                }
            }).Forget("file operation progress");
        }

        /// <summary>The engine waits for a decision; the panel shows it (UI thread).</summary>
        internal void SetNeedsDecision(bool waiting)
        {
            if (IsFinished)
            {
                return;
            }

            if (waiting)
            {
                State = FileOperationJobState.NeedsDecision;
                DetailText = "Waiting for your decision";
                IsShown = true;
            }
            else if (State == FileOperationJobState.NeedsDecision)
            {
                // Paused while the question was open: it stays paused (its pause gate is still closed).
                State = PauseGate.IsPaused ? FileOperationJobState.Paused : FileOperationJobState.Running;
                DetailText = PauseGate.IsPaused ? "Paused" : DetailText;
                ResetSpeed();
            }
        }

        internal void Complete(FileOperationResult result)
        {
            if (result == null)
            {
                Finish(FileOperationJobState.Completed, "Done");
                return;
            }

            if (result.WasCancelled)
            {
                Finish(FileOperationJobState.Cancelled, "Cancelled");
            }
            else if (result.HasFailures)
            {
                Finish(FileOperationJobState.CompletedWithProblems, $"{result.Failures.Count:N0} couldn't be {PastTenseText ?? PastTense(Kind)}");
            }
            else if (result.HasPartialMoves)
            {
                Finish(FileOperationJobState.CompletedWithProblems, $"{result.PartialMoves.Count:N0} original{(result.PartialMoves.Count == 1 ? string.Empty : "s")} left behind");
            }
            else
            {
                string skipped = result.SkippedCount > 0 ? $" — {result.SkippedCount:N0} skipped" : string.Empty;
                Finish(FileOperationJobState.Completed, "Done" + skipped);
            }
        }

        internal void Fail(string message) => Finish(FileOperationJobState.Failed, message);

        internal void MarkCancelled() => Finish(FileOperationJobState.Cancelled, "Cancelled");

        private void Finish(FileOperationJobState state, string text)
        {
            State = state;
            ResultText = text;
            DetailText = text;
            SpeedText = null;
            RemainingText = null;
            IsIndeterminate = false;
            if (state == FileOperationJobState.Completed)
            {
                Fraction = 1;
            }

            _clock.Stop();
        }

        private void ApplyLatest()
        {
            FileOperationProgress p;
            lock (_latestLock)
            {
                p = _latest;
            }

            if (IsFinished)
            {
                return;
            }

            if (!string.IsNullOrEmpty(p.CurrentItem))
            {
                CurrentItem = p.CurrentItem;
            }

            if (p.Phase == FileOperationPhase.WaitingForDecision)
            {
                SetNeedsDecision(true);
                return;
            }

            if (State == FileOperationJobState.NeedsDecision)
            {
                SetNeedsDecision(false);
            }

            if (State == FileOperationJobState.Paused)
            {
                return;
            }

            if (p.Phase == FileOperationPhase.Preparing)
            {
                IsIndeterminate = true;
                DetailText = "Counting items…";
                return;
            }

            IsIndeterminate = p.TotalBytes <= 0 && p.TotalItems <= 0;
            Fraction = p.Fraction;

            if (p.TotalBytes > 0)
            {
                DetailText = $"{FormatBytes(p.CompletedBytes)} of {FormatBytes(p.TotalBytes)}";
                AddSample(p.CompletedBytes);
                double speed = BytesPerSecond();
                if (speed > 0)
                {
                    SpeedText = FormatBytes((long)speed) + "/s";
                    RemainingText = FormatRemaining((p.TotalBytes - p.CompletedBytes) / speed);
                }
            }
            else if (p.TotalItems > 0)
            {
                DetailText = $"{p.CompletedItems:N0} of {p.TotalItems:N0} item{(p.TotalItems == 1 ? string.Empty : "s")}";
                SpeedText = null;
                RemainingText = null;
            }
        }

        private void AddSample(long bytes)
        {
            double now = _clock.Elapsed.TotalSeconds;
            int index = (_sampleStart + _sampleLength) % SampleCount;
            _samples[index] = (now, bytes);
            if (_sampleLength < SampleCount)
            {
                _sampleLength++;
            }
            else
            {
                _sampleStart = (_sampleStart + 1) % SampleCount;
            }

            // Drop samples older than the window, keeping at least two.
            while (_sampleLength > 2 && now - _samples[_sampleStart].Seconds > SpeedWindowSeconds)
            {
                _sampleStart = (_sampleStart + 1) % SampleCount;
                _sampleLength--;
            }
        }

        private double BytesPerSecond()
        {
            if (_sampleLength < 2)
            {
                return 0;
            }

            (double firstSeconds, long firstBytes) = _samples[_sampleStart];
            (double lastSeconds, long lastBytes) = _samples[(_sampleStart + _sampleLength - 1) % SampleCount];
            double elapsed = lastSeconds - firstSeconds;
            return elapsed >= 0.5 ? Math.Max(0, (lastBytes - firstBytes) / elapsed) : 0;
        }

        private void ResetSpeed()
        {
            _sampleStart = 0;
            _sampleLength = 0;
            SpeedText = null;
            RemainingText = null;
        }

        private static string PastTense(FileOperationKind kind) => kind switch
        {
            FileOperationKind.Copy => "copied",
            FileOperationKind.Move => "moved",
            FileOperationKind.Delete => "deleted",
            _ => "undone"
        };

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes"
                : size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        /// <summary>Rounded the way people read it: "About 5 seconds left", "About 3 minutes left", "About 1 hour 20 minutes left".</summary>
        internal static string FormatRemaining(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                return null;
            }

            if (seconds < 5)
            {
                return "A few seconds left";
            }

            if (seconds < 60)
            {
                return $"About {Math.Ceiling(seconds / 5) * 5:0} seconds left";
            }

            if (seconds < 3600)
            {
                int minutes = (int)Math.Round(seconds / 60);
                return minutes <= 1 ? "About a minute left" : $"About {minutes} minutes left";
            }

            int hours = (int)(seconds / 3600);
            int rest = (int)Math.Round((seconds - hours * 3600) / 60);
            if (rest == 60)
            {
                hours++;
                rest = 0;
            }

            string hourText = hours == 1 ? "1 hour" : $"{hours} hours";
            return rest == 0 ? $"About {hourText} left" : $"About {hourText} {rest} minute{(rest == 1 ? string.Empty : "s")} left";
        }

        public void Dispose()
        {
            _cts.Dispose();
            PauseGate.Dispose();
        }
    }
}
