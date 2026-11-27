// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Search;
using ByteExplorer.Views.Search;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>Global search in the window: typing, results with highlights, sorting, filters, keys, live updates.</summary>
    public sealed class SearchUiTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-searchui-" + Guid.NewGuid().ToString("N"));

        public SearchUiTests()
        {
            foreach (string file in new[] { "Reports/report-2024.pdf", "Reports/Annual Report.docx", "Photos/report-cover.jpg", "Photos/beach.png", "notes.txt" })
            {
                string path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, new byte[file.Length * 100]);
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [AvaloniaFact]
        public async Task Typing_ShowsRankedHighlightedResults_SortFilterAndOpen()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            List<string> locations = null;
            List<string> saved = null;
            try
            {
                await Pump(30); // startup loads the settings (a new object) first
                locations = main.SettingsService.Current.Index.Locations;
                saved = locations.ToList();
                locations.Clear();
                locations.Add(_root);
                await main.SearchService.RefreshIndexAsync();
                await WaitFor(() => main.IsSearchEnabled && main.SearchService.IndexedRoots.Contains(_root), message: () => $"{main.SearchService.State} {main.SearchService.ShortStatus} / {main.SearchService.DetailStatus} / items {main.SearchService.TotalItems} enabled {main.IsSearchEnabled} roots {string.Join(",", main.SearchService.IndexedRoots)}");

                // Type: results replace the file list, best match first, matching letters highlighted.
                var box = window.FindControl<TextBox>("GlobalSearchBox");
                box.Focus();
                window.KeyTextInput("report");
                await WaitFor(() => main.Search.Results?.Count == 4, message: () => $"{main.Search.Results?.Count} results: {main.Search.StatusText}: {string.Join(" | ", main.Search.Results?.Select(r => r.FullPath) ?? Array.Empty<string>())} roots {string.Join(",", main.SearchService.IndexedRoots)} items {main.SearchService.TotalItems}");
                Assert.True(main.IsSearchActive);
                var view = window.GetVisualDescendants().OfType<SearchResultsView>().Single();
                Assert.True(view.IsEffectivelyVisible);
                SearchResultList results = main.Search.Results;
                Assert.Equal("Reports", results[0].Name); // a folder named like the word, before files that start with it
                Assert.True(results[0].IsDirectory);
                Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, results[0].HighlightPositions);
                Assert.Equal(new[] { 7, 8, 9, 10, 11, 12 }, results.First(r => r.Name == "Annual Report.docx").HighlightPositions);
                Assert.Contains("4 results", main.Search.StatusText);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "search-results.png"));
                }

                // Sort by name; then the Pictures filter.
                main.Search.SortCommand.Execute("Name");
                await WaitFor(() => main.Search.Sort == ByteSearcher.SearchSort.Name && main.Search.Results[0].Name == "Annual Report.docx");
                main.Search.SelectFilterCommand.Execute(main.Search.Filters.Single(f => f.Label == "Pictures"));
                await WaitFor(() => main.Search.Results?.Count == 1);
                Assert.Equal("report-cover.jpg", main.Search.Results[0].Name);
                main.Search.SelectFilterCommand.Execute(main.Search.Filters[0]);
                await WaitFor(() => main.Search.Results?.Count == 4);

                // Live update: a new matching file appears in the results.
                File.WriteAllText(Path.Combine(_root, "Reports", "report-draft.txt"), "x");
                main.SearchService.NotifyFoldersChanged(new[] { Path.Combine(_root, "Reports") });
                await WaitFor(() => main.Search.Results?.Count == 5, rounds: 400);

                // Keys in the results act on them: Ctrl+C copies the selected result, not the hidden file list's selection.
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                await Pump(3);
                Assert.True(view.FindControl<ListBox>("ResultsList").IsKeyboardFocusWithin);
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
                await Pump(3);
                (bool ok, IReadOnlyList<string> files, _) = await ClipboardFileService.TryGetFilesAsync();
                Assert.True(ok);
                Assert.Equal(new[] { main.Search.Results[0].FullPath }, files);

                // Enter on a folder result opens it in the pane and ends the search.
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await Pump(2);
                main.Search.Query = "photos";
                await WaitFor(() => main.Search.Results?.Count >= 1 && main.Search.Results[0].IsDirectory);
                await main.OpenSearchResultAsync(main.Search.Results[0]);
                await WaitFor(() => main.FileListViewModel.CurrentPath == Path.Combine(_root, "Photos"));
                Assert.False(main.IsSearchActive);
            }
            finally
            {
                if (locations != null)
                {
                    locations.Clear();
                    locations.AddRange(saved);
                }

                window.Close();
            }
        }

        private static async Task WaitFor(Func<bool> condition, int rounds = 250, Func<string> message = null)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition(), message?.Invoke());
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
