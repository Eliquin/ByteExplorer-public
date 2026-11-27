// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Fire-and-forget with the failure observed. A task discarded with "_ = DoAsync()" reports an exception only when the
    /// garbage collector finalizes it (TaskScheduler.UnobservedTaskException), which may be much later or never; this
    /// logs it as soon as the task fails, with the caller's description. Cancellation is expected and not logged.
    /// </summary>
    public static class TaskExtensions
    {
        public static void Forget(this Task task, string context)
        {
            if (task == null)
            {
                return;
            }

            if (task.IsCompleted)
            {
                // The common synchronous-completion case: no continuation to allocate.
                Observe(task, context);
                return;
            }

            task.ContinueWith(
                static (t, state) => Observe(t, (string)state),
                context,
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void Observe(Task task, string context)
        {
            if (task.IsFaulted)
            {
                Exception ex = task.Exception?.GetBaseException();
                if (!(ex is OperationCanceledException))
                {
                    Log.Logger.Error(ex, "Background operation failed: {Context}", context);
                }
            }
        }
    }
}
