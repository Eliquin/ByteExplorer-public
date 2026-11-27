// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Starts the real Modern window on the headless platform: it must construct, initialize its view models and render
    /// without an exception. Set BYTEEXPLORER_SCREENSHOT_DIR to also save a PNG of the rendered window.
    /// </summary>
    public sealed class MainWindowSmokeTests
    {
        [AvaloniaFact]
        public async Task ModernWindow_StartsAndRenders()
        {
            var window = new ModernMainWindow { Width = 1400, Height = 800 };
            window.Show();

            // Let InitializeAsync (settings, drives, the home dashboard) run its continuations.
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(25);
                Dispatcher.UIThread.RunJobs();
            }

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, "modern-window.png"));
            }

            window.Close();
        }

        /// <summary>Opens a real folder in each main view mode; every mode must render (and is saved as a PNG when asked).</summary>
        [AvaloniaFact]
        public async Task ModernWindow_RendersAFolderInEveryMode()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-shot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Projects"));
            Directory.CreateDirectory(Path.Combine(folder, "Photos"));
            foreach (string name in new[] { "report.pdf", "notes.txt", "budget.xlsx", "song.mp3", "clip.mp4", "archive.zip", "main.cs", "picture.png" })
            {
                File.WriteAllText(Path.Combine(folder, name), new string('x', name.Length * 1000));
            }

            var window = new ModernMainWindow { Width = 1400, Height = 800 };
            window.Show();
            try
            {
                await PumpAsync(40);
                var viewModel = (MainViewModel)window.DataContext;
                await viewModel.FileListViewModel.LoadFolderAsync(folder);
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");

                foreach (FileListViewPreset preset in new[] { FileListViewPreset.Details, FileListViewPreset.MediumIcons, FileListViewPreset.Tiles, FileListViewPreset.List })
                {
                    viewModel.FileListViewModel.SelectedPreset = preset;
                    await PumpAsync(10);
                    var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        frame.Save(Path.Combine(directory, $"folder-{preset}.png"));
                    }
                }
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, recursive: true);
            }
        }

        private static async Task PumpAsync(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(25);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
