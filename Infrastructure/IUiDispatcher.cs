// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Abstraction over the WPF dispatcher. Every subsystem that needs to
    /// marshal work back to the UI thread (filesystem watchers, icon
    /// loaders, scan results) takes this as a dependency instead of
    /// reaching for Application.Current.Dispatcher directly. That keeps
    /// those subsystems decoupled from WPF and unit-testable.
    /// </summary>
    public interface IUiDispatcher
    {
        bool CheckAccess();

        void Invoke(Action action);

        Task InvokeAsync(Action action);
    }
}
