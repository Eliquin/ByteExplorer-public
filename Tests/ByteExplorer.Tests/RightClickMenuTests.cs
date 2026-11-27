// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Shell;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views.Menus;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The right-click menu choice (Settings › Folder options › Right-click menu): ByteExplorer, Windows or Hybrid. The
    /// Windows shell menu itself needs Windows; here the choice, where it applies, "Show more options" and the verbs the
    /// app runs itself are checked (with the shell marked available, nothing calls it).
    /// </summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class RightClickMenuTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-rclick-" + Guid.NewGuid().ToString("N"));

        public RightClickMenuTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Folder"));
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(_root, "Other"));
            File.WriteAllText(Path.Combine(_root, "Other", "b.txt"), "b");
        }

        public void Dispose()
        {
            ShellContextMenu.SupportedForTests = null;
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Setting_DefaultsToHybrid_AndUnknownValuesAreMended()
        {
            Assert.Equal(FolderOptionsSettings.ContextMenuHybrid, new FolderOptionsSettings().ContextMenu);
            var options = new FolderOptionsSettings { ContextMenu = "Fancy" };
            options.Sanitize();
            Assert.Equal(FolderOptionsSettings.ContextMenuHybrid, options.ContextMenu);
            options.ContextMenu = FolderOptionsSettings.ContextMenuWindows;
            options.Sanitize();
            Assert.Equal(FolderOptionsSettings.ContextMenuWindows, options.ContextMenu);
        }

        [Fact]
        public void TheShellMenu_IsOfferedOnlyForRealFilesOfOneFolder()
        {
            ShellContextMenu.SupportedForTests = true;
            string a = Path.Combine(_root, "a.txt");
            string folder = Path.Combine(_root, "Folder");
            Assert.True(ShellContextMenu.CanShowFor(new[] { a, folder }));
            Assert.False(ShellContextMenu.CanShowFor(new[] { a, Path.Combine(_root, "Other", "b.txt") })); // two folders
            Assert.False(ShellContextMenu.CanShowFor(new[] { "relative.txt" }));
            Assert.False(ShellContextMenu.CanShowFor(new[] { a + ".bxlock" })); // a password-protected item
            Assert.True(ShellContextMenu.CanShowForBackground(_root));
            Assert.False(ShellContextMenu.CanShowForBackground(null));

            ShellContextMenu.SupportedForTests = false;
            Assert.False(ShellContextMenu.CanShowFor(new[] { a })); // Linux, macOS: the app's menu
        }

        [AvaloniaFact]
        public async Task Hybrid_AddsShowMoreOptions_TheAppsMenuDoesNot_AndVerbsRunInTheApp()
        {
            var window = new ModernMainWindow { Width = 1100, Height = 760 };
            window.Show();
            MainViewModel main = window.ViewModel;
            await Pump(30);
            FolderOptionsSettings options = FolderOptions.Current; // what the menus read
            string saved = options.ContextMenu;
            try
            {
                FileListViewModel pane = main.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);
                string a = Path.Combine(_root, "a.txt");
                string folder = Path.Combine(_root, "Folder");

                // Where the Windows menu is unavailable (this system), no "Show more options" whatever the setting.
                options.ContextMenu = FolderOptionsSettings.ContextMenuHybrid;
                Assert.DoesNotContain(Headers(FileContextMenu.ForItems(window, pane, new[] { a }, true)), h => h == "Show more options");

                // Hybrid with the shell available: the item ends the file and background menus; ByteExplorer: none.
                ShellContextMenu.SupportedForTests = true;
                List<string> hybrid = Headers(FileContextMenu.ForItems(window, pane, new[] { a }, true));
                Assert.Equal("Show more options", hybrid.Last());
                Assert.Equal("Show more options", Headers(FileContextMenu.ForBackground(pane, window)).Last());
                Assert.DoesNotContain(Headers(FileContextMenu.ForItems(window, pane, new[] { a, Path.Combine(_root, "Other", "b.txt") }, false)),
                    h => h == "Show more options"); // items of two folders: the shell can't make one menu
                options.ContextMenu = FolderOptionsSettings.ContextMenuByteExplorer;
                Assert.DoesNotContain(Headers(FileContextMenu.ForItems(window, pane, new[] { a }, true)), h => h == "Show more options");
                Assert.DoesNotContain(Headers(FileContextMenu.ForBackground(pane, window)), h => h == "Show more options");
                Assert.Equal("Properties", Headers(FileContextMenu.ForItems(window, pane, new[] { a }, true)).Last());

                // Verbs chosen in the Windows menu that the app runs itself; the rest go to the shell.
                Assert.True(FileContextMenu.RunItemVerb(pane, new[] { folder }, false, "open"));
                for (int i = 0; i < 100 && pane.CurrentPath != folder; i++)
                {
                    await Pump(1);
                }

                Assert.Equal(folder, pane.CurrentPath);
                var tabs = new List<string>();
                var windows = new List<string>();
                Func<IReadOnlyList<string>, bool, Task> previous = pane.OpenInNewTabs;
                Func<IReadOnlyList<string>, Task> previousWindows = pane.OpenInNewWindows;
                pane.OpenInNewTabs = (paths, show) =>
                {
                    tabs.AddRange(paths);
                    return Task.CompletedTask;
                };
                pane.OpenInNewWindows = paths =>
                {
                    windows.AddRange(paths);
                    return Task.CompletedTask;
                };
                try
                {
                    Assert.True(FileContextMenu.RunItemVerb(pane, new[] { _root }, false, "opennewwindow")); // a window of this process
                    Assert.Equal(new[] { _root }, windows);
                    Assert.Empty(tabs);
                    Assert.True(FileContextMenu.RunItemVerb(pane, new[] { _root }, false, "opennewtab"));
                    Assert.Equal(new[] { _root }, tabs);
                    Assert.False(FileContextMenu.RunItemVerb(pane, new[] { a }, false, "opennewtab")); // a file: the shell's
                    Assert.False(FileContextMenu.RunItemVerb(pane, new[] { a }, false, "opennewwindow"));
                }
                finally
                {
                    pane.OpenInNewTabs = previous;
                    pane.OpenInNewWindows = previousWindows;
                }

                foreach (string shellVerb in new[] { "properties", "sendto", "openas", "runas", "Windows.Share", "copyaspath" })
                {
                    Assert.False(FileContextMenu.RunItemVerb(pane, new[] { a }, false, shellVerb));
                }

                Assert.False(FileContextMenu.RunItemVerb(pane, new[] { a }, false, null)); // an entry without a verb (an extension's)
                Assert.False(FileContextMenu.RunItemVerb(pane, new[] { a }, false, "paste")); // paste into a file: not a folder
                Assert.True(FileContextMenu.RunBackgroundVerb(pane, folder, "refresh"));
                Assert.True(FileContextMenu.RunBackgroundVerb(pane, folder, "Undo"));
                Assert.False(FileContextMenu.RunBackgroundVerb(pane, folder, "properties"));
                Assert.False(FileContextMenu.RunBackgroundVerb(pane, folder, "cmd"));

                // The choice is on the Folder options page, greyed where the Windows menus can't be had.
                ShellContextMenu.SupportedForTests = null;
                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                ChoiceSetting choice = vm.Pages.Single(p => p.Key == "folderoptions").Groups.SelectMany(g => g.Items).OfType<ChoiceSetting>()
                    .Single(c => c.Title == "Right-click menu");
                Assert.Equal(new[] { "Hybrid", "ByteExplorer", "Windows" }, choice.Options.Select(o => o.Value));
                Assert.Equal(OperatingSystem.IsWindows(), choice.IsAvailable);
            }
            finally
            {
                ShellContextMenu.SupportedForTests = null;
                options.ContextMenu = saved;
                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
            }
        }

        private static List<string> Headers(ContextMenu menu) =>
            (menu.ItemsSource as IEnumerable<Control>)?.OfType<MenuItem>().Select(m => m.Header as string).ToList() ?? new List<string>();

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
