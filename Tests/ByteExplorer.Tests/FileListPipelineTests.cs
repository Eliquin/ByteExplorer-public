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
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The file list's data path: natural name order, the display sorter (serial and parallel), the item cache, and
    /// refreshing the folder shown in place (filter, selection and list kept).
    /// </summary>
    public sealed class FileListPipelineTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-pipe-" + Guid.NewGuid().ToString("N"));

        public FileListPipelineTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A watcher may still hold it for a moment.
            }
        }

        // ------------------------------------------------------------------ natural order

        [Theory]
        [InlineData("file2", "file10", -1)]
        [InlineData("file10", "file2", 1)]
        [InlineData("File2", "file10", -1)]       // case ignored
        [InlineData("IMG_9.jpg", "img_10.jpg", -1)]
        [InlineData("a", "ab", -1)]                // a prefix first
        [InlineData("1", "01", -1)]                // same value: fewer leading zeros first
        [InlineData("x007", "x7", 1)]
        [InlineData("Report 2024-1", "Report 2024-01", -1)]
        [InlineData("v1.10", "v1.9", 1)]
        [InlineData("a", "a", 0)]
        [InlineData("99999999999999999999999", "100000000000000000000000", -1)] // longer than any integer type
        [InlineData("Été", "ete", 1)]              // non-ASCII: invariant upper case, then code point
        public void Natural_Order(string a, string b, int expected)
        {
            Assert.Equal(expected, Math.Sign(NaturalStringComparer.CompareManaged(a, b)));
            Assert.Equal(-expected, Math.Sign(NaturalStringComparer.CompareManaged(b, a)));
        }

        [Fact]
        public void Natural_CaseOnlyDifferences_HaveAFixedOrder()
        {
            Assert.NotEqual(0, NaturalStringComparer.CompareManaged("Readme", "README"));
            Assert.Equal(-Math.Sign(NaturalStringComparer.CompareManaged("Readme", "README")),
                Math.Sign(NaturalStringComparer.CompareManaged("README", "Readme")));
        }

        [Fact]
        public void Natural_SortsAListAsPeopleRead_It()
        {
            string[] names = { "file10.txt", "file1.txt", "File3.txt", "file2.txt", "file20.txt", "file02.txt" };
            Array.Sort(names, (x, y) => NaturalStringComparer.CompareManaged(x, y));
            Assert.Equal(new[] { "file1.txt", "file2.txt", "file02.txt", "File3.txt", "file10.txt", "file20.txt" }, names);
        }

        // ------------------------------------------------------------------ display sorter

        /// <summary>The orders by a field of the entry (Tags orders by a key from the tag store: see Sorter_ByTags_*).</summary>
        public static IEnumerable<object[]> Orders() =>
            from key in Enum.GetValues<EntryOrder>()
            where key != EntryOrder.Tags
            from up in new[] { true, false }
            from size in new[] { 50, DirectoryEntrySorter.ParallelThreshold * 3 + 17 }
            select new object[] { key, up, size };

        [Theory]
        [InlineData(true, 40)]
        [InlineData(false, 40)]
        [InlineData(true, DirectoryEntrySorter.ParallelThreshold * 2 + 5)]
        public void Sorter_ByTags_TaggedInTagOrder_UntaggedLast_FoldersFirst(bool ascending, int count)
        {
            DirectoryIndexEntry[] input = RandomEntries(count, seed: 7);
            string[] tags = { "Blue", "alpha", "Green", string.Empty, string.Empty };
            string Key(DirectoryIndexEntry e) => tags[Math.Abs(e.Name.GetHashCode(StringComparison.Ordinal)) % tags.Length];
            DirectoryIndexEntry[] sorted = DirectoryEntrySorter.Sort(input, EntryOrder.Tags, ascending, tagKey: Key);

            Assert.Equal(input.Length, sorted.Length);
            int folders = sorted.TakeWhile(e => e.IsDirectory).Count();
            Assert.Equal(input.Count(e => e.IsDirectory), folders);
            foreach ((int start, int end) in new[] { (0, folders), (folders, sorted.Length) })
            {
                for (int i = start + 1; i < end; i++)
                {
                    string a = Key(sorted[i - 1]);
                    string b = Key(sorted[i]);
                    if (a.Length == 0)
                    {
                        Assert.True(b.Length == 0, "untagged items come last");
                    }
                    else if (b.Length > 0)
                    {
                        int c = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
                        Assert.True(ascending ? c <= 0 : c >= 0, $"{a} before {b}");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Orders))]
        public void Sorter_FoldersFirst_ThenEachGroupInOrder(EntryOrder order, bool ascending, int count)
        {
            DirectoryIndexEntry[] input = RandomEntries(count, seed: count + (int)order);
            DirectoryIndexEntry[] sorted = DirectoryEntrySorter.Sort(input, order, ascending);

            Assert.Equal(input.Length, sorted.Length);
            Assert.Equal(input.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal), sorted.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            int folders = input.Count(e => e.IsDirectory);
            Assert.All(sorted.Take(folders), e => Assert.True(e.IsDirectory));
            Assert.All(sorted.Skip(folders), e => Assert.False(e.IsDirectory));

            IComparer<DirectoryIndexEntry> comparer = DirectoryEntrySorter.ComparerFor(order, ascending);
            for (int i = 1; i < sorted.Length; i++)
            {
                if (i != folders)
                {
                    Assert.True(comparer.Compare(sorted[i - 1], sorted[i]) <= 0, $"{sorted[i - 1].Name} before {sorted[i].Name}");
                }
            }
        }

        [Fact]
        public void Sorter_NameOrderFromTheIndex_IsReusedOrReversed()
        {
            DirectoryIndexEntry[] byName = DirectoryEntrySorter.Sort(RandomEntries(500, 3), EntryOrder.Name, ascending: true);
            Assert.Equal(byName.Select(e => e.Name), DirectoryEntrySorter.Sort(byName, EntryOrder.Name, true, inNameOrder: true).Select(e => e.Name));

            DirectoryIndexEntry[] descending = DirectoryEntrySorter.Sort(byName, EntryOrder.Name, false, inNameOrder: true);
            Assert.Equal(DirectoryEntrySorter.Sort(byName, EntryOrder.Name, false).Select(e => e.Name), descending.Select(e => e.Name));
            Assert.True(descending[0].IsDirectory); // folders stay first when descending
        }

        [Fact]
        public void Sorter_ParallelSortOfARange_MatchesArraySort()
        {
            var random = new Random(5);
            int[] values = Enumerable.Range(0, 100_020).Select(_ => random.Next(1000)).ToArray();
            int[] expected = (int[])values.Clone();
            Array.Sort(expected, 10, 100_000);
            DirectoryEntrySorter.SortRange(values, 10, 100_000, Comparer<int>.Default);
            Assert.Equal(expected, values);
        }

        // ------------------------------------------------------------------ item cache

        // Items create brushes: Avalonia objects belong on the headless UI thread (see AsyncSafetyTests).
        [AvaloniaFact]
        public void ItemCache_KeepsTheRecentlyUsedItems()
        {
            var index = new DirectoryIndex("/x", RandomEntries(400, 9), false, null);
            var list = new VirtualizingFileList(index, residentWindow: 50);
            var first = (FileListItemViewModel)list[0];
            for (int i = 1; i < 100; i++)
            {
                _ = list[i];
                _ = list[0]; // keeps item 0 recent
            }

            Assert.Same(first, list[0]);
            var evicted = (FileListItemViewModel)list[1];
            Assert.Equal(index.Entries[1].Name, evicted.Name);
            Assert.Equal(1, list.IndexOf(evicted));
        }

        // Items create brushes: Avalonia objects belong on the headless UI thread (see AsyncSafetyTests).
        [AvaloniaFact]
        public void Item_FormatsItsTextOnFirstUse()
        {
            var item = new FileListItemViewModel(new DirectoryIndexEntry("photo.JPG", false, 2048, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), FileAttributes.Normal), "/x");
            Assert.Equal("JPG File", item.TypeDisplay);
            Assert.Same(item.TypeDisplay, new FileListItemViewModel(new DirectoryIndexEntry("b.jpg", false, 1, DateTime.UtcNow, 0), "/x").TypeDisplay);
            Assert.EndsWith("KB", item.SizeDisplay);
            Assert.False(string.IsNullOrEmpty(item.DateModifiedDisplay));
            Assert.Equal("File", new FileListItemViewModel(new DirectoryIndexEntry("Makefile", false, 1, DateTime.UtcNow, 0), "/x").TypeDisplay);
            Assert.Equal("File folder", new FileListItemViewModel(new DirectoryIndexEntry("src", true, 0, DateTime.UtcNow, 0), "/x").TypeDisplay);
        }

        // ------------------------------------------------------------------ refreshing in place

        [AvaloniaFact]
        public async Task LiveChange_KeepsFilterAndSelection_AndAnUnchangedFolderKeepsItsList()
        {
            foreach (string name in new[] { "report-a.txt", "report-b.txt", "notes.txt" })
            {
                File.WriteAllText(Path.Combine(_root, name), "x");
            }

            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel vm = window.ViewModel.FileListViewModel;
                vm.SelectedPreset = FileListViewPreset.Details;
                await vm.LoadFolderAsync(_root);
                await Pump(5);
                vm.FilterText = "report";
                await WaitFor(() => vm.Items?.Count == 2);

                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.IsEffectivelyVisible && ReferenceEquals(l.ItemsSource, vm.Items));
                list.Selection.Select(1);
                await Pump(3);
                Assert.Equal("report-b.txt", vm.SelectedItem?.Name);

                // Nothing changed on disk: the refresh leaves the list alone.
                object before = vm.Items;
                await vm.RefreshCurrentFolderAsync();
                Assert.Same(before, vm.Items);

                // A new matching file appears: the list updates, the filter and the selection stay.
                File.WriteAllText(Path.Combine(_root, "report-c.txt"), "x");
                await WaitFor(() => vm.Items?.Count == 3);
                await Pump(5);
                Assert.Equal("report", vm.FilterText);
                Assert.Equal("report-b.txt", vm.SelectedItem?.Name);
                Assert.False(vm.IsLoading);

                // F5 rebuilds the list even when nothing changed, and keeps the filter too.
                before = vm.Items;
                vm.RefreshCommand.Execute(null);
                await WaitFor(() => !ReferenceEquals(before, vm.Items));
                Assert.Equal("report", vm.FilterText);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Refresh_OfAFolderThatWasDeleted_ShowsTheError()
        {
            string doomed = Directory.CreateDirectory(Path.Combine(_root, "doomed")).FullName;
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel vm = window.ViewModel.FileListViewModel;
                await vm.LoadFolderAsync(doomed);
                await Pump(5);
                Directory.Delete(doomed);
                await vm.RefreshCurrentFolderAsync();
                await WaitFor(() => vm.ErrorMessage != null);
                Assert.NotNull(vm.ErrorMessage);
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static DirectoryIndexEntry[] RandomEntries(int count, int seed)
        {
            var random = new Random(seed);
            string[] extensions = { ".txt", ".JPG", ".pdf", string.Empty, ".tar.gz", ".Txt" };
            var entries = new DirectoryIndexEntry[count];
            for (int i = 0; i < count; i++)
            {
                bool folder = random.Next(10) == 0;
                string name = (random.Next(3) == 0 ? "Item " : "item ") + random.Next(100_000) + "-" + i + (folder ? string.Empty : extensions[random.Next(extensions.Length)]);
                entries[i] = new DirectoryIndexEntry(name, folder, folder ? 0 : random.Next(1000), new DateTime(2020, 1, 1).AddMinutes(random.Next(5000)), FileAttributes.Normal);
            }

            return entries;
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 250 && !condition(); i++)
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
