// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Tabs;
using ByteExplorer.Views.Menus;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Several main windows in one process: Ctrl+N, the app menu and "Open in new window" open one showing a folder; the
    /// windows share the app's services (settings, search index, tags, operations) and nothing else; closing a window
    /// leaves the others working, and the last one's close stops the shared services and quits.
    /// </summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class MultiWindowTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-windows-" + Guid.NewGuid().ToString("N"));

        public MultiWindowTests()
        {
            foreach (string name in new[] { "Alpha", "Beta", "Gamma" })
            {
                Directory.CreateDirectory(Path.Combine(_root, name));
                File.WriteAllText(Path.Combine(_root, name, "file.txt"), "x");
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

        private string P(string name) => Path.Combine(_root, name);

        [AvaloniaFact]
        public async Task CtrlN_OpensAWindowOfTheSameLayout_AtTheFolder_SharingTheAppServicesOnly()
        {
            MainWindowBase first = App.CreateWindowForLayout("Modern");
            first.Show();
            MainWindowBase second = null;
            int quits = 0;
            EventHandler quit = (_, _) => quits++;
            MainWindowBase.LastWindowClosed += quit;
            try
            {
                await WaitFor(() => first.FirstPlaceShown.IsCompleted);
                MainViewModel a = first.ViewModel;
                await a.PrimaryFileListViewModel.LoadFolderAsync(P("Alpha"));
                await Pump(3);

                first.KeyPress(Key.N, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.N, null);
                await WaitFor(() => MainWindowBase.OpenWindows.Count(w => w != first) == 1);
                second = MainWindowBase.OpenWindows.Single(w => w != first);
                MainViewModel b = second.ViewModel;
                await WaitFor(() => second.FirstPlaceShown.IsCompleted && b.PrimaryFileListViewModel.CurrentPath == P("Alpha"));
                Assert.IsType(first.GetType(), second);
                Assert.Single(b.Tabs.Tabs); // the folder only, not the last session's tabs

                // One of each app-wide service ...
                Assert.True(a.Services.IsShared);
                Assert.Same(a.Services, b.Services);
                Assert.Same(a.SettingsService, b.SettingsService);
                Assert.Same(a.SearchService, b.SearchService);
                Assert.Same(a.Tags, b.Tags);
                Assert.Same(a.Tags, Core.Tags.TagStore.Default);
                Assert.Same(a.Operations, b.Operations);
                Assert.Equal(2, a.Services.Windows.Count);

                // ... and each window's own tabs, panes and navigation.
                Assert.NotSame(a.Tabs, b.Tabs);
                Assert.NotSame(a.PrimaryFileListViewModel, b.PrimaryFileListViewModel);
                Assert.NotSame(a.NavigationViewModel, b.NavigationViewModel);
                await b.PrimaryFileListViewModel.LoadFolderAsync(P("Beta"));
                Assert.Equal(P("Alpha"), a.PrimaryFileListViewModel.CurrentPath);

                // A later start's folder goes to the window used last.
                Assert.Same(second, MainWindowBase.MostRecentlyActive);

                // Closing the first window asks nothing (another stays open) and stops nothing shared.
                first.Close();
                await WaitFor(() => !MainWindowBase.OpenWindows.Contains(first));
                Assert.False(b.Services.IsDisposed);
                Assert.Single(b.Services.Windows);
                Assert.Equal(0, quits);
                await b.PrimaryFileListViewModel.LoadFolderAsync(P("Gamma"));
                await WaitFor(() => b.PrimaryFileListViewModel.Items?.Count == 1);
                Assert.Same(second, MainWindowBase.MostRecentlyActive);

                // The undo history is the app's: an operation is still recorded and undone from the remaining window.
                string made = await b.Operations.RunCreatingAsync("New folder", "New folder", (_, _) =>
                {
                    string path = Path.Combine(P("Gamma"), "Made");
                    Directory.CreateDirectory(path);
                    return Task.FromResult(path);
                });
                Assert.True(Directory.Exists(made));

                // The last window's close stops the shared services and quits the app.
                second.CloseWithoutAsking();
                await WaitFor(() => quits == 1);
                Assert.True(b.Services.IsDisposed);
                Assert.Empty(MainWindowBase.OpenWindows);
            }
            finally
            {
                MainWindowBase.LastWindowClosed -= quit;
                first.CloseWithoutAsking();
                second?.CloseWithoutAsking();
            }
        }

        [AvaloniaFact]
        public async Task OpenInNewWindow_FromTheMenus_AndSettingsApplyToEveryWindow()
        {
            MainWindowBase first = App.CreateWindowForLayout("Trove");
            first.Show();
            var opened = new List<MainWindowBase>();
            try
            {
                await WaitFor(() => first.FirstPlaceShown.IsCompleted);
                MainViewModel a = first.ViewModel;
                await a.PrimaryFileListViewModel.LoadFolderAsync(_root);
                await Pump(3);

                // The folder's right-click menu: "Open in new window" after "Open in new tab".
                ContextMenu menu = FileContextMenu.ForItems(first, a.PrimaryFileListViewModel, new[] { P("Beta") }, false);
                List<MenuItem> items = (menu.ItemsSource as IEnumerable<Control>).OfType<MenuItem>().ToList();
                List<string> headers = items.Select(i => i.Header as string).ToList();
                Assert.Equal(headers.IndexOf("Open in new tab") + 1, headers.IndexOf("Open in new window"));
                Assert.DoesNotContain("Open in new window", (FileContextMenu.ForItems(first, a.PrimaryFileListViewModel, new[] { P("Beta"), P("Gamma") }, false)
                    .ItemsSource as IEnumerable<Control>).OfType<MenuItem>().Select(i => i.Header as string));
                items[headers.IndexOf("Open in new window")].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitFor(() => MainWindowBase.OpenWindows.Any(w => w != first));
                MainWindowBase second = MainWindowBase.OpenWindows.Single(w => w != first);
                opened.Add(second);
                await WaitFor(() => second.FirstPlaceShown.IsCompleted && second.ViewModel.PrimaryFileListViewModel.CurrentPath == P("Beta"));
                Assert.IsType<NemoMainWindow>(second);
                MainViewModel b = second.ViewModel;

                // The app menu's row (and the omnibar's command) run the same: a window at the active pane's place.
                Assert.True(a.NewWindowCommand.CanExecute(null));
                a.NewWindowCommand.Execute(null);
                await WaitFor(() => MainWindowBase.OpenWindows.Count == 3);
                MainWindowBase third = MainWindowBase.OpenWindows.Single(w => w != first && w != second);
                opened.Add(third);
                await WaitFor(() => third.FirstPlaceShown.IsCompleted && third.ViewModel.PrimaryFileListViewModel.CurrentPath == _root);
                third.CloseWithoutAsking();
                await WaitFor(() => MainWindowBase.OpenWindows.Count == 2);

                // A view model without a window can't open one.
                using (var lone = new MainViewModel(new UiDispatcher(Dispatcher.UIThread)))
                {
                    Assert.False(lone.NewWindowCommand.CanExecute(null));
                    Assert.False(lone.Services.IsShared);
                    Assert.NotSame(a.SettingsService, lone.SettingsService);
                }

                Assert.False(a.Services.IsDisposed);

                // A customization, the tab bar and the folder options made in one window's settings apply to both.
                int customized = 0;
                EventHandler onCustomized = (_, _) => customized++;
                b.CustomizationChanged += onCustomized;
                a.NotifyCustomizationChanged();
                b.CustomizationChanged -= onCustomized;
                Assert.Equal(1, customized);

                string placement = a.SettingsService.Current.Tabs.Placement;
                try
                {
                    a.SettingsService.Current.Tabs.Placement = Core.Models.TabSettings.PlacementLeft;
                    a.NotifyTabSettingsChanged();
                    Assert.Equal(Core.Models.TabSettings.PlacementLeft, b.Tabs.Placement);
                }
                finally
                {
                    a.SettingsService.Current.Tabs.Placement = placement;
                    a.NotifyTabSettingsChanged();
                }

                var titles = new List<string>();
                b.PropertyChanged += (_, e) => titles.Add(e.PropertyName);
                a.ApplyFolderOptions();
                Assert.Contains(nameof(MainViewModel.WindowTitle), titles);
                Assert.Same(a.PrimaryFileListViewModel, a.AllPanesOfAllWindows.First());
                Assert.Contains(b.PrimaryFileListViewModel, a.AllPanesOfAllWindows);

                // Quick access pinned in one window shows in the other.
                bool wasPinned = a.Pins.Contains(P("Gamma"));
                await a.Pins.ToggleAsync(P("Gamma"));
                try
                {
                    b.SyncSidebarWithSettings();
                    Assert.Equal(!wasPinned, b.Pins.Contains(P("Gamma")));
                }
                finally
                {
                    await a.Pins.ToggleAsync(P("Gamma"));
                    b.SyncSidebarWithSettings();
                }

                Assert.Equal(wasPinned, b.Pins.Contains(P("Gamma")));
            }
            finally
            {
                foreach (MainWindowBase window in opened)
                {
                    window.CloseWithoutAsking();
                }

                first.CloseWithoutAsking();
            }
        }

        private static async Task WaitFor(Func<bool> condition, int rounds = 250)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition());
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
