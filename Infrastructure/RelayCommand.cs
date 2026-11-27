// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Windows.Input; // ICommand (BCL, shared by WPF and Avalonia)

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Standard synchronous command. Use this only for work that is
    /// genuinely instantaneous (toggling a view option, etc.) — anything
    /// that touches the filesystem or does real work belongs in
    /// <see cref="AsyncRelayCommand"/> instead, per the zero-lag policy.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        /// <summary>
        /// See AsyncRelayCommand.RaiseCanExecuteChanged's remarks — same
        /// reasoning applies here.
        /// </summary>
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

        public void Execute(object parameter) => _execute(parameter);
    }
}
