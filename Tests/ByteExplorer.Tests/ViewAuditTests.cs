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
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Every view of the file list driven with real (simulated) pointer and keyboard input on a real window: double-click
    /// opens a folder, Enter opens, Backspace goes back up, click selects, arrows move.
    /// </summary>
    public sealed class ViewAuditTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-audit-" + Guid.NewGuid().ToString("N"));

        public ViewAuditTests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_root);
            for (int i = 0; i < 40; i++)
            {
                string folder = Path.Combine(_root, $"Folder {i:D3}");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
            }

            for (int i = 0; i < 200; i++)
            {
                File.WriteAllText(Path.Combine(_root, $"file {i:D4}.txt"), new string('x', i));
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        public static IEnumerable<object[]> Presets() =>
            Enum.GetValues<FileListViewPreset>().Select(p => new object[] { p });

        [AvaloniaTheory]
        [MemberData(nameof(Presets))]
        public async Task DoubleClickOnAFolder_OpensIt(FileListViewPreset preset)
        {
            (ModernMainWindow window, FileListViewModel vm) = await OpenAsync(preset);
            try
            {
                ListBoxItem container = FirstItemContainer(window, vm, folder: true);
                Assert.True(container != null, $"{preset}: no realized folder item");
                string expected = ((ItemOfContainer(container))?.FullPath) ?? throw new InvalidOperationException("no item");

                Point center = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window) ?? default;

                // Every view opens it (in Columns it becomes the new root). Headless input is stamped with real time, so on a
                // loaded machine the first click's own work can push the second past the double-click time: then the two
                // clicks count as single ones and the double-click is tried again.
                for (int attempt = 0; attempt < 3 && vm.CurrentPath != expected; attempt++)
                {
                    window.MouseDown(center, MouseButton.Left);
                    window.MouseUp(center, MouseButton.Left);
                    window.MouseDown(center, MouseButton.Left);
                    window.MouseUp(center, MouseButton.Left);
                    for (int i = 0; i < 100 && vm.CurrentPath != expected; i++)
                    {
                        await PumpAsync(1);
                    }
                }

                Assert.Equal(expected, vm.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTheory]
        [MemberData(nameof(Presets))]
        public async Task ClickSelects_ArrowMoves_EnterOpens_BackspaceReturns(FileListViewPreset preset)
        {
            (ModernMainWindow window, FileListViewModel vm) = await OpenAsync(preset);
            try
            {
                ListBoxItem container = FirstItemContainer(window, vm, folder: true);
                Assert.NotNull(container);
                Point center = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window) ?? default;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                await PumpAsync(4);
                FileListItemViewModel clicked = ItemOfContainer(container);
                Assert.Equal(clicked?.FullPath, vm.SelectedItem?.FullPath);

                if (preset != FileListViewPreset.Columns)
                {
                    window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                    window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                    await PumpAsync(4);
                    Assert.NotEqual(clicked?.FullPath, vm.SelectedItem?.FullPath);
                    window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                    window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                    await PumpAsync(4);
                }

                string target = vm.SelectedItem?.FullPath;
                Assert.True(target != null && Directory.Exists(target), $"{preset}: selection is not a folder: {target}");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await PumpUntilAsync(() => vm.CurrentPath == target && !vm.IsLoading);
                Assert.Equal(target, vm.CurrentPath);

                window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
                window.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
                await PumpUntilAsync(() => vm.CurrentPath == _root && !vm.IsLoading);
                Assert.Equal(_root, vm.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Back_RestoresScrollAndSelection_Up_SelectsTheFolderLeft()
        {
            (ModernMainWindow window, FileListViewModel vm) = await OpenAsync(FileListViewPreset.Details);
            try
            {
                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                var scroller = (ScrollViewer)list.Scroll;
                list.Selection.Clear();
                list.Selection.Select(150);
                list.ScrollIntoView(150);
                await PumpAsync(5);
                double offset = scroller.Offset.Y;
                Assert.True(offset > 100, "the list should have scrolled");
                string selectedName = vm.SelectedItem?.Name;
                Assert.Equal("file 0110.txt", selectedName); // 40 folders first, then files

                string folder = Path.Combine(_root, "Folder 007");
                await vm.LoadFolderAsync(folder);
                await PumpAsync(10);
                Assert.True(vm.NavigateBackCommand.CanExecute(null));
                vm.NavigateBackCommand.Execute(null);
                await PumpUntilAsync(() => vm.CurrentPath == _root && !vm.IsLoading);
                Assert.Equal(_root, vm.CurrentPath);
                Assert.Equal(selectedName, vm.SelectedItem?.Name);
                Assert.Equal(offset, ((ScrollViewer)list.Scroll).Offset.Y, 1);

                // Into a folder, then Up: the folder just left is selected.
                await vm.LoadFolderAsync(folder);
                await PumpAsync(10);
                vm.NavigateUpCommand.Execute(null);
                await PumpUntilAsync(() => vm.CurrentPath == _root && !vm.IsLoading);
                Assert.Equal(_root, vm.CurrentPath);
                Assert.Equal("Folder 007", vm.SelectedItem?.Name);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTheory]
        [InlineData(FileListViewPreset.Details)]
        [InlineData(FileListViewPreset.MediumIcons)]
        [InlineData(FileListViewPreset.Tiles)]
        public async Task F2_RenamesInPlace_EscapeCancels(FileListViewPreset preset)
        {
            (ModernMainWindow window, FileListViewModel vm) = await OpenAsync(preset);
            try
            {
                // Folders come first; select the first file (index 40) and bring it into view, as a click would.
                ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.IsEffectivelyVisible && l.Classes.Contains("fileList"));
                list.Selection.Clear();
                list.Selection.Select(40);
                list.Selection.AnchorIndex = 40;
                list.ScrollIntoView(40);
                await PumpAsync(4);
                list.ContainerFromIndex(40)?.Focus();
                string original = vm.SelectedItem.Name;
                Assert.Equal("file 0000.txt", original);

                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                window.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                await PumpAsync(5);
                TextBox box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Text == original && t.IsFocused);
                Assert.True(box != null, $"{preset}: no in-place rename box");
                Assert.Equal(Path.GetFileNameWithoutExtension(original).Length, box.SelectionEnd);

                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await PumpAsync(5);
                Assert.True(File.Exists(Path.Combine(_root, original)));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == original);

                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                window.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                await PumpAsync(5);
                box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == original && t.IsFocused);
                box.Text = "renamed in place.txt";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await PumpAsync(25);
                Assert.False(File.Exists(Path.Combine(_root, original)));
                Assert.True(File.Exists(Path.Combine(_root, "renamed in place.txt")));
                Assert.Equal(_root, vm.CurrentPath); // Enter in the box must not open anything
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Escape_InAddressBar_CancelsEditing()
        {
            (ModernMainWindow window, FileListViewModel vm) = await OpenAsync(FileListViewPreset.Details);
            try
            {
                window.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
                window.KeyRelease(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
                await PumpAsync(5);
                Assert.True(window.ViewModel.Omnibar.IsEditing);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await PumpAsync(5);
                Assert.False(window.ViewModel.Omnibar.IsEditing);
            }
            finally
            {
                window.Close();
            }
        }

        private async Task<(ModernMainWindow, FileListViewModel)> OpenAsync(FileListViewPreset preset)
        {
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            await PumpAsync(30);
            var main = (MainViewModel)window.DataContext;
            FileListViewModel vm = main.FileListViewModel;
            vm.SelectedPreset = preset;
            await vm.LoadFolderAsync(_root);
            await PumpAsync(20);
            return (window, vm);
        }

        private static ListBoxItem FirstItemContainer(Window window, FileListViewModel vm, bool folder)
        {
            return window.GetVisualDescendants().OfType<ListBoxItem>()
                .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && ItemOfContainer(c) is FileListItemViewModel item && item.IsDirectory == folder)
                .Where(c => c.TranslatePoint(default, window) is Point p && p.Y > 0 && p.Y < window.Bounds.Height - 60)
                .FirstOrDefault();
        }

        private static FileListItemViewModel ItemOfContainer(ListBoxItem container) =>
            container.DataContext as FileListItemViewModel ?? (container.DataContext as Views.TimelineRow)?.Item;

        /// <summary>Pumps until the condition holds (or about 5 s pass): fixed round counts are flaky under parallel load.</summary>
        private static async Task PumpUntilAsync(Func<bool> condition)
        {
            for (int i = 0; i < 250 && !condition(); i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            await PumpAsync(2); // let the views catch up with the view model
        }

        private static async Task PumpAsync(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
