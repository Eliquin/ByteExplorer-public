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
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The application menu's rows, clicked with the pointer as a person does, in both styles (a category's items in a
    /// submenu beside the menu, or unfolded in place): every command row runs its command. (A category's command rows
    /// used to do nothing in the submenu style: the menu closed before the command ran and the row lost its binding.)
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class AppMenuRowsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-appmenu-" + Guid.NewGuid().ToString("N"));

        public AppMenuRowsTests()
        {
            Directory.CreateDirectory(_root);
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                File.WriteAllText(Path.Combine(_root, name), name);
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
        [InlineData(true)]
        [InlineData(false)]
        public async Task EveryKindOfRow_DoesItsJob(bool submenus)
        {
            var window = new ModernMainWindow { Width = 1200, Height = 760 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool savedStyle = true;
            TabSettings tabs = null;
            try
            {
                await Pump(30);
                savedStyle = main.IsMenuSubmenuHorizontal;
                main.IsMenuSubmenuHorizontal = submenus;
                tabs = main.SettingsService.Current.Tabs;
                FileListViewModel pane = main.FileListViewModel;
                pane.SelectedPreset = FileListViewPreset.Details;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 3);
                Button trigger = window.FindControl<AppMenu>("AppMenu").FindControl<Button>("TriggerButton");

                // Edit: Select all, Invert selection.
                await Run(window, trigger, "EditHeader", "Select all");
                await WaitUntil(() => pane.SelectedItems.Count == 3);
                Assert.False(trigger.Flyout.IsOpen); // the menu closed after the command
                await Run(window, trigger, "EditHeader", "Invert selection");
                await WaitUntil(() => pane.SelectedItems.Count == 0);

                // Edit: New folder, New text file; then Undo (a top row) takes the text file back.
                await Run(window, trigger, "EditHeader", "New folder");
                await WaitUntil(() => Directory.Exists(Path.Combine(_root, "New folder")));
                await Run(window, trigger, "EditHeader", "New text file");
                string text = Path.Combine(_root, "New text document.txt");
                await WaitUntil(() => File.Exists(text));
                await Run(window, trigger, null, "Undo New text document");
                await WaitUntil(() => !File.Exists(text));

                // Edit: Copy, then Paste: a copy beside it.
                await WaitUntil(() => pane.Items?.OfType<FileListItemViewModel>().Any(i => i.Name == "a.txt") == true);
                pane.SelectedItems.Clear();
                pane.SelectedItems.Add(pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "a.txt"));
                await Run(window, trigger, "EditHeader", "Copy");
                await Pump(5);
                await Run(window, trigger, "EditHeader", "Paste");
                await WaitUntil(() => File.Exists(Path.Combine(_root, "a - Copy.txt")), 500);

                // Tabs (top rows): New tab, then Reopen closed tab after closing it.
                await Run(window, trigger, null, "New tab");
                await WaitUntil(() => main.Tabs.Count == 2);
                main.Tabs.CloseTab(main.Tabs.SelectedTab);
                await Run(window, trigger, null, "Reopen closed tab");
                await WaitUntil(() => main.Tabs.Count == 2);
                main.Tabs.CloseTab(main.Tabs.Tabs[1]);
                await WaitUntil(() => main.FileListViewModel.CurrentPath == _root);
                pane = main.FileListViewModel;

                // View: the tab bar's position (a command row), a view preset (a click handler).
                await Run(window, trigger, "ViewHeader", "Left");
                Assert.Equal(TabSettings.PlacementLeft, tabs.Placement);
                await Run(window, trigger, "ViewHeader", "Top");
                Assert.Equal(TabSettings.PlacementTop, tabs.Placement);
                await Run(window, trigger, "ViewHeader", "List");
                Assert.Equal(FileListViewPreset.List, pane.SelectedPreset);

                // Sort.
                await Run(window, trigger, "SortHeader", "Size");
                Assert.Equal(FileListSortColumn.Size, pane.SortColumn);
                await Run(window, trigger, "SortHeader", "Descending");
                Assert.False(pane.SortAscending);
                await Run(window, trigger, "SortHeader", "Name");
                await Run(window, trigger, "SortHeader", "Ascending");

                // Switches: split view (View), hidden items (a top row); they leave the menu open.
                await Switch(window, trigger, "ViewHeader", "Split view");
                await WaitUntil(() => main.IsSplitViewActive);
                await Switch(window, trigger, "ViewHeader", "Split view");
                await WaitUntil(() => !main.IsSplitViewActive);
                bool hidden = pane.ShowHiddenItems;
                await Switch(window, trigger, null, "Hidden items");
                await WaitUntil(() => pane.ShowHiddenItems != hidden);
                await Switch(window, trigger, null, "Hidden items");
                await WaitUntil(() => pane.ShowHiddenItems == hidden);

                // Zoom, Refresh, Theme, Settings.
                double size = pane.IconSize;
                await ClickIn(window, trigger, b => (ToolTip.GetTip(b) as string)?.StartsWith("Zoom in", StringComparison.Ordinal) == true);
                Assert.True(pane.IconSize > size);
                await ClickIn(window, trigger, b => (ToolTip.GetTip(b) as string)?.StartsWith("Zoom out", StringComparison.Ordinal) == true);
                Assert.Equal(size, pane.IconSize, 1);
                File.WriteAllText(Path.Combine(_root, "made outside.txt"), "x");
                await Run(window, trigger, null, "Refresh");
                await WaitUntil(() => pane.Items?.OfType<FileListItemViewModel>().Any(i => i.Name == "made outside.txt") == true);
                string theme = main.Theme;
                await ClickIn(window, trigger, b => b.Content as string == (theme == "Dark" ? "Light" : "Dark"));
                Assert.NotEqual(theme, main.Theme);
                await ClickIn(window, trigger, b => b.Content as string == theme);
                Assert.Equal(theme, main.Theme);
                await Run(window, trigger, null, "Settings");
                await WaitUntil(() => window.OwnedWindows.OfType<ByteExplorer.Views.Settings.SettingsWindow>().Any());
                window.OwnedWindows.OfType<ByteExplorer.Views.Settings.SettingsWindow>().First().Close();
                await Pump(5);

                // Go.
                await Run(window, trigger, "GoHeader", "This PC");
                await WaitUntil(() => pane.IsThisPcView);
                await Run(window, trigger, "GoHeader", "Home");
                await WaitUntil(() => pane.IsHomeView);
            }
            finally
            {
                main.IsMenuSubmenuHorizontal = savedStyle;
                main.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                if (tabs != null)
                {
                    tabs.Placement = TabSettings.PlacementTop;
                }

                window.Close();
            }
        }

        /// <summary>Opens the menu, the category (when given), and clicks the row with that text, all with the pointer.</summary>
        private static async Task Run(Window window, Button trigger, string category, string row)
        {
            Click(window, trigger);
            await Pump(8);
            Assert.True(trigger.Flyout.IsOpen, "the menu opens");
            var content = (Control)((Flyout)trigger.Flyout).Content;
            Button Find() => content.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && TextOf(b) == row);
            if (category != null)
            {
                // Open the category; under load the hover timer can open or close it around the click: until the row shows.
                Button header = content.GetLogicalDescendants().OfType<Button>().First(b => b.Name == category);
                for (int attempt = 0; attempt < 4 && Find() == null; attempt++)
                {
                    Click(window, header);
                    for (int i = 0; i < 25 && Find() == null; i++)
                    {
                        await Pump(1);
                    }
                }
            }

            Button target = Find();
            Assert.True(target != null, $"row \"{row}\" is shown");
            target.BringIntoView();
            await Pump(3);
            Assert.True(target.IsEffectivelyEnabled, $"row \"{row}\" is enabled");
            Click(window, target);
            await Pump(8);
        }

        /// <summary>Flips the switch on the row with that label (the menu stays open: it is closed afterwards).</summary>
        private static async Task Switch(Window window, Button trigger, string category, string label)
        {
            Click(window, trigger);
            await Pump(8);
            var content = (Control)((Flyout)trigger.Flyout).Content;
            if (category != null)
            {
                Click(window, content.GetLogicalDescendants().OfType<Button>().First(b => b.Name == category));
                await Pump(6);
            }

            ToggleSwitch toggle = content.GetLogicalDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("switchRow") && g.IsEffectivelyVisible && g.Children.OfType<TextBlock>().Any(t => t.Text == label))
                .Select(g => g.Children.OfType<ToggleSwitch>().FirstOrDefault())
                .First(t => t != null);
            toggle.BringIntoView();
            await Pump(3);
            Click(window, toggle);
            await Pump(6);
            trigger.Flyout.Hide();
            await Pump(4);
        }

        /// <summary>Opens the menu and clicks the first button matching <paramref name="match"/> (zoom, theme).</summary>
        private static async Task ClickIn(Window window, Button trigger, Func<Button, bool> match)
        {
            Click(window, trigger);
            await Pump(8);
            var content = (Control)((Flyout)trigger.Flyout).Content;
            Button button = content.GetLogicalDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && match(b));
            button.BringIntoView();
            await Pump(3);
            Click(window, button);
            await Pump(6);
            trigger.Flyout.Hide();
            await Pump(4);
        }

        private static string TextOf(Button button) => button.Content as string ??
            button.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => !t.Classes.Contains("gesture"))?.Text;

        private static void Click(Window window, Visual target)
        {
            TopLevel top = TopLevel.GetTopLevel(target) ?? window;
            Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top) ?? default;
            top.MouseDown(center, MouseButton.Left);
            top.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
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
}
