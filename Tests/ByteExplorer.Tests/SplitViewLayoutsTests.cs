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
using ByteExplorer.Core;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Split view in every layout (Modern, Nemo, Finder): turned on by its button and its shortcut, the active pane
    /// following clicks (and marked), and everything outside the panes acting on the active one — the address bar, the
    /// status bar, the preview, the Finder title and view switcher, the sidebar's rows — plus a middle click opening a
    /// sidebar place in the other pane.
    /// </summary>
    public sealed class SplitViewLayoutsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-split-" + Guid.NewGuid().ToString("N"));
        private readonly string _left;
        private readonly string _right;

        public SplitViewLayoutsTests()
        {
            _left = Directory.CreateDirectory(Path.Combine(_root, "Left folder")).FullName;
            _right = Directory.CreateDirectory(Path.Combine(_root, "Right folder")).FullName;
            File.WriteAllText(Path.Combine(_left, "one.txt"), "1");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                File.WriteAllText(Path.Combine(_right, name), "x");
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

        [AvaloniaTheory]
        [InlineData("Modern")]
        [InlineData("Nemo")]
        [InlineData("Finder")]
        public async Task SplitView_WorksInEveryLayout(string layout)
        {
            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1400;
            window.Height = 860;
            window.Show();
            MainViewModel main = window.ViewModel;
            try
            {
                await Pump(30);
                if (main.IsSplitViewActive)
                {
                    main.ToggleSplitViewCommand.Execute(null);
                    await Pump(5);
                }

                main.PrimaryFileListViewModel.SelectedPreset = FileListViewPreset.Details;
                await main.PrimaryFileListViewModel.LoadFolderAsync(_left);
                await Pump(5);

                // On by the layout's own button (the Modern toolbar's, Nemo's title bar, Finder's toolbar).
                Button splitButton = window.GetVisualDescendants().OfType<Button>()
                    .First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Split view", StringComparison.Ordinal) == true && b.IsEffectivelyVisible);
                Click(window, splitButton);
                await WaitFor(() => main.IsSplitViewActive && main.SecondaryFileListViewModel?.CurrentPath == _left);
                FileListView primary = Pane(window, "PrimaryPane");
                FileListView secondary = Pane(window, "SecondaryPane");
                await Pump(5);
                Assert.True(secondary.IsVisible);
                Assert.True(primary.Bounds.Width > 300, $"primary {primary.Bounds.Width}");
                Assert.True(secondary.Bounds.Width > 300, $"secondary {secondary.Bounds.Width}");
                Assert.Contains("active", splitButton.Classes);
                Assert.Contains("activePane", primary.Classes);
                Assert.DoesNotContain("activePane", secondary.Classes);

                // A click on the second pane's empty background makes it the active pane.
                ClickAt(window, secondary, new Point(secondary.Bounds.Width / 2, secondary.Bounds.Height - 40));
                await Pump(3);
                Assert.Same(main.SecondaryFileListViewModel, main.FileListViewModel);
                Assert.Contains("activePane", secondary.Classes);
                Assert.DoesNotContain("activePane", primary.Classes);

                // What lives outside the panes acts on the active one.
                await main.FileListViewModel.LoadFolderAsync(_right);
                await Pump(10);
                Assert.Equal(_left, main.PrimaryFileListViewModel.CurrentPath);
                Assert.Equal(_right, main.SecondaryFileListViewModel.CurrentPath);
                Assert.Equal(_right, main.Omnibar.CurrentFolder);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == main.SecondaryFileListViewModel.StatusText && t.IsEffectivelyVisible);
                Assert.True(main.SecondaryFileListViewModel.Preview.IsActive || !main.IsDetailsPaneVisible);
                Assert.False(main.PrimaryFileListViewModel.Preview.IsActive);
                if (window is FinderMainWindow finder)
                {
                    Assert.Equal("Right folder", finder.FindControl<TextBlock>("LocationTitle").Text);
                    main.FileListViewModel.SelectedPreset = FileListViewPreset.Tiles;
                    await Pump(5);
                    Button gallery = finder.GetVisualDescendants().OfType<Button>().Single(b => (b.Tag as string) == "Tiles" && b.Classes.Contains("segment"));
                    Assert.Contains("checked", gallery.Classes);
                    Assert.Equal(FileListViewPreset.Details, main.PrimaryFileListViewModel.SelectedPreset); // the other pane keeps its view
                }

                // Columns in one pane of a split view.
                main.FileListViewModel.SelectedPreset = FileListViewPreset.Columns;
                await Pump(15);
                Save(window, $"split-{layout.ToLowerInvariant()}-columns.png");
                Assert.True(main.IsSplitViewActive);
                Assert.Equal(FileListViewMode.Columns, main.SecondaryFileListViewModel.ViewMode);
                Assert.True(secondary.Bounds.Width > 300, $"secondary in Columns {secondary.Bounds.Width}");
                main.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                await Pump(10);
                Save(window, $"split-{layout.ToLowerInvariant()}.png");

                // Back to the first pane by a click: the address bar and title follow.
                ClickAt(window, primary, new Point(primary.Bounds.Width / 2, primary.Bounds.Height - 40));
                await Pump(3);
                Assert.Same(main.PrimaryFileListViewModel, main.FileListViewModel);
                Assert.Equal(_left, main.Omnibar.CurrentFolder);
                if (window is FinderMainWindow finderAgain)
                {
                    Assert.Equal("Left folder", finderAgain.FindControl<TextBlock>("LocationTitle").Text);
                }

                // The sidebar's Home row opens Home in the active pane only.
                Border home = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("navRow") &&
                    (b.Name == "HomeRow" || (ToolTip.GetTip(b) as string) == "Recent and frequent folders"));
                home.BringIntoView(); // the Modern panel is one scroller, which may show the tree's current folder
                await Pump(3);
                Click(window, home);
                await WaitFor(() => main.PrimaryFileListViewModel.IsHomeView);
                Assert.Equal(_right, main.SecondaryFileListViewModel.CurrentPath);

                // The shortcut turns split view off; the first pane is the only one again.
                window.KeyPress(Key.OemBackslash, RawInputModifiers.Control, PhysicalKey.Backslash, null);
                await WaitFor(() => !main.IsSplitViewActive);
                Assert.False(secondary.IsVisible);
                Assert.Same(main.PrimaryFileListViewModel, main.FileListViewModel);
                Assert.DoesNotContain("splitPane", primary.Classes);

                // A middle click on a sidebar place opens it in a new tab behind this one (Explorer's and browsers' way);
                // with Settings › Tabs › "Middle-clicking a folder" = other pane, in the other pane, turning split view on.
                Border homeFolder = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "HomeFolderRow");
                if (homeFolder != null)
                {
                    homeFolder.Tag = _right; // a small folder instead of the real home folder
                    ByteExplorer.ViewModels.Tabs.TabViewModel current = main.Tabs.SelectedTab;
                    MiddleClick(window, homeFolder);
                    await WaitFor(() => main.Tabs.Count == 2 && main.Tabs.Tabs[1].Primary.CurrentPath == _right);
                    Assert.Same(current, main.Tabs.SelectedTab);
                    Assert.False(main.IsSplitViewActive);
                    main.Tabs.CloseTab(main.Tabs.Tabs[1]);

                    main.SettingsService.Current.Tabs.MiddleClickOpens = ByteExplorer.Core.Models.TabSettings.MiddleClickOtherPane;
                    MiddleClick(window, homeFolder);
                    await WaitFor(() => main.IsSplitViewActive && main.SecondaryFileListViewModel?.CurrentPath == _right);
                    main.ToggleSplitViewCommand.Execute(null);
                    await Pump(5);
                }
            }
            finally
            {
                main.SettingsService.Current.Tabs.MiddleClickOpens = ByteExplorer.Core.Models.TabSettings.MiddleClickNewTab;
                if (main.IsSplitViewActive)
                {
                    main.ToggleSplitViewCommand.Execute(null);
                }

                window.Close();
            }
        }

        [AvaloniaTheory]
        [InlineData("Modern")]
        [InlineData("Nemo")]
        [InlineData("Finder")]
        public async Task SplitView_TurnsOnFromColumnsView(string layout)
        {
            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1400;
            window.Height = 860;
            window.Show();
            MainViewModel main = window.ViewModel;
            try
            {
                await Pump(30);
                if (main.IsSplitViewActive)
                {
                    main.ToggleSplitViewCommand.Execute(null);
                    await Pump(5);
                }

                main.PrimaryFileListViewModel.SelectedPreset = FileListViewPreset.Columns;
                await main.PrimaryFileListViewModel.LoadFolderAsync(_right);
                await Pump(10);
                Assert.True(main.ToggleSplitViewCommand.CanExecute(null));

                window.KeyPress(Key.OemBackslash, RawInputModifiers.Control, PhysicalKey.Backslash, null);
                await WaitFor(() => main.IsSplitViewActive && main.SecondaryFileListViewModel?.CurrentPath == _right);
                await Pump(10);
                Assert.Equal(FileListViewMode.Columns, main.SecondaryFileListViewModel.ViewMode);
                FileListView secondary = Pane(window, "SecondaryPane");
                Assert.True(secondary.IsVisible);
                Assert.True(secondary.Bounds.Width > 300, $"secondary {secondary.Bounds.Width}");
                Save(window, $"split-{layout.ToLowerInvariant()}-from-columns.png");
                Assert.Contains(secondary.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Right folder" && t.IsEffectivelyVisible && t.Bounds.Width > 0); // the column browser's header

                // Nexus still keeps split view off: it needs the window's width.
                main.ToggleSplitViewCommand.Execute(null);
                await WaitFor(() => !main.IsSplitViewActive);
                main.PrimaryFileListViewModel.SelectedPreset = FileListViewPreset.Nexus;
                await Pump(5);
                Assert.False(main.ToggleSplitViewCommand.CanExecute(null));
                main.PrimaryFileListViewModel.SelectedPreset = FileListViewPreset.Details;
                await Pump(5);
            }
            finally
            {
                if (main.IsSplitViewActive)
                {
                    main.ToggleSplitViewCommand.Execute(null);
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

        private static FileListView Pane(Window window, string name) =>
            window.GetVisualDescendants().OfType<FileListView>().Single(p => p.Name == name);

        private static void Click(Window window, Visual target) =>
            ClickAt(window, target, new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));

        private static void ClickAt(Window window, Visual target, Point local)
        {
            TopLevel top = TopLevel.GetTopLevel(target) ?? window;
            Point at = target.TranslatePoint(local, top) ?? default;
            top.MouseDown(at, MouseButton.Left);
            top.MouseUp(at, MouseButton.Left);
        }

        private static void MiddleClick(Window window, Visual target)
        {
            Point at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
            window.MouseDown(at, MouseButton.Middle);
            window.MouseUp(at, MouseButton.Middle);
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
