// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.ViewModels.Search;
using ByteExplorer.Views.Search;
using ByteSearcher;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// 94,000 results with long folder paths (the case that froze the window): dragging the scroll bar to the middle
    /// keeps a page of rows, shows the right ones, and stays far from a hang; long folders are cut at the start.
    /// </summary>
    public sealed class SearchResultsScrollTests
    {
        [AvaloniaFact]
        public async Task ManyResults_DragToTheMiddle_StaysResponsive()
        {
            SearchResults results = BuildResults(94_000, out string deepFolder);
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            try
            {
                await Pump(30);
                window.ViewModel.Search.ShowResultsForTests(results);
                await Pump(10);
                var list = window.GetVisualDescendants().OfType<SearchResultsView>().Single().FindControl<ListBox>("ResultsList");
                var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.Equal(94_000, list.ItemCount);

                var stopwatch = Stopwatch.StartNew();
                for (int step = 1; step <= 40; step++)
                {
                    scroller.Offset = new Vector(0, scroller.Extent.Height * 0.5 * step / 40);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"40 scroll steps took {stopwatch.Elapsed.TotalSeconds:F1} s");
                Control[] realized = list.GetRealizedContainers().ToArray();
                Assert.InRange(realized.Length, 1, 60); // a page of rows, not thousands
                int first = realized.Min(c => list.IndexFromContainer(c));
                Assert.InRange(first, 46_000, 47_500); // the rows of the middle are on screen
                var row = realized[0].GetVisualDescendants().OfType<SearchResultRowView>().Single();
                Assert.IsType<SearchResultRow>(row.DataContext);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public void LongFolder_IsCutAtTheStart_ToFit()
        {
            SearchResults results = BuildResults(10, out string deepFolder);
            var list = new SearchResultList(results);
            SearchResultRow row = list[3];
            var typeface = new Typeface(FontFamily.Default);
            string trimmed = SearchResultRowView.FolderText(row, typeface, Brushes.White, 200);
            Assert.StartsWith("…", trimmed);
            Assert.EndsWith(trimmed.Substring(1), row.FolderPath);
            Assert.True(SearchResultRowView.Measure(trimmed, typeface) <= 200);
            Assert.Same(trimmed, SearchResultRowView.FolderText(row, typeface, Brushes.White, 200)); // kept with the row
            Assert.Equal(row.FolderPath, SearchResultRowView.FolderText(row, typeface, Brushes.White, 100_000)); // fits: whole
        }

        private static SearchResults BuildResults(int count, out string deepFolder)
        {
            var builder = new IndexBuilder();
            var batch = new EntryBatch();
            batch.Add("/data", -1, EntryFlags.Directory, 0, 0, DateTime.UtcNow.Ticks);
            builder.Append(batch);
            batch.Clear();
            int parent = 0;
            deepFolder = "/data";
            for (int depth = 0; depth < 14; depth++)
            {
                string name = "very long folder name number " + depth + " with extra words";
                deepFolder += "/" + name;
                batch.Add(name, parent, EntryFlags.Directory, 0, 0, DateTime.UtcNow.Ticks);
                parent = builder.Append(batch);
                batch.Clear();
            }

            var random = new Random(1);
            int total = count + 15;
            while (builder.Count < total)
            {
                for (int i = 0; i < EntryBatch.MaxCount && builder.Count + batch.Count < total; i++)
                {
                    batch.Add("quarterly report draft " + random.Next(1_000_000) + ".pdf", parent, EntryFlags.None, 1, random.Next(1, 1 << 20), DateTime.UtcNow.Ticks);
                }

                builder.Append(batch);
                batch.Clear();
            }

            return SearchEngine.Run(builder.Finish(), SearchQuery.Parse("report"), new SearchOptions(), CancellationToken.None);
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
