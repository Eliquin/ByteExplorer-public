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
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The item spacing of the four views with a rubber band (Details, List, Icons, Tiles): the gap between items is empty
    /// list space of the chosen size, a press there starts the band and a drag selects the items it covers, and the
    /// spacing set in the settings window lays the list out again at once.
    /// </summary>
    public sealed class ItemSpacingTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "be-spacing-" + Guid.NewGuid().ToString("N"));

        public ItemSpacingTests()
        {
            Directory.CreateDirectory(_folder);
            for (int i = 0; i < 24; i++)
            {
                File.WriteAllText(Path.Combine(_folder, $"file {i:00}.txt"), "x");
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        [AvaloniaTheory]
        [InlineData(FileListViewPreset.Details, "DetailsList", FileListSettings.DefaultItemSpacingDetails, "Details view")]
        [InlineData(FileListViewPreset.List, "ListModeList", FileListSettings.DefaultItemSpacingList, "List view")]
        [InlineData(FileListViewPreset.MediumIcons, "IconsList", FileListSettings.DefaultItemSpacingIcons, "Icons views")]
        [InlineData(FileListViewPreset.Tiles, "TilesList", FileListSettings.DefaultItemSpacingTiles, "Tiles view")]
        public async Task TheGapBetweenItems_StartsARubberBand_AndItsSizeIsASetting(FileListViewPreset preset, string listName, int defaultGap, string settingTitle)
        {
            var window = new ModernMainWindow { Width = 1300, Height = 850 };
            window.Show();
            MainViewModel main = window.ViewModel;
            FileListSettings settings = null;
            try
            {
                await Pump(30);
                settings = main.SettingsService.Current.FileList;
                Write(settings, listName, defaultGap);
                main.PrimaryFileListViewModel.NotifyDisplaySettingsChanged();
                main.PrimaryFileListViewModel.SelectedPreset = preset;
                await main.PrimaryFileListViewModel.LoadFolderAsync(_folder);
                await Pump(15);

                ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == listName && l.IsEffectivelyVisible);
                bool rows = preset is FileListViewPreset.Details or FileListViewPreset.List;
                Assert.Equal(defaultGap, Gap(list, rows), 1);

                // A press in the gap between the first two items starts the band; dragging over the second selects it.
                Control first = list.ContainerFromIndex(0), second = list.ContainerFromIndex(1);
                Point gap = rows
                    ? new Point(first.Bounds.Center.X, (first.Bounds.Bottom + second.Bounds.Top) / 2)
                    : new Point((first.Bounds.Right + second.Bounds.Left) / 2, first.Bounds.Center.Y);
                Point start = first.GetVisualParent().TranslatePoint(gap, window) ?? default;
                Point end = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height / 2), window) ?? default;
                Assert.Same(list, Hit(window, start)); // the gap is the list's own space, not an item's
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.LeftMouseButton);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                await Pump(3);
                Border band = window.FindControl<Views.FileListView>("PrimaryPane").FindControl<Border>("SelectionBand");
                Assert.True(band.IsVisible, "the press in the gap did not start a rubber band");
                Save(window, $"spacing-{preset.ToString().ToLowerInvariant()}-band.png");
                window.MouseUp(end, MouseButton.Left);
                await Pump(3);
                Assert.Contains(1, list.Selection.SelectedIndexes);
                Assert.Equal(_folder, main.PrimaryFileListViewModel.CurrentPath); // nothing was dragged or opened

                // The setting: a wider gap, applied to the open list at once.
                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                NumberSetting spacing = vm.Pages.Single(p => p.Key == "files").Items.OfType<NumberSetting>().Single(i => i.Title == settingTitle);
                Assert.Equal(defaultGap, spacing.Value);
                vm.SelectedPage = vm.Pages.Single(p => p.Key == "files");
                window.SettingsWindowForTests.Height = 1200;
                await Pump(5);
                Save(window.SettingsWindowForTests, "settings-item-spacing.png");
                spacing.Value = 30;
                await Pump(10);
                Assert.Equal(30, Read(settings, listName));
                Assert.Equal(30, Gap(list, rows), 1);
                spacing.Value = 0;
                await Pump(10);
                Assert.Equal(0, Gap(list, rows), 1);
            }
            finally
            {
                if (settings != null)
                {
                    // The settings window saved the changes to the tests' shared settings file: put the default back there too.
                    Write(settings, listName, defaultGap);
                    await main.SettingsService.SaveAsync();
                }

                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
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

        /// <summary>The empty space between the first two items (below the first row, or right of the first item).</summary>
        private static double Gap(ListBox list, bool rows)
        {
            Control first = list.ContainerFromIndex(0), second = list.ContainerFromIndex(1);
            return rows ? second.Bounds.Top - first.Bounds.Bottom : second.Bounds.Left - first.Bounds.Right;
        }

        /// <summary>The innermost list or item under a window point.</summary>
        private static Control Hit(Window window, Point point)
        {
            for (Visual visual = window.InputHitTest(point) as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is ListBoxItem or ListBox)
                {
                    return (Control)visual;
                }
            }

            return null;
        }

        private static int Read(FileListSettings s, string list) => list switch
        {
            "DetailsList" => s.ItemSpacingDetails,
            "ListModeList" => s.ItemSpacingList,
            "IconsList" => s.ItemSpacingIcons,
            _ => s.ItemSpacingTiles,
        };

        private static void Write(FileListSettings s, string list, int value)
        {
            switch (list)
            {
                case "DetailsList": s.ItemSpacingDetails = value; break;
                case "ListModeList": s.ItemSpacingList = value; break;
                case "IconsList": s.ItemSpacingIcons = value; break;
                default: s.ItemSpacingTiles = value; break;
            }
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
