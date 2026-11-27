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
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views;
using ByteExplorer.Views.Settings;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The application menu's two category styles (submenus beside the menu, or unfolded in place), the settings window
    /// (pages, search, live changes both ways, restart notice, lists, numbers), the bar heights and settings import.
    /// </summary>
    public sealed class SettingsWindowTests
    {
        [AvaloniaFact]
        public async Task Menu_Horizontal_OpensCategoriesBesideIt_AndHasNoHistoryRow()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool saved = true;
            try
            {
                await Pump(30);
                saved = main.IsMenuSubmenuHorizontal;
                main.IsMenuSubmenuHorizontal = true;
                var menu = window.FindControl<AppMenu>("AppMenu");
                var trigger = menu.FindControl<Button>("TriggerButton");
                Click(window, trigger);
                await Pump(10);
                Assert.True(trigger.Flyout.IsOpen);
                Assert.True(menu.IsHorizontal);

                Control content = (Control)((Flyout)trigger.Flyout).Content;
                Assert.DoesNotContain(content.GetLogicalDescendantsOfType<Button>(), b => (ToolTip.GetTip(b) as string)?.StartsWith("Back", StringComparison.Ordinal) == true);
                Assert.DoesNotContain(content.GetLogicalDescendantsOfType<Button>(), b => (ToolTip.GetTip(b) as string)?.StartsWith("Up", StringComparison.Ordinal) == true);
                Assert.Contains(content.GetLogicalDescendantsOfType<Button>(), b => b.Name == "SettingsRow");

                // A click opens the category beside the menu; its items moved there, nothing unfolded in the menu.
                var editHeader = Named<Button>(content, "EditHeader");
                Click(window, editHeader);
                await Pump(5);
                var popup = Named<Popup>(content, "SubmenuPopup");
                Assert.True(popup.IsOpen);
                Assert.Equal("EditHeader", menu.OpenSubmenuName);
                var editPanel = Named<StackPanel>(content, "EditPanel");
                Assert.Same(Named<Border>(content, "SubmenuHost"), editPanel.Parent);
                Assert.Contains("cascaded", editPanel.Classes);
                Point editAt = editHeader.TranslatePoint(default, window) ?? default;
                Point panelAt = editPanel.TranslatePoint(default, window) ?? default;
                Assert.True(panelAt.X >= editAt.X + editHeader.Bounds.Width, $"submenu at {panelAt.X}, header ends at {editAt.X + editHeader.Bounds.Width}");

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "menu-horizontal.png"));
                }

                // Resting on another category switches the submenu to it.
                var sortHeader = Named<Button>(content, "SortHeader");
                TopLevel menuTop = TopLevel.GetTopLevel(sortHeader);
                menuTop.MouseMove(sortHeader.TranslatePoint(new Point(20, sortHeader.Bounds.Height / 2), menuTop) ?? default);
                await Pump(30);
                Assert.Equal("SortHeader", menu.OpenSubmenuName);
                Assert.False(editPanel.IsVisible); // back under its header, closed

                // Closing the menu puts every category back under its header, closed.
                trigger.Flyout.Hide();
                await Pump(5);
                Assert.False(popup.IsOpen);
                Assert.Null(menu.OpenSubmenuName);
                var sortPanel = Named<StackPanel>(content, "SortPanel");
                var menuPanel = Named<StackPanel>(content, "MenuPanel");
                Assert.Same(menuPanel, sortPanel.Parent);
                Assert.Equal(menuPanel.Children.IndexOf(Named<Button>(content, "SortHeader")) + 1, menuPanel.Children.IndexOf(sortPanel));
                Assert.False(sortPanel.IsVisible);
            }
            finally
            {
                main.IsMenuSubmenuHorizontal = saved;
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Menu_Vertical_UnfoldsCategoriesInPlace()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool saved = true;
            try
            {
                await Pump(30);
                saved = main.IsMenuSubmenuHorizontal;
                main.IsMenuSubmenuHorizontal = false;
                Assert.Equal(AppearanceSettings.SubmenuVertical, main.SettingsService.Current.Appearance.MenuSubmenuStyle);
                var menu = window.FindControl<AppMenu>("AppMenu");
                var trigger = menu.FindControl<Button>("TriggerButton");
                Click(window, trigger);
                await Pump(10);
                Assert.False(menu.IsHorizontal);
                Control content = (Control)((Flyout)trigger.Flyout).Content;
                Click(window, Named<Button>(content, "ViewHeader"));
                await Pump(5);
                var viewPanel = Named<StackPanel>(content, "ViewOptionsPanel");
                Assert.True(viewPanel.IsVisible);
                Assert.Same(Named<StackPanel>(content, "MenuPanel"), viewPanel.Parent);
                Assert.False(Named<Popup>(content, "SubmenuPopup").IsOpen);
                trigger.Flyout.Hide();
            }
            finally
            {
                main.IsMenuSubmenuHorizontal = saved;
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task BarHeights_StatusBarTallerToolbarLower()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            (bool Toolbar, bool Status) saved = (main.IsToolbarVisible, main.IsStatusBarVisible);
            try
            {
                await Pump(20);
                main.IsToolbarVisible = true;
                main.IsStatusBarVisible = true;
                await Pump(3);
                var rows = window.FindControl<Grid>("RootGrid").RowDefinitions;
                Assert.Equal(40, rows[2].Height.Value); // 44 - 10 %
                Assert.Equal(32, rows[4].Height.Value); // 26 + 10 % + 10 % (rounded)
            }
            finally
            {
                main.IsToolbarVisible = saved.Toolbar;
                main.IsStatusBarVisible = saved.Status;
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task SettingsWindow_PagesSearchAndLiveChanges()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            AppSettings settings = null;
            (bool Details, bool Status, string Font, bool Horizontal, bool Thumbnails, int Grouped) saved = default;
            System.Collections.Generic.List<string> savedNames = null;
            try
            {
                await Pump(30);
                settings = main.SettingsService.Current;
                saved = (main.IsDetailsPaneVisible, main.IsStatusBarVisible, settings.Appearance.FontFamily, main.IsMenuSubmenuHorizontal,
                    settings.FileList.ShowThumbnails, settings.FileList.MaxItemsForGroupedViews);
                savedNames = settings.Index.ExcludedNames.ToList();

                // Ctrl+, opens it; a second time brings the same window forward.
                window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
                for (int i = 0; i < 250 && window.SettingsWindowForTests == null; i++)
                {
                    await Pump(1);
                }

                SettingsWindow settingsWindow = window.SettingsWindowForTests;
                Assert.NotNull(settingsWindow);
                window.OpenSettings();
                Assert.Same(settingsWindow, window.SettingsWindowForTests);
                settingsWindow.Width = 1060;
                settingsWindow.Height = 760;
                SettingsViewModel vm = settingsWindow.ViewModel;
                Assert.Equal(new[] { "Appearance", "Layout", "Left panel", "Folder options", "Tabs", "Files and folders", "File operations", "Search", "Tags", "Network", "Sounds and greetings", "Privacy", "Advanced", "Settings file", "About" },
                    vm.Pages.Select(p => p.Title));
                await Pump(10);
                Save(settingsWindow, "settings-appearance.png");

                // Layout: the panel switch drives the window; a change from the window shows in the switch.
                vm.ShowPage("layout");
                await Pump(5);
                Assert.Equal("Layout", settingsWindow.FindControl<TextBlock>("HeadingText").Text);
                Assert.Contains(vm.Pages.Single(p => p.Key == "layout").Items, i => i.Title == "Toolbar"); // Modern has one
                var status = vm.Find<ToggleSetting>("Status bar");
                status.Value = false;
                Assert.False(main.IsStatusBarVisible);
                var details = vm.Find<ToggleSetting>("Details pane");
                main.IsDetailsPaneVisible = !main.IsDetailsPaneVisible;
                Assert.Equal(main.IsDetailsPaneVisible, details.Value);
                var categories = vm.Find<ChoiceSetting>("Menu categories");
                categories.Selected = categories.Options.Single(o => o.Value == AppearanceSettings.SubmenuVertical);
                Assert.False(main.IsMenuSubmenuHorizontal);
                Save(settingsWindow, "settings-layout.png");

                // Thumbnails reach the file list at once; a number is clamped to its range.
                vm.Find<ToggleSetting>("Show thumbnails").Value = false;
                Assert.False(main.FileListViewModel.ShowThumbnails);
                var grouped = vm.Find<NumberSetting>("Item limit for Timeline and Nexus");
                grouped.Value = 5;
                Assert.Equal(100, settings.FileList.MaxItemsForGroupedViews);
                Assert.Equal(100m, grouped.Value);

                // A setting read at startup asks for a restart.
                Assert.False(vm.IsRestartPending);
                Assert.False(settingsWindow.FindControl<Border>("RestartBar").IsVisible);
                var font = vm.Find<ChoiceSetting>("Font");
                font.Selected = font.Options.Count > 1 ? font.Options[1] : new SettingOption("Some Font", "Some Font");
                Assert.True(vm.IsRestartPending);
                await Pump(3);
                Assert.True(settingsWindow.FindControl<Border>("RestartBar").IsVisible);

                // Lists write the settings; names are typed.
                var names = vm.Find<ListSetting>("Excluded names");
                names.NewEntry = "node_modules_test";
                names.AddTypedCommand.Execute(null);
                Assert.Contains("node_modules_test", settings.Index.ExcludedNames);
                Assert.Equal(string.Empty, names.NewEntry);
                Assert.False(names.Add("NODE_MODULES_TEST")); // already there, whatever the case
                names.RemoveCommand.Execute("node_modules_test");
                Assert.DoesNotContain("node_modules_test", settings.Index.ExcludedNames);

                // Search finds settings on every page; picking a page leaves the search.
                vm.SearchText = "thumbnail";
                await Pump(3);
                Assert.Equal("Search results", vm.Heading);
                Assert.Single(vm.VisibleGroups);
                Assert.Equal("Folder options", vm.VisibleGroups[0].Title); // Show thumbnails: with Explorer's other View options
                Save(settingsWindow, "settings-search.png");
                vm.SearchText = "zzzz-nothing";
                Assert.True(vm.HasNoResults);
                vm.SelectedPage = vm.Pages.Single(p => p.Key == "file");
                Assert.False(vm.IsSearching);
                Assert.Equal(main.SettingsService.FilePath, vm.Find<InfoSetting>("settings.json").Value);
                await Pump(5);
                Save(settingsWindow, "settings-file.png");
                vm.ShowPage("about");
                await Pump(5);
                Save(settingsWindow, "settings-about.png");

                settingsWindow.Close();
                await Pump(3);
                Assert.Null(window.SettingsWindowForTests);
            }
            finally
            {
                if (settings != null)
                {
                    main.IsDetailsPaneVisible = saved.Details;
                    main.IsStatusBarVisible = saved.Status;
                    main.IsMenuSubmenuHorizontal = saved.Horizontal;
                    settings.Appearance.FontFamily = saved.Font;
                    settings.FileList.ShowThumbnails = saved.Thumbnails;
                    settings.FileList.MaxItemsForGroupedViews = saved.Grouped;
                    settings.Index.ExcludedNames = savedNames;
                    await main.SettingsService.SaveAsync();
                }

                window.Close();
            }
        }

        [Fact]
        public async Task SettingsService_ParsesImports_AndReplacesEverything()
        {
            Assert.False(SettingsService.TryParse("", out _, out string empty));
            Assert.NotNull(empty);
            Assert.False(SettingsService.TryParse("{ not json", out _, out string bad));
            Assert.Contains("not valid", bad);
            Assert.True(SettingsService.TryParse("{ // comment\n \"Appearance\": { \"Theme\": \"Dark\", \"MenuSubmenuStyle\": \"vertical\", \"FontSize\": 99 }, }", out AppSettings parsed, out _));
            Assert.Equal("Dark", parsed.Appearance.Theme);
            Assert.Equal(AppearanceSettings.SubmenuVertical, parsed.Appearance.MenuSubmenuStyle);
            Assert.Equal(12.5, parsed.Appearance.FontSize); // out of range: sanitized
            Assert.NotNull(parsed.FileList);

            string path = Path.Combine(Path.GetTempPath(), "be-settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var service = new SettingsService(path);
                await service.LoadAsync();
                Assert.Equal(AppearanceSettings.SubmenuHorizontal, service.Current.Appearance.MenuSubmenuStyle);
                await service.ReplaceAsync(parsed);
                Assert.Same(parsed, service.Current);
                var reread = new SettingsService(path);
                await reread.LoadAsync();
                Assert.Equal("Dark", reread.Current.Appearance.Theme);
                Assert.Equal(AppearanceSettings.SubmenuVertical, reread.Current.Appearance.MenuSubmenuStyle);
                Assert.Contains("\"MenuSubmenuStyle\": \"Vertical\"", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Restart_StartsTheSameProgramAgain()
        {
            System.Diagnostics.ProcessStartInfo info = MainWindowBase.RestartStartInfo();
            Assert.Equal(Environment.ProcessPath, info.FileName);
            Assert.False(info.UseShellExecute);
        }

        // ------------------------------------------------------------------ helpers

        private static T Named<T>(Control root, string name) where T : Control =>
            root.GetLogicalDescendantsOfType<T>().First(c => c.Name == name);

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
            }
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
}
