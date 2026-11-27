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
using ByteExplorer.Controls;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Settings › Folder options (Windows Explorer's Folder Options): each option, through the real window where it shows.
    /// Every test puts the options back to their defaults (the settings file is shared by the whole test run).
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class FolderOptionsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-folderopts-" + Guid.NewGuid().ToString("N"));

        public FolderOptionsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
            Directory.CreateDirectory(Path.Combine(_root, "Beta"));
            File.WriteAllText(Path.Combine(_root, "report.pdf"), "pdf");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "txt");
            File.WriteAllText(Path.Combine(_root, "data.qqq"), "unknown type");
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

        // ------------------------------------------------------------------ pure rules

        [AvaloniaFact]
        public void KnownExtensions_AreHiddenOnlyWhenAsked_AndRenamesKeepThem()
        {
            var options = new FolderOptionsSettings();
            FolderOptions.Attach(() => options);
            try
            {
                Assert.Equal("report.pdf", FolderOptions.DisplayNameFor("report.pdf", false));
                options.HideKnownExtensions = true;
                FolderOptions.NotifyChanged();
                Assert.Equal("report", FolderOptions.DisplayNameFor("report.pdf", false));
                Assert.Equal("holiday", FolderOptions.DisplayNameFor("holiday.JPG", false));
                Assert.Equal("data.qqq", FolderOptions.DisplayNameFor("data.qqq", false)); // a type nobody knows keeps it
                Assert.Equal(".bashrc", FolderOptions.DisplayNameFor(".bashrc", false));
                Assert.Equal("Photos.2024", FolderOptions.DisplayNameFor("Photos.2024", true)); // folders: never
                Assert.Equal(".pdf", FolderOptions.HiddenExtensionOf("report.pdf", false));
                Assert.Equal(string.Empty, FolderOptions.HiddenExtensionOf("data.qqq", false));

                var item = new FileListItemViewModel(new DirectoryIndexEntry("report.pdf", false, 3, DateTime.UtcNow, FileAttributes.Normal), _root);
                Assert.Equal("report", item.DisplayName);
                options.HideKnownExtensions = false;
                FolderOptions.NotifyChanged();
                Assert.Equal("report.pdf", item.DisplayName); // the item follows the option without being rebuilt
                options.ShowSizeInTooltips = false;
                FolderOptions.NotifyChanged();
                Assert.DoesNotContain("Size:", item.ToolTipText);
            }
            finally
            {
                FolderOptions.Attach(() => null);
            }
        }

        [AvaloniaFact]
        public void DriveLetters_AndEmptyDrives_FollowTheOptions()
        {
            var drive = new DriveNodeInfo(@"C:\", "System", DriveType.Fixed, true, 100, 50);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("System (C:)", DriveLabels.Format(drive, showLetter: true, letterFirst: false));
                Assert.Equal("(C:) System", DriveLabels.Format(drive, showLetter: true, letterFirst: true));
                Assert.Equal("System", DriveLabels.Format(drive, showLetter: false, letterFirst: false));
            }

            var drives = new[]
            {
                drive,
                new DriveNodeInfo(@"E:\", null, DriveType.Removable, isReady: false, 0, 0),
                new DriveNodeInfo(@"F:\", null, DriveType.CDRom, isReady: false, 0, 0),
                new DriveNodeInfo(@"\\nas\share", null, DriveType.Network, isReady: false, 0, 0, probeTimedOut: true),
            };
            Assert.Equal(4, NavigationViewModel.FilterDrives(drives, hideEmpty: false).Count);
            IReadOnlyList<DriveNodeInfo> shown = NavigationViewModel.FilterDrives(drives, hideEmpty: true);
            Assert.Equal(new[] { @"C:\", @"\\nas\share" }, shown.Select(d => d.RootPath)); // unreachable isn't empty
        }

        [AvaloniaFact]
        public void BadValuesInTheSettingsFile_FallBackToTheDefaults()
        {
            var options = new FolderOptionsSettings { OpenAt = "Moon", TypingInList = "Shout", StartupFolder = null };
            options.Sanitize();
            Assert.Equal(FolderOptionsSettings.OpenAtHome, options.OpenAt);
            Assert.Equal(FolderOptionsSettings.TypeSelects, options.TypingInList);
            Assert.Equal(string.Empty, options.StartupFolder);
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task OneClickOpens_WhenSingleClickIsChosen()
        {
            (ModernMainWindow window, FileListViewModel pane) = await OpenAsync(o => o.OpenWithSingleClick = true);
            try
            {
                ListBoxItem alpha = Container(window, "Alpha");
                Click(window, alpha, new Point(alpha.Bounds.Width / 2, alpha.Bounds.Height / 2));
                await WaitUntil(() => PathUtil.AreSame(pane.CurrentPath, Path.Combine(_root, "Alpha")));

                // Back, and with double-click (the default) one click only selects.
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 5);
                SetOptions(window, o => o.OpenWithSingleClick = false);
                await Pump(30); // past the double-click time: two clicks on the same spot that close together are a double-click
                alpha = Container(window, "Alpha");
                Click(window, alpha, new Point(alpha.Bounds.Width / 2, alpha.Bounds.Height / 2));
                await Pump(10);
                Assert.True(PathUtil.AreSame(pane.CurrentPath, _root));
                Assert.Equal("Alpha", pane.SelectedItem?.Name);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaFact]
        public async Task CheckBoxes_SelectSeveralItems_WithoutCtrl()
        {
            (ModernMainWindow window, FileListViewModel pane) = await OpenAsync(o =>
            {
                o.UseCheckBoxes = true;
                o.HideKnownExtensions = true;
                o.FullPathInTitleBar = true;
            });
            try
            {
                Assert.True(pane.ShowCheckBoxes);
                ClickCheckBox(window, "notes.txt");
                ClickCheckBox(window, "report.pdf");
                await Pump(5);
                Assert.Equal(new[] { "notes.txt", "report.pdf" }, pane.SelectedItems.Select(i => i.Name).OrderBy(n => n));
                Screenshot(window, "folder-options-checkboxes.png");

                ClickCheckBox(window, "report.pdf"); // unticked again: only that one
                await Pump(5);
                Assert.Equal("notes.txt", Assert.Single(pane.SelectedItems).Name);
                Assert.True(PathUtil.AreSame(pane.CurrentPath, _root)); // a tick never opens anything
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaFact]
        public async Task HiddenExtensions_ShowInTheList_AndAnInPlaceRenameKeepsThem()
        {
            (ModernMainWindow window, FileListViewModel pane) = await OpenAsync(o => o.HideKnownExtensions = true);
            try
            {
                FileListItemViewModel report = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "report.pdf");
                Assert.Equal("report", report.DisplayName);

                ListBoxItem container = Container(window, "report.pdf");
                ListBox list = container.FindAncestorOfType<ListBox>();
                list.Selection.Clear();
                list.Selection.Select(list.IndexFromContainer(container));
                container.Focus();
                await Pump(3);
                window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                window.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
                await Pump(5);
                TextBox box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "report" && t.IsFocused);
                box.Text = "summary";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await WaitUntil(() => File.Exists(Path.Combine(_root, "summary.pdf")));
                Assert.False(File.Exists(Path.Combine(_root, "summary")));
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaFact]
        public async Task TheTitleBar_TheTree_TypingAndPrivacy_FollowTheirOptions()
        {
            (ModernMainWindow window, FileListViewModel pane) = await OpenAsync(_ => { });
            try
            {
                Assert.Equal("ByteExplorer", window.Title);
                SetOptions(window, o => o.FullPathInTitleBar = true);
                await Pump(2);
                Assert.Equal(_root, window.Title);
                await pane.LoadThisPcAsync();
                await Pump(3);
                Assert.Equal("This PC", window.Title);
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 5);

                // "Show all folders": the home folder above This PC, and gone again when turned off.
                NavigationViewModel tree = window.ViewModel.NavigationViewModel;
                SetOptions(window, o => o.ShowAllFolders = true);
                Assert.True(PathUtil.AreSame(tree.RootNodes[0].FullPath, SpecialFolders.Home));
                SetOptions(window, o => o.ShowAllFolders = false);
                Assert.Null(tree.RootNodes[0].FullPath); // This PC

                // Typing in the list goes to the search box.
                SetOptions(window, o => o.TypingInList = FolderOptionsSettings.TypeSearches);
                ListBoxItem notes = Container(window, "notes.txt");
                notes.Focus();
                await Pump(2);
                window.KeyTextInput("n");
                await Pump(3);
                TextBox search = window.FindControl<TextBox>("GlobalSearchBox") ??
                                 window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsFocused);
                Assert.Equal("n", search.Text);
                Assert.True(search.IsFocused);
                search.Text = string.Empty;

                // Privacy: the recent and frequent lists hide.
                Assert.True(pane.ShowRecentFiles);
                SetOptions(window, o =>
                {
                    o.ShowRecentFiles = false;
                    o.ShowFrequentFolders = false;
                });
                Assert.False(pane.ShowRecentFiles);
                Assert.False(pane.ShowFrequentFolders);
                Assert.False(pane.HasSidebarFrequentFolders);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaFact]
        public async Task ANewWindow_OpensWhereTheOptionsSay_IncludingWhereItWasLeft()
        {
            var first = new ModernMainWindow { Width = 1200, Height = 700 };
            first.Show();
            try
            {
                await Pump(30);
                SetOptions(first, o => o.OpenAt = FolderOptionsSettings.OpenAtThisPc);
            }
            finally
            {
                first.Close();
            }

            var second = new ModernMainWindow { Width = 1200, Height = 700 };
            second.Show();
            try
            {
                await WaitUntil(() => second.ViewModel.FileListViewModel.IsThisPcView);

                // Where I left off: the folder open when the window closed.
                SetOptions(second, o => o.OpenAt = FolderOptionsSettings.OpenAtLastSession);
                await second.ViewModel.FileListViewModel.LoadFolderAsync(Path.Combine(_root, "Beta"));
                await Pump(5);
                await second.ViewModel.SaveSessionAsync();
            }
            finally
            {
                second.Close();
            }

            var third = new ModernMainWindow { Width = 1200, Height = 700 };
            third.Show();
            try
            {
                await WaitUntil(() => PathUtil.AreSame(third.ViewModel.FileListViewModel.CurrentPath, Path.Combine(_root, "Beta")));
            }
            finally
            {
                Close(third);
            }
        }

        [AvaloniaFact]
        public async Task TheSettingsWindow_HasAFolderOptionsPage()
        {
            (ModernMainWindow window, _) = await OpenAsync(_ => { });
            try
            {
                window.OpenSettings();
                for (int i = 0; i < 250 && window.SettingsWindowForTests == null; i++)
                {
                    await Pump(1);
                }

                Views.Settings.SettingsWindow settingsWindow = window.SettingsWindowForTests;
                settingsWindow.Width = 1060;
                settingsWindow.Height = 900;
                ViewModels.Settings.SettingsViewModel settings = settingsWindow.ViewModel;
                settings.ShowPage("folderoptions");
                await Pump(10);
                Screenshot(settingsWindow, "folder-options-page.png");
                ViewModels.Settings.SettingsPage page = settings.Pages.Single(p => p.Key == "folderoptions");
                Assert.Equal("Folder options", page.Title);
                string[] titles = page.Items.Select(i => i.Title).ToArray();
                foreach (string expected in new[]
                         {
                             "Open ByteExplorer to", "Click items as follows", "When typing into the file list", "Show hidden files and folders",
                             "Hide extensions for known file types", "Hide empty drives", "Display the full path in the title bar",
                             "Use check boxes to select items", "Show pop-up descriptions for items", "Show all folders",
                             "Show recently used files", "Show frequently used folders", "Clear activity history"
                         })
                {
                    Assert.Contains(expected, titles);
                }

                // A change made there applies at once.
                var hide = (ViewModels.Settings.ToggleSetting)page.Items.Single(i => i.Title == "Hide extensions for known file types");
                hide.Value = true;
                Assert.True(FolderOptions.Current.HideKnownExtensions);
                settingsWindow.Close();
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ helpers

        private async Task<(ModernMainWindow Window, FileListViewModel Pane)> OpenAsync(Action<FolderOptionsSettings> change)
        {
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            await Pump(30);
            SetOptions(window, change);
            FileListViewModel pane = window.ViewModel.FileListViewModel;
            pane.SelectedPreset = FileListViewPreset.Details;
            await pane.LoadFolderAsync(_root);
            await WaitUntil(() => pane.Items?.Count == 5);
            await Pump(5);
            return (window, pane);
        }

        private static void SetOptions(MainWindowBase window, Action<FolderOptionsSettings> change)
        {
            change(window.ViewModel.SettingsService.Current.FolderOptions);
            window.ViewModel.ApplyFolderOptions();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Closes the window with every option back to its default (and saved: other tests read the same file).</summary>
        private static void Close(MainWindowBase window)
        {
            window.ViewModel.SettingsService.Current.FolderOptions = new FolderOptionsSettings();
            window.ViewModel.ApplyFolderOptions();
            window.ViewModel.SettingsService.SaveAsync().GetAwaiter().GetResult();
            window.Close();
        }

        private static void Screenshot(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Dispatcher.UIThread.RunJobs();
                Directory.CreateDirectory(directory);
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
            }
        }

        private static ListBoxItem Container(Window window, string name) =>
            window.GetVisualDescendants().OfType<ListBoxItem>()
                .First(c => c.IsEffectivelyVisible && c.DataContext is FileListItemViewModel item && item.Name == name);

        private static void Click(Window window, Visual target, Point local)
        {
            Point point = target.TranslatePoint(local, window) ?? default;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        private static void ClickCheckBox(Window window, string name)
        {
            ListBoxItem container = Container(window, name);
            FileItemView view = container.GetVisualDescendants().OfType<FileItemView>().Single();
            var onBox = new Point(8 + 7, view.Bounds.Height / 2);
            Assert.True(view.IsOnCheckBox(onBox));
            Click(window, view, onBox);
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 300)
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
