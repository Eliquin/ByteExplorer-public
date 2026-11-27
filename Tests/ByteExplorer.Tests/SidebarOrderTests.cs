// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views.Sidebar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The left panel's section order: the order rules, and each layout reordered from the settings window.</summary>
    public sealed class SidebarOrderTests
    {
        private static readonly string[] LayoutOrder = { "Places", "QuickAccess", "Drives", "Tags", "Network" };

        [Fact]
        public void Effective_KeepsTheSavedOrder_SkipsUnknownKeys_AndSlotsInMissingSections()
        {
            Assert.Equal(LayoutOrder, SidebarOrder.Effective(LayoutOrder, null));
            Assert.Equal(LayoutOrder, SidebarOrder.Effective(LayoutOrder, new List<string>()));
            Assert.Equal(new[] { "Tags", "Places", "QuickAccess", "Drives", "Network" },
                SidebarOrder.Effective(LayoutOrder, new[] { "Tags", "Places", "QuickAccess", "Drives", "Network" }));

            // "Recent" is another layout's section: skipped. Network missing: after Tags, where the layout has it.
            Assert.Equal(new[] { "Drives", "Tags", "Network", "Places", "QuickAccess" },
                SidebarOrder.Effective(LayoutOrder, new[] { "Drives", "Recent", "Tags", "Places", "QuickAccess", "Tags" }));

            // The first section missing goes first.
            Assert.Equal(new[] { "Places", "Network", "Tags", "Drives", "QuickAccess" },
                SidebarOrder.Effective(LayoutOrder, new[] { "Network", "Tags", "Drives", "QuickAccess" }));
        }

        [AvaloniaTheory]
        [InlineData("Modern")]
        [InlineData("Nemo")]
        [InlineData("Finder")]
        public async Task SectionOrder_FromTheSettingsWindow_ReordersTheLeftPanel(string layout)
        {
            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1300;
            window.Height = 860;
            window.Show();
            MainViewModel main = window.ViewModel;
            List<string> saved = main.SettingsService.Current.Navigation.SidebarSectionOrder.ToList();
            try
            {
                await Pump(30);
                main.SettingsService.Current.Navigation.SidebarSectionOrder = new List<string>();
                window.ApplyCustomization();
                IReadOnlyList<string> defaults = window.SidebarLayoutOrder;
                Assert.Equal(layout == "Modern"
                        ? new[] { "Places", "QuickAccess", "Lens", "Recent", "Drives", "Tags", "Network" }
                        : layout == "Nemo" ? new[] { "Places", "QuickAccess", "Drives", "Tags", "Network" }
                        : new[] { "Places", "QuickAccess", "Drives", "Network", "Tags" },
                    defaults);
                Assert.Equal(defaults, ShownOrder(window));

                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                OrderSetting order = vm.Pages.Single(p => p.Key == "leftpanel").Items.OfType<OrderSetting>().Single();
                Assert.Equal(defaults, order.Keys);
                Assert.True(order.IsDefault);

                // Tags to the top, one step at a time with the up button.
                while (order.Keys[0] != "Tags")
                {
                    order.MoveUpCommand.Execute(order.Entries.Single(e => e.Key == "Tags"));
                }

                await Pump(3);
                Save(window, $"sidebar-order-{layout.ToLowerInvariant()}.png");
                vm.SelectedPage = vm.Pages.Single(p => p.Key == "leftpanel");
                await Pump(5);
                Save(window.SettingsWindowForTests, "settings-left-panel.png");
                Assert.Equal("Tags", ShownOrder(window)[0]);
                Assert.Equal(order.Keys, main.SettingsService.Current.Navigation.SidebarSectionOrder);
                Assert.False(order.IsDefault);
                Assert.False(Divider(window, "Tags")?.IsVisible ?? false); // the first section has no line above it
                if (Divider(window, "Places") is Control placesLine)
                {
                    Assert.True(placesLine.IsVisible);
                }

                if (layout == "Modern")
                {
                    // One scroller for the whole panel: the tree (Drives) is a section like the others and moves too.
                    while (order.Keys[0] != "Drives")
                    {
                        order.MoveUpCommand.Execute(order.Entries.Single(e => e.Key == "Drives"));
                    }

                    await Pump(3);
                    StackPanel sections = window.FindControl<StackPanel>("SidebarSections");
                    Assert.Equal("Drives", sections.Children.Select(SidebarOrder.GetKey).First(k => !string.IsNullOrEmpty(k)));
                    Assert.Equal("Drives", ShownOrder(window)[0]);
                    Assert.False(Divider(window, "Drives")?.IsVisible ?? false);
                    Assert.NotNull(window.FindControl<ScrollViewer>("SidebarScroller"));
                    Assert.Same(window.FindControl<ScrollViewer>("SidebarScroller"),
                        window.FindControl<Views.NavigationPaneView>("NavigationPane").FindAncestorOfType<ScrollViewer>());
                }

                // Down at the bottom and up at the top do nothing; the default order comes back.
                order.MoveDownCommand.Execute(order.Entries.Last());
                order.MoveUpCommand.Execute(order.Entries.First());
                order.ResetCommand.Execute(null);
                await Pump(3);
                Assert.True(order.IsDefault);
                Assert.Empty(main.SettingsService.Current.Navigation.SidebarSectionOrder);
                Assert.Equal(defaults, ShownOrder(window));
                Assert.Equal(defaults, order.Keys);
            }
            finally
            {
                main.SettingsService.Current.Navigation.SidebarSectionOrder = saved;
                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
            }
        }

        /// <summary>The sections in the order the left panel shows them, top to bottom.</summary>
        private static List<string> ShownOrder(Window window) =>
            window.GetVisualDescendants().OfType<Control>()
                .Where(c => !string.IsNullOrEmpty(SidebarOrder.GetKey(c)))
                .Select(SidebarOrder.GetKey)
                .ToList();

        private static Control Divider(Window window, string key) =>
            window.GetVisualDescendants().OfType<Panel>().FirstOrDefault(c => SidebarOrder.GetKey(c) == key)?
                .Children.FirstOrDefault(SidebarOrder.GetIsDivider);

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(directory, name));
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
