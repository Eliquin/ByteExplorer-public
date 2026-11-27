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
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views.Sidebar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The left panel designs (Settings › Left panel › Design): each one applies, and only the look changes.</summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class SidebarDesignTests
    {
        [AvaloniaFact]
        public async Task EveryDesign_Applies_CompactOnesSaveSpace_AndChipsOpenTheirSection()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 900 };
            window.Show();
            MainViewModel main = window.ViewModel;
            NavigationSettings navigation = main.SettingsService.Current.Navigation;
            string savedDesign = navigation.SidebarDesign;
            List<string> savedCollapsed = navigation.CollapsedSidebarSections.ToList();
            try
            {
                await Pump(30);
                App.ApplyTheme("Light"); // the screenshots in the light theme (after the window applied its own)
                await Pump(5);
                Border panel = window.FindControl<Border>("LeftPanel");
                List<SidebarSection> sections = window.GetVisualDescendants().OfType<SidebarSection>().ToList();
                Assert.Equal(new[] { "QuickAccess", "Lens", "Recent", "Tags", "Network" }.OrderBy(k => k), sections.Select(s => s.SectionKey).OrderBy(k => k));
                Assert.All(sections, s => Assert.NotEqual(string.Empty, s.HeaderCaps));
                Assert.Equal("LENS", sections.Single(s => s.SectionKey == "Lens").HeaderCaps);

                // The setting is on the Left panel page.
                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                ChoiceSetting choice = vm.Pages.Single(p => p.Key == "leftpanel").Groups.First().Items.OfType<ChoiceSetting>().Single();
                Assert.Equal(NavigationSettings.SidebarDesigns, choice.Options.Select(o => o.Value));
                window.SettingsWindowForTests.Close();

                var collapsedHeights = new Dictionary<string, double>();
                foreach (string design in NavigationSettings.SidebarDesigns)
                {
                    SetDesign(choice, design);
                    await Pump(5);
                    Assert.Equal(design != NavigationSettings.SidebarDesignClassic, panel.Classes.Contains("sbModern"));

                    // Open: Quick access and Lens shown, Recent and Network collapsed (screenshot), then everything collapsed.
                    foreach (SidebarSection s in sections)
                    {
                        s.IsExpanded = s.SectionKey is "QuickAccess" or "Lens" or "Tags";
                    }

                    await Pump(8);
                    Save(window, panel, $"sidebar-design-{design.ToLowerInvariant()}-open.png");
                    foreach (SidebarSection s in sections)
                    {
                        s.IsExpanded = false;
                    }

                    await Pump(8);
                    Save(window, panel, $"sidebar-design-{design.ToLowerInvariant()}-collapsed.png");
                    collapsedHeights[design] = sections.Where(s => s.IsEffectivelyVisible).Sum(s => s.Bounds.Height + s.Margin.Top + s.Margin.Bottom);

                    if (design == NavigationSettings.SidebarDesignChips)
                    {
                        // Collapsed sections leave the list and show as chips above the tree; a chip opens its section.
                        SidebarChipDock dock = window.FindControl<SidebarChipDock>("SidebarChips");
                        Assert.True(dock.IsVisible);
                        Assert.All(sections, s => Assert.False(s.IsVisible));
                        List<Button> chips = dock.Children.OfType<Button>().ToList();
                        Assert.Contains(chips, c => (string)c.Tag == "QuickAccess");
                        Assert.Contains(chips, c => (string)c.Tag == "Lens");
                        chips.Single(c => (string)c.Tag == "Lens").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        await Pump(5);
                        SidebarSection lens = sections.Single(s => s.SectionKey == "Lens");
                        Assert.True(lens.IsExpanded);
                        Assert.True(lens.IsVisible);
                        Assert.DoesNotContain(dock.Children.OfType<Button>(), c => (string)c.Tag == "Lens");
                        Assert.DoesNotContain("Lens", main.SettingsService.Current.Navigation.CollapsedSidebarSections); // remembered as open, as in every design
                    }
                    else
                    {
                        Assert.False(window.FindControl<SidebarChipDock>("SidebarChips").IsVisible);
                    }
                }

                // The compact designs take less room than Classic when collapsed.
                Assert.True(collapsedHeights[NavigationSettings.SidebarDesignQuiet] < collapsedHeights[NavigationSettings.SidebarDesignClassic],
                    string.Join(", ", collapsedHeights.Select(kv => $"{kv.Key} {kv.Value}")));
                Assert.True(collapsedHeights[NavigationSettings.SidebarDesignGuides] < collapsedHeights[NavigationSettings.SidebarDesignClassic]);

                // Saved, and an unknown value reads as Classic.
                Assert.Equal(NavigationSettings.SidebarDesignChips, main.SettingsService.Current.Navigation.SidebarDesign);
                var settings = new NavigationSettings { SidebarDesign = "Bubbles" };
                settings.Sanitize();
                Assert.Equal(NavigationSettings.SidebarDesignClassic, settings.SidebarDesign);
                settings.SidebarDesign = "cards";
                settings.Sanitize();
                Assert.Equal(NavigationSettings.SidebarDesignCards, settings.SidebarDesign);
            }
            finally
            {
                main.SettingsService.Current.Navigation.SidebarDesign = savedDesign;
                foreach (SidebarSection s in window.GetVisualDescendants().OfType<SidebarSection>())
                {
                    s.IsExpanded = !savedCollapsed.Contains(s.SectionKey);
                }

                main.SettingsService.Current.Navigation.CollapsedSidebarSections = savedCollapsed;
                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
                App.ApplyTheme(main.SettingsService.Current.Appearance?.Theme);
            }
        }

        private static void SetDesign(ChoiceSetting choice, string design) =>
            choice.Selected = choice.Options.Single(o => o.Value == design);

        private static void Save(Window window, Control part, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            if (frame == null)
            {
                return;
            }

            Point topLeft = part.TranslatePoint(default, window) ?? default;
            double scale = frame.PixelSize.Width / window.Bounds.Width;
            var crop = new PixelRect((int)(topLeft.X * scale), (int)(topLeft.Y * scale), (int)(part.Bounds.Width * scale), (int)(part.Bounds.Height * scale));
            using var cropped = new Avalonia.Media.Imaging.CroppedBitmap(frame, crop);
            var target = new Avalonia.Media.Imaging.RenderTargetBitmap(crop.Size);
            using (var context = target.CreateDrawingContext())
            {
                context.DrawImage(cropped, new Rect(0, 0, crop.Width, crop.Height));
            }

            target.Save(Path.Combine(directory, name));
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
