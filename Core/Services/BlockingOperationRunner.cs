// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Runs a synchronous, potentially-blocking operation — a native
    /// Win32 call, a <see cref="System.IO.DriveInfo"/> property read
    /// against a network share, <c>Directory.EnumerateDirectories</c>
    /// against an unresponsive UNC path — under a genuine, enforced
    /// wall-clock deadline.
    ///
    /// Why this exists: a <see cref="CancellationToken"/> can only be
    /// *observed* by cooperative code running between steps. Once
    /// execution is already inside a blocking native/OS call, no token
    /// — however it's configured — can interrupt it; the calling thread
    /// is stuck until the OS's own network timeout resolves it, which
    /// for an unreachable SMB server can be 30-120+ seconds, sometimes
    /// effectively unbounded. Every "NetworkDriveTimeoutMs"-style
    /// setting in this app is only real if something actually enforces
    /// it from the outside — this class is that enforcement.
    ///
    /// The blocking call runs on its own dedicated, background,
    /// below-normal-priority <see cref="Thread"/> — deliberately never
    /// a ThreadPool thread. A ThreadPool thread stuck on a hung network
    /// call doesn't just cost one thread; the pool grows slowly under
    /// sustained starvation (roughly one new thread per ~500ms-1s), so
    /// a single bad network drive can measurably stall every other
    /// background operation in the app (indexing, other navigations,
    /// file operations) for the time it takes the pool to compensate.
    /// A dedicated thread costs the same hang but never touches shared
    /// pool capacity.
    ///
    /// If the operation finishes before the deadline, its result or
    /// exception is returned normally. If the deadline elapses first,
    /// this returns immediately with a <see cref="TimeoutException"/>
    /// (or, if the caller's own token fired, the usual
    /// <see cref="OperationCanceledException"/>) and the dedicated
    /// thread is abandoned — left to finish or die on its own. Nothing
    /// in this class ever calls Thread.Abort or joins that thread; it
    /// holds no ThreadPool capacity and is otherwise harmless to leave
    /// running. Callers that want the eventual result anyway (e.g. to
    /// warm a cache) can have <paramref name="work"/> itself do that as
    /// a side effect — it keeps running to completion regardless of
    /// whether anyone is still awaiting this method's returned task.
    /// </summary>
    public static class BlockingOperationRunner
    {
        /// <summary>
        /// Runs <paramref name="work"/> on a dedicated thread, bounded by
        /// <paramref name="hardTimeoutMs"/>. <paramref name="work"/> is
        /// handed its own <see cref="CancellationToken"/> (signalled once
        /// the deadline or <paramref name="cancellationToken"/> fires)
        /// purely so a well-behaved implementation that checks between
        /// steps (e.g. between directory entries) can stop early and
        /// cheaply once it's actually running — but correctness here
        /// never depends on that cooperation: the deadline is enforced
        /// from the outside regardless of what <paramref name="work"/>
        /// does or doesn't check.
        /// </summary>
        /// <exception cref="TimeoutException">
        /// The operation did not complete within <paramref name="hardTimeoutMs"/>.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled before the
        /// operation completed or timed out.
        /// </exception>
        public static Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            int hardTimeoutMs,
            CancellationToken cancellationToken = default,
            string threadName = "ByteExplorer.BlockingIO")
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (hardTimeoutMs <= 0)
            {
                hardTimeoutMs = 1;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadlineCts = new CancellationTokenSource();
            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(deadlineCts.Token, cancellationToken);

            var thread = new Thread(() =>
            {
                try
                {
                    T result = work(linkedCts.Token);
                    tcs.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    // Classified by whichever fired: the caller's own
                    // token means genuine cancellation; anything else
                    // means our own deadline elapsed. TrySet* is
                    // idempotent, so whichever of this branch or the
                    // registrations below runs first wins and the rest
                    // are harmless no-ops.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        tcs.TrySetException(new TimeoutException(
                            "The operation exceeded its hard timeout of " + hardTimeoutMs.ToString() + "ms."));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = threadName,
                Priority = ThreadPriority.BelowNormal
            };

            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                // Thread creation itself failing (exhausted OS resources,
                // extremely rare) must not throw synchronously out of
                // this method — callers await a task, not a direct call.
                linkedCts.Dispose();
                deadlineCts.Dispose();
                return Task.FromException<T>(ex);
            }

            deadlineCts.CancelAfter(hardTimeoutMs);

            CancellationTokenRegistration deadlineRegistration = deadlineCts.Token.Register(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetException(new TimeoutException(
                        "The operation exceeded its hard timeout of " + hardTimeoutMs.ToString() + "ms."));
                }
            });

            CancellationTokenRegistration externalRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            // Clean-up runs once the observed task settles. The
            // dedicated thread itself is deliberately NOT joined or
            // interrupted here — see the class remarks.
            tcs.Task.ContinueWith(
                _ =>
                {
                    try { deadlineRegistration.Dispose(); } catch { /* best-effort */ }
                    try { externalRegistration.Dispose(); } catch { /* best-effort */ }
                    try { linkedCts.Dispose(); } catch { /* best-effort */ }
                    try { deadlineCts.Dispose(); } catch { /* best-effort */ }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return tcs.Task;
        }
    }
}
