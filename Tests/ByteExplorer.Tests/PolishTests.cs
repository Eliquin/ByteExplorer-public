// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The public-release polish: friendly errors, the status bar, Explorer's keys, Enter on several items, accessible names.</summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class PolishTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-polish-" + Guid.NewGuid().ToString("N"));

        public PolishTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
            Directory.CreateDirectory(Path.Combine(_root, "Beta"));
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
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

        [Fact]
        public void Errors_AreExplainedInPlainWords_NeverAsProgrammingErrors()
        {
            Assert.Equal(FriendlyErrors.Generic, FriendlyErrors.Explain(new NullReferenceException()));
            Assert.Equal(FriendlyErrors.Generic, FriendlyErrors.Explain(new InvalidOperationException("Collection was modified")));
            Assert.Contains("Access was denied", FriendlyErrors.Explain(new UnauthorizedAccessException("Access to the path 'C:\\x' is denied.")));
            Assert.Contains("couldn't be found", FriendlyErrors.Explain(new FileNotFoundException("x")));
            Assert.Contains("open in another program", FriendlyErrors.Explain(new IOException("in use", unchecked((int)0x80070020))));
            Assert.Contains("enough space", FriendlyErrors.Explain(new IOException("full", unchecked((int)0x80070070))));
            Assert.Equal("The network name cannot be found.", FriendlyErrors.Explain(new IOException("The network name cannot be found.")));
            Assert.Equal(FriendlyErrors.Generic, FriendlyErrors.Explain(new IOException("Error HRESULT E_FAIL has been returned from a call to a COM component.")));
            Assert.Equal(FriendlyErrors.Generic, FriendlyErrors.Explain(new Win32Exception(unchecked((int)0x80004005), "Unknown error (0x80004005)")));
            Assert.StartsWith("Couldn't rename.\n\n", FriendlyErrors.With("Couldn't rename.", new FileNotFoundException()));
        }

        [AvaloniaFact]
        public async Task TheStatusBar_SaysTheCountOnce_AndExplorersKeysWork()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 3);

                Assert.Equal("2 folders \u2022 1 file", pane.SelectionSummaryText);
                Assert.DoesNotContain("items", pane.SelectionSummaryText);

                // F11: full screen and back.
                WindowState before = window.WindowState;
                Press(window, Key.F11);
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                Press(window, Key.F11);
                Assert.Equal(before, window.WindowState);

                // Alt+P: the details pane.
                bool pane0 = window.ViewModel.IsDetailsPaneVisible;
                Press(window, Key.P, KeyModifiers.Alt);
                Assert.NotEqual(pane0, window.ViewModel.IsDetailsPaneVisible);
                Press(window, Key.P, KeyModifiers.Alt);
                Assert.Equal(pane0, window.ViewModel.IsDetailsPaneVisible);

                // Esc in the files clears the selection.
                pane.SelectAllCommand.Execute(null); // selected in the list itself, as a person would
                await WaitUntil(() => pane.SelectedItems.Count == 3);
                window.Activate();
                window.PrimaryPaneForTests.FocusItems();
                await Pump(5);
                object focusedBefore = window.FocusManager.GetFocusedElement();
                Press(window, Key.Escape);
                await Pump(2);
                Assert.True(pane.SelectedItems.Count == 0, "focus: " + focusedBefore);

                // Ctrl+E: the search box.
                Press(window, Key.E, KeyModifiers.Control);
                await Pump(2);
                Assert.IsType<TextBox>(window.FocusManager.GetFocusedElement());
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Enter_OnSeveralFolders_OpensThemAll_InNewTabs()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 3);
                var opened = new List<string>();
                Func<IReadOnlyList<string>, bool, Task> original = pane.OpenInNewTabs;
                pane.OpenInNewTabs = (paths, show) =>
                {
                    opened.AddRange(paths);
                    return Task.CompletedTask;
                };
                try
                {
                    pane.UpdateSelection(pane.Items.OfType<FileListItemViewModel>().Where(i => i.IsDirectory));
                    await pane.OpenSelectionAsync(pane.SelectedItems[0]);
                    Assert.Equal(new[] { "Alpha", "Beta" }, opened.Select(Path.GetFileName).OrderBy(n => n));
                    Assert.Equal(_root, pane.CurrentPath); // this pane stays where it is
                }
                finally
                {
                    pane.OpenInNewTabs = original;
                }
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task IconOnlyButtons_HaveAccessibleNames()
        {
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                List<Button> iconOnly = window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && b.Content is not string && ToolTip.GetTip(b) is string)
                    .ToList();
                Assert.True(iconOnly.Count > 10);
                Assert.All(iconOnly, b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b)), "unnamed: " + ToolTip.GetTip(b)));
            }
            finally
            {
                window.Close();
            }
        }

        private static void Press(Window window, Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            RawInputModifiers raw = RawInputModifiers.None;
            if ((modifiers & KeyModifiers.Alt) != 0)
            {
                raw |= RawInputModifiers.Alt;
            }

            if ((modifiers & KeyModifiers.Control) != 0)
            {
                raw |= RawInputModifiers.Control;
            }

            if ((modifiers & KeyModifiers.Shift) != 0)
            {
                raw |= RawInputModifiers.Shift;
            }

            window.KeyPress(key, raw, PhysicalKey.None, null);
            window.KeyRelease(key, raw, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
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
