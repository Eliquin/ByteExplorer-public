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
using SkiaSharp;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class ThumbnailTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-thumbs-" + Guid.NewGuid().ToString("N"));

        public ThumbnailTests()
        {
            Directory.CreateDirectory(_root);
            var colors = new[] { SKColors.OrangeRed, SKColors.SeaGreen, SKColors.RoyalBlue, SKColors.Gold, SKColors.MediumPurple, SKColors.Teal };
            for (int i = 0; i < 12; i++)
            {
                using var bitmap = new SKBitmap(i % 2 == 0 ? 640 : 360, i % 2 == 0 ? 360 : 640);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(colors[i % colors.Length]);
                    using var paint = new SKPaint { Color = SKColors.White };
                    canvas.DrawCircle(bitmap.Width / 2f, bitmap.Height / 2f, Math.Min(bitmap.Width, bitmap.Height) / 4f, paint);
                }

                SKEncodedImageFormat format = i % 3 == 0 ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                using SKData data = bitmap.Encode(format, 90);
                File.WriteAllBytes(Path.Combine(_root, $"photo {i:D2}.{(format == SKEncodedImageFormat.Png ? "png" : "jpg")}"), data.ToArray());
            }

            File.WriteAllText(Path.Combine(_root, "broken.jpg"), "not an image");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "text");
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
        [InlineData(FileListViewPreset.MediumIcons)]
        [InlineData(FileListViewPreset.LargeIcons)]
        [InlineData(FileListViewPreset.Tiles)]
        public async Task ImagesShowThumbnails(FileListViewPreset preset)
        {
            long start = ThumbnailCache.DecodedCount;
            long ready = 0;
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                var vm = ((MainViewModel)window.DataContext).FileListViewModel;
                vm.SelectedPreset = preset;
                await vm.LoadFolderAsync(_root);
                for (int i = 0; i < 200 && ready < 12; i++)
                {
                    await Pump(1);
                    ready = ThumbnailCache.DecodedCount - start;
                }

                Assert.True(ready >= 12, $"{preset}: only {ready} thumbnails decoded");
                var frame = window.CaptureRenderedFrame();
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    frame.Save(Path.Combine(directory, $"thumbs-{preset}.png"));
                }
            }
            finally
            {
                window.Close();
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
