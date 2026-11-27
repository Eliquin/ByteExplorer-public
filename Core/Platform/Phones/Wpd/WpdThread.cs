// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace ByteExplorer.Core.Platform.Phones.Wpd
{
    /// <summary>
    /// The one thread every Windows Portable Devices call runs on: the device manager, the opened devices, their content
    /// and property objects and the transfer streams are made, used and released here, one call at a time.
    /// <para>
    /// Why: these are COM objects backed by the phone's user-mode driver. Before, they were made on whichever short-lived
    /// worker thread happened to ask first (the drive poll's, which ends after each poll) and then used from other
    /// short-lived threads, sometimes at the same moment (the poll while a folder of the phone was listed or a file
    /// copied). Objects whose creating thread is gone, and a driver called from two threads at once, are how a COM call
    /// ends in an access violation; .NET can't catch that, and the process dies without a word in the log. Here the
    /// objects live as long as the app, in a multithreaded apartment that never goes away, and the driver sees one call
    /// at a time, which is also how Explorer talks to a phone.
    /// </para>
    /// <para>
    /// Callers block until their call has run (they are workers: the drive poll, folder listings, file operations,
    /// each with its own timeout around it). A call made from this thread (one provider method calling another) runs
    /// at once instead of waiting for itself.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class WpdThread
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static readonly Thread Worker = Start();

        public static bool IsCurrent => ReferenceEquals(Thread.CurrentThread, Worker);

        private static Thread Start()
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true, // never keeps the app from exiting
                Name = "ByteExplorer.PortableDevices"
            };
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.MTA); // stated, not left to the first COM call to decide
            }
            thread.Start();
            return thread;
        }

        private static void Loop()
        {
            foreach (Action work in Queue.GetConsumingEnumerable())
            {
                work(); // each item catches its own exceptions (see Run)
            }
        }

        /// <summary>Runs <paramref name="work"/> on the portable-devices thread and returns its result (or throws its exception).</summary>
        public static T Run<T>(Func<T> work)
        {
            if (IsCurrent)
            {
                return work();
            }

            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });

            // GetResult rethrows the call's own exception (not wrapped), with its stack.
            return done.Task.GetAwaiter().GetResult();
        }

        public static void Run(Action work) => Run(() =>
        {
            work();
            return true;
        });
    }
}
