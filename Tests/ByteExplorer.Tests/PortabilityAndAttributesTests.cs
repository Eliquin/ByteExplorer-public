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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Sidebar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Settings that travel with the portable app (Quick access follows the computer it runs on), and how hidden and
    /// system items look (translucent; system text in the system-item color).
    /// </summary>
    public sealed class PortabilityAndAttributesTests
    {
        [Fact]
        public void KnownFolders_AreSavedAsTokens_AndResolvedOnThisComputer()
        {
            string documents = SpecialFolders.Documents;
            string projects = Path.Combine(documents, "Projects", "2024");
            Assert.Equal("::documents", PortablePaths.ToPortable(documents));
            Assert.Equal("::documents/Projects/2024", PortablePaths.ToPortable(projects));
            Assert.Equal(projects, PortablePaths.Resolve("::documents/Projects/2024"));
            Assert.Equal(SpecialFolders.Desktop, PortablePaths.Resolve(PortablePaths.ToPortable(SpecialFolders.Desktop)));
            Assert.Equal(SpecialFolders.Home, PortablePaths.Resolve("::home"));
            Assert.StartsWith("::home/", PortablePaths.ToPortable(Path.Combine(SpecialFolders.Home, "some-folder-outside-the-known-ones")));

            // Left alone: other drives, network paths, the trash key, unknown tokens.
            string other = OperatingSystem.IsWindows() ? @"D:\Data\Work" : "/srv/data/work";
            Assert.Equal(other, PortablePaths.ToPortable(other));
            Assert.Equal(other, PortablePaths.Resolve(other));
            Assert.Equal(@"\\server\share", PortablePaths.ToPortable(@"\\server\share"));
            Assert.Equal(PinColors.TrashKey, PortablePaths.Resolve(PinColors.TrashKey));
            Assert.Equal("::future-token", PortablePaths.Resolve("::future-token"));
        }

        [Fact]
        public void AnotherComputersProfilePaths_MapToThisComputer()
        {
            Assert.Equal(Path.Combine(SpecialFolders.Documents, "Work"), PortablePaths.Resolve(@"C:\Users\someone-else-xyz\Documents\Work"));
            Assert.Equal(SpecialFolders.Desktop, PortablePaths.Resolve(@"C:\Users\someone-else-xyz\OneDrive\Desktop"));
            Assert.Equal(SpecialFolders.Downloads, PortablePaths.Resolve("/home/someone-else-xyz/Downloads"));
            Assert.Equal(SpecialFolders.Videos, PortablePaths.Resolve("/Users/someone-else-xyz/Movies"));
            Assert.Equal(SpecialFolders.Home, PortablePaths.Resolve("/home/someone-else-xyz"));

            // A folder of the other profile that has no counterpart here stays as it was (shown dimmed).
            string unknown = @"C:\Users\someone-else-xyz\Code\private-" + Guid.NewGuid().ToString("N");
            Assert.Equal(unknown, PortablePaths.Resolve(unknown));
        }

        [AvaloniaFact]
        public async Task QuickAccess_FromAnotherComputer_ShowsThisComputersFolders_AndIsSavedPortable()
        {
            string file = Path.Combine(Path.GetTempPath(), "be-pins-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var settings = new SettingsService(file);
                await settings.LoadAsync();
                settings.Current.Navigation.Pinned = new List<PinnedFolderSettings>
                {
                    new PinnedFolderSettings { Path = @"C:\Users\laptop-owner-xyz\Downloads" },
                    new PinnedFolderSettings { Path = "/home/laptop-owner-xyz/Pictures/Holidays", Name = "Trips", Color = "Blue" },
                    new PinnedFolderSettings { Path = PinColors.TrashKey },
                };

                var pins = new PinnedFoldersViewModel(settings);
                pins.Reload();
                Assert.Equal(new[] { SpecialFolders.Downloads, Path.Combine(SpecialFolders.Pictures, "Holidays"), PinColors.TrashKey }, pins.Items.Select(i => i.Path));
                Assert.Equal("Trips", pins.Items[1].Name);
                await Pump(5);

                // Saved again in the portable form, so the next computer resolves it to its own folders.
                Assert.Equal(new[] { "::downloads", "::pictures/Holidays", PinColors.TrashKey }, settings.Current.Navigation.Pinned.Select(p => p.Path));
                Assert.Contains("::downloads", File.ReadAllText(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [AvaloniaFact]
        public void Defaults_AreSavedAsTokens()
        {
            List<PinnedFolderSettings> defaults = PinnedFoldersViewModel.CreateDefaults(Array.Empty<string>());
            Assert.Contains(defaults, p => PortablePaths.ToPortable(p.Path) == "::desktop");
            Assert.Contains(defaults, p => PortablePaths.ToPortable(p.Path) == "::downloads");
        }

        [AvaloniaFact]
        public void Items_CarryHiddenAndSystem_AndSystemTextIsDrawnInTheSystemColor()
        {
            var system = new FileListItemViewModel(new DirectoryIndexEntry("pagefile.sys", false, 10, DateTime.UtcNow, FileAttributes.System), "/x");
            var hidden = new FileListItemViewModel(new DirectoryIndexEntry(".config", true, 0, DateTime.UtcNow, FileAttributes.Hidden | FileAttributes.Directory), "/x");
            var plain = new FileListItemViewModel(new DirectoryIndexEntry("notes.txt", false, 10, DateTime.UtcNow, FileAttributes.Normal), "/x");
            Assert.True(system.IsSystem);
            Assert.False(system.IsHidden);
            Assert.True(hidden.IsHidden);
            Assert.False(plain.IsHidden || plain.IsSystem);

            (int systemRed, int systemInk) = RenderRow(system);
            (int plainRed, int plainInk) = RenderRow(plain);
            Assert.True(systemRed > 20, $"system row: {systemRed} reddish of {systemInk} ink pixels");
            Assert.True(plainInk > 20, "the plain row draws text");
            Assert.Equal(0, plainRed);

            // Tree folders and search results carry the same attributes.
            var node = new DirectoryNodeInfo("System Volume Information", "/x/System Volume Information", isHidden: true, isSystem: true);
            Assert.True(node.IsHidden && node.IsSystem);
        }

        [AvaloniaFact]
        public async Task Window_ShowsHiddenItemsTranslucent_AndNetworkUnderTheTree()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-hidden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, ".cache"));
            Directory.CreateDirectory(Path.Combine(folder, "Projects"));
            File.WriteAllText(Path.Combine(folder, ".env"), "x");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
            var window = new ModernMainWindow { Width = 1200, Height = 760 };
            window.Show();
            MainViewModel main = window.ViewModel;
            bool showHidden = false;
            try
            {
                await Pump(30);
                showHidden = main.FileListViewModel.ShowHiddenItems;
                if (!showHidden)
                {
                    main.FileListViewModel.ToggleShowHiddenItemsCommand.Execute(null);
                }

                main.FileListViewModel.SelectedPreset = FileListViewPreset.Details;
                await main.FileListViewModel.LoadFolderAsync(folder);
                await Pump(10);
                var items = main.FileListViewModel.Items.Cast<FileListItemViewModel>().ToList();
                Assert.Equal(4, items.Count);
                Assert.True(items.Single(i => i.Name == ".env").IsHidden);
                Assert.True(items.Single(i => i.Name == ".cache").IsHidden);
                Assert.False(items.Single(i => i.Name == "notes.txt").IsHidden);

                // The left panel: Quick access and Recent on top, the tree, then Network under it.
                var tree = window.FindControl<Views.NavigationPaneView>("NavigationPane");
                var network = window.GetLogicalDescendantsOfType<Views.Sidebar.SidebarSection>().Single(sec => sec.SectionKey == "Network");
                Point treeAt = tree.TranslatePoint(default, window) ?? default;
                Point networkAt = network.TranslatePoint(default, window) ?? default;
                Assert.True(networkAt.Y > treeAt.Y, $"network at {networkAt.Y}, tree at {treeAt.Y}");

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "hidden-items.png"));
                }
            }
            finally
            {
                if (main.FileListViewModel.ShowHiddenItems != showHidden)
                {
                    main.FileListViewModel.ToggleShowHiddenItemsCommand.Execute(null);
                }

                window.Close();
                Directory.Delete(folder, true);
            }
        }

        /// <summary>Draws a Details row on a neutral background: (reddish text pixels, all text pixels).</summary>
        private static (int Red, int Ink) RenderRow(FileListItemViewModel item)
        {
            var view = new FileItemView { Kind = FileItemKind.Details, ItemHeight = 28, IconSize = 0, FontSize = 16, DataContext = item, Foreground = Brushes.White };
            var window = new Window { Width = 400, Height = 40, Background = Brushes.Black, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // render now, as the other captures do
                Dispatcher.UIThread.RunJobs();
                using WriteableBitmap frame = window.CaptureRenderedFrame() as WriteableBitmap ?? throw new InvalidOperationException("no frame");
                return CountPixels(frame);
            }
            finally
            {
                window.Close();
            }
        }

        private static unsafe (int Red, int Ink) CountPixels(WriteableBitmap bitmap)
        {
            using var buffer = bitmap.Lock();
            bool rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            int red = 0;
            int ink = 0;
            for (int y = 0; y < buffer.Size.Height; y++)
            {
                byte* line = (byte*)buffer.Address + y * buffer.RowBytes;
                for (int x = 0; x < buffer.Size.Width; x++)
                {
                    byte first = line[x * 4], g = line[x * 4 + 1], third = line[x * 4 + 2];
                    byte r = rgba ? first : third;
                    byte b = rgba ? third : first;
                    if (r + g + b > 150)
                    {
                        ink++;
                        if (r > g + 60 && r > b + 40)
                        {
                            red++;
                        }
                    }
                }
            }

            return (red, ink);
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
