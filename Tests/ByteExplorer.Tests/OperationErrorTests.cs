// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.FileOperations;
using ByteExplorer.Views.Dialogs;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The error question (Retry, Skip, Skip all, Cancel) and "Retry failed". A failing file is simulated with the engine's
    /// test hook throwing a permission error (EACCES: not retried on its own) while the test wants it to fail.
    /// </summary>
    public sealed class OperationErrorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-errors-" + Guid.NewGuid().ToString("N"));
        private readonly string _src;
        private readonly string _dst;
        private readonly HashSet<string> _failing = new HashSet<string>();
        private readonly Action<string, long> _hook;

        public OperationErrorTests()
        {
            _src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
            _dst = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
            foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
            {
                File.WriteAllText(Path.Combine(_src, name), "content of " + name);
            }

            _hook = (path, bytes) =>
            {
                lock (_failing)
                {
                    if (path.StartsWith(_dst, StringComparison.Ordinal) && _failing.Any(name => path.Contains("." + name + ".", StringComparison.Ordinal)))
                    {
                        throw new IOException("Permission denied", 13);
                    }
                }
            };
            FileOperationService.AfterBufferForTests += _hook;
        }

        public void Dispose()
        {
            FileOperationService.AfterBufferForTests -= _hook;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private string[] Sources => new[] { "1.txt", "2.txt", "3.txt" }.Select(n => Path.Combine(_src, n)).ToArray();

        private void Fail(params string[] names)
        {
            lock (_failing)
            {
                _failing.Clear();
                _failing.UnionWith(names);
            }
        }

        [Fact]
        public async Task Retry_TriesTheSameItemAgain_AndTheOperationCompletes()
        {
            Fail("2.txt");
            var resolver = new ScriptedResolver(() =>
            {
                Fail(); // the person closed the program holding it
                return ErrorChoice.Retry;
            });

            FileOperationOutcome outcome = await new FileOperationService(null).TransferAsync(FileOperationKind.Copy, Sources, _dst, new FileOperationContext { ErrorResolver = resolver });

            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal(1, resolver.Calls);
            Assert.Equal("2.txt", Path.GetFileName(resolver.Errors[0].Path));
            Assert.Contains("Permission denied", resolver.Errors[0].Message);
            Assert.Equal(3, Directory.GetFiles(_dst).Length);
        }

        [Fact]
        public async Task SkipAll_AsksOnce_AndListsEveryFailureWithWhereItWasGoing()
        {
            Fail("1.txt", "3.txt");
            var resolver = new ScriptedResolver(() => ErrorChoice.SkipAll);

            FileOperationOutcome outcome = await new FileOperationService(null).TransferAsync(FileOperationKind.Copy, Sources, _dst, new FileOperationContext { ErrorResolver = resolver });

            Assert.Equal(1, resolver.Calls);
            Assert.Equal(new[] { "1.txt", "3.txt" }, outcome.Result.Failures.Select(f => Path.GetFileName(f.SourcePath)).OrderBy(n => n));
            Assert.All(outcome.Result.Failures, f => Assert.Equal(_dst, f.RetryDestination));
            Assert.Equal(new[] { "2.txt" }, Directory.GetFiles(_dst).Select(Path.GetFileName)); // no partial files either
        }

        [Fact]
        public async Task Cancel_StopsTheOperation()
        {
            Fail("1.txt");
            var resolver = new ScriptedResolver(() => ErrorChoice.Cancel);
            FileOperationOutcome outcome = await new FileOperationService(null).TransferAsync(FileOperationKind.Copy, Sources, _dst, new FileOperationContext { ErrorResolver = resolver });
            Assert.True(outcome.Result.WasCancelled);
            Assert.Empty(Directory.GetFiles(_dst));
        }

        [Fact]
        public async Task WithTheSettingOff_NothingIsAsked_FailuresAreReportedAtTheEnd()
        {
            var settings = new SettingsService(Path.Combine(_root, "settings.json"));
            settings.Current.FileList.AskOnFileOperationErrors = false;
            Fail("2.txt");
            var resolver = new ScriptedResolver(() => ErrorChoice.Cancel);

            FileOperationOutcome outcome = await new FileOperationService(settings).TransferAsync(FileOperationKind.Copy, Sources, _dst, new FileOperationContext { ErrorResolver = resolver });

            Assert.Equal(0, resolver.Calls);
            Assert.Single(outcome.Result.Failures);
            Assert.Equal(2, Directory.GetFiles(_dst).Length);
        }

        [AvaloniaFact]
        public async Task RetryFailed_FromTheFinishedJob_SendsOnlyTheFailedItemsWhereTheyWereGoing()
        {
            string nested = Directory.CreateDirectory(Path.Combine(_src, "folder")).FullName;
            File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep");
            Fail("2.txt", "deep.txt");
            var queue = new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null); // no one to ask: skipped

            FileOperationOutcome first = await queue.TransferAsync(FileOperationKind.Copy, Sources.Append(nested).ToArray(), _dst).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, first.Result.Failures.Count);
            FileOperationJob job = queue.Jobs.Single(j => j.IsFinished);
            Assert.True(job.CanRetry);

            Fail(); // the cause is gone
            job.Retry();
            Assert.False(job.CanRetry); // offered once
            for (int i = 0; i < 250 && queue.HasActiveJobs; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal("content of 2.txt", File.ReadAllText(Path.Combine(_dst, "2.txt")));
            Assert.Equal("deep", File.ReadAllText(Path.Combine(_dst, "folder", "deep.txt"))); // into its own subfolder
            Assert.Equal(4, Directory.GetFiles(_dst, "*", SearchOption.AllDirectories).Length);
        }

        [AvaloniaFact]
        public void TheErrorDialog_OffersRetrySkipSkipAllAndCancel()
        {
            var dialog = new ErrorDialog(new ErrorInfo(FileOperationKind.Move, "/home/me/report.docx", "The file is open in another program."));
            dialog.Show();
            try
            {
                string[] buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>()
                    .Select(b => b.Content as string).ToArray();
                Assert.Equal(new[] { "Retry", "Skip", "Skip all", "Cancel" }, buttons);
                string text = string.Join(" ", Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));
                Assert.Contains("\"report.docx\" couldn't be moved", text);
                Assert.Contains("open in another program", text);
                Assert.Equal(ErrorChoice.Cancel, dialog.Choice); // closing without an answer cancels
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    dialog.CaptureRenderedFrame()?.Save(Path.Combine(directory, "error-dialog.png"));
                }
            }
            finally
            {
                dialog.Close();
            }
        }

        private sealed class ScriptedResolver : IErrorResolver
        {
            private readonly Func<ErrorChoice> _answer;

            public ScriptedResolver(Func<ErrorChoice> answer) => _answer = answer;

            public int Calls => Errors.Count;

            public List<ErrorInfo> Errors { get; } = new List<ErrorInfo>();

            public Task<ErrorChoice> ResolveAsync(ErrorInfo error, CancellationToken cancellationToken)
            {
                Errors.Add(error);
                return Task.FromResult(_answer());
            }
        }
    }
}
