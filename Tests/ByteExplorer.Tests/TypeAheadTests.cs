// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class TypeAheadTests
    {
        private static readonly string[] Names = { "alpha", "Archive.zip", "beta", "report-2024.pdf", "report.txt", "Reports", "zeta" };
        private static readonly long Second = TimeSpan.TicksPerSecond;

        private static int Type(TypeAheadSearch search, string text, int current, long at) =>
            search.Next(text, current, Names.Length, i => Names[i], at);

        [Fact]
        public void Prefix_AccumulatesAndStaysOnAMatchingItem()
        {
            var search = new TypeAheadSearch();
            long t = 100 * Second;
            int index = Type(search, "r", -1, t);
            Assert.Equal(3, index);
            index = Type(search, "e", index, t + Second / 10);
            Assert.Equal(3, index);                              // "re" still matches report-2024.pdf
            index = Type(search, "port.", index, t + Second / 5);
            Assert.Equal(4, index);                              // "report." moves to report.txt
        }

        [Fact]
        public void RepeatedCharacter_CyclesAndWraps()
        {
            var search = new TypeAheadSearch();
            long t = 100 * Second;
            int index = Type(search, "a", -1, t);
            Assert.Equal(0, index);
            index = Type(search, "a", index, t + Second / 10);
            Assert.Equal(1, index);                              // next item starting with a/A
            index = Type(search, "a", index, t + Second / 5);
            Assert.Equal(0, index);                              // wraps around
        }

        [Fact]
        public void Pause_StartsANewPrefix_AndMissesKeepNothing()
        {
            var search = new TypeAheadSearch();
            long t = 100 * Second;
            int index = Type(search, "z", -1, t);
            Assert.Equal(6, index);
            index = Type(search, "b", index, t + 2 * Second);    // after a pause: "b", not "zb"
            Assert.Equal(2, index);
            Assert.Equal(-1, Type(search, "q", index, t + 4 * Second));
        }

        [AvaloniaFact]
        public async Task FileList_SelectsTheTypedItem()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-typeahead-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            foreach (string name in Names)
            {
                File.WriteAllText(Path.Combine(folder, name), "x");
            }

            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await PumpAsync(30);
                var viewModel = (MainViewModel)window.DataContext;
                await viewModel.FileListViewModel.LoadFolderAsync(folder);
                viewModel.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                await PumpAsync(10);

                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                // As after a click: keyboard focus on an item of the list.
                Assert.True(list.ContainerFromIndex(0)?.Focus() ?? false);
                window.KeyTextInput("rep");
                await PumpAsync(4);
                Assert.Equal("report-2024.pdf", viewModel.FileListViewModel.SelectedItem?.Name);

                window.KeyTextInput("ort.t");
                await PumpAsync(4);
                Assert.Equal("report.txt", viewModel.FileListViewModel.SelectedItem?.Name);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, recursive: true);
            }
        }

        private static async Task PumpAsync(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(25);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
