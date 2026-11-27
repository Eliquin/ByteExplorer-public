// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// A sort or filter that fails is made visible (status bar, a notification, the log) instead of silently leaving the
    /// list as it was; a folder whose sort fails while opening is shown by name rather than not at all.
    /// </summary>
    public sealed class ViewBuildFailureTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-viewfail-" + Guid.NewGuid().ToString("N"));

        public ViewBuildFailureTests()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "b.txt"), new string('x', 10));
            File.WriteAllText(Path.Combine(_root, "a.txt"), new string('x', 500));
            File.WriteAllText(Path.Combine(_root, "c.txt"), new string('x', 100));
        }

        public void Dispose()
        {
            FileListViewModel.SortFaultForTests = null;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [AvaloniaFact]
        public async Task FailedSortOrFilter_IsReported_AndTheListStaysUsable()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            FileListViewModel pane = window.ViewModel.FileListViewModel;
            var raised = new List<string>();
            pane.ViewBuildFailed += (_, message) => raised.Add(message);
            try
            {
                await Pump(30);
                await pane.LoadFolderAsync(_root);
                await Pump(5);
                Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Names(pane));

                // A re-sort that throws: reported, and the list keeps its previous order.
                FileListViewModel.SortFaultForTests = (folder, column) =>
                    PathUtil.AreSame(folder, _root) && column == FileListSortColumn.Size ? new InvalidOperationException("sorter exploded") : null;
                pane.SetSort(FileListSortColumn.Size, false);
                await WaitFor(() => raised.Count == 1);
                Assert.Contains("Couldn't sort by size.", raised[0]);
                Assert.DoesNotContain("sorter exploded", raised[0]); // a programming error's text is for the log, not the person
                Assert.Contains("previous order", raised[0]);
                Assert.Equal(raised[0], pane.StatusText);
                Assert.Equal(raised[0], pane.LastViewBuildFailure);
                Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Names(pane));
                await Pump(3);
                Assert.Contains("Couldn't sort by size", window.FindControl<TextBlock>("ToastText")?.Text ?? FindToastText(window));

                // The same failure again within a few seconds (typing, clicking): the status says it, no second notification.
                pane.SetSort(FileListSortColumn.Size, false);
                await Pump(10);
                Assert.Single(raised);

                // Opening the folder while the sort fails: shown by name, and said so.
                await pane.LoadFolderAsync(_root);
                await WaitFor(() => pane.Items?.Count == 3);
                Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Names(pane));
                Assert.Contains("shown by name instead", pane.StatusText);
                Assert.StartsWith("3 items · ", pane.StatusText);

                // Fixed: the next sort works and the failure is cleared.
                FileListViewModel.SortFaultForTests = null;
                pane.SetSort(FileListSortColumn.Size, false);
                await WaitFor(() => Names(pane).SequenceEqual(new[] { "a.txt", "c.txt", "b.txt" }));
                Assert.Null(pane.LastViewBuildFailure);
            }
            finally
            {
                FileListViewModel.SortFaultForTests = null;
                pane.SetSort(FileListSortColumn.Name, true);
                window.Close();
            }
        }

        private static string FindToastText(Window window) =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>()
                .Select(t => t.Text).FirstOrDefault(t => t != null && t.Contains("Couldn't sort by size", StringComparison.Ordinal)) ?? string.Empty;

        private static string[] Names(FileListViewModel pane) => pane.Items.Cast<FileListItemViewModel>().Select(i => i.Name).ToArray();

        private static async Task WaitFor(Func<bool> condition, int rounds = 250)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition());
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
