// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class ThemeTests
    {
        [AvaloniaFact]
        public async Task ThemeCommand_SwitchesTheWholeWindow()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-theme-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Projects"));
            foreach (string name in new[] { "report.pdf", "budget.xlsx", "notes.txt", "photo.png", "song.mp3" })
            {
                File.WriteAllText(Path.Combine(folder, name), new string('x', 2048));
            }

            var window = new ModernMainWindow { Width = 1224, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                var main = (MainViewModel)window.DataContext;
                await main.FileListViewModel.LoadFolderAsync(folder);
                main.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");

                foreach (string theme in new[] { "Light", "Dark" })
                {
                    main.SetThemeCommand.Execute(theme);
                    await Pump(10);
                    Assert.Equal(theme, main.Theme);
                    Assert.Equal(theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark, Application.Current.ActualThemeVariant);

                    Assert.True(window.TryFindResource("ContentBrush", window.ActualThemeVariant, out object brush));
                    Color content = ((ISolidColorBrush)brush).Color;
                    Assert.Equal(theme == "Light", content.R > 200);

                    var frame = window.CaptureRenderedFrame();
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        frame.Save(Path.Combine(directory, $"theme-{theme}.png"));
                    }
                }
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
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
