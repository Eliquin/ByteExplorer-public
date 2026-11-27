// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using Avalonia.Threading;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Stand-in for WPF's CommandManager, which Avalonia does not have. WPF re-queried every command's CanExecute after
    /// input and focus changes (RequerySuggested); the view models and RelayCommand were written against that, so this
    /// class keeps the same two members and the same behaviour:
    /// <list type="bullet">
    /// <item><see cref="InvalidateRequerySuggested"/> may be called from any thread, any number of times; the event is
    /// raised once, on the UI thread, at background priority (as WPF coalesced its requery).</item>
    /// <item>The main window calls it after every key and pointer release and focus change
    /// (<see cref="Views.InputRequery"/>), and <see cref="ViewModelBase"/> after every property change, which together
    /// cover what WPF's input-driven requery did.</item>
    /// </list>
    /// Subscriptions are strong: Avalonia's Button and MenuItem unsubscribe from CanExecuteChanged when they leave the
    /// visual tree, so nothing leaks.
    /// </summary>
    public static class CommandManager
    {
        private static int _pending;

        public static event EventHandler RequerySuggested;

        public static void InvalidateRequerySuggested()
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1)
            {
                return;
            }

            UiThread.Post(RaiseRequery, DispatcherPriority.Background);
        }

        private static void RaiseRequery()
        {
            Interlocked.Exchange(ref _pending, 0);
            RequerySuggested?.Invoke(null, EventArgs.Empty);
        }
    }
}
