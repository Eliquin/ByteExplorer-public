// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using System.Windows.Input; // ICommand (BCL, shared by WPF and Avalonia)

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The default command type for this app. Any command that touches
    /// the filesystem, does I/O, or otherwise takes non-trivial time
    /// should be an AsyncRelayCommand, never a plain RelayCommand.
    ///
    /// Guards against re-entrancy: if the user double-clicks (or the
    /// command fires twice before the first run completes), the second
    /// invocation is a no-op instead of stacking concurrent filesystem
    /// operations. CanExecute reflects busy state so bound UI (buttons,
    /// menu items) disables itself automatically while work is in flight.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;
        private readonly bool _allowOverlap;
        private bool _isExecuting;

        /// <param name="executeAsync">The work.</param>
        /// <param name="canExecute">Whether it can run now.</param>
        /// <param name="allowOverlap">
        /// True: a new run may start while the previous one is still awaiting (navigation: Up pressed twice goes up two
        /// levels, Back during a slow load goes back at once; each load cancels the one before). False (the default): the
        /// command is disabled until its run finishes, so an operation is never started twice by a double click.
        /// </param>
        public AsyncRelayCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null, bool allowOverlap = false)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
            _allowOverlap = allowOverlap;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => (_allowOverlap || !_isExecuting) && (_canExecute?.Invoke(parameter) ?? true);

        /// <summary>
        /// CommandManager.RequerySuggested fires on UI input events
        /// (clicks, focus changes, key presses) but not automatically
        /// when a bound property changes from a background task or a
        /// property setter. View models that gate CanExecute on their
        /// own state (like "is a path currently selected") should call
        /// this after changing that state, so a bound button's enabled
        /// state updates immediately rather than waiting for the next
        /// incidental UI event to trigger a requery.
        /// </summary>
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

        /// <summary>
        /// Raised on the UI thread when a command's work throws (other than cancellation), after it is logged. The main
        /// window shows it as a message, so a failure is never silent; before this an exception left Execute (async
        /// void) and reached only the dispatcher's last-chance handler.
        /// </summary>
        public static event EventHandler<Exception> Failed;

        /// <summary>
        /// ICommand.Execute cannot return a Task, so this is async void by necessity; it therefore catches everything
        /// its work throws.
        /// </summary>
        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _executeAsync(parameter).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the user or superseded: not a failure.
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Command failed");
                try
                {
                    Failed?.Invoke(this, ex);
                }
                catch (Exception handlerEx)
                {
                    Serilog.Log.Logger.Error(handlerEx, "Command failure handler threw");
                }
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
