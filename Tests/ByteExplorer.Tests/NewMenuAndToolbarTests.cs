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
using ByteExplorer.Core.Platform;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The toolbar: the New button (Folder, Shortcut, Text Document, the system's file kinds; here a Templates folder),
    /// the "..." menu's Select none, Compress to ZIP file and Pin to Quick access, and the order: New first, Split view
    /// just before "...", View at the right end.
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class NewMenuAndToolbarTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-newmenu-" + Guid.NewGuid().ToString("N"));
        private readonly string _templates;

        public NewMenuAndToolbarTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Work", "Folder A"));
            File.WriteAllText(Path.Combine(_root, "Work", "a.txt"), "aaa");
            _templates = Path.Combine(_root, "Templates");
            Directory.CreateDirectory(_templates);
            File.WriteAllText(Path.Combine(_templates, "Report.odt"), "template contents");
            File.WriteAllText(Path.Combine(_templates, ".hidden"), "x");
            NewItemTemplates.ResetForTests(_templates);
        }

        public void Dispose()
        {
            NewItemTemplates.ResetForTests();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private string Work => Path.Combine(_root, "Work");

        [AvaloniaFact]
        public async Task TheNewMenu_OffersTheSystemsKinds_AndMakesThemFromTheirTemplates()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                FileListViewModel pane = await OpenAsync(window);
                await NewItemTemplates.GetAsync();
                var items = NewItemSelector.BuildItems(pane, null).OfType<MenuItem>().ToList();
                string[] headers = items.Select(i => (string)i.Header).ToArray();
                Assert.Equal(new[] { "Folder", OperatingSystem.IsWindows() ? "Shortcut…" : "Link…", "Text Document", "Report" }, headers);
                Assert.All(items, i => Assert.True(i.IsEnabled));

                items.Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                string made = Path.Combine(Work, "Report.odt");
                await WaitUntil(() => File.Exists(made));
                Assert.Equal("template contents", File.ReadAllText(made)); // a copy of the template
                items.Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await WaitUntil(() => File.Exists(Path.Combine(Work, "Report (2).odt"))); // never over an existing file

                await window.ViewModel.Operations.UndoLastAsync();
                await WaitUntil(() => !File.Exists(Path.Combine(Work, "Report (2).odt")));
                await window.ViewModel.Operations.RedoLastAsync();
                await WaitUntil(() => File.Exists(Path.Combine(Work, "Report (2).odt")));
                Assert.Equal("template contents", File.ReadAllText(Path.Combine(Work, "Report (2).odt"))); // redone with its contents

                // The right-click menu's New has the same items.
                Assert.Contains(NewItemTemplates.ScanTemplatesFolder(_templates), t => t.DisplayName == "Report");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>The New button clicked with the pointer: its menu opens with every row in it, laid out (it used to open empty).</summary>
        [AvaloniaFact]
        public async Task TheNewButton_OpensItsMenuWithItsRows()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await OpenAsync(window);
                await NewItemTemplates.GetAsync();
                var selector = window.FindControl<NewItemSelector>("NewItemSelectorControl");
                Button button = selector.GetVisualDescendants().OfType<Button>().First();
                Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window).Value;
                window.MouseDown(center, Avalonia.Input.MouseButton.Left);
                window.MouseUp(center, Avalonia.Input.MouseButton.Left);
                await WaitUntil(() => selector.OpenMenu != null);
                await Pump(5);

                var rows = selector.OpenMenu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(new[] { "Folder", OperatingSystem.IsWindows() ? "Shortcut…" : "Link…", "Text Document", "Report" }, rows.Select(r => (string)r.Header));
                Assert.All(rows, r =>
                {
                    Assert.NotNull(TopLevel.GetTopLevel(r)); // in the open menu's popup
                    Assert.True(r.Bounds.Height > 10 && r.Bounds.Width > 50, $"{r.Header} {r.Bounds}");
                });

                // A row clicked with the pointer does its job (Folder), and the menu closes.
                MenuItem folder = rows[0];
                TopLevel popup = TopLevel.GetTopLevel(folder);
                Point at = folder.TranslatePoint(new Point(folder.Bounds.Width / 2, folder.Bounds.Height / 2), popup).Value;
                popup.MouseDown(at, Avalonia.Input.MouseButton.Left);
                popup.MouseUp(at, Avalonia.Input.MouseButton.Left);
                await WaitUntil(() => Directory.Exists(Path.Combine(Work, "New folder")));
                await WaitUntil(() => selector.OpenMenu == null);

                // Clicked again: a new menu, again with its rows.
                window.MouseDown(center, Avalonia.Input.MouseButton.Left);
                window.MouseUp(center, Avalonia.Input.MouseButton.Left);
                await WaitUntil(() => selector.OpenMenu != null);
                await Pump(3);
                Assert.All(selector.OpenMenu.Items.OfType<MenuItem>(), r => Assert.NotNull(TopLevel.GetTopLevel(r)));
                selector.OpenMenu.Hide();
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task MoreOptions_SelectNoneCompressAndPin_AndTheToolbarsOrder()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                FileListViewModel pane = await OpenAsync(window);
                FileListItemViewModel folder = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "Folder A");
                pane.SelectedItems.Clear();
                pane.SelectedItems.Add(folder);
                await Pump(2);

                // Pin to Quick access (the text follows), then unpin.
                Assert.Equal("Pin to Quick access", pane.PinSelectionText);
                pane.TogglePinSelectionCommand.Execute(null);
                await WaitUntil(() => window.ViewModel.Pins.Contains(folder.FullPath));
                Assert.Equal("Unpin from Quick access", pane.PinSelectionText);
                await WaitUntil(() => pane.TogglePinSelectionCommand.CanExecute(null)); // the pin's save has finished
                pane.TogglePinSelectionCommand.Execute(null);
                await WaitUntil(() => !window.ViewModel.Pins.Contains(folder.FullPath));

                // Compress to ZIP file.
                Assert.Equal(new[] { "Folder A" }, pane.SelectedItems.Select(i => i.Name));
                Assert.True(pane.CompressSelectionCommand.CanExecute(null));
                pane.CompressSelectionCommand.Execute(null);
                await WaitUntil(() => File.Exists(Path.Combine(Work, "Folder A.zip")), 500);

                // Select none.
                Assert.True(pane.SelectNoneCommand.CanExecute(null));
                pane.SelectNoneCommand.Execute(null);
                await Pump(2);
                Assert.Empty(pane.SelectedItems);
                Assert.False(pane.SelectNoneCommand.CanExecute(null));

                // The order: New first; Split view right before "..."; View at the right end.
                Control toolbar = window.FindControl<Control>("Toolbar");
                double X(Control c) => c.TranslatePoint(new Point(0, 0), toolbar).Value.X;
                Control newButton = window.FindControl<NewItemSelector>("NewItemSelectorControl");
                Control more = window.FindControl<MoreOptionsSelector>("MoreOptionsSelectorControl");
                Control view = window.FindControl<ViewModeSelector>("ViewModeSelectorControl");
                Button split = toolbar.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Split view", StringComparison.Ordinal) == true);
                Assert.True(X(newButton) < 30);
                Assert.True(X(split) < X(more) && X(more) - (X(split) + split.Bounds.Width) < 10);
                Assert.True(X(view) + view.Bounds.Width > toolbar.Bounds.Width - 20); // at the right end
                Assert.True(X(view) > X(more) + 200);

                string shots = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(shots))
                {
                    Directory.CreateDirectory(shots);
                    Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame()?.Save(Path.Combine(shots, "toolbar.png"));
                }
            }
            finally
            {
                foreach (string path in window.ViewModel.Pins.Items.Select(p => p.Path).Where(p => p.StartsWith(_root, StringComparison.Ordinal)).ToList())
                {
                    await window.ViewModel.Pins.UnpinAsync(path);
                }

                window.Close();
            }
        }

        private async Task<FileListViewModel> OpenAsync(MainWindowBase window)
        {
            await Pump(30);
            FileListViewModel pane = window.ViewModel.FileListViewModel;
            await pane.LoadFolderAsync(Work);
            await WaitUntil(() => pane.Items?.Count == 2);
            return pane;
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
