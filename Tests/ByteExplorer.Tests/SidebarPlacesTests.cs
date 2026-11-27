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
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>Network locations and Ctrl+D pinning: saving, naming, validating, and the sidebar rows.</summary>
    public sealed class SidebarPlacesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-places-" + Guid.NewGuid().ToString("N"));

        public SidebarPlacesTests() => Directory.CreateDirectory(_root);

        public void Dispose() => Directory.Delete(_root, true);

        // In the headless session: view models post command requeries to the UI dispatcher (see AsyncSafetyTests).
        [AvaloniaFact]
        public async Task NetworkLocations_AreSavedAndReloaded_WithoutDuplicates()
        {
            string settingsFile = Path.Combine(_root, "settings.json");
            var settings = new SettingsService(settingsFile);
            await settings.LoadAsync();
            var locations = new SavedLocationList(settings, n => n.NetworkLocations, (n, l) => n.NetworkLocations = l);

            await locations.AddAsync("smb://nas/photos");
            await locations.AddAsync("SMB://NAS/photos");
            Assert.Single(locations.Items);
            Assert.Equal("photos on nas", locations.Items[0].Name);

            var reloaded = new SettingsService(settingsFile);
            await reloaded.LoadAsync();
            Assert.Equal(new[] { "smb://nas/photos" }, reloaded.Current.Navigation.NetworkLocations);

            await locations.RemoveAsync("smb://nas/photos");
            Assert.False(locations.HasItems);
        }

        [Theory]
        [InlineData(@"\\nas\photos", "photos on nas")]
        [InlineData("smb://nas/photos", "photos on nas")]
        [InlineData("sftp://me@server.lan/home/me/docs", "docs on server.lan")]
        [InlineData("smb://nas", "nas (SMB)")]
        [InlineData("/mnt/backup", "backup")]
        public void NetworkLocation_Names(string address, string expected)
        {
            Assert.Equal(expected, NetworkLocations.DisplayName(address));
        }

        [Fact]
        public void NetworkLocation_Validation_FollowsTheOs()
        {
            Assert.NotNull(NetworkLocations.Validate(""));
            Assert.NotNull(NetworkLocations.Validate("nas/photos"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(NetworkLocations.Validate(@"\\nas\photos"));
                Assert.NotNull(NetworkLocations.Validate("smb://nas/photos"));
            }
            else
            {
                Assert.Null(NetworkLocations.Validate("smb://nas/photos"));
                Assert.NotNull(NetworkLocations.Validate(@"\\nas\photos"));
                Assert.NotNull(NetworkLocations.Validate("smb:///photos")); // no server
            }
        }

        [Fact]
        public void GioInfo_LocalPathIsParsed()
        {
            const string info = "display name: photos on nas\nlocal path: /run/user/1000/gvfs/smb-share:server=nas,share=photos\nuri: smb://nas/photos/\n";
            Assert.Equal("/run/user/1000/gvfs/smb-share:server=nas,share=photos", NetworkLocations.ParseLocalPath(info));
            Assert.Null(NetworkLocations.ParseLocalPath("uri: smb://nas/"));
        }

        [AvaloniaFact]
        public async Task CtrlD_PinsTheFolder_AndTheRowOpensIt()
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, "Reports")).FullName;
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            try
            {
                await Pump(30);
                MainViewModel main = window.ViewModel;
                foreach (var existing in main.Pins.Items.ToList())
                {
                    await main.Pins.UnpinAsync(existing); // a clean list, whatever earlier runs saved
                }

                await main.FileListViewModel.LoadFolderAsync(folder);
                await Pump(5);
                window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
                window.KeyRelease(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
                for (int i = 0; i < 100 && !main.Pins.Contains(folder); i++)
                {
                    await Pump(1);
                }

                Assert.True(main.Pins.Contains(folder));
                Assert.True(main.Pins.Items.Single().IsActive); // the folder shown is highlighted
                await main.NetworkLocations.AddAsync(OperatingSystem.IsWindows() ? @"\\nas\photos" : "smb://nas/photos");
                await Pump(5);

                Border row = window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("pinRow") && (b.Tag as ViewModels.Sidebar.PinnedItemViewModel)?.Path == folder);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "photos on nas");

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "sidebar-places.png"));
                }

                await main.FileListViewModel.LoadHomeAsync();
                await Pump(5);
                // The left panel is one scroller: opening the folder from the file list scrolled it to the folder in the
                // tree (so the pin may be above the view). Scrolled back up, the pin is clicked where it shows.
                window.FindControl<ScrollViewer>("SidebarScroller").Offset = default;
                await Pump(3);
                Point center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window) ?? default;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                for (int i = 0; i < 100 && main.FileListViewModel.CurrentPath != folder; i++)
                {
                    await Pump(1);
                }

                Assert.Equal(folder, main.FileListViewModel.CurrentPath);

                await main.Pins.UnpinAsync(folder);
                await main.NetworkLocations.RemoveAsync(OperatingSystem.IsWindows() ? @"\\nas\photos" : "smb://nas/photos");
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
