// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Lens;
using ByteExplorer.ViewModels.Search;
using ByteExplorer.Views.Lens;
using ByteSearcher;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Lens (left panel › Lens): the time groups and the river, the rows with their headings, the index leaving out
    /// system folders, the view model over a source, and the Modern window showing a lens in place of the file list.
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class LensTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 6, 18, 16, 0, 0, DateTimeKind.Local); // a Thursday

        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-lens-" + Guid.NewGuid().ToString("N"));
        private readonly string _store = Path.Combine(Path.GetTempPath(), "be-lens-store-" + Guid.NewGuid().ToString("N"));

        public LensTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            foreach (string folder in new[] { _root, _store })
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
            }
        }

        // ------------------------------------------------------------------ time groups

        [Theory]
        [InlineData("2026-06-18 09:10", "This morning")]
        [InlineData("2026-06-18 13:00", "This afternoon")]
        [InlineData("2026-06-18 19:30", "This evening")]
        [InlineData("2026-06-17 22:00", "Yesterday")]
        [InlineData("2026-06-15 08:00", "Earlier this week")] // Monday
        [InlineData("2026-06-10 08:00", "Last week")]
        [InlineData("2026-06-02 08:00", "Earlier this month")]
        [InlineData("2026-03-02 08:00", "March")]
        [InlineData("2024-11-30 08:00", "November 2024")]
        public void Times_FallUnderTheHeadingsPeopleUse(string when, string heading)
        {
            DateTime local = DateTime.Parse(when, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(heading, LensLayout.GroupLabel(local, Now));
        }

        [Fact]
        public void TheLayout_GroupsNewestFirst_AndTheRiverCountsMonthsThenYears()
        {
            DateTime[] times =
            {
                At("2026-06-18 15:00"), At("2026-06-18 14:00"), At("2026-06-18 10:00"),
                At("2026-06-17 10:00"),
                At("2026-05-03 10:00"), At("2026-05-02 10:00"),
                At("2025-01-01 10:00"),
                At("2021-03-01 10:00"),
            };
            LensLayout layout = LensLayout.Build(times.Length, i => times[i].ToUniversalTime(), Now);

            Assert.Equal(new[] { "This afternoon", "This morning", "Yesterday", "May", "January 2025", "March 2021" }, layout.GroupLabels);
            Assert.Equal(new[] { 0, 2, 3, 4, 6, 7 }, layout.GroupStarts);
            Assert.Equal(times.Length + 6, layout.RowCount);

            LensRiverBucket june = layout.River[0];
            Assert.Equal(4, june.Count);
            Assert.Equal(1.0, june.Fraction);
            Assert.Equal(0, june.FirstRow); // the first heading
            Assert.Equal(2, layout.River[1].Count); // May
            Assert.Equal(0, layout.River[2].Count); // April: empty, a sliver, can't be jumped to
            Assert.False(layout.River[2].HasFiles);
            Assert.Equal(12 + 5, layout.River.Count); // twelve months, then the years back to the oldest file's (2025…2021)
            Assert.Equal("2021", layout.River[^1].Label);
            Assert.Equal(1, layout.River[^1].Count);
        }

        [Fact]
        public void Rows_PutAHeadingBeforeEachGroup_AndMapBothWays()
        {
            DateTime[] times = { At("2026-06-18 15:00"), At("2026-06-18 10:00"), At("2026-06-18 09:00"), At("2026-06-17 10:00") };
            SearchResults results = Results(times.Select((t, i) => ("f" + i + ".txt", t)).ToArray());
            var rows = new LensRowList(results, LensLayout.Build(times.Length, results.ModifiedUtcAt, Now));

            Assert.Equal(7, rows.Count);
            Assert.Equal("This afternoon", Assert.IsType<LensGroupHeader>(rows[0]).Label);
            Assert.Equal("f0.txt", Assert.IsType<SearchResultRow>(rows[1]).Name);
            LensGroupHeader morning = Assert.IsType<LensGroupHeader>(rows[2]);
            Assert.Equal("This morning", morning.Label);
            Assert.Equal("2 files", morning.CountText);
            Assert.Equal("f2.txt", Assert.IsType<SearchResultRow>(rows[4]).Name);
            Assert.Equal("Yesterday", Assert.IsType<LensGroupHeader>(rows[5]).Label);
            Assert.Equal("f3.txt", Assert.IsType<SearchResultRow>(rows[6]).Name);

            Assert.Equal(4, rows.RowOfItem(2));
            Assert.Equal(1, rows.GroupOfRow(4));
            Assert.Equal(4, ((System.Collections.IList)rows).IndexOf(rows[4]));
            Assert.Equal(5, ((System.Collections.IList)rows).IndexOf(rows[5]));
        }

        // ------------------------------------------------------------------ the index

        [Fact]
        public async Task TheIndex_LeavesOutTheFoldersALensExcludes()
        {
            Write("Pictures/beach.jpg");
            Write("Pictures/2026/sunset.png");
            Write("Programs/App/icons/toolbar.png");
            Write("Programs/App/splash.jpg");
            using var index = new SearchIndex(_store);
            await index.BuildIndexAsync(restrictToRoots: new[] { _root });

            SearchResults all = index.Search("kind:image", new SearchOptions { FoldersOnly = false });
            Assert.Equal(4, all.Count);

            SearchResults lens = index.Search("kind:image", new SearchOptions
            {
                FoldersOnly = false,
                ExcludedFolders = new[] { Path.Combine(_root, "Programs"), Path.Combine(_root, "not-indexed") }
            });
            Assert.Equal(new[] { "beach.jpg", "sunset.png" }, Enumerable.Range(0, lens.Count).Select(i => lens.GetHit(i).Name).OrderBy(n => n));
            Assert.Equal(lens.GetHit(0).ModifiedUtc, lens.ModifiedUtcAt(0));

            // A re-sort keeps the exclusion.
            Assert.Equal(2, lens.Sorted(SearchSort.Name, false).Count);
        }

        // ------------------------------------------------------------------ the view model

        [AvaloniaFact]
        public async Task TheViewModel_ShowsALens_FiltersIt_CountsTheLenses_AndCloses()
        {
            var source = new FakeSource(Results(("today.docx", DateTime.Now), ("older.pdf", DateTime.Now.AddDays(-40))));
            var vm = new LensViewModel(source, new UiDispatcher(Dispatcher.UIThread), () => false);

            await vm.OpenAsync("photos");
            Assert.True(vm.IsActive);
            Assert.Equal("Photos", vm.Title);
            Assert.Equal("kind:image", source.Queries[^1]);
            Assert.True(source.Options[^1].ExcludedFolders.Count > 0);
            Assert.False(source.Options[^1].IncludeHidden);
            Assert.Equal(SearchSort.Modified, source.Options[^1].Sort);
            Assert.True(source.Options[^1].Descending);
            Assert.Equal("2 files", vm.Summary);
            Assert.Equal(4, vm.Rows.Count); // two headings, two files
            Assert.Equal(1, vm.Lenses.Count(l => l.IsActive));
            Assert.Equal(0, vm.CurrentRiverIndex);

            vm.FilterText = "invoice";
            await WaitUntil(() => source.Queries[^1] == "kind:image invoice");

            source.Queries.Clear();
            source.Options.Clear();
            await vm.RefreshCountsAsync(force: true);
            Assert.Equal(LensCatalog.All.Count, source.Queries.Count);
            Assert.All(source.Options, o => Assert.Equal(1, o.MaxResults));
            Assert.All(vm.Lenses, l => Assert.Equal("2", l.CountText));

            vm.Close();
            Assert.False(vm.IsActive);
            Assert.Empty(vm.Rows);
            Assert.DoesNotContain(vm.Lenses, l => l.IsActive);
        }

        [AvaloniaFact]
        public async Task BeforeTheIndexIsReady_TheLensSaysSo()
        {
            var source = new FakeSource(Results()) { Ready = false };
            var vm = new LensViewModel(source, new UiDispatcher(Dispatcher.UIThread), () => false);
            await vm.OpenAsync("today");
            Assert.True(vm.IsEmpty);
            Assert.Contains("search index", vm.EmptyText);
            Assert.Empty(source.Queries);
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task TheModernWindow_ListsTheLenses_ShowsOneInPlaceOfTheFiles_AndLeavesItForAFolder()
        {
            var items = new List<(string, DateTime)>();
            for (int i = 0; i < 40; i++)
            {
                items.Add(($"photo-{i:00}.jpg", DateTime.Now.AddDays(-i * 9)));
            }

            var source = new FakeSource(Results(items.ToArray()));
            var window = new ModernMainWindow { Width = 1300, Height = 860 };
            window.Show();
            try
            {
                await Pump(30);
                window.ViewModel.Lens.UseSourceForTests(source);
                ItemsControl lensRows = window.FindControl<ItemsControl>("LensRows");
                Assert.NotNull(lensRows);
                Assert.Equal(LensCatalog.All.Count, lensRows.ItemCount);

                await window.ViewModel.OpenLensAsync(window.ViewModel.Lens.Lenses.First(l => l.Definition.Id == "photos"));
                await Pump(10);
                LensView lens = window.FindControl<LensView>("LensPanel");
                Assert.True(lens.IsVisible);
                Assert.False(window.FindControl<Grid>("PaneGrid").IsVisible);
                Assert.True(window.ViewModel.IsLensActive);
                Assert.Contains(lens.GetVisualDescendants().OfType<LensRowView>(), r => r.DataContext is LensGroupHeader);
                Assert.Contains(lens.GetVisualDescendants().OfType<LensRowView>(), r => r.DataContext is SearchResultRow);
                Assert.True(lens.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("riverBar")) >= 12);

                // A river bar jumps there: the list's top row moves to that period.
                LensRiverBucket last = window.ViewModel.Lens.River.Last(b => b.HasFiles);
                lens.ScrollToTop(last.FirstRow);
                await Pump(10);
                var riverNow = window.ViewModel.Lens.River;
                Assert.True(last.IsCurrent, $"current {window.ViewModel.Lens.CurrentRiverIndex}, clicked {riverNow.ToList().IndexOf(last)} row {last.FirstRow}, rows {window.ViewModel.Lens.Rows.Count}, firsts {string.Join(",", riverNow.Select(b => b.FirstRow))}");

                SaveScreenshot(window, "lens-photos.png");

                // The address bar says where we are; Back leaves the lens for the folder it was opened over.
                Assert.Equal(new[] { "Lens", "Photos" }, window.ViewModel.Omnibar.Crumbs.Select(c => c.Name));
                Assert.Equal(ByteExplorer.Icons.Symbol.Sparkle, window.ViewModel.Omnibar.LocationSymbol);
                Assert.False(window.ViewModel.Omnibar.CanPin);
                await window.ViewModel.OpenLensAsync(window.ViewModel.Lens.Lenses.First(l => l.Definition.Id == "today"));
                await Pump(3);
                Assert.Equal(new[] { "Lens", "Today" }, window.ViewModel.Omnibar.Crumbs.Select(c => c.Name));
                Assert.True(window.ViewModel.FileListViewModel.NavigateBackCommand.CanExecute(null));
                window.ViewModel.FileListViewModel.NavigateBackCommand.Execute(null);
                await Pump(3);
                Assert.False(window.ViewModel.IsLensActive);
                Assert.DoesNotContain(window.ViewModel.Omnibar.Crumbs, c => c.Kind == ByteExplorer.Core.Omnibar.CrumbKind.Lens);
                await window.ViewModel.OpenLensAsync(window.ViewModel.Lens.Lenses.First(l => l.Definition.Id == "photos"));
                await Pump(5);

                // Opening a folder brings the file list back.
                await window.ViewModel.FileListViewModel.LoadFolderAsync(_root);
                await Pump(5);
                Assert.False(window.ViewModel.IsLensActive);
                Assert.False(lens.IsVisible);
                Assert.True(window.FindControl<Grid>("PaneGrid").IsVisible);
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static DateTime At(string local) =>
            DateTime.SpecifyKind(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Local);

        private static SearchResults Results(params (string Name, DateTime Modified)[] items)
        {
            var hits = new List<SearchHit>();
            int id = 0;
            foreach ((string name, DateTime modified) in items.OrderByDescending(i => i.Modified))
            {
                string path = Path.Combine(Path.GetTempPath(), "lens-fake", name);
                hits.Add(new SearchHit(id++, name, path, false, false, 1234, modified.ToUniversalTime(), FileKind.Image, Array.Empty<TextRange>()));
            }

            return SearchResults.FromHits(hits, string.Empty, new SearchOptions(), TimeSpan.Zero);
        }

        private void Write(string relative)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[10]);
        }

        private static void SaveScreenshot(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 300)
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

        /// <summary>Answers every search with the same results; counts are their number; remembers what was asked.</summary>
        private sealed class FakeSource : ILensSource
        {
            private readonly SearchResults _results;

            public FakeSource(SearchResults results) => _results = results;

            public bool Ready { get; set; } = true;
            public bool IsReady => Ready;
            public List<string> Queries { get; } = new List<string>();
            public List<SearchOptions> Options { get; } = new List<SearchOptions>();

            public event EventHandler Changed { add { } remove { } }

            public Task<SearchResults> SearchAsync(string query, SearchOptions options, CancellationToken cancellationToken)
            {
                Queries.Add(query);
                Options.Add(options);
                return Task.FromResult(_results);
            }
        }
    }
}
