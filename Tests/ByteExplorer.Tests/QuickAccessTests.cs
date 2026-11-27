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
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Sidebar;
using ByteExplorer.Views.Sidebar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Quick access: first-run defaults and the move of older bookmarks, pinning, reordering, renaming, colors,
    /// availability, the active highlight, saving, and the sidebar control (keyboard, unpin button, collapsible section).
    /// View models post command requeries to the UI dispatcher, so these run in the headless session.
    /// </summary>
    public sealed class QuickAccessTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-pins-" + Guid.NewGuid().ToString("N"));

        public QuickAccessTests()
        {
            foreach (string name in new[] { "Alpha", "Beta", "Gamma" })
            {
                Directory.CreateDirectory(Path.Combine(_root, name));
            }
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

        private string Folder(string name) => Path.Combine(_root, name);

        private async Task<(SettingsService Settings, PinnedFoldersViewModel Pins)> NewPinsAsync(Action<NavigationSettings> seed = null)
        {
            var settings = new SettingsService(Path.Combine(_root, "settings.json"));
            await settings.LoadAsync();
            seed?.Invoke(settings.Current.Navigation);
            var pins = new PinnedFoldersViewModel(settings);
            pins.Reload();
            await Pump(2);
            return (settings, pins);
        }

        [AvaloniaFact]
        public async Task FirstRun_PinsAllTheSpecialFolders_TheTrash_AndTheOldBookmarks()
        {
            (SettingsService settings, PinnedFoldersViewModel pins) = await NewPinsAsync(n =>
            {
                n.Pinned = null;
                n.Bookmarks = new List<string> { Folder("Alpha") };
            });

            var expected = new[] { SpecialFolders.Desktop, SpecialFolders.Downloads, SpecialFolders.Documents, SpecialFolders.Pictures, SpecialFolders.Music, SpecialFolders.Videos }
                .Concat(new[] { PinColors.TrashKey, Folder("Alpha") }).ToArray();
            Assert.Equal(expected, pins.Items.Select(p => p.Path));
            Assert.Empty(settings.Current.Navigation.Bookmarks); // moved, not duplicated
            Assert.True(pins.Items.Single(p => p.IsTrash).IsAvailable);
            foreach (PinnedItemViewModel pin in pins.Items.Where(p => !p.IsTrash && p.Path != Folder("Alpha")))
            {
                Assert.Equal(Directory.Exists(pin.Path), pin.IsAvailable); // a missing one is kept, dimmed
            }

            // Saved in the portable form (the special folders as tokens, so the settings work on another computer): a fresh
            // load gets the same list, without setting it up again.
            var reloaded = new SettingsService(Path.Combine(_root, "settings.json"));
            await reloaded.LoadAsync();
            Assert.Equal(new[] { "::desktop", "::downloads", "::documents", "::pictures", "::music", "::videos", PinColors.TrashKey, Folder("Alpha") },
                reloaded.Current.Navigation.Pinned.Select(p => p.Path));
            Assert.Equal(expected, reloaded.Current.Navigation.Pinned.Select(p => PortablePaths.Resolve(p.Path)));
        }

        [AvaloniaFact]
        public async Task Pin_SkipsDuplicatesAndNonFolders_UnpinAndToggle()
        {
            (_, PinnedFoldersViewModel pins) = await NewPinsAsync(n => n.Pinned = new List<PinnedFolderSettings>());
            string file = Path.Combine(_root, "note.txt");
            File.WriteAllText(file, "x");

            Assert.Equal(2, await pins.PinAsync(new[] { Folder("Alpha"), Folder("Beta"), Folder("Alpha"), file, Path.Combine(_root, "missing") }));
            Assert.Equal(new[] { "Alpha", "Beta" }, pins.Items.Select(p => p.Name));

            await pins.PinAsync(Folder("Gamma"), index: 0);
            Assert.Equal("Gamma", pins.Items[0].Name);

            await pins.ToggleAsync(Folder("Beta"));
            Assert.False(pins.Contains(Folder("Beta")));
            await pins.ToggleAsync(Folder("Beta"));
            Assert.True(pins.Contains(Folder("Beta") + Path.DirectorySeparatorChar)); // same folder, trailing separator or not
        }

        [AvaloniaFact]
        public async Task Reorder_BySlotAndByStep()
        {
            (_, PinnedFoldersViewModel pins) = await NewPinsAsync(n => n.Pinned = new List<PinnedFolderSettings>());
            await pins.PinAsync(new[] { Folder("Alpha"), Folder("Beta"), Folder("Gamma") });
            PinnedItemViewModel alpha = pins.Items[0];

            await pins.MoveToSlotAsync(alpha, 3); // below the last row
            Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, pins.Items.Select(p => p.Name));
            await pins.MoveToSlotAsync(alpha, 0); // above the first
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, pins.Items.Select(p => p.Name));
            await pins.MoveToSlotAsync(alpha, 1); // its own place: nothing moves
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, pins.Items.Select(p => p.Name));

            await pins.MoveByAsync(pins.Items[2], -1);
            Assert.Equal(new[] { "Alpha", "Gamma", "Beta" }, pins.Items.Select(p => p.Name));
            await pins.MoveByAsync(pins.Items[0], -1); // already first
            Assert.Equal("Alpha", pins.Items[0].Name);
        }

        [AvaloniaFact]
        public async Task Rename_AndColor_AreSaved_AndReset()
        {
            (SettingsService settings, PinnedFoldersViewModel pins) = await NewPinsAsync(n => n.Pinned = new List<PinnedFolderSettings>());
            await pins.PinAsync(Folder("Alpha"));
            PinnedItemViewModel pin = pins.Items[0];

            await pins.RenameAsync(pin, "  Work  ");
            await pins.SetColorAsync(pin, "teal");
            Assert.Equal("Work", pin.Name);
            Assert.Equal("Teal", pin.ColorName);
            Assert.Equal(Avalonia.Media.Color.Parse("#12A594"), ((Avalonia.Media.ISolidColorBrush)pin.IconBrush).Color);
            Assert.Equal("Work", settings.Current.Navigation.Pinned[0].Name);
            Assert.Equal("Teal", settings.Current.Navigation.Pinned[0].Color);

            await pins.RenameAsync(pin, "Alpha"); // the folder's own name: no alias
            await pins.SetColorAsync(pin, "not a color");
            Assert.Null(pin.Alias);
            Assert.Null(pin.ColorName);
        }

        [AvaloniaFact]
        public async Task Availability_AndActiveHighlight()
        {
            (_, PinnedFoldersViewModel pins) = await NewPinsAsync(n => n.Pinned = new List<PinnedFolderSettings>());
            await pins.PinAsync(new[] { Folder("Alpha"), Folder("Beta") });

            pins.UpdateActive(Folder("Beta"));
            Assert.False(pins.Items[0].IsActive);
            Assert.True(pins.Items[1].IsActive);

            Directory.Delete(Folder("Alpha"));
            pins.RefreshAvailability();
            Assert.False(pins.Items[0].IsAvailable); // dimmed, kept
            Assert.Contains("Not available", pins.Items[0].ToolTip);
        }

        [Fact]
        public void Settings_CleanPins()
        {
            var navigation = new NavigationSettings
            {
                Pinned = new List<PinnedFolderSettings>
                {
                    new PinnedFolderSettings { Path = "/a", Name = "  ", Color = "PURPLE" },
                    new PinnedFolderSettings { Path = "/a" },
                    new PinnedFolderSettings { Path = "" },
                    new PinnedFolderSettings { Path = "/b", Color = "chartreuse" }
                }
            };
            navigation.Sanitize();
            Assert.Equal(new[] { "/a", "/b" }, navigation.Pinned.Select(p => p.Path));
            Assert.Null(navigation.Pinned[0].Name);
            Assert.Equal("Purple", navigation.Pinned[0].Color);
            Assert.Null(navigation.Pinned[1].Color);

            var fresh = new NavigationSettings { Pinned = null };
            fresh.Sanitize();
            Assert.Null(fresh.Pinned); // still "not set up": the app creates the defaults
        }

        [AvaloniaFact]
        public async Task Sidebar_Keyboard_UnpinButton_AndCollapsibleSection()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            try
            {
                await Pump(30);
                PinnedFoldersViewModel pins = window.ViewModel.Pins;
                foreach (PinnedItemViewModel existing in pins.Items.ToList())
                {
                    await pins.UnpinAsync(existing);
                }

                await pins.PinAsync(new[] { Folder("Alpha"), Folder("Beta"), Folder("Gamma") });
                await pins.SetColorAsync(pins.Items[1], "Orange");
                await pins.RenameAsync(pins.Items[2], "Gamma project");
                await window.ViewModel.FileListViewModel.LoadFolderAsync(Folder("Beta"));
                await Pump(8);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "quick-access.png"));
                }

                // Keyboard: Alt+Down moves the focused pin, Delete unpins it, Enter opens.
                Border Row(string name) => window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("pinRow") && (b.Tag as PinnedItemViewModel)?.Name == name);
                Row("Alpha").Focus(NavigationMethod.Tab);
                await Pump(2);
                window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
                window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
                await Pump(3);
                Assert.Equal(new[] { "Beta", "Alpha", "Gamma project" }, pins.Items.Select(p => p.Name));

                Row("Gamma project").Focus(NavigationMethod.Tab);
                await Pump(2);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await WaitFor(() => window.ViewModel.FileListViewModel.CurrentPath == Folder("Gamma"));
                Assert.True(pins.Items.Single(p => p.Name == "Gamma project").IsActive);

                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                await Pump(3);
                Assert.Equal(new[] { "Beta", "Alpha" }, pins.Items.Select(p => p.Name));

                // The hover button unpins too.
                Button unpin = Row("Alpha").GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("unpin"));
                unpin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Pump(3);
                Assert.Equal(new[] { "Beta" }, pins.Items.Select(p => p.Name));

                // The section folds, and remembers it.
                SidebarSection section = window.GetVisualDescendants().OfType<SidebarSection>().First(s => s.SectionKey == "QuickAccess");
                section.IsExpanded = false;
                await Pump(2);
                Assert.True(window.ViewModel.IsSidebarSectionCollapsed("QuickAccess"));
                section.IsExpanded = true;
                await Pump(2);
                Assert.False(window.ViewModel.IsSidebarSectionCollapsed("QuickAccess"));

                await pins.UnpinAsync(pins.Items[0]);
            }
            finally
            {
                window.Close();
            }
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 200 && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition(), "timed out waiting for the condition");
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
