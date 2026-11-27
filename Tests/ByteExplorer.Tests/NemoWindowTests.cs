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
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The Nemo window layout: it starts and renders in both themes, its sidebar rows go where they say, its panels
    /// toggle, the tools menu applies a view, and the layout setting picks the right window.
    /// </summary>
    public sealed class NemoWindowTests
    {
        [Fact]
        public void LayoutSetting_AcceptsModernNemoAndTheOldDarkName()
        {
            foreach ((string stored, string expected) in new[]
                     {
                         ("Dark", AppearanceSettings.LayoutModern), ("dark", AppearanceSettings.LayoutModern),
                         ("modern", AppearanceSettings.LayoutModern), ("NEMO", AppearanceSettings.LayoutTrove), ("dori", AppearanceSettings.LayoutTrove), ("Trove", AppearanceSettings.LayoutTrove),
                         ("Default", AppearanceSettings.LayoutDefault), ("Something", AppearanceSettings.LayoutDefault),
                         (null, AppearanceSettings.LayoutDefault)
                     })
            {
                var settings = new AppearanceSettings { WindowLayout = stored };
                settings.Sanitize();
                Assert.Equal(expected, settings.WindowLayout);
            }

            Assert.Equal(AppearanceSettings.LayoutModern, new AppearanceSettings().WindowLayout);
        }

        [AvaloniaFact]
        public void LayoutName_CreatesItsWindow()
        {
            Assert.IsType<NemoMainWindow>(App.CreateWindowForLayout("Nemo"));
            Assert.IsType<NemoMainWindow>(App.CreateWindowForLayout("nemo"));
            Assert.IsType<ModernMainWindow>(App.CreateWindowForLayout("Modern"));
            Assert.IsType<ModernMainWindow>(App.CreateWindowForLayout("Default"));
            Assert.IsType<ModernMainWindow>(App.CreateWindowForLayout(null));
        }

        [AvaloniaFact]
        public async Task NemoWindow_RendersAFolderInBothThemes()
        {
            string folder = CreateFolder();
            var window = new NemoMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(40);
                MainViewModel main = window.ViewModel;
                await main.FileListViewModel.LoadFolderAsync(folder);
                main.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");

                foreach (string theme in new[] { "Light", "Dark" })
                {
                    main.SetThemeCommand.Execute(theme);
                    await Pump(10);
                    Assert.Equal(theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark, Application.Current.ActualThemeVariant);

                    // The sidebar follows the theme (the WPF Nemo window had fixed light colors).
                    var sidebar = window.FindControl<Border>("Sidebar");
                    Color pane = ((ISolidColorBrush)sidebar.Background).Color;
                    Assert.Equal(theme == "Light", pane.R > 200);

                    var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        frame.Save(Path.Combine(directory, $"nemo-{theme}.png"));
                    }
                }
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task SidebarRows_OpenThisPcRecentAndFolders()
        {
            string folder = CreateFolder();
            var window = new NemoMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(40);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(folder);
                await Pump(5);

                Click(window, SidebarRow(window, "Computer"));
                await Pump(20);
                Assert.True(pane.IsThisPcView);
                Assert.Contains("active", SidebarRow(window, "Computer").Classes);

                Click(window, SidebarRow(window, "Recent"));
                await Pump(20);
                Assert.True(pane.IsHomeView);
                Assert.Contains("active", SidebarRow(window, "Recent").Classes);
                Assert.DoesNotContain("active", SidebarRow(window, "Computer").Classes);

                Border home = SidebarRow(window, "Home");
                Assert.Equal(SpecialFolders.Home, home.Tag);
                Click(window, home);
                await Pump(20);
                Assert.True(PathUtilEquals(SpecialFolders.Home, pane.CurrentPath));

                // Quick access is shown with Nemo's gray icons, the same list as the Modern layout's.
                var pinned = window.GetVisualDescendants().OfType<Views.Sidebar.PinnedAreaView>().Single();
                Assert.Equal("Nemo", pinned.Variant);
                Assert.Same(window.ViewModel.Pins, pinned.DataContext);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task PanelsToggle_AndToolsMenuAppliesAView()
        {
            string folder = CreateFolder();
            var window = new NemoMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(40);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(folder);
                pane.SelectedPreset = FileListViewPreset.Details;
                await Pump(5);

                var rootGrid = window.FindControl<Grid>("RootGrid");
                var sidebar = window.FindControl<Border>("Sidebar");
                double sidebarWidth = rootGrid.ColumnDefinitions[0].Width.Value;
                Assert.True(sidebarWidth > 0);
                window.FindControl<Button>("MaximizeButton"); // named controls exist for the chrome
                ClickButton(window, "Show/hide sidebar");
                await Pump(5);
                Assert.Equal(0, rootGrid.ColumnDefinitions[0].Width.Value);
                Assert.False(sidebar.IsVisible);
                ClickButton(window, "Show/hide sidebar");
                await Pump(5);
                Assert.Equal(sidebarWidth, rootGrid.ColumnDefinitions[0].Width.Value);
                Assert.True(sidebar.IsVisible);

                // Tools menu: open it, expand View, pick Tiles.
                var tools = window.FindControl<AppMenu>("ToolsMenu");
                var trigger = tools.FindControl<Button>("TriggerButton");
                Click(window, trigger);
                await Pump(10);
                Assert.True(trigger.Flyout.IsOpen);
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    var menuContent = (Control)((Flyout)trigger.Flyout).Content;
                    TopLevel.GetTopLevel(menuContent)?.CaptureRenderedFrame()?.Save(Path.Combine(directory, "nemo-tools-menu.png"));
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "nemo-with-menu.png"));
                }

                Button viewRow = FindMenuButton(window, b => b.Content is Grid grid && grid.Children.OfType<TextBlock>().Any(t => t.Text == "View"));
                Click(window, viewRow);
                await Pump(5);
                Button tiles = FindMenuButton(window, b => (b.Tag as string) == "Tiles");
                Click(window, tiles);
                await Pump(10);
                Assert.Equal(FileListViewPreset.Tiles, pane.SelectedPreset);
                Assert.False(trigger.Flyout.IsOpen);

                // The View category's "Details pane" switch drives the window through the view model, and is remembered.
                var bodyGrid = window.FindControl<Grid>("BodyGrid");
                Click(window, trigger);
                await Pump(10);
                Assert.True(trigger.Flyout.IsOpen, "the menu opens again");
                Assert.False(FindMenuButton(window, b => (b.Tag as string) == "Tiles").IsEffectivelyVisible, "categories start collapsed");
                Click(window, FindMenuButton(window, b => b.Name == "ViewHeader"));
                await Pump(5);
                var detailsSwitch = FindMenuControl<ToggleSwitch>(window, "DetailsPaneSwitch");
                detailsSwitch.BringIntoView(); // the menu scrolls when it is taller than the space below the button
                await Pump(5);
                bool detailsWereVisible = bodyGrid.ColumnDefinitions[1].Width.Value > 0; // the startup setting may hide them
                Assert.Equal(detailsWereVisible, detailsSwitch.IsChecked);
                Click(window, detailsSwitch);
                await Pump(5);
                Assert.Equal(!detailsWereVisible, bodyGrid.ColumnDefinitions[1].Width.Value > 0);
                Assert.Equal(!detailsWereVisible, window.FindControl<Border>("DetailsPanel").IsVisible);
                Assert.Equal(detailsWereVisible, window.ViewModel.SettingsService.Current.FileList.StartWithRightPanelCollapsed);
                Assert.False(window.FindControl<AppMenu>("ToolsMenu").HasToolbar);
                Assert.False(FindMenuControl<ToggleSwitch>(window, "ToolbarSwitch").IsEffectivelyVisible, "Nemo has no toolbar to hide");
                window.ViewModel.IsDetailsPaneVisible = detailsWereVisible; // leave the shared settings as they were
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string CreateFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-nemo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Projects"));
            foreach (string name in new[] { "report.pdf", "budget.xlsx", "notes.txt", "photo.png", "song.mp3" })
            {
                File.WriteAllText(Path.Combine(folder, name), new string('x', 2048));
            }

            return folder;
        }

        private static bool PathUtilEquals(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a ?? string.Empty), Path.TrimEndingDirectorySeparator(b ?? string.Empty),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        /// <summary>The sidebar row whose label reads <paramref name="label"/>.</summary>
        private static Border SidebarRow(Window window, string label) =>
            window.FindControl<Border>("Sidebar").GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("navRow") &&
                                 border.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

        private static void ClickButton(Window window, string tip) =>
            Click(window, window.GetVisualDescendants().OfType<Button>().First(button => (ToolTip.GetTip(button) as string) == tip));

        /// <summary>A button in the open tools flyout (it lives in a popup, outside the window's visual tree).</summary>
        private static Button FindMenuButton(Window window, Func<Button, bool> match)
        {
            var trigger = window.FindControl<AppMenu>("ToolsMenu").FindControl<Button>("TriggerButton");
            var content = (Control)((Flyout)trigger.Flyout).Content;
            return content.GetLogicalDescendantsOfType<Button>().First(match);
        }

        /// <summary>A named control in the open tools flyout.</summary>
        private static T FindMenuControl<T>(Window window, string name) where T : Control
        {
            var trigger = window.FindControl<AppMenu>("ToolsMenu").FindControl<Button>("TriggerButton");
            var content = (Control)((Flyout)trigger.Flyout).Content;
            return content.GetLogicalDescendantsOfType<T>().First(control => control.Name == name);
        }

        private static void Click(Window window, Visual target)
        {
            TopLevel top = TopLevel.GetTopLevel(target) ?? window;
            Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top) ?? default;
            top.MouseDown(center, MouseButton.Left);
            top.MouseUp(center, MouseButton.Left);
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

    internal static class LogicalTreeTestExtensions
    {
        public static System.Collections.Generic.IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control root) where T : class
        {
            foreach (Avalonia.LogicalTree.ILogical child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root))
            {
                if (child is T match)
                {
                    yield return match;
                }
            }
        }
    }
}
