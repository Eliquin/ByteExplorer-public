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
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The application menu of the Modern layout (its place left of Back, the View category's panel switches and their
    /// persistence, the Navigation tree's follow setting) and the shared details pane (header for an item, a multiple
    /// selection and the open folder; no loading while hidden).
    /// </summary>
    public sealed class AppMenuAndDetailsPaneTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-menu-" + Guid.NewGuid().ToString("N"));

        public AppMenuAndDetailsPaneTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects", "Alpha"));
            foreach (string name in new[] { "report.pdf", "notes.txt", "data.bin" })
            {
                File.WriteAllBytes(Path.Combine(_root, name), new byte[1000]);
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
        public async Task ModernMenu_SitsLeftOfBack_AndItsViewSwitchesCollapsePanelsAndPersist()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            FileListSettings settings = null;
            (bool Left, bool Right, bool Toolbar, bool Status) saved = default;
            try
            {
                await Pump(30);
                settings = main.SettingsService.Current.FileList;
                saved = (settings.StartWithLeftPanelCollapsed, settings.StartWithRightPanelCollapsed, settings.StartWithToolbarCollapsed, settings.StartWithStatusBarCollapsed);
                main.IsNavigationPaneVisible = true;
                main.IsToolbarVisible = true;
                main.IsStatusBarVisible = true;
                await Pump(3);

                var menu = window.FindControl<AppMenu>("AppMenu");
                Assert.True(menu.HasToolbar);
                var trigger = menu.FindControl<Button>("TriggerButton");
                Button back = window.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Back", StringComparison.Ordinal) == true);
                Point menuAt = trigger.TranslatePoint(default, window) ?? default;
                Point backAt = back.TranslatePoint(default, window) ?? default;
                Assert.True(menuAt.X < backAt.X, $"menu at {menuAt.X}, Back at {backAt.X}");
                Assert.Equal(menuAt.Y + trigger.Bounds.Height / 2, backAt.Y + back.Bounds.Height / 2, 1);

                Click(window, trigger);
                await Pump(10);
                Assert.True(trigger.Flyout.IsOpen);
                Click(window, FindMenu<Button>(trigger, "ViewHeader"));
                await Pump(5);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "modern-app-menu.png"));
                }

                var rootGrid = window.FindControl<Grid>("RootGrid");
                double toolbarHeight = rootGrid.RowDefinitions[2].Height.Value;
                Assert.True(toolbarHeight > 0);

                // Toolbar off: its row closes, the setting remembers it, and the menu stays open for the next switch.
                await Toggle(window, FindMenu<ToggleSwitch>(trigger, "ToolbarSwitch"));
                Assert.Equal(0, rootGrid.RowDefinitions[2].Height.Value);
                Assert.False(window.FindControl<Border>("Toolbar").IsVisible);
                Assert.True(settings.StartWithToolbarCollapsed);
                Assert.True(trigger.Flyout.IsOpen);

                // Status bar and the navigation pane, then everything back on.
                await Toggle(window, FindMenu<ToggleSwitch>(trigger, "StatusBarSwitch"));
                Assert.Equal(0, rootGrid.RowDefinitions[4].Height.Value);
                Assert.False(window.FindControl<Border>("StatusBar").IsVisible);
                Assert.True(settings.StartWithStatusBarCollapsed);
                await Toggle(window, FindMenu<ToggleSwitch>(trigger, "NavigationPaneSwitch"));
                Assert.False(main.IsNavigationPaneVisible);
                Assert.Equal(0, window.FindControl<Grid>("BodyGrid").ColumnDefinitions[0].Width.Value);
                Assert.True(settings.StartWithLeftPanelCollapsed);

                main.IsToolbarVisible = true;
                main.IsStatusBarVisible = true;
                main.IsNavigationPaneVisible = true;
                await Pump(3);
                Assert.Equal(toolbarHeight, rootGrid.RowDefinitions[2].Height.Value);
                Assert.True(FindMenu<ToggleSwitch>(trigger, "ToolbarSwitch").IsChecked);
                Assert.True(window.FindControl<Border>("StatusBar").IsVisible);
                Assert.True(window.FindControl<Grid>("BodyGrid").ColumnDefinitions[0].Width.Value > 0);
            }
            finally
            {
                if (settings != null)
                {
                    main.IsNavigationPaneVisible = !saved.Left;
                    main.IsDetailsPaneVisible = !saved.Right;
                    main.IsToolbarVisible = !saved.Toolbar;
                    main.IsStatusBarVisible = !saved.Status;
                }

                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task TreeFollowSetting_Off_LeavesTheTreeWhereItIs_OnCatchesUp()
        {
            string alpha = Path.Combine(_root, "Projects", "Alpha");
            string projects = Path.Combine(_root, "Projects");
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool savedFollow = true;
            try
            {
                await Pump(30);
                savedFollow = main.TreeFollowsCurrentFolder;
                main.TreeFollowsCurrentFolder = true;
                var list = window.GetVisualDescendants().OfType<NavigationPaneView>().Single().FindControl<ListBox>("Rows");
                for (int i = 0; i < 100 && main.NavigationViewModel.ThisPcNode == null; i++)
                {
                    await Pump(1);
                }

                await main.FileListViewModel.LoadFolderAsync(projects);
                await WaitFor(() => SelectedTreePath(list) == projects);

                // Off: navigating in the file list leaves the tree's selection alone, and the setting is saved.
                main.TreeFollowsCurrentFolder = false;
                Assert.False(main.SettingsService.Current.Navigation.TreeFollowsCurrentFolder);
                Assert.False(main.NavigationViewModel.FollowCurrentFolder);
                await main.FileListViewModel.LoadFolderAsync(alpha);
                await Pump(20);
                Assert.Equal(projects, SelectedTreePath(list));

                // On again: the tree catches up with the folder shown now, without another navigation.
                main.TreeFollowsCurrentFolder = true;
                await WaitFor(() => SelectedTreePath(list) == alpha);
                Assert.Equal(alpha, main.FileListViewModel.CurrentPath);
            }
            finally
            {
                main.TreeFollowsCurrentFolder = savedFollow;
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task DetailsPane_ShowsItemSelectionOrFolder_AndLoadsNothingWhileHidden()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool savedDetails = false;
            try
            {
                await Pump(30);
                savedDetails = main.IsDetailsPaneVisible;
                main.IsDetailsPaneVisible = true;
                FileListViewModel pane = main.FileListViewModel;
                FilePreviewViewModel preview = pane.Preview;
                await pane.LoadFolderAsync(_root);
                await WaitFor(() => pane.Items.Count == 4);
                Assert.True(preview.IsActive);

                // Nothing selected: the open folder.
                pane.SelectedItem = null;
                await WaitFor(() => preview.IsFolderSubject);
                Assert.True(preview.HasSubject);
                Assert.Equal(Path.GetFileName(_root), preview.Title);
                Assert.Contains(preview.Details, row => row.Label == "Type" && row.Value == "File folder (open)");

                // One item: its name and rows at once (before metadata arrives).
                FileListItemViewModel notes = pane.Items.Cast<FileListItemViewModel>().First(i => i.Name == "notes.txt");
                pane.SelectedItem = notes;
                Assert.Equal("notes.txt", preview.Title);
                Assert.False(preview.IsFolderSubject);
                Assert.Contains(preview.Details, row => row.Label == "Size");

                // Several: a count and a total, and no preview below.
                pane.UpdateSelection(pane.Items.Cast<FileListItemViewModel>().Where(i => !i.IsDirectory).ToList());
                await Pump(2);
                Assert.True(preview.IsMultiSelection);
                Assert.Equal("3 items selected", preview.Title);
                Assert.Contains(preview.Details, row => row.Label == "Contains" && row.Value == "0 folders, 3 files");
                Assert.Contains(preview.Details, row => row.Label == "Total size");
                var view = window.GetVisualDescendants().OfType<DetailsPaneView>().Single();
                Assert.False(view.FindControl<FilePreviewPanel>("PreviewPanel").IsVisible);
                Assert.InRange(preview.MaxImageSide, 384, 2560);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "details-multi.png"));
                }

                // Hidden: inactive, nothing loads; shown again: the current subject loads.
                main.IsDetailsPaneVisible = false;
                Assert.False(preview.IsActive);
                pane.UpdateSelection(new[] { notes });
                pane.SelectedItem = notes;
                await Pump(15);
                Assert.Equal("notes.txt", preview.Title); // the header is cheap and stays current
                Assert.Equal(FilePreviewKind.None, preview.Kind);
                main.IsDetailsPaneVisible = true;
                Assert.True(preview.IsActive);
                await WaitFor(() => preview.Kind != FilePreviewKind.None && !preview.IsLoading);
            }
            finally
            {
                main.IsDetailsPaneVisible = savedDetails;
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string SelectedTreePath(ListBox list) => (list.SelectedItem as NavigationTreeRow)?.Node.FullPath;

        private static T FindMenu<T>(Button trigger, string name) where T : Control
        {
            var content = (Control)((Flyout)trigger.Flyout).Content;
            return content.GetLogicalDescendantsOfType<T>().First(control => control.Name == name);
        }

        private static async Task Toggle(Window window, ToggleSwitch toggle)
        {
            toggle.BringIntoView();
            await Pump(3);
            Click(window, toggle);
            await Pump(5);
        }

        private static void Click(Window window, Visual target)
        {
            TopLevel top = TopLevel.GetTopLevel(target) ?? window;
            Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top) ?? default;
            top.MouseDown(center, MouseButton.Left);
            top.MouseUp(center, MouseButton.Left);
        }

        private static async Task WaitFor(Func<bool> condition, int rounds = 250)
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
    }
}
