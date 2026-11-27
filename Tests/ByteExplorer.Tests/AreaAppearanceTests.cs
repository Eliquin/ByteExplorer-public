// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The window areas' text size, text color and background, the main view's background image and "Reset to default",
    /// changed from the settings window in every layout.
    /// </summary>
    public sealed class AreaAppearanceTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "be-look-" + Guid.NewGuid().ToString("N"));

        public AreaAppearanceTests() => Directory.CreateDirectory(_folder);

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

        [Fact]
        public void Colors_AreNormalized_AndAreasSanitized()
        {
            Assert.Equal("#FF8800", AreaAppearance.NormalizeColor("f80"));
            Assert.Equal("#1A2B3C", AreaAppearance.NormalizeColor(" #1a2b3c "));
            Assert.Equal("#801A2B3C", AreaAppearance.NormalizeColor("801a2b3c"));
            Assert.Equal(string.Empty, AreaAppearance.NormalizeColor("#12345"));
            Assert.Equal(string.Empty, AreaAppearance.NormalizeColor("red"));

            var appearance = new AppearanceSettings();
            appearance.Areas.Add(new AreaAppearance { Area = "Nowhere", TextScale = 1.2 });
            appearance.Areas.Add(new AreaAppearance { Area = AreaAppearance.MainView, TextScale = 9 });
            appearance.Areas.Add(new AreaAppearance { Area = AreaAppearance.MainView, TextColorHex = "#FFF" });
            appearance.Areas.Add(new AreaAppearance { Area = AreaAppearance.StatusBar, TextColorHex = "nonsense" }); // nothing left: dropped
            appearance.MainViewBackgroundImageOpacity = 7;
            appearance.MainViewBackgroundImageFit = "tile";
            appearance.Sanitize();
            AreaAppearance mainView = Assert.Single(appearance.Areas);
            Assert.Equal(AreaAppearance.MaxScale, mainView.TextScale);
            Assert.Equal(1.0, appearance.MainViewBackgroundImageOpacity);
            Assert.Equal(AppearanceSettings.ImageFitTile, appearance.MainViewBackgroundImageFit);
        }

        [AvaloniaTheory]
        [InlineData("Modern")]
        [InlineData("Nemo")]
        [InlineData("Finder")]
        public async Task Areas_AreCustomizedFromTheSettings_AndResetToTheDefaultLook(string layout)
        {
            string image = Path.Combine(_folder, "wall.png");
            using (var bitmap = new WriteableBitmap(new PixelSize(320, 200), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque))
            {
                using (ILockedFramebuffer frame = bitmap.Lock())
                {
                    var row = new byte[frame.RowBytes];
                    for (int y = 0; y < frame.Size.Height; y++)
                    {
                        for (int x = 0; x < frame.Size.Width; x++)
                        {
                            row[x * 4] = (byte)(255 - y);          // blue
                            row[x * 4 + 1] = (byte)(x * 255 / 320); // green
                            row[x * 4 + 2] = (byte)(y + 40);        // red
                            row[x * 4 + 3] = 255;
                        }

                        System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
                    }
                }

                bitmap.Save(image);
            }

            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1300;
            window.Height = 860;
            window.Show();
            MainViewModel main = window.ViewModel;
            AppearanceSettings appearance = null;
            try
            {
                await Pump(30);
                appearance = main.SettingsService.Current.Appearance; // after the window loaded the settings (a new object)
                appearance.ResetCustomizations();
                window.ApplyCustomization();
                AreaPart status = window.AreaFor(AreaAppearance.StatusBar);
                AreaPart mainView = window.AreaFor(AreaAppearance.MainView);
                AreaPart toolbar = window.AreaFor(AreaAppearance.Toolbar);
                Assert.Equal(6, AreaAppearance.All.Count(a => window.AreaFor(a.Key) != null));
                IBrush statusBackground = Background(status.Host);
                double toolbarRow = toolbar.Row?.Height.Value ?? 0;

                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                SettingsPage page = vm.Pages.Single(p => p.Key == "appearance");
                var area = page.Items.OfType<ChoiceSetting>().Single(i => i.Title == "Area");
                var size = page.Items.OfType<NumberSetting>().Single(i => i.Title == "Text size" && i.Unit == "%");
                var textColor = page.Items.OfType<ColorSetting>().Single(i => i.Title == "Text color");
                var background = page.Items.OfType<ColorSetting>().Single(i => i.Title == "Background color");

                // The status bar: red text, a navy background, bigger.
                area.Selected = area.Options.Single(o => o.Value == AreaAppearance.StatusBar);
                textColor.EditText = "#E8112"; // half typed: nothing changes yet
                Assert.False(textColor.IsEditTextValid);
                Assert.Null(appearance.FindArea(AreaAppearance.StatusBar));
                textColor.EditText = "#E81123";
                background.PickCommand.Execute("#1E3A5F");
                size.Value = 125;
                await Pump(5);
                Assert.Equal(Color.Parse("#E81123"), ((ISolidColorBrush)status.Host.Resources["TextPrimary"]).Color);
                Assert.Equal(Color.Parse("#1E3A5F"), ((ISolidColorBrush)Background(status.Host)).Color);
                Assert.Equal(1.25, ((ScaleTransform)AreaStyler.ScalerOf(status.Host).LayoutTransform).ScaleX);
                Assert.True(status.Host.Bounds.Height * 1.2 < AreaStyler.ScalerOf(status.Host).Bounds.Height + 1);
                AreaAppearance saved = appearance.FindArea(AreaAppearance.StatusBar);
                Assert.Equal((1.25, "#E81123", "#1E3A5F"), (saved.TextScale, saved.TextColorHex, saved.BackgroundColorHex));

                // Another area shows its own (default) values; the toolbar's fixed row grows with its text.
                area.Selected = area.Options.Single(o => o.Value == AreaAppearance.Toolbar);
                Assert.True(textColor.IsDefault);
                Assert.Equal(100, size.Value);
                size.Value = 120;
                await Pump(3);
                if (toolbar.Row != null && toolbarRow > 0)
                {
                    Assert.Equal(Math.Round(toolbarRow * 1.2), toolbar.Row.Height.Value);
                }

                // A menu opened from a customized area keeps the theme's text color.
                var menu = new ContextMenu { Items = { new MenuItem { Header = "Copy" } } };
                status.Host.ContextMenu = menu;
                menu.Open(status.Host);
                await Pump(3);
                object themeText = Application.Current.FindResource(window.ActualThemeVariant, "TextPrimary");
                Assert.Equal(((ISolidColorBrush)themeText).Color, ((ISolidColorBrush)menu.FindResource("TextPrimary")).Color);
                menu.Close();

                // "Use everywhere" gives every area the status bar's background.
                area.Selected = area.Options.Single(o => o.Value == AreaAppearance.StatusBar);
                page.Items.OfType<ActionSetting>().Single(i => i.Title == "Every area").Command.Execute(null);
                await Pump(3);
                foreach ((string key, _) in AreaAppearance.All)
                {
                    Assert.Equal(Color.Parse("#1E3A5F"), ((ISolidColorBrush)Background(window.AreaFor(key).Host)).Color);
                }

                // The background image: a layer under the files, which are drawn on a transparent surface.
                var imageItem = page.Items.OfType<FolderSetting>().Single(i => i.Title == "Background image");
                var loaded = new TaskCompletionSource();
                window.BackgroundImageLoaded += (_, _) => loaded.TrySetResult();
                imageItem.Path = image;
                for (int i = 0; i < 200 && !loaded.Task.IsCompleted; i++)
                {
                    await Pump(1);
                }

                Assert.True(loaded.Task.IsCompleted, "the background image never loaded");
                Border layer = window.BackgroundImageLayerForTests;
                Assert.IsType<ImageBrush>(layer.Background);
                Assert.Equal(AppearanceSettings.DefaultImageOpacity, layer.Opacity);
                Assert.Same(Brushes.Transparent, AreaStyler.ContentOf(mainView.Host).Resources["ContentBrush"]);
                Save(window, $"look-{layout.ToLowerInvariant()}.png");
                vm.SelectedPage = page;
                window.SettingsWindowForTests.Height = 2100;
                await Pump(8);
                Save(window.SettingsWindowForTests, "settings-appearance-areas.png");

                // Reset to default: the window looks as the theme has it again, nothing customized is saved.
                vm.ResetLook();
                await Pump(5);
                Assert.False(appearance.HasCustomizations);
                Assert.False(status.Host.Resources.ContainsKey("TextPrimary"));
                Assert.Same(statusBackground, Background(status.Host));
                Assert.Null(AreaStyler.ScalerOf(status.Host).LayoutTransform);
                Assert.Null(window.BackgroundImageLayerForTests);
                Assert.False(AreaStyler.ContentOf(mainView.Host).Resources.ContainsKey("ContentBrush"));
                if (toolbar.Row != null && toolbarRow > 0)
                {
                    Assert.Equal(toolbarRow, toolbar.Row.Height.Value);
                }

                Assert.True(textColor.IsDefault);
                Assert.True(background.IsDefault);
            }
            finally
            {
                appearance?.ResetCustomizations();
                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
            }
        }

        private static IBrush Background(Control host) => host switch
        {
            Border border => border.Background,
            TemplatedControl control => control.Background,
            Panel panel => panel.Background,
            _ => null,
        };

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
