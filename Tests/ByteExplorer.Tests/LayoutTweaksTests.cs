// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Sorting by tags, the title bar's app icon, the order of the address bar's buttons, the Tags section under the tree
    /// and the zoom slider on the status bar's center line, in both layouts.
    /// </summary>
    public sealed class LayoutTweaksTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-layout-" + Guid.NewGuid().ToString("N"));

        public LayoutTweaksTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Zeta folder"));
            Directory.CreateDirectory(Path.Combine(_root, "Alpha folder"));
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
            {
                File.WriteAllText(Path.Combine(_root, name), "x");
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
        public async Task Modern_TagSort_TitleIcon_ButtonOrder_TagsUnderTree_SliderCentered()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            TagStore store = main.Tags;
            string tagId = null;
            string otherId = null;
            try
            {
                await Pump(30);
                FileListViewModel pane = main.FileListViewModel;
                pane.SelectedPreset = FileListViewPreset.Details;
                await pane.LoadFolderAsync(_root);
                await Pump(10);

                // Sort by tags: tagged items in tag-name order, untagged ones last (in both directions), folders first.
                TagDefinition b = store.CreateTag("LayoutTest B", "Blue");
                TagDefinition a = store.CreateTag("LayoutTest A", "Red");
                tagId = b.Id;
                otherId = a.Id;
                store.Assign(new[] { Path.Combine(_root, "c.txt") }, a.Id);
                store.Assign(new[] { Path.Combine(_root, "b.txt") }, b.Id);
                store.Assign(new[] { Path.Combine(_root, "Zeta folder") }, a.Id);
                pane.SetSort(FileListSortColumn.Tags, true);
                await WaitFor(() => Names(pane).SequenceEqual(new[] { "Zeta folder", "Alpha folder", "c.txt", "b.txt", "a.txt", "d.txt" }),
                    () => string.Join(",", Names(pane)) + $" | default is store: {ReferenceEquals(TagStore.Default, store)}; key c: '{store.SortKey(Path.Combine(_root, "c.txt"))}'; col {pane.SortColumn}; current {pane.CurrentPath}");
                pane.SetSort(FileListSortColumn.Tags, false);
                await WaitFor(() => Names(pane).SequenceEqual(new[] { "Zeta folder", "Alpha folder", "b.txt", "c.txt", "a.txt", "d.txt" }), () => string.Join(",", Names(pane)));

                // A tag given while sorted by tags re-sorts the list.
                store.Assign(new[] { Path.Combine(_root, "d.txt") }, a.Id);
                await WaitFor(() => Names(pane).SequenceEqual(new[] { "Zeta folder", "Alpha folder", "b.txt", "c.txt", "d.txt", "a.txt" }), () => string.Join(",", Names(pane)));
                Assert.True(window.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Single(c => c.Name == "SortGlyphTags").IsVisible);
                pane.SetSort(FileListSortColumn.Name, true);

                // The title bar shows the app's icon.
                var icon = window.FindControl<Image>("TitleIcon");
                Assert.NotNull(icon.Source);
                Assert.Equal(16, icon.Bounds.Width);

                // Back, Refresh, Forward.
                Button[] nav = window.FindControl<Border>("Toolbar") == null ? Array.Empty<Button>() : NavButtons(window);
                Assert.Equal(new[] { "Back", "Refresh", "Forward" }, nav.Select(ButtonName));

                // Tags under the tree, then Network.
                Point tree = window.FindControl<Views.NavigationPaneView>("NavigationPane").TranslatePoint(default, window) ?? default;
                Point tags = window.FindControl<Views.Sidebar.TagsSidebarView>("TagsView").TranslatePoint(default, window) ?? default;
                Point network = window.GetLogicalDescendantsOfType<Views.Sidebar.SidebarSection>().Single(s => s.SectionKey == "Network").TranslatePoint(default, window) ?? default;
                Assert.True(tags.Y > tree.Y && network.Y > tags.Y, $"tree {tree.Y}, tags {tags.Y}, network {network.Y}");

                AssertSliderCentered(window);
                Save(window, "layout-modern.png");
            }
            finally
            {
                if (tagId != null)
                {
                    store.DeleteTag(tagId);
                    store.DeleteTag(otherId);
                    await store.FlushAsync();
                }

                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Nemo_ButtonOrder_StatusBarTaller_SliderCentered()
        {
            var window = new NemoMainWindow { Width = 1300, Height = 850 };
            window.Show();
            try
            {
                await Pump(30);
                Border status = window.FindControl<Border>("StatusBar");
                Assert.InRange(status.Bounds.Height, 42.5, 43.5); // 35 + 10 % + 10 % (rounded)
                Assert.Equal(new[] { "Back", "Refresh", "Forward" }, NavButtons(window).Select(ButtonName));
                AssertSliderCentered(window);
            }
            finally
            {
                window.Close();
            }
        }

        private static void AssertSliderCentered(Window window)
        {
            Border status = window.FindControl<Border>("StatusBar");
            Slider slider = status.GetVisualDescendants().OfType<Slider>().Single();
            Thumb thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
            double statusCenter = (status.TranslatePoint(default, window)?.Y ?? 0) + status.Bounds.Height / 2;
            double thumbCenter = (thumb.TranslatePoint(default, window)?.Y ?? 0) + thumb.Bounds.Height / 2;
            Assert.True(Math.Abs(statusCenter - thumbCenter) <= 1.0, $"status bar center {statusCenter:F1}, slider thumb center {thumbCenter:F1}");
        }

        private static Button[] NavButtons(Window window) =>
            window.GetVisualDescendants().OfType<Button>()
                .Where(b => ButtonName(b) != null && b.IsEffectivelyVisible)
                .OrderBy(b => b.TranslatePoint(default, window)?.X ?? 0)
                .Take(3)
                .ToArray();

        private static string ButtonName(Button button)
        {
            string tip = ToolTip.GetTip(button) as string ?? string.Empty;
            return tip.StartsWith("Back (", StringComparison.Ordinal) ? "Back"
                : tip.StartsWith("Forward (", StringComparison.Ordinal) ? "Forward"
                : tip.StartsWith("Refresh (", StringComparison.Ordinal) ? "Refresh"
                : null;
        }

        private static string[] Names(FileListViewModel pane) => pane.Items.Cast<FileListItemViewModel>().Select(i => i.Name).ToArray();

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
            }
        }

        private static async Task WaitFor(Func<bool> condition, Func<string> message, int rounds = 250)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition(), message());
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
