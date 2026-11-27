// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// <see cref="IUiDispatcher"/> on Avalonia's UI thread dispatcher. Same contract as the WPF version: run inline when
    /// already on the UI thread, otherwise marshal (synchronously for <see cref="Invoke"/>, posted for
    /// <see cref="InvokeAsync"/>).
    /// </summary>
    public sealed class UiDispatcher : IUiDispatcher
    {
        private readonly Dispatcher _dispatcher;

        public UiDispatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public bool CheckAccess() => _dispatcher.CheckAccess();

        public void Invoke(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.Invoke(action);
            }
        }

        public Task InvokeAsync(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _dispatcher.InvokeAsync(action).GetTask();
        }
    }
}
