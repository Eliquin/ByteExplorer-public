// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The Finder layout: chosen by the setting, its toolbar title and view switcher, sidebar sections, striped rows.</summary>
    public sealed class FinderWindowTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-finder-" + Guid.NewGuid().ToString("N"), "Projects 2026");

        public FinderWindowTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Designs"));
            Directory.CreateDirectory(Path.Combine(_root, "Invoices"));
            foreach (string name in new[] { "brief.pdf", "budget.xlsx", "cover.png", "notes.txt", "readme.md", "song.mp3" })
            {
                File.WriteAllText(Path.Combine(_root, name), new string('x', name.Length * 300));
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(_root), true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Setting_AcceptsFolio_AndTheOldFinderName()
        {
            foreach (string stored in new[] { "Folio", "folio", "Finder", "finder" })
            {
                var appearance = new AppearanceSettings { WindowLayout = stored };
                appearance.Sanitize();
                Assert.Equal(AppearanceSettings.LayoutFolio, appearance.WindowLayout);
            }
        }

        [AvaloniaFact]
        public async Task FinderWindow_ToolbarTitle_ViewSwitcher_Sidebar_Stripes()
        {
            Assert.IsType<FinderMainWindow>(App.CreateWindowForLayout("Folio"));
            Assert.IsType<FinderMainWindow>(App.CreateWindowForLayout("Finder")); // the old name still works
            var window = new FinderMainWindow { Width = 1300, Height = 820 };
            window.Show();
            MainViewModel main = window.ViewModel;
            string savedTheme = main.Theme;
            string tagId = null;
            try
            {
                await Pump(30);
                FileListViewModel pane = main.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);

                // The title is the folder's name; the view switcher sets and shows the view.
                Assert.Equal("Projects 2026", window.FindControl<TextBlock>("LocationTitle").Text);
                Button list = window.GetVisualDescendants().OfType<Button>().Single(b => (b.Tag as string) == "Details" && b.Classes.Contains("segment"));
                list.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Pump(5);
                Assert.Equal(FileListViewPreset.Details, pane.SelectedPreset);
                Assert.Contains("checked", list.Classes);

                // The status bar's cycle button, right of the zoom slider, moves to the next view.
                Button cycle = window.FindControl<Button>("CycleViewButton");
                Slider zoom = window.FindControl<Border>("StatusBar").GetVisualDescendants().OfType<Slider>().Single();
                Assert.True(cycle.TranslatePoint(default, window)?.X > zoom.TranslatePoint(default, window)?.X + zoom.Bounds.Width - 1);
                cycle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Pump(5);
                Assert.NotEqual(FileListViewPreset.Details, pane.SelectedPreset);
                pane.SelectedPreset = FileListViewPreset.Details;
                await Pump(5);

                // Sidebar: Favorites and Locations, the Tags section; Home row's folder set.
                Assert.Equal(SpecialFolders.Home, window.FindControl<Border>("HomeFolderRow").Tag);
                string[] sections = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("finderSection")).Select(t => t.Text).ToArray();
                Assert.Equal(new[] { "Favorites", "Locations" }, sections);
                Assert.NotNull(window.FindControl<Views.Sidebar.TagsSidebarView>("TagsView"));
                Assert.Equal(window.ViewModel.Pins.Items.Count, window.ViewModel.Pins.Items.Count);

                // Striped rows: every second row of the list has the stripe color.
                TagDefinition tag = main.Tags.CreateTag("FinderTest Work", "Orange");
                tagId = tag.Id;
                main.Tags.Assign(new[] { Path.Combine(_root, "brief.pdf"), Path.Combine(_root, "Designs") }, tag.Id);
                await Pump(10);
                ListBox details = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                var rows = details.GetRealizedContainers().OfType<ListBoxItem>().OrderBy(c => details.IndexFromContainer(c)).ToList();
                Assert.True(rows.Count >= 6);
                Assert.NotEqual(rows[0].Background?.ToString(), rows[1].Background?.ToString());
                Assert.Equal(rows[1].Background?.ToString(), rows[3].Background?.ToString());

                main.SetThemeCommand.Execute("Dark");
                await Pump(10);
                Save(window, "finder-dark.png");
                main.SetThemeCommand.Execute("Light");
                await Pump(10);
                Save(window, "finder-light.png");
            }
            finally
            {
                main.SetThemeCommand.Execute(savedTheme);
                if (tagId != null)
                {
                    main.Tags.DeleteTag(tagId);
                    await main.Tags.FlushAsync();
                }

                window.Close();
            }
        }

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
            }
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
