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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The folder color setting: folders are drawn in it everywhere, files keep theirs, "Reset to default" undoes it.</summary>
    public sealed class FolderColorTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "be-foldercolor-" + Guid.NewGuid().ToString("N"));

        public FolderColorTests()
        {
            for (int i = 0; i < 6; i++)
            {
                Directory.CreateDirectory(Path.Combine(_folder, $"folder {i}"));
            }

            File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");
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

        [AvaloniaFact]
        public async Task FolderColor_PaintsFolders_InTheViewsAndTheme_AndResets()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            MainViewModel main = window.ViewModel;
            AppearanceSettings appearance = null;
            try
            {
                await Pump(30);
                appearance = main.SettingsService.Current.Appearance;
                appearance.FolderColorHex = string.Empty;
                window.ApplyCustomization();
                main.PrimaryFileListViewModel.SelectedPreset = FileListViewPreset.LargeIcons;
                await main.PrimaryFileListViewModel.LoadFolderAsync(_folder);
                await Pump(15);
                Assert.Equal(FileIconGlyphs.DefaultFolderColorHex, FileIconGlyphs.FolderColorHex);
                Assert.True(Count(window, c => c.R > 200 && c.G > 150 && c.B < 110) > 200, "no default yellow folders drawn");

                window.OpenSettings();
                await Pump(10);
                ColorSetting folderColor = window.SettingsWindowForTests.ViewModel.Pages.Single(p => p.Key == "appearance")
                    .Items.OfType<ColorSetting>().Single(i => i.Title == "Folder color");
                Assert.True(folderColor.IsDefault);
                folderColor.PickCommand.Execute("#8764B8"); // purple
                await Pump(8);

                Assert.Equal("#8764B8", appearance.FolderColorHex);
                FileListItemViewModel folderItem = main.PrimaryFileListViewModel.Items.OfType<FileListItemViewModel>().First(i => i.IsDirectory);
                FileListItemViewModel fileItem = main.PrimaryFileListViewModel.Items.OfType<FileListItemViewModel>().First(i => !i.IsDirectory);
                Assert.Equal(Color.Parse("#8764B8"), ((ISolidColorBrush)folderItem.IconColorBrush).Color);
                Assert.Equal(FileIconGlyphs.ColorHexFor("notes.txt", false), fileItem.IconColorHex); // files keep their color
                var themeFolder = (ISolidColorBrush)window.FindResource("FolderYellow");
                var themeLid = (ISolidColorBrush)window.FindResource("FolderYellowLid");
                Assert.Equal(Color.Parse("#8764B8"), themeFolder.Color);
                Assert.True(themeLid.Color.R > themeFolder.Color.R && themeLid.Color.G > themeFolder.Color.G); // a lighter lid
                Assert.True(Count(window, c => c.R > 110 && c.R < 160 && c.G > 80 && c.G < 120 && c.B > 160) > 200, "no purple folders drawn");
                Assert.True(Count(window, c => c.R > 200 && c.G > 150 && c.B < 110) < 50, "yellow folders left on screen");
                Save(window, "folder-color-purple.png");

                // Reset to default: the yellow is back, and nothing is saved.
                window.SettingsWindowForTests.ViewModel.ResetLook();
                await Pump(8);
                Assert.Equal(string.Empty, appearance.FolderColorHex);
                Assert.Equal(FileIconGlyphs.DefaultFolderColorHex, FileIconGlyphs.FolderColorHex);
                Assert.False(Application.Current.Resources.ContainsKey("FolderYellow"));
                Assert.True(folderColor.IsDefault);
                Assert.True(Count(window, c => c.R > 200 && c.G > 150 && c.B < 110) > 200, "the default yellow did not come back");
            }
            finally
            {
                if (appearance != null)
                {
                    appearance.FolderColorHex = string.Empty;
                    window.ApplyCustomization();
                    await main.SettingsService.SaveAsync();
                }

                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
            }
        }

        /// <summary>How many pixels of the rendered window match.</summary>
        private static unsafe int Count(Avalonia.Controls.Window window, Func<Color, bool> match)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame() as WriteableBitmap ?? throw new InvalidOperationException("no frame");
            using var buffer = frame.Lock();
            bool rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            int count = 0;
            for (int y = 0; y < buffer.Size.Height; y++)
            {
                byte* line = (byte*)buffer.Address + y * buffer.RowBytes;
                for (int x = 0; x < buffer.Size.Width; x++)
                {
                    byte first = line[x * 4], g = line[x * 4 + 1], third = line[x * 4 + 2];
                    if (match(Color.FromRgb(rgba ? first : third, g, rgba ? third : first)))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void Save(Avalonia.Controls.Window window, string name)
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
