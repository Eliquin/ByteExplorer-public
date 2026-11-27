// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using Avalonia.Threading;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The UI thread's dispatcher, captured once on the UI thread when the app starts. Code that may run on a background
    /// thread (decoders, file watchers, index events, cancellation callbacks) posts through this instead of reading
    /// <see cref="Dispatcher.UIThread"/>.
    /// </summary>
    /// <remarks>
    /// Why not <see cref="Dispatcher.UIThread"/>: its getter creates the dispatcher on first use, on whatever thread asks,
    /// from the platform registered at that moment, and is not atomic. In the app that happens once at startup, but where
    /// the dispatcher is ever cleared and rebuilt (Avalonia's headless tests with per-test isolation), a background thread
    /// reading it at the wrong moment installs a dispatcher with no platform behind it (NullDispatcherImpl), and the UI
    /// thread's next frame fails with PlatformNotSupportedException. Reading a field never creates anything; work from such
    /// a thread goes to the dispatcher it belongs to, and a shut-down dispatcher drops it. Before the app starts (unit
    /// tests without one) there is no UI to update and the work is dropped instead of creating a dispatcher.
    /// </remarks>
    public static class UiThread
    {
        private static Dispatcher s_dispatcher;

        /// <summary>The captured dispatcher; null before <see cref="Capture"/> (no UI exists yet).</summary>
        public static Dispatcher Dispatcher => Volatile.Read(ref s_dispatcher);

        /// <summary>Called on the UI thread once the platform is set up (App.Initialize).</summary>
        public static void Capture() => Volatile.Write(ref s_dispatcher, Avalonia.Threading.Dispatcher.UIThread);

        /// <summary>True on the UI thread; false before the app has started.</summary>
        public static bool CheckAccess() => Dispatcher?.CheckAccess() == true;

        /// <summary>Runs the action on the UI thread later. Before the app has started there is no UI to update, so it is dropped.</summary>
        public static void Post(Action action) => Dispatcher?.Post(action);

        /// <inheritdoc cref="Post(Action)"/>
        public static void Post(Action action, DispatcherPriority priority) => Dispatcher?.Post(action, priority);
    }
}
