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
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Omnibar;
using ByteExplorer.Views.Omnibar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The omnibar in the real window: typing, completion, Enter, reveal, commands, rejection, breadcrumbs.</summary>
    public sealed class OmnibarUiTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-omni-" + Guid.NewGuid().ToString("N"));
        private readonly char _sep = Path.DirectorySeparatorChar;

        public OmnibarUiTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Documents", "Taxes"));
            Directory.CreateDirectory(Path.Combine(_root, "Downloads"));
            Directory.CreateDirectory(Path.Combine(_root, "Music"));
            File.WriteAllText(Path.Combine(_root, "Documents", "report.pdf"), "pdf");
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "notes");
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

        [AvaloniaFact]
        public async Task TypePrefix_TabCompletes_EnterGoes()
        {
            (ModernMainWindow window, FileListViewModel pane, OmnibarViewModel omni) = await OpenAsync();
            try
            {
                Press(window, Key.L, RawInputModifiers.Control);
                await WaitFor(() => omni.IsEditing);
                Assert.Equal(_root, omni.Text);

                TextBox input = Input(window);
                input.Text = _root + _sep + "Doc";
                input.ClearSelection();
                input.CaretIndex = input.Text.Length; // as after typing
                await WaitFor(() => omni.Suggestions.Count > 0 && omni.Suggestions[0].Title == "Documents");
                Assert.Equal("uments" + _sep, omni.Ghost);
                Assert.True(omni.IsPopupOpen);
                await Pump(2);
                TextBlock ghost = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "GhostText");
                Assert.True(ghost.IsVisible, "the faded completion shows after the caret");

                SaveShot(window, "omnibar-typing.png");

                Press(window, Key.Tab);
                await Pump(3);
                Assert.Equal(_root + _sep + "Documents" + _sep, omni.Text);

                Press(window, Key.Enter);
                await WaitFor(() => pane.CurrentPath == Path.Combine(_root, "Documents"));
                Assert.False(omni.IsEditing);
                Assert.Equal(new[] { "Documents" }, omni.Crumbs.Skip(omni.Crumbs.Count - 1).Select(c => c.Name));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task TypedFilePath_OpensItsFolderWithTheFileSelected()
        {
            (ModernMainWindow window, FileListViewModel pane, OmnibarViewModel omni) = await OpenAsync();
            try
            {
                window.Omnibar.BeginEdit();
                await Pump(3);
                Input(window).Text = Path.Combine(_root, "Documents", "report.pdf");
                await Pump(3);
                await omni.CommitAsync();
                for (int i = 0; i < 250 && pane.SelectedItem?.Name != "report.pdf"; i++)
                {
                    await Pump(1);
                }

                Assert.True(pane.SelectedItem?.Name == "report.pdf",
                    $"folder {pane.CurrentPath}, selected {pane.SelectedItem?.Name ?? "nothing"}, items {pane.Items.Count}, address \"{omni.Text}\", editing {omni.IsEditing}");
                Assert.Equal(Path.Combine(_root, "Documents"), pane.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task NonexistentPath_IsRejected_AndEditingContinues()
        {
            (ModernMainWindow window, FileListViewModel pane, OmnibarViewModel omni) = await OpenAsync();
            try
            {
                bool rejected = false;
                omni.InputRejected += (_, _) => rejected = true;
                window.Omnibar.BeginEdit();
                await Pump(3);
                Input(window).Text = Path.Combine(_root, "nowhere", "at-all");
                await Pump(3);
                await omni.CommitAsync();
                await Pump(3);
                Assert.True(rejected);
                Assert.True(omni.IsEditing);
                Assert.True(omni.HasHint);
                Assert.Equal(_root, pane.CurrentPath);
                SaveShot(window, "omnibar-rejected.png");
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Commands_Words_AndSlashToStart()
        {
            (ModernMainWindow window, FileListViewModel pane, OmnibarViewModel omni) = await OpenAsync();
            try
            {
                // "> new folder" runs the command.
                window.Omnibar.BeginEdit();
                await Pump(3);
                Input(window).Text = "> new fol";
                await WaitFor(() => omni.Suggestions.Count > 0 && omni.Suggestions[0].Action == OmnibarActionKind.RunCommand);
                SaveShot(window, "omnibar-commands.png");
                Press(window, Key.Enter);
                await WaitFor(() => Directory.Exists(Path.Combine(_root, "New folder")));
                Assert.False(omni.IsEditing);

                // Words: "musi" goes to the Music folder here.
                window.Omnibar.BeginEdit();
                await Pump(3);
                Input(window).Text = "musi";
                await WaitFor(() => omni.Suggestions.Any(s => s.Title == "Music" && s.Group == "In this folder"));
                omni.SelectedIndex = omni.Suggestions.IndexOf(omni.Suggestions.First(s => s.Title == "Music" && s.Group == "In this folder"));
                await omni.CommitAsync();
                await WaitFor(() => pane.CurrentPath == Path.Combine(_root, "Music"));

                // Typing "/" in the file list starts the omnibar with it (Unix; "\" on Windows).
                window.ViewModel.Omnibar.CancelEdit();
                window.Focus();
                await Pump(2);
                window.KeyTextInput(OperatingSystem.IsWindows() ? "\\" : "/");
                await Pump(3);
                Assert.True(omni.IsEditing);
                Assert.Equal(OperatingSystem.IsWindows() ? "\\" : "/", omni.Text);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Breadcrumbs_NavigateFold_AndOfferSubfolders()
        {
            string deep = Path.Combine(_root, "Documents", "Taxes", "2024", "Quarterly reports", "Spring", "Receipts and invoices");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(_root, "Documents", "Letters"));
            (ModernMainWindow window, FileListViewModel pane, OmnibarViewModel omni) = await OpenAsync();
            try
            {
                await pane.LoadFolderAsync(deep);
                await Pump(10);
                BreadcrumbPanel crumbs = window.GetVisualDescendants().OfType<BreadcrumbPanel>().Single();
                SaveShot(window, "omnibar-breadcrumbs.png");

                // Narrow the window: leading segments fold into "…", the current folder stays.
                window.Width = 760;
                await Pump(10);
                Assert.True(crumbs.HiddenCount > 0, "segments should fold in a narrow window");
                Border current = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("crumb") && b.Classes.Contains("current"));
                Assert.True(current.Bounds.X >= 0 && current.IsEffectivelyVisible);
                SaveShot(window, "omnibar-folded.png");
                window.Width = 1300;
                await Pump(10);

                // A segment goes there.
                Border documents = window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("crumb") && (b.Tag as Core.Omnibar.Crumb)?.Name == "Documents");
                Click(window, documents);
                await WaitFor(() => pane.CurrentPath == Path.Combine(_root, "Documents"));

                // Its separator lists its subfolders.
                IReadOnlyList<string> subfolders = await omni.GetSubfoldersAsync(Path.Combine(_root, "Documents"));
                Assert.Equal(new[] { "Letters", "Taxes" }, subfolders);
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private async Task<(ModernMainWindow, FileListViewModel, OmnibarViewModel)> OpenAsync()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            await Pump(30);
            FileListViewModel pane = window.ViewModel.FileListViewModel;
            await pane.LoadFolderAsync(_root);
            await Pump(5);
            return (window, pane, window.ViewModel.Omnibar);
        }

        private static TextBox Input(Window window) =>
            window.GetVisualDescendants().OfType<OmnibarView>().Single().GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "Input");

        private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        private static void Click(Window window, Visual target)
        {
            Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        }

        private static void SaveShot(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 250 && !condition(); i++)
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
