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
using ByteExplorer.Controls;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using ByteExplorer.Views.Dialogs;
using SkiaSharp;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The Details view's chosen columns: the layout, its settings, new sorts, cell texts, the header's menu and the chooser.</summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class DetailsColumnsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-columns-" + Guid.NewGuid().ToString("N"));

        public DetailsColumnsTests()
        {
            Directory.CreateDirectory(_root);
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

        [Fact]
        public void Layout_AddsRemovesMovesAndKeepsNameFirst()
        {
            DetailsColumnLayout layout = DetailsColumnLayout.Default;
            Assert.Equal(new[] { DetailsColumnKind.Name, DetailsColumnKind.DateModified, DetailsColumnKind.Type, DetailsColumnKind.Size, DetailsColumnKind.Tags }, layout.Kinds);
            Assert.Equal(320 + 150 + 130 + 90 + 170, layout.TotalWidth);
            Assert.Equal(ItemDetailsNeeds.None, layout.Needs);

            DetailsColumnLayout added = layout.With(DetailsColumnKind.Dimensions);
            Assert.Equal(DetailsColumnKind.Dimensions, added.KindAt(added.Count - 1));
            Assert.Equal(DetailsColumnCatalog.Get(DetailsColumnKind.Dimensions).DefaultWidth, added[added.Count - 1]);
            Assert.True(added.Needs.HasFlag(ItemDetailsNeeds.Image));
            Assert.Same(added, added.With(DetailsColumnKind.Dimensions));

            Assert.False(added.Without(DetailsColumnKind.Type).Contains(DetailsColumnKind.Type));
            Assert.Same(added, added.Without(DetailsColumnKind.Name)); // Name can't be hidden
            Assert.Same(added, added.Without(DetailsColumnKind.Album)); // not shown

            DetailsColumnLayout moved = layout.Moved(DetailsColumnKind.Size, -1);
            Assert.Equal(new[] { DetailsColumnKind.Name, DetailsColumnKind.DateModified, DetailsColumnKind.Size, DetailsColumnKind.Type, DetailsColumnKind.Tags }, moved.Kinds);
            Assert.Equal(90, moved[2]); // the width moves with it
            Assert.Same(layout, layout.Moved(DetailsColumnKind.DateModified, -1)); // nothing goes before Name
            Assert.Same(layout, layout.Moved(DetailsColumnKind.Tags, +1));

            Assert.Equal(DetailsColumnLayout.MinColumnWidth, layout.WithWidth(1, 3)[1]);
            Assert.Equal(DetailsColumnLayout.MaxColumnWidth, layout.WithWidth(1, 1e9)[1]);
            Assert.Equal(DetailsColumnLayout.MinColumnWidth, layout.WithWidth(1, double.NaN)[1]);
        }

        [Fact]
        public void Layout_RoundTripsThroughTheSettings_AndMendsBadEntries()
        {
            DetailsColumnLayout layout = DetailsColumnLayout.Default.With(DetailsColumnKind.Owner, 210.4).Without(DetailsColumnKind.Tags);
            List<DetailsColumnEntry> entries = layout.ToEntries();
            Assert.Equal(new[] { "Name", "DateModified", "Type", "Size", "Owner" }, entries.Select(e => e.Key));
            Assert.Equal(210, entries[4].Width);
            DetailsColumnLayout back = DetailsColumnLayout.FromEntries(entries);
            Assert.Equal(layout.Kinds, back.Kinds);

            DetailsColumnLayout mended = DetailsColumnLayout.FromEntries(new[]
            {
                new DetailsColumnEntry { Key = "Size", Width = -5 },
                new DetailsColumnEntry { Key = "NoSuchColumn", Width = 100 },
                new DetailsColumnEntry { Key = "size", Width = 100 }, // repeated
                null,
                new DetailsColumnEntry { Key = "Length", Width = double.PositiveInfinity },
                new DetailsColumnEntry { Key = "name", Width = 400 },
            });
            Assert.Equal(new[] { DetailsColumnKind.Name, DetailsColumnKind.Size, DetailsColumnKind.Length }, mended.Kinds);
            Assert.Equal(400, mended[0]);
            Assert.Equal(DetailsColumnCatalog.Get(DetailsColumnKind.Size).DefaultWidth, mended[1]);
            Assert.Equal(DetailsColumnCatalog.Get(DetailsColumnKind.Length).DefaultWidth, mended[2]);

            Assert.Same(DetailsColumnLayout.Default, DetailsColumnLayout.FromEntries(Array.Empty<DetailsColumnEntry>()));
            Assert.Equal(new[] { DetailsColumnKind.Name, DetailsColumnKind.Album },
                DetailsColumnLayout.FromEntries(new[] { new DetailsColumnEntry { Key = "Album", Width = 100 } }).Kinds);

            var settings = new FileListSettings { DetailsColumns = Enumerable.Range(0, 100).Select(i => new DetailsColumnEntry { Key = "Size", Width = i }).Append(null).ToList() };
            settings.Sanitize();
            Assert.True(settings.DetailsColumns.Count <= 64);
            Assert.DoesNotContain(null, settings.DetailsColumns);
        }

        [Fact]
        public void Catalog_IsInEnumOrder_AndOnlyListingColumnsSort()
        {
            foreach (DetailsColumnKind kind in Enum.GetValues<DetailsColumnKind>())
            {
                Assert.Equal(kind, DetailsColumnCatalog.Get(kind).Kind);
                DetailsColumnInfo info = DetailsColumnCatalog.Get(kind);
                Assert.False(string.IsNullOrWhiteSpace(info.Title));
                Assert.False(string.IsNullOrWhiteSpace(info.Description));
                Assert.True(info.Needs == ItemDetailsNeeds.None || info.Sort == null, kind + ": read file by file, so it can't sort");
            }

            Assert.Equal(Enum.GetValues<DetailsColumnKind>().Length, DetailsColumnCatalog.All.Count);
            Assert.True(DetailsColumnCatalog.TryParse("datecreated", out DetailsColumnKind created));
            Assert.Equal(DetailsColumnKind.DateCreated, created);
            Assert.False(DetailsColumnCatalog.TryParse("42", out _));
        }

        [AvaloniaFact] // FileItemView's static brushes need the (headless) platform
        public void CellTexts_ShowTheListingsFacts()
        {
            DateTime created = new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Utc);
            var entry = new DirectoryIndexEntry("report.PDF", false, 1234, created.AddDays(2), FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive,
                created, created.AddDays(9));
            var item = new FileListItemViewModel(entry, _root);
            Assert.Equal(".PDF", FileItemView.CellText(item, DetailsColumnKind.Extension, null));
            Assert.Equal(PathUtil.TrimTrailingSeparator(_root), PathUtil.TrimTrailingSeparator(FileItemView.CellText(item, DetailsColumnKind.FolderPath, null)));
            Assert.Equal("RHA", FileItemView.CellText(item, DetailsColumnKind.Attributes, null));
            Assert.Equal(item.DateCreatedDisplay, FileItemView.CellText(item, DetailsColumnKind.DateCreated, null));
            Assert.NotEqual(item.DateCreatedDisplay, item.DateAccessedDisplay);
            Assert.False(string.IsNullOrEmpty(item.DateCreatedDisplay));

            var unknown = new DirectoryIndexEntry("x.txt", false, 1, created, FileAttributes.Normal);
            Assert.Equal(string.Empty, new FileListItemViewModel(unknown, _root).DateCreatedDisplay); // never read: no 1601 date
            Assert.Equal("D", FileListItemViewModel.AttributeLetters(FileAttributes.Directory));
            Assert.Equal("SLCEO", FileListItemViewModel.AttributeLetters(FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.Offline));

            var details = new ItemDetails { Width = 1920, Height = 1080, Length = TimeSpan.FromSeconds(3725), Artist = "Someone", Album = "Record", Owner = "me", Camera = "Cam X" };
            Assert.Equal($"{1920:N0} × {1080:N0}", FileItemView.CellText(item, DetailsColumnKind.Dimensions, details));
            Assert.Equal(FileMetadataReader.FormatDuration(TimeSpan.FromSeconds(3725)), FileItemView.CellText(item, DetailsColumnKind.Length, details));
            Assert.Equal("Someone", FileItemView.CellText(item, DetailsColumnKind.Artist, details));
            Assert.Equal("Record", FileItemView.CellText(item, DetailsColumnKind.Album, details));
            Assert.Equal("me", FileItemView.CellText(item, DetailsColumnKind.Owner, details));
            Assert.Equal("Cam X", FileItemView.CellText(item, DetailsColumnKind.Camera, details));
            Assert.Equal(string.Empty, FileItemView.CellText(item, DetailsColumnKind.Dimensions, null) ?? string.Empty);
        }

        [Fact]
        public void Sorter_SortsByDateCreatedAndAccessed()
        {
            DateTime t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var entries = new List<DirectoryIndexEntry>
            {
                new DirectoryIndexEntry("b", false, 1, t, FileAttributes.Normal, t.AddDays(3), t.AddDays(1)),
                new DirectoryIndexEntry("a", false, 1, t, FileAttributes.Normal, t.AddDays(1), t.AddDays(3)),
                new DirectoryIndexEntry("c", false, 1, t, FileAttributes.Normal, t.AddDays(2), t.AddDays(2)),
            };
            var created = entries.ToList();
            created.Sort(DirectoryEntrySorter.ComparerFor(EntryOrder.DateCreated, ascending: true));
            Assert.Equal(new[] { "a", "c", "b" }, created.Select(e => e.Name));
            var accessed = entries.ToList();
            accessed.Sort(DirectoryEntrySorter.ComparerFor(EntryOrder.DateAccessed, ascending: false));
            Assert.Equal(new[] { "a", "c", "b" }, accessed.Select(e => e.Name));
        }

        [AvaloniaFact]
        public async Task DetailsCache_ReadsPictureDimensionsInTheBackground()
        {
            string png = Path.Combine(_root, "picture.png");
            using (var bitmap = new SKBitmap(37, 21))
            using (SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream file = File.Create(png))
            {
                data.SaveTo(file);
            }

            DateTime modified = File.GetLastWriteTimeUtc(png);
            var observer = new Observer();
            Assert.Null(ItemDetailsCache.Peek(png, modified));
            ItemDetails details = ItemDetailsCache.TryGet(png, modified, false, ItemDetailsNeeds.Image | ItemDetailsNeeds.Owner, observer);
            for (int i = 0; i < 250 && (details == null || details.Width == 0 || details.Owner == null); i++)
            {
                await Pump(1); // the values arrive on the UI thread
                details = ItemDetailsCache.TryGet(png, modified, false, ItemDetailsNeeds.Image | ItemDetailsNeeds.Owner, null);
            }

            Assert.NotNull(details);
            Assert.Equal(37, details.Width);
            Assert.Equal(21, details.Height);
            Assert.Equal($"{37:N0} × {21:N0}", details.DimensionsText);
            Assert.False(string.IsNullOrEmpty(details.Owner));
            Assert.Same(details, ItemDetailsCache.Peek(png, modified));
            Assert.True(observer.Notified > 0);

            // A text file asks no picture facts (nothing to read for Dimensions).
            string text = Path.Combine(_root, "note.txt");
            File.WriteAllText(text, "x");
            ItemDetails none = ItemDetailsCache.TryGet(text, File.GetLastWriteTimeUtc(text), false, ItemDetailsNeeds.Image, null);
            Assert.True(none == null || none.Width == 0);
        }

        private sealed class Observer : IItemDetailsObserver
        {
            public int Notified;

            public void OnItemDetailsReady() => Notified++;
        }

        [AvaloniaFact]
        public async Task HeaderMenu_HidesAndShowsColumns_AndEveryPaneFollows()
        {
            var window = new ModernMainWindow { Width = 1400, Height = 860 };
            window.Show();
            MainViewModel main = window.ViewModel;
            try
            {
                foreach (string name in new[] { "alpha.txt", "beta-with-a-much-longer-name-than-the-others.txt" })
                {
                    File.WriteAllText(Path.Combine(_root, name), "content");
                }

                await Pump(20);
                DetailsColumnsState.Reset();
                FileListViewModel pane = main.FileListViewModel;
                pane.SelectedPreset = FileListViewPreset.Details;
                await pane.LoadFolderAsync(_root);
                await Pump(10);

                FileListView view = window.GetVisualDescendants().OfType<FileListView>().First(v => ReferenceEquals(v.DataContext, pane));
                Assert.Equal(new[] { "Name", "Date modified", "Type", "Size", "Tags" }, HeaderTitles(view));

                // Right-click Type › Hide "Type".
                List<Control> items = view.BuildColumnMenuItems(DetailsColumnKind.Type);
                MenuItem hide = items.OfType<MenuItem>().First();
                Assert.Equal("Hide “Type”", hide.Header);
                Click(hide);
                await Pump(3);
                Assert.Equal(new[] { "Name", "Date modified", "Size", "Tags" }, HeaderTitles(view));
                Assert.False(DetailsColumnsState.Current.Contains(DetailsColumnKind.Type));

                // Name's "Hide" is disabled; the list shows what's checked; Date created is added from the list.
                Assert.False(view.BuildColumnMenuItems(DetailsColumnKind.Name).OfType<MenuItem>().First().IsEnabled);
                items = view.BuildColumnMenuItems(null);
                MenuItem type = items.OfType<MenuItem>().Single(m => (string)m.Header == "Type");
                Assert.False(type.IsChecked);
                Assert.True(items.OfType<MenuItem>().Single(m => (string)m.Header == "Size").IsChecked);
                Click(items.OfType<MenuItem>().Single(m => (string)m.Header == "Date created"));
                await Pump(3);
                Assert.Equal("Date created", HeaderTitles(view).Last());
                DetailsColumnsState.Set(DetailsColumnsState.Current.With(DetailsColumnKind.Attributes).With(DetailsColumnKind.Owner));
                await Pump(30);
                Save(window, "details-columns.png");
                DetailsColumnsState.Set(DetailsColumnsState.Current.Without(DetailsColumnKind.Attributes).Without(DetailsColumnKind.Owner));
                await Pump(3);
                Assert.Equal(DetailsColumnsState.Current, DetailsRowPanel.GetColumns(view.FindControl<DockPanel>("DetailsHost")));

                // Sorting by a new column marks its header and orders the list.
                pane.SetSortColumn(FileListSortColumn.DateCreated);
                await Pump(5);
                Assert.True(view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "SortGlyphDateCreated").IsVisible);
                Assert.False(view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "SortGlyphName").IsVisible);
                pane.SetSort(FileListSortColumn.Name, true);

                // Size to fit makes Name fit the longest name.
                double before = DetailsColumnsState.Current[0];
                view.SizeColumnToFit(DetailsColumnKind.Name);
                Assert.NotEqual(before, DetailsColumnsState.Current[0]);
                Assert.True(DetailsColumnsState.Current[0] > 200);

                // Saved in the settings (as keys and widths), and Reset gives the defaults (saved as "none chosen").
                Assert.Contains(main.SettingsService.Current.FileList.DetailsColumns, e => e.Key == "DateCreated");
                Click(view.BuildColumnMenuItems(null).OfType<MenuItem>().Single(m => (string)m.Header == "Reset columns"));
                await Pump(3);
                Assert.Same(DetailsColumnLayout.Default, DetailsColumnsState.Current);
                Assert.Empty(main.SettingsService.Current.FileList.DetailsColumns);
                Assert.Equal(new[] { "Name", "Date modified", "Type", "Size", "Tags" }, HeaderTitles(view));
                Assert.False(view.BuildColumnMenuItems(null).OfType<MenuItem>().Single(m => (string)m.Header == "Reset columns").IsEnabled);
            }
            finally
            {
                DetailsColumnsState.Reset();
                window.Close();
            }
        }

        [AvaloniaFact]
        public void ChooseColumnsDialog_ShowsHidesAndOrders()
        {
            var dialog = new ChooseColumnsDialog(DetailsColumnLayout.Default.Without(DetailsColumnKind.Tags));
            dialog.SelectForTest(DetailsColumnKind.Album);
            dialog.SetShownForTest(true);
            dialog.SelectForTest(DetailsColumnKind.Album);
            dialog.MoveForTest(-1);
            dialog.SelectForTest(DetailsColumnKind.Type);
            dialog.SetShownForTest(false);
            dialog.SelectForTest(DetailsColumnKind.Name);
            dialog.SetShownForTest(false); // Name stays
            dialog.MoveForTest(+1); // and stays first
            DetailsColumnLayout layout = dialog.BuildLayout();
            Assert.Equal(new[] { DetailsColumnKind.Name, DetailsColumnKind.DateModified, DetailsColumnKind.Album, DetailsColumnKind.Size }, layout.Kinds);

            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Save(dialog, "choose-columns.png");
            dialog.Close();

            var unchanged = new ChooseColumnsDialog(DetailsColumnLayout.Default);
            Assert.Same(DetailsColumnLayout.Default, unchanged.BuildLayout());
        }

        private static string[] HeaderTitles(FileListView view) =>
            view.FindControl<DetailsRowPanel>("DetailsHeader").Children
                .Select(c => c.GetVisualDescendants().OfType<TextBlock>().First().Text).ToArray();

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(directory, name));
            }
        }

        private static void Click(MenuItem item) => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

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
