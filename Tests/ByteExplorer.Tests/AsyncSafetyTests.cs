// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Failures of asynchronous work are caught and reported, never thrown into the UI loop. The command tests run in the
    /// Avalonia headless session: AsyncRelayCommand posts to Dispatcher.UIThread, and touching it first from a plain test
    /// thread would bind the UI dispatcher to that thread and break every window test that runs after.
    /// </summary>
    public sealed class AsyncSafetyTests
    {
        [AvaloniaFact]
        public async Task AsyncRelayCommand_ReportsAFailureInsteadOfThrowing()
        {
            Exception reported = null;
            var command = new AsyncRelayCommand(async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            });

            // The event is static and other tests run in parallel: listen to this command only.
            EventHandler<Exception> handler = (sender, ex) =>
            {
                if (ReferenceEquals(sender, command))
                {
                    reported = ex;
                }
            };
            AsyncRelayCommand.Failed += handler;
            try
            {
                command.Execute(null); // async void: must not throw, now or later
                // Failed is raised in the catch; the busy flag clears in the finally right after, maybe on another thread.
                for (int i = 0; i < 500 && (reported == null || !command.CanExecute(null)); i++)
                {
                    await Task.Delay(10);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.IsType<InvalidOperationException>(reported);
                Assert.True(command.CanExecute(null)); // not stuck "busy" after the failure
            }
            finally
            {
                AsyncRelayCommand.Failed -= handler;
            }
        }

        [AvaloniaFact]
        public async Task AsyncRelayCommand_TreatsCancellationAsNoFailure()
        {
            Exception reported = null;
            var command = new AsyncRelayCommand(_ => Task.FromCanceled(new System.Threading.CancellationToken(true)));
            EventHandler<Exception> handler = (sender, ex) =>
            {
                if (ReferenceEquals(sender, command))
                {
                    reported = ex;
                }
            };
            AsyncRelayCommand.Failed += handler;
            try
            {
                command.Execute(null);
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(10);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.Null(reported);
            }
            finally
            {
                AsyncRelayCommand.Failed -= handler;
            }
        }

        [Fact]
        public async Task Forget_ObservesFaultedAndCompletedTasks()
        {
            // Neither call may throw; the failures are logged.
            Task.FromException(new InvalidOperationException("sync")).Forget("test");
            Task.Run(() => throw new InvalidOperationException("async")).Forget("test");
            ((Task)null).Forget("test");
            await Task.Delay(30);
        }
    }
}
