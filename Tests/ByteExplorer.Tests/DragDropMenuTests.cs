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
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Platform;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Menus;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Dragging in the file list as in Explorer: Ctrl+press on a selected item keeps the selection (so Ctrl+drag copies,
    /// a duplicate in the same folder), Ctrl+click still deselects; a right-button drag asks on drop: Copy here, Move here,
    /// Create shortcuts here, Cancel (shortcuts undoable).
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class DragDropMenuTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-dropmenu-" + Guid.NewGuid().ToString("N"));

        public DragDropMenuTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Target"));
            File.WriteAllText(Path.Combine(_root, "report.txt"), "report");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "notes");
        }

        public void Dispose()
        {
            DropChoice.OpenMenu = null;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private string P(string name) => Path.Combine(_root, name);

        [AvaloniaFact]
        public async Task CtrlPress_KeepsTheSelectionForADrag_AndCtrlClickStillDeselects()
        {
            var window = new ModernMainWindow { Width = 1100, Height = 700 };
            window.Show();
            try
            {
                FileListViewModel pane = await OpenAsync(window);
                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                int index = pane.Items.OfType<FileListItemViewModel>().ToList().FindIndex(i => i.Name == "report.txt");
                list.Selection.Select(index);
                await Pump(3);
                Control container = list.ContainerFromIndex(index);
                Point center = container.TranslatePoint(new Point(40, container.Bounds.Height / 2), window).Value;

                window.MouseDown(center, MouseButton.Left, RawInputModifiers.Control);
                await Pump(1);
                Assert.Contains(pane.SelectedItems, i => i.Name == "report.txt"); // still selected while the button is down: a drag takes it

                window.MouseUp(center, MouseButton.Left, RawInputModifiers.Control);
                await Pump(2);
                Assert.DoesNotContain(pane.SelectedItems, i => i.Name == "report.txt"); // a click without a drag deselects
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Drops_CtrlInTheSameFolderDuplicates_RightDragAsksCopyMoveOrShortcut()
        {
            var window = new ModernMainWindow { Width = 1100, Height = 700 };
            window.Show();
            try
            {
                FileListViewModel pane = await OpenAsync(window);
                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                Point empty = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 30), window).Value;
                using Avalonia.Input.DataTransfer data = await ClipboardFileService.BuildTransferAsync(new[] { P("report.txt") }, isCut: false, storage: null);

                // Ctrl+drop into the item's own folder: "report - Copy.txt".
                DropAt(window, empty, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.Control);
                await WaitUntil(() => File.Exists(P("report - Copy.txt")));

                // A plain drop there does nothing (as in Explorer).
                DropAt(window, empty, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
                await Pump(10);
                Assert.Equal(3, Directory.GetFiles(_root).Length);

                // Right-button drag, same folder: the menu offers Copy (bold), no Move, shortcuts; Copy duplicates again.
                string[] offered = null;
                string bold = null;
                string choose = "Copy here";
                DropChoice.OpenMenu = (menu, _) =>
                {
                    var items = menu.ItemsSource.OfType<MenuItem>().ToList();
                    offered = items.Where(i => i.IsEnabled).Select(i => (string)i.Header).ToArray();
                    bold = items.FirstOrDefault(i => i.FontWeight == Avalonia.Media.FontWeight.SemiBold)?.Header as string;
                    MenuItem pick = items.First(i => (string)i.Header == choose);
                    Dispatcher.UIThread.Post(() => pick.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)));
                };
                using (FileDragDropHelper.SimulateRightButtonDragForTests())
                {
                    DropAt(window, empty, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
                }

                await WaitUntil(() => File.Exists(P("report - Copy (2).txt")));
                Assert.Equal(new[] { "Copy here", "Create link here", "Cancel" }.Select(s => OperatingSystem.IsWindows() ? s.Replace("link", "shortcut") : s), offered);
                Assert.Equal("Copy here", bold);

                // Onto the Target folder's row: Move here moves.
                Control target = list.ContainerFromIndex(pane.Items.OfType<FileListItemViewModel>().ToList().FindIndex(i => i.Name == "Target"));
                Point onTarget = target.TranslatePoint(new Point(40, target.Bounds.Height / 2), window).Value;
                choose = "Move here";
                using (FileDragDropHelper.SimulateRightButtonDragForTests())
                {
                    DropAt(window, onTarget, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
                }

                await WaitUntil(() => File.Exists(Path.Combine(_root, "Target", "report.txt")) && !File.Exists(P("report.txt")));
                Assert.Equal("Move here", bold); // same drive: a left drag would have moved

                // Create shortcuts here (a link on Linux and macOS, a .lnk on Windows), then Ctrl+Z removes it.
                using Avalonia.Input.DataTransfer notes = await ClipboardFileService.BuildTransferAsync(new[] { P("notes.txt") }, isCut: false, storage: null);
                choose = OperatingSystem.IsWindows() ? "Create shortcut here" : "Create link here";
                using (FileDragDropHelper.SimulateRightButtonDragForTests())
                {
                    DropAt(window, onTarget, notes, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
                }

                string link = Path.Combine(_root, "Target", OperatingSystem.IsWindows() ? "notes.txt - Shortcut.lnk" : "Link to notes.txt");
                await WaitUntil(() => File.Exists(link));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(P("notes.txt"), new FileInfo(link).LinkTarget);
                }

                Assert.True(File.Exists(P("notes.txt"))); // the original stays
                await window.ViewModel.Operations.UndoLastAsync();
                await WaitUntil(() => !File.Exists(link));

                // Cancel does nothing.
                choose = "Cancel";
                using (FileDragDropHelper.SimulateRightButtonDragForTests())
                {
                    DropAt(window, onTarget, notes, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
                }

                await Pump(15);
                Assert.Equal(new[] { "report.txt" }, Directory.GetFileSystemEntries(Path.Combine(_root, "Target")).Select(Path.GetFileName));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public void ShortcutNames_FollowTheOsAndNeverTakeAnExistingName()
        {
            string first = Shortcuts.PathFor(P("report.txt"), _root);
            Assert.Equal(P(OperatingSystem.IsWindows() ? "report.txt - Shortcut.lnk" : "Link to report.txt"), first);
            File.WriteAllText(first, "taken");
            Assert.Equal(P(OperatingSystem.IsWindows() ? "report.txt - Shortcut (2).lnk" : "Link to report.txt (2)"), Shortcuts.PathFor(P("report.txt"), _root));
            Assert.False(Shortcuts.CanCreate("phone://Pixel/Internal storage/a.jpg", _root));
        }

        /// <summary>A whole drag over the point: enter, over, drop (as a real drag delivers it).</summary>
        private static void DropAt(Window window, Point at, IDataTransfer data, DragDropEffects allowed, RawInputModifiers modifiers)
        {
            window.DragDrop(at, RawDragEventType.DragEnter, data, allowed, modifiers);
            window.DragDrop(at, RawDragEventType.DragOver, data, allowed, modifiers);
            window.DragDrop(at, RawDragEventType.Drop, data, allowed, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        private async Task<FileListViewModel> OpenAsync(MainWindowBase window)
        {
            await Pump(30);
            FileListViewModel pane = window.ViewModel.FileListViewModel;
            pane.SelectedPreset = FileListViewPreset.Details;
            await pane.LoadFolderAsync(_root);
            await WaitUntil(() => pane.Items?.Count == 3);
            await Pump(5);
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
