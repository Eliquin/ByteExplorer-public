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
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Tabs;
using ByteExplorer.Views.Tabs;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Tabs: the rules of the tab list (where tabs open, close, reopen, pin and move), each tab's own workspace (folder,
    /// split, focus, selection, search), sessions (sleeping tabs, pinned tabs at every start), and the bar in every layout
    /// and position with its pointer and keyboard gestures and file drops.
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class TabsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-tabs-" + Guid.NewGuid().ToString("N"));

        public TabsTests()
        {
            foreach (string name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
            {
                Directory.CreateDirectory(Path.Combine(_root, name));
            }

            for (int i = 0; i < 40; i++)
            {
                File.WriteAllText(Path.Combine(_root, "Alpha", $"file{i:00}.txt"), "x");
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

        private string P(string name) => Path.Combine(_root, name);

        private static string[] Titles(TabsViewModel tabs) => tabs.Tabs.Select(t => t.Title).ToArray();

        /// <summary>A window's view model without a window, its settings left at their defaults (nothing read from disk).</summary>
        private static MainViewModel NewMain() => new MainViewModel(new UiDispatcher(Dispatcher.UIThread));

        // ------------------------------------------------------------------ the rules of the list

        [AvaloniaFact]
        public async Task Opening_NewTabsGoToTheEnd_FoldersNextToTheirTab_InOrder_DuplicatesRightAfter()
        {
            using MainViewModel main = NewMain();
            TabsViewModel tabs = main.Tabs;
            Assert.Single(tabs.Tabs);
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            await tabs.NewTabAsync(); // Ctrl+T: at the end, shown, Home
            Assert.Equal(new[] { "Alpha", "Home" }, Titles(tabs));
            Assert.Same(tabs.Tabs[1], tabs.SelectedTab);

            tabs.Select(tabs.Tabs[0]);
            await main.OpenInNewTabAsync(P("Beta"));  // middle click: behind, right after Alpha
            await main.OpenInNewTabAsync(P("Gamma")); // the next one after Beta, not before it (Chrome's rule)
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Home" }, Titles(tabs));
            Assert.Equal("Alpha", tabs.SelectedTab.Title); // opened behind

            await tabs.DuplicateAsync(tabs.Tabs[1]);
            Assert.Equal(new[] { "Alpha", "Beta", "Beta", "Gamma", "Home" }, Titles(tabs));
            Assert.Same(tabs.Tabs[2], tabs.SelectedTab);

            // "Open in new tab" from the menu shows the tab; a selection of folders opens behind, in order.
            await main.OpenFolderAsync(P("Delta"), FolderOpenTarget.NewTab);
            Assert.Equal("Delta", tabs.SelectedTab.Title);
            await main.OpenFoldersInNewTabsAsync(new[] { P("Alpha"), P("Epsilon") }, showFirst: false);
            Assert.Equal("Delta", tabs.SelectedTab.Title);
            int delta = tabs.Tabs.IndexOf(tabs.SelectedTab);
            Assert.Equal(new[] { "Alpha", "Epsilon" }, new[] { tabs.Tabs[delta + 1].Title, tabs.Tabs[delta + 2].Title });

            // Invalid places never make a tab.
            Assert.Null(await tabs.OpenInNewTabAsync("relative\\path"));
            Assert.Null(await tabs.OpenInNewTabAsync(""));
        }

        [AvaloniaFact]
        public async Task Closing_SelectsTheNextTab_KeepsOneTab_AndReopensWhereItWas()
        {
            using MainViewModel main = NewMain();
            TabsViewModel tabs = main.Tabs;
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            foreach (string name in new[] { "Beta", "Gamma", "Delta" })
            {
                await tabs.OpenInNewTabAsync(P(name), select: false);
            }

            // Order: Alpha Delta Gamma Beta? No: each opens after the previous one opened from Alpha.
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Delta" }, Titles(tabs));
            int panes = main.AllPanes.Count();
            var released = new List<FileListViewModel>();
            main.PaneRemoved += (_, pane) => released.Add(pane);

            tabs.Select(tabs.Tabs[1]);
            tabs.CloseTab(tabs.SelectedTab); // Beta closes: the tab after it (Gamma) is selected
            Assert.Equal("Gamma", tabs.SelectedTab.Title);
            Assert.Single(released); // its pane is released (watcher, timers)
            Assert.Equal(panes - 1, main.AllPanes.Count());

            tabs.Select(tabs.Tabs[^1]);
            tabs.CloseTab(tabs.SelectedTab); // the last tab: the one before it
            Assert.Equal("Gamma", tabs.SelectedTab.Title);

            // Ctrl+Shift+T brings them back, newest first, where they were.
            await tabs.ReopenClosedTabAsync();
            Assert.Equal(new[] { "Alpha", "Gamma", "Delta" }, Titles(tabs));
            Assert.Equal("Delta", tabs.SelectedTab.Title);
            await tabs.ReopenClosedTabAsync();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Delta" }, Titles(tabs));
            Assert.False(tabs.ReopenClosedTabCommand.CanExecute(null));

            // The only tab left: closing it leaves a new tab (Home) in its place, and it can be reopened.
            tabs.CloseOtherTabsCommand.Execute(tabs.Tabs[0]);
            Assert.Equal(new[] { "Alpha" }, Titles(tabs));
            tabs.CloseTab(tabs.Tabs[0]);
            await WaitUntil(() => tabs.Tabs.Count == 1 && tabs.Tabs[0].Title == "Home");
            await tabs.ReopenClosedTabAsync();
            Assert.Equal(new[] { "Alpha", "Home" }, Titles(tabs));
        }

        [AvaloniaFact]
        public async Task Pinning_KeepsPinnedTabsFirst_AndMovesCantCrossTheLine()
        {
            using MainViewModel main = NewMain();
            TabsViewModel tabs = main.Tabs;
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            foreach (string name in new[] { "Beta", "Gamma", "Delta" })
            {
                await tabs.OpenInNewTabAsync(P(name), select: false);
            }

            tabs.TogglePin(tabs.Tabs[2]); // Gamma: to the end of the pinned tabs (the start)
            Assert.Equal(new[] { "Gamma", "Alpha", "Beta", "Delta" }, Titles(tabs));
            tabs.TogglePin(tabs.Tabs[3]); // Delta: after Gamma
            Assert.Equal(new[] { "Gamma", "Delta", "Alpha", "Beta" }, Titles(tabs));
            Assert.Equal(2, tabs.PinnedCount);

            tabs.Move(tabs.Tabs[3], 0); // Beta can't go among the pinned tabs
            Assert.Equal(new[] { "Gamma", "Delta", "Beta", "Alpha" }, Titles(tabs));
            tabs.Move(tabs.Tabs[0], 10); // nor Gamma past them
            Assert.Equal(new[] { "Delta", "Gamma", "Beta", "Alpha" }, Titles(tabs));
            Assert.False(tabs.MoveTabBackCommand.CanExecute(tabs.Tabs[2]));
            Assert.True(tabs.MoveTabForwardCommand.CanExecute(tabs.Tabs[2]));

            // Close others and close to one side leave pinned tabs open.
            tabs.CloseOtherTabsCommand.Execute(tabs.Tabs[3]);
            Assert.Equal(new[] { "Delta", "Gamma", "Alpha" }, Titles(tabs));
            tabs.CloseTabsBeforeCommand.Execute(tabs.Tabs[2]);
            Assert.Equal(new[] { "Delta", "Gamma", "Alpha" }, Titles(tabs));

            tabs.TogglePin(tabs.Tabs[0]); // unpinned: the first of the other tabs
            Assert.Equal(new[] { "Gamma", "Delta", "Alpha" }, Titles(tabs));
            Assert.Equal(1, tabs.PinnedCount);
        }

        [AvaloniaFact]
        public async Task Keyboard_NumbersNextPreviousAndTheTabLimit()
        {
            using MainViewModel main = NewMain();
            TabsViewModel tabs = main.Tabs;
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            foreach (string name in new[] { "Beta", "Gamma" })
            {
                await tabs.OpenInNewTabAsync(P(name), select: false);
            }

            tabs.SelectByNumber(2);
            Assert.Equal("Beta", tabs.SelectedTab.Title);
            tabs.SelectByNumber(9); // the last tab
            Assert.Equal("Gamma", tabs.SelectedTab.Title);
            tabs.SelectByNumber(7); // no seventh tab: nothing happens
            Assert.Equal("Gamma", tabs.SelectedTab.Title);
            tabs.SelectRelative(1); // wraps around
            Assert.Equal("Alpha", tabs.SelectedTab.Title);
            tabs.SelectRelative(-1);
            Assert.Equal("Gamma", tabs.SelectedTab.Title);

            // A window holds up to MaxTabs tabs; one more is refused (with a message where there's a dialog service).
            while (tabs.Count < TabSettings.MaxTabs)
            {
                Assert.NotNull(await tabs.OpenInNewTabAsync(TabLocation.ThisPc, select: false));
            }

            Assert.Null(await tabs.OpenInNewTabAsync(P("Delta"), select: false));
            Assert.Null(await tabs.NewTabAsync());
            Assert.Equal(TabSettings.MaxTabs, tabs.Count);
        }

        // ------------------------------------------------------------------ each tab's workspace

        [AvaloniaFact]
        public async Task EachTab_KeepsItsFolderSplitFocusAndSearch_AndTheWindowFollowsTheSelectedTab()
        {
            using MainViewModel main = NewMain();
            TabsViewModel tabs = main.Tabs;
            var changes = new List<string>();
            main.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
            TabViewModel first = tabs.SelectedTab;

            main.ToggleSplitViewCommand.Execute(null);
            await WaitUntil(() => first.IsSplitViewActive && first.Secondary?.CurrentPath != null);
            await first.Secondary.LoadFolderAsync(P("Beta"));
            main.SetFocusedPane(isSecondary: true);
            Assert.Same(first.Secondary, main.FileListViewModel);
            Assert.Equal("Beta", first.Title); // the active pane names the tab
            Assert.Contains("(other pane)", first.ToolTip);

            TabViewModel second = await tabs.OpenInNewTabAsync(P("Gamma"), select: true);
            Assert.False(main.IsSplitViewActive); // the new tab isn't split
            Assert.Null(main.SecondaryFileListViewModel);
            Assert.Same(second.Primary, main.PrimaryFileListViewModel);
            Assert.Same(second.Primary, main.FileListViewModel);
            Assert.Contains(nameof(MainViewModel.PrimaryFileListViewModel), changes);
            Assert.Contains(nameof(MainViewModel.IsSplitViewActive), changes);

            tabs.Select(first); // back: split, with the second pane focused, as it was left
            Assert.True(main.IsSplitViewActive);
            Assert.True(main.IsSecondaryPaneFocused);
            Assert.Same(first.Secondary, main.FileListViewModel);

            // Ending the split in one tab doesn't touch the other tabs; it releases the second pane.
            int panes = main.AllPanes.Count();
            main.ToggleSplitViewCommand.Execute(null);
            await WaitUntil(() => !first.IsSplitViewActive);
            Assert.Equal(panes - 1, main.AllPanes.Count());
            Assert.Same(first.Primary, main.FileListViewModel);

            // The view (mode and zoom) of a new tab is the one being looked at.
            first.Primary.SelectedPreset = FileListViewPreset.Tiles;
            TabViewModel third = await tabs.OpenInNewTabAsync(P("Delta"), select: false);
            Assert.Equal(FileListViewPreset.Tiles, third.Primary.SelectedPreset);
        }

        [AvaloniaFact]
        public async Task Session_RestoresEveryTab_SleepingUntilSelected_AndPinnedTabsComeBackAtEveryStart()
        {
            List<TabSessionEntry> saved;
            int selected;
            using (MainViewModel main = NewMain())
            {
                TabsViewModel tabs = main.Tabs;
                await main.ProbeLoadAsync(tabs.SelectedTab.Primary, P("Alpha"));
                await tabs.OpenInNewTabAsync(P("Beta"), select: false);
                TabViewModel gamma = await tabs.OpenInNewTabAsync(P("Gamma"), select: true);
                main.ToggleSplitViewCommand.Execute(null);
                await WaitUntil(() => gamma.Secondary?.CurrentPath != null);
                await gamma.Secondary.LoadFolderAsync(P("Delta"));
                main.SetFocusedPane(isSecondary: true);
                await tabs.OpenInNewTabAsync(TabLocation.ThisPc, select: false);
                tabs.TogglePin(tabs.Tabs[1]); // Beta pinned
                saved = tabs.CaptureSession(out selected);
            }

            Assert.Equal(new[] { P("Beta"), P("Alpha"), P("Gamma"), TabLocation.ThisPc }, saved.Select(e => e.Location));
            Assert.Equal(2, selected);
            Assert.True(saved[0].Pinned);
            Assert.True(saved[2].Split && saved[2].SecondFocused && saved[2].SecondLocation == P("Delta"));

            // A broken entry, one that's gone, one from the file with a relative path: the good ones come back.
            saved.Add(new TabSessionEntry { Location = "not/rooted" });
            saved.Add(new TabSessionEntry { Location = P("Gone") });
            using (MainViewModel main = NewMain())
            {
                TabsViewModel tabs = main.Tabs;
                await tabs.RestoreAsync(saved, selected);
                Assert.Equal(5, tabs.Count);
                TabViewModel gamma = tabs.SelectedTab;
                Assert.Equal("Delta", gamma.Title); // the selected tab loaded at once, split, second pane focused
                Assert.True(gamma.IsSplitViewActive && gamma.IsSecondaryPaneFocused);
                Assert.Equal(P("Gamma"), gamma.Primary.CurrentPath);

                // The others sleep: titled from the saved place, nothing read until selected.
                TabViewModel alpha = tabs.Tabs[1];
                Assert.True(alpha.IsSleeping);
                Assert.Equal("Alpha", alpha.Title);
                Assert.Null(alpha.Primary.CurrentPath);
                tabs.Select(alpha);
                await WaitUntil(() => alpha.Primary.CurrentPath == P("Alpha"));
                Assert.False(alpha.IsSleeping);

                TabViewModel gone = tabs.Tabs[^1];
                tabs.Select(gone);
                await WaitUntil(() => gone.Primary.IsHomeView); // a folder that's gone shows Home
            }
        }

        [Fact]
        public void TheDefault_IsTheTitleBar_AndAnOlderFilesTop_MovesThereOnce()
        {
            Assert.Equal(TabSettings.PlacementTitleBar, new TabSettings().Placement);

            var older = new AppSettings { SchemaVersion = 2, Tabs = new TabSettings { Placement = TabSettings.PlacementTop } };
            older.Migrate();
            Assert.Equal(TabSettings.PlacementTitleBar, older.Tabs.Placement); // "Top" was the default then, not a choice
            Assert.Equal(AppSettings.CurrentSchemaVersion, older.SchemaVersion);

            var vertical = new AppSettings { SchemaVersion = 2, Tabs = new TabSettings { Placement = TabSettings.PlacementLeft } };
            vertical.Migrate();
            Assert.Equal(TabSettings.PlacementLeft, vertical.Tabs.Placement); // a real choice stays

            var current = new AppSettings { Tabs = new TabSettings { Placement = TabSettings.PlacementTop } };
            current.Migrate();
            Assert.Equal(TabSettings.PlacementTop, current.Tabs.Placement); // chosen after the title bar existed: kept
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task TheBar_InEveryLayoutAndPosition_FollowsTheTabs()
        {
            foreach (Func<MainWindowBase> make in new Func<MainWindowBase>[] { () => new ModernMainWindow(), () => new NemoMainWindow(), () => new FinderMainWindow() })
            {
                MainWindowBase window = make();
                window.Width = 1280;
                window.Height = 720;
                window.Show();
                TabSettings settings = null;
                try
                {
                    await Pump(30);
                    settings = window.ViewModel.SettingsService.Current.Tabs;
                    TabsViewModel tabs = window.ViewModel.Tabs;
                    TabStripView strip = window.FindControl<TabStripView>("TabStrip");
                    DockPanel area = window.FindControl<DockPanel>("TabbedArea");
                    Assert.NotNull(strip);
                    strip.AnimateMoves = false;
                    await window.ViewModel.FileListViewModel.LoadFolderAsync(P("Alpha"));
                    await tabs.OpenInNewTabAsync(P("Beta"), select: false);
                    await tabs.OpenInNewTabAsync(P("Gamma"), select: true);
                    tabs.Tabs[0].IsPinned = true; // the first tab: no move, and no save (the settings file is shared with parallel tests)
                    await Pump(5);
                    Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, strip.ItemViews.Select(v => v.Tab.Title));
                    Assert.Contains(strip.ItemViews, v => v.Classes.Contains("selected") && v.Tab.Title == "Gamma");

                    bool modern = window is ModernMainWindow;
                    Panel titleBarHost = window.FindControl<Panel>("TitleBarTabHost");
                    Panel leftHost = window.FindControl<Panel>("LeftTabHost");
                    foreach (string placement in new[] { TabSettings.PlacementTitleBar, TabSettings.PlacementTop, TabSettings.PlacementLeft, TabSettings.PlacementRight })
                    {
                        settings.Placement = placement; // as the settings window does, without saving (the file is shared with parallel tests)
                        tabs.NotifySettingsChanged();
                        await Pump(8);
                        Assert.Equal(3, strip.ItemViews.Count);
                        TabItemView pinned = strip.ItemViews[0];
                        bool inTitleBar = placement == TabSettings.PlacementTitleBar && modern;
                        bool atWindowEdge = placement == TabSettings.PlacementLeft && modern;
                        if (inTitleBar)
                        {
                            // In the title bar (Modern): the title's text gives way, the bar grows, a free strip stays before Minimize.
                            Assert.Same(titleBarHost, strip.Parent);
                            Assert.True(strip.IsInTitleBar);
                            Assert.False(window.FindControl<TextBlock>("TitleText").IsVisible);
                            Assert.Equal(40, window.FindControl<Grid>("RootGrid").RowDefinitions[0].Height.Value);
                            Control captions = window.FindControl<Control>("CaptionButtons");
                            double stripRight = strip.TranslatePoint(new Point(strip.Bounds.Width, 0), window).Value.X;
                            double captionsLeft = captions.TranslatePoint(new Point(0, 0), window).Value.X;
                            Assert.True(captionsLeft - stripRight >= 72, $"free title bar {captionsLeft - stripRight}");
                        }
                        else if (atWindowEdge)
                        {
                            // Left (Modern): at the window's left edge, before the sidebar.
                            Assert.Same(leftHost, strip.Parent);
                            Control sidebar = window.FindControl<Control>("LeftPanel");
                            Assert.True(strip.TranslatePoint(new Point(0, 0), window).Value.X < sidebar.TranslatePoint(new Point(0, 0), window).Value.X);
                        }
                        else
                        {
                            Assert.Same(area, strip.Parent);
                            Dock expected = placement == TabSettings.PlacementLeft ? Dock.Left : placement == TabSettings.PlacementRight ? Dock.Right : Dock.Top;
                            Assert.Equal(expected, DockPanel.GetDock(strip)); // Nemo and Finder: title bar tabs show on top
                            Assert.False(strip.IsInTitleBar);
                        }

                        if (placement == TabSettings.PlacementTitleBar || placement == TabSettings.PlacementTop)
                        {
                            Assert.True(strip.Bounds.Height < 60 && strip.ItemViews[1].Bounds.Width > 90);
                            Assert.True(pinned.Bounds.Width < strip.ItemViews[1].Bounds.Width); // pinned tabs are narrow
                        }
                        else
                        {
                            Assert.True(strip.Bounds.Width >= TabSettings.MinVerticalWidth && strip.Bounds.Height > 400);
                            Assert.True(strip.ItemViews[1].Bounds.Y > pinned.Bounds.Y);
                        }

                        if (!inTitleBar && modern)
                        {
                            Assert.True(window.FindControl<TextBlock>("TitleText").IsVisible); // the title is back
                        }

                        string shots = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                        if (!string.IsNullOrEmpty(shots))
                        {
                            Directory.CreateDirectory(shots);
                            Dispatcher.UIThread.RunJobs();
                            window.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"tabs-{window.GetType().Name}-{placement}.png"));
                        }
                    }

                    if (modern)
                    {
                        // Many tabs in the title bar: they shrink, then scroll; the free strip before Minimize stays.
                        settings.Placement = TabSettings.PlacementTitleBar;
                        tabs.NotifySettingsChanged();
                        for (int i = 0; i < 25; i++)
                        {
                            await tabs.OpenInNewTabAsync(TabLocation.ThisPc, select: false);
                        }

                        await Pump(10);
                        Assert.True(strip.Panel.IsOverflowing);
                        Control captions = window.FindControl<Control>("CaptionButtons");
                        double stripRight = strip.TranslatePoint(new Point(strip.Bounds.Width, 0), window).Value.X;
                        Assert.True(captions.TranslatePoint(new Point(0, 0), window).Value.X - stripRight >= 72);
                        string shots = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                        if (!string.IsNullOrEmpty(shots))
                        {
                            Dispatcher.UIThread.RunJobs();
                            window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "tabs-ModernMainWindow-TitleBar-many.png"));
                        }

                        tabs.CloseTabsAfterCommand.Execute(tabs.Tabs[2]);
                        settings.Placement = TabSettings.PlacementRight;
                        tabs.NotifySettingsChanged();
                        await Pump(5);
                    }

                    // Shrunk to icons, and back.
                    settings.VerticalCollapsed = true;
                    tabs.NotifySettingsChanged();
                    await Pump(12);
                    Assert.True(strip.Bounds.Width < 80);
                    settings.VerticalCollapsed = false;
                    settings.Placement = TabSettings.PlacementTop;
                    tabs.NotifySettingsChanged();

                    // Hidden with one tab when the setting says so.
                    settings.ShowWithOneTab = false;
                    tabs.CloseOtherTabsCommand.Execute(tabs.Tabs[2]);
                    tabs.Tabs[0].IsPinned = false;
                    tabs.CloseOtherTabsCommand.Execute(tabs.SelectedTab);
                    tabs.NotifySettingsChanged();
                    await Pump(3);
                    Assert.Equal(1, tabs.Count);
                    Assert.False(strip.IsVisible);
                    await tabs.NewTabAsync();
                    await Pump(3);
                    Assert.True(strip.IsVisible);
                }
                finally
                {
                    await CloseCleanly(window, settings);
                }
            }
        }

        [AvaloniaFact]
        public async Task KeysAndPointer_OpenCloseSwitchReorder()
        {
            var window = new ModernMainWindow { Width = 1280, Height = 720 };
            window.Show();
            TabSettings settings = null;
            try
            {
                await Pump(30);
                settings = window.ViewModel.SettingsService.Current.Tabs;
                TabsViewModel tabs = window.ViewModel.Tabs;
                TabStripView strip = window.FindControl<TabStripView>("TabStrip");
                strip.AnimateMoves = false;
                // On top: the bar's empty part is the bar's own there (in the title bar it is title bar: a double click
                // maximizes the window). Set here, not left to the shared settings file other tests write.
                settings.Placement = TabSettings.PlacementTop;
                tabs.NotifySettingsChanged();
                await window.ViewModel.FileListViewModel.LoadFolderAsync(P("Alpha"));

                Press(window, Key.T, RawInputModifiers.Control); // Ctrl+T
                await WaitUntil(() => tabs.Count == 2 && tabs.SelectedTab.Title == "Home");
                Press(window, Key.Tab, RawInputModifiers.Control); // Ctrl+Tab
                Assert.Equal("Alpha", tabs.SelectedTab.Title);
                Press(window, Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal("Home", tabs.SelectedTab.Title);
                Press(window, Key.D1, RawInputModifiers.Control); // Ctrl+1
                Assert.Equal("Alpha", tabs.SelectedTab.Title);
                Press(window, Key.D1, RawInputModifiers.Control | RawInputModifiers.Shift); // Ctrl+Shift+1 stays a view shortcut
                Assert.Equal("Alpha", tabs.SelectedTab.Title);
                Assert.Equal(FileListViewPreset.ExtraLargeIcons, window.ViewModel.FileListViewModel.SelectedPreset);
                window.ViewModel.FileListViewModel.SelectedPreset = FileListViewPreset.Details; // the saved default view, as it was
                Press(window, Key.W, RawInputModifiers.Control); // Ctrl+W
                Assert.Equal(new[] { "Home" }, Titles(tabs));
                Press(window, Key.T, RawInputModifiers.Control | RawInputModifiers.Shift); // Ctrl+Shift+T
                await WaitUntil(() => tabs.Count == 2 && tabs.SelectedTab.Title == "Alpha");
                Assert.Equal(new[] { "Alpha", "Home" }, Titles(tabs));

                // Pointer: a click selects, a middle click closes, a drag reorders.
                await tabs.OpenInNewTabAsync(P("Beta"), select: false);
                await tabs.OpenInNewTabAsync(P("Gamma"), select: false);
                await Pump(5);
                Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Home" }, Titles(tabs));
                Point Center(string title)
                {
                    TabItemView view = strip.ItemViews.First(v => v.Tab.Title == title);
                    return view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window).Value;
                }

                window.MouseDown(Center("Gamma"), MouseButton.Left);
                window.MouseUp(Center("Gamma"), MouseButton.Left);
                Assert.Equal("Gamma", tabs.SelectedTab.Title);

                Point from = Center("Alpha");
                Point to = Center("Gamma");
                window.MouseDown(from, MouseButton.Left);
                for (int i = 1; i <= 10; i++)
                {
                    window.MouseMove(new Point(from.X + (to.X - from.X + 20) * i / 10, from.Y), RawInputModifiers.LeftMouseButton);
                    await Pump(1);
                }

                window.MouseUp(new Point(to.X + 20, from.Y), MouseButton.Left);
                await Pump(3);
                Assert.Equal(new[] { "Beta", "Gamma", "Alpha", "Home" }, Titles(tabs));
                Assert.Equal("Alpha", tabs.SelectedTab.Title); // pressing it selected it
                Assert.Equal(new[] { "Beta", "Gamma", "Alpha", "Home" }, strip.ItemViews.Select(v => v.Tab.Title));

                Point beta = Center("Beta");
                window.MouseDown(beta, MouseButton.Middle);
                window.MouseUp(beta, MouseButton.Middle);
                await Pump(2);
                Assert.Equal(new[] { "Gamma", "Alpha", "Home" }, Titles(tabs));

                // Closing with the pointer keeps the tabs' width until the pointer leaves the bar.
                double width = strip.Panel.CurrentTabWidth;
                TabItemView gamma = strip.ItemViews.First(v => v.Tab.Title == "Gamma");
                gamma.CloseButton.Command = null;
                gamma.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                // Checked at once: other tests' windows opening and closing meanwhile reset the pointer-over state, which
                // reads as the pointer leaving the bar (and rightly ends the freeze).
                Assert.Equal(width, strip.Panel.FreezeTabWidth);
                await Pump(2);
                Assert.Equal(new[] { "Alpha", "Home" }, Titles(tabs));

                // A double click on the bar's empty part opens a new tab.
                Point empty = strip.TranslatePoint(new Point(strip.Bounds.Width - 140, 20), window).Value;
                window.MouseDown(empty, MouseButton.Left);
                window.MouseUp(empty, MouseButton.Left);
                window.MouseDown(empty, MouseButton.Left);
                window.MouseUp(empty, MouseButton.Left);
                await WaitUntil(() => tabs.Count == 3);
            }
            finally
            {
                await CloseCleanly(window, settings);
            }
        }

        [AvaloniaFact]
        public async Task SwitchingTabs_KeepsEachTabsSelection_AndFilesDroppedOnATabGoIntoItsFolder()
        {
            var window = new ModernMainWindow { Width = 1280, Height = 720 };
            window.Show();
            TabSettings settings = null;
            try
            {
                await Pump(30);
                settings = window.ViewModel.SettingsService.Current.Tabs;
                TabsViewModel tabs = window.ViewModel.Tabs;
                TabStripView strip = window.FindControl<TabStripView>("TabStrip");
                strip.AnimateMoves = false;
                FileListViewModel alpha = window.ViewModel.FileListViewModel;
                alpha.SelectedPreset = FileListViewPreset.Details;
                await alpha.LoadFolderAsync(P("Alpha"));
                await WaitUntil(() => alpha.Items?.Count == 40);
                FileListItemViewModel picked = alpha.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "file07.txt");
                alpha.SelectedItems.Clear();
                alpha.SelectedItems.Add(picked);
                await Pump(3);

                TabViewModel beta = await tabs.OpenInNewTabAsync(P("Beta"), select: true);
                await Pump(5);
                Assert.Same(beta.Primary, window.ViewModel.FileListViewModel);
                Assert.Equal(P("Beta"), window.ViewModel.Omnibar.Crumbs.Last().Path); // the address bar follows the tab

                tabs.Select(tabs.Tabs[0]);
                await Pump(5);
                Assert.Equal(new[] { "file07.txt" }, alpha.SelectedItems.Select(i => i.Name)); // the selection came back with the tab

                // Files dragged from the list and dropped on the Beta tab are copied into Beta (Ctrl held).
                string source = P(Path.Combine("Alpha", "file03.txt"));
                using Avalonia.Input.DataTransfer data = await ClipboardFileService.BuildTransferAsync(new[] { source }, isCut: false, storage: null);
                TabItemView betaView = strip.ItemViews.First(v => v.Tab.Title == "Beta");
                Point over = betaView.TranslatePoint(new Point(betaView.Bounds.Width / 2, betaView.Bounds.Height / 2), window).Value;
                window.DragDrop(over, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                window.DragDrop(over, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                Assert.Contains("drop", betaView.Classes);
                window.DragDrop(over, RawDragEventType.Drop, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                await WaitUntil(() => File.Exists(P(Path.Combine("Beta", "file03.txt"))), 500);
                Assert.True(File.Exists(source)); // copied, not moved
                Assert.DoesNotContain("drop", betaView.Classes);

                // The Beta tab's pane, in the background, shows the new file.
                await WaitUntil(() => beta.Primary.Items?.OfType<FileListItemViewModel>().Any(i => i.Name == "file03.txt") == true, 500);

                // Held over a tab, the tab opens (spring-loaded).
                window.DragDrop(over, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                window.DragDrop(over, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                await WaitUntil(() => tabs.SelectedTab == beta, 100);
                window.DragDrop(over, RawDragEventType.DragLeave, data, DragDropEffects.None, RawInputModifiers.None);
            }
            finally
            {
                await CloseCleanly(window, settings);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void Press(Window window, Key key, RawInputModifiers modifiers)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// Closes the window leaving no tab state for the next test: the settings file is shared, and pinned tabs (and the
        /// bar's position) would come back in every window opened after.
        /// </summary>
        private static async Task CloseCleanly(MainWindowBase window, TabSettings settings)
        {
            foreach (TabViewModel tab in window.ViewModel.Tabs.Tabs.Where(t => t.IsPinned).ToList())
            {
                window.ViewModel.Tabs.TogglePin(tab);
            }

            window.Close();
            await Pump(10);
            if (settings != null)
            {
                settings.Placement = TabSettings.PlacementTitleBar; // the default
                settings.VerticalCollapsed = false;
                settings.ShowWithOneTab = true;
                settings.SessionTabs = new List<TabSessionEntry>();
                settings.SessionSelectedTab = 0;
                await window.ViewModel.SettingsService.SaveAsync();
            }
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 300)
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

    internal static class TabTestExtensions
    {
        /// <summary>Loads a folder into a pane and waits for its items (the tab's title follows).</summary>
        public static async Task ProbeLoadAsync(this MainViewModel main, FileListViewModel pane, string folder)
        {
            await pane.LoadFolderAsync(folder);
            for (int i = 0; i < 200 && pane.CurrentPath != folder; i++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
