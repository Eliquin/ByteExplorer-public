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
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The toolbar's "..." menu has the right-click menu's Password submenu, for the selection.</summary>
    public sealed class MoreOptionsPasswordTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-morepw-" + Guid.NewGuid().ToString("N"));

        public MoreOptionsPasswordTests()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "secret.txt"), "x");
            File.WriteAllText(Path.Combine(_root, "locked.txt.bxlock"), "not really encrypted");
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
        public async Task ThePasswordSubmenu_FollowsTheSelection()
        {
            var window = new ModernMainWindow { Width = 1100, Height = 760 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);
                MoreOptionsSelector more = window.GetVisualDescendants().OfType<MoreOptionsSelector>().Single();

                pane.SelectedItems.Clear();
                more.UpdatePasswordItem(pane);
                Assert.False(more.PasswordItem.IsEnabled);
                Assert.Contains("Select files or folders", ToolTip.GetTip(more.PasswordItem) as string);

                FileListItemViewModel secret = pane.Items.Cast<FileListItemViewModel>().Single(i => i.Name == "secret.txt");
                pane.SelectedItems.ReplaceAll(new[] { secret });
                more.UpdatePasswordItem(pane);
                Assert.True(more.PasswordItem.IsEnabled);
                Assert.Equal(new[] { "Protect with password…" }, Headers(more.PasswordItem));

                // A protected item: its own choices, as in the right-click menu.
                FileListItemViewModel locked = pane.Items.Cast<FileListItemViewModel>().Single(i => i.Name == "locked.txt.bxlock");
                pane.SelectedItems.ReplaceAll(new[] { locked });
                more.UpdatePasswordItem(pane);
                Assert.Contains("Open with password…", Headers(more.PasswordItem));
                Assert.Contains("Remove protection…", Headers(more.PasswordItem));

                // Protected and unprotected together: nothing to offer.
                pane.SelectedItems.ReplaceAll(new[] { secret, locked });
                more.UpdatePasswordItem(pane);
                Assert.False(more.PasswordItem.IsEnabled);
            }
            finally
            {
                window.CloseWithoutAsking();
            }
        }

        [AvaloniaTheory]
        [InlineData("Nemo")]
        [InlineData("Finder")]
        [InlineData("Modern")]
        public async Task TheToolsMenu_HasThePasswordCategory_InEveryLayout(string layout)
        {
            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1100;
            window.Height = 760;
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);
                AppMenu menu = window.GetVisualDescendants().OfType<AppMenu>().First();

                pane.SelectedItems.Clear();
                menu.BuildPasswordRows();
                Button only = menu.PasswordPanel.Children.OfType<Button>().Single();
                Assert.False(only.IsEnabled);
                Assert.Equal("Select files or folders first", only.Content);

                FileListItemViewModel secret = pane.Items.Cast<FileListItemViewModel>().Single(i => i.Name == "secret.txt");
                pane.SelectedItems.ReplaceAll(new[] { secret });
                menu.BuildPasswordRows();
                Assert.Equal(new object[] { "Protect with password…" }, menu.PasswordPanel.Children.OfType<Button>().Select(b => b.Content));

                FileListItemViewModel locked = pane.Items.Cast<FileListItemViewModel>().Single(i => i.Name == "locked.txt.bxlock");
                pane.SelectedItems.ReplaceAll(new[] { locked });
                menu.BuildPasswordRows();
                List<object> rows = menu.PasswordPanel.Children.OfType<Button>().Select(b => b.Content).ToList();
                Assert.Contains("Open with password…", rows);
                Assert.Contains("Change password…", rows);
                Assert.Contains("Remove protection…", rows);
                Assert.Contains(menu.PasswordPanel.Children, c => c is Border); // the divider, as in the submenu
            }
            finally
            {
                window.CloseWithoutAsking();
            }
        }

        private static List<string> Headers(MenuItem menu) =>
            (menu.ItemsSource as IEnumerable<Control>)?.OfType<MenuItem>().Select(m => m.Header as string).ToList() ?? new List<string>();

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
