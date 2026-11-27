// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Navigation: the back/forward history (Home and This PC as places of their own, multi-step jumps, the history
    /// menus), tree reloads that keep what is open, and the tree following the folder the pane shows.
    /// </summary>
    public sealed class NavigationFeaturesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-nav-" + Guid.NewGuid().ToString("N"));

        public NavigationFeaturesTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A watcher may still hold the folder for a moment; the temp folder is cleaned eventually.
            }
        }

        // ------------------------------------------------------------------ history model

        [Fact]
        public void History_RecordsPlacesAndMovesSeveralSteps()
        {
            var history = new NavigationHistory();
            history.Arrive(HistoryEntry.Home); // the starting point is not something to go back to
            Assert.False(history.CanGoBack);

            history.Arrive(HistoryEntry.ThisPc);
            history.Arrive(HistoryEntry.Folder("/a"));
            history.Arrive(HistoryEntry.Folder("/a/")); // the same folder: nothing recorded
            history.Arrive(HistoryEntry.Folder("/b"));
            history.Arrive(HistoryEntry.Folder("/c"));
            Assert.Equal(new[] { "/b", "/a", "This PC", "Home" }, history.BackEntries.Select(e => e.ToString()));

            // Three steps back lands on This PC, not Home (they used to share a null path).
            Assert.True(history.TryMove(-3, out HistoryEntry target));
            Assert.Equal(HistoryPlace.ThisPc, target.Place);
            Assert.Equal(new[] { "/a", "/b", "/c" }, history.ForwardEntries.Select(e => e.ToString()));
            Assert.Equal(new[] { "Home" }, history.BackEntries.Select(e => e.ToString()));

            Assert.False(history.TryMove(4, out _)); // not that many
            Assert.True(history.TryMove(2, out target));
            Assert.Equal("/b", target.Path);
            Assert.Equal(new[] { "/a", "This PC", "Home" }, history.BackEntries.Select(e => e.ToString()));
            Assert.Equal(new[] { "/c" }, history.ForwardEntries.Select(e => e.ToString()));

            // Going somewhere new drops the forward list.
            history.Arrive(HistoryEntry.Folder("/d"));
            Assert.False(history.CanGoForward);
            Assert.Equal("/b", history.BackEntries[0].Path);
        }

        [Fact]
        public void History_KeepsAtMostMaxDepthEntries()
        {
            var history = new NavigationHistory();
            for (int i = 0; i <= NavigationHistory.MaxDepth + 10; i++)
            {
                history.Arrive(HistoryEntry.Folder("/f" + i));
            }

            Assert.Equal(NavigationHistory.MaxDepth, history.BackEntries.Count);
            Assert.Equal("/f" + (NavigationHistory.MaxDepth + 9), history.BackEntries[0].Path);
        }

        [AvaloniaFact]
        public async Task Pane_BackFromAFolderToThisPc_ReturnsToThisPc_AndTheMenuJumps()
        {
            string a = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
            string b = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadHomeAsync();
                await pane.LoadThisPcAsync();
                await pane.LoadFolderAsync(a);
                await pane.LoadFolderAsync(b);
                await Pump(3);

                Assert.Equal(new[] { "A", "This PC", "Home" }, pane.BackHistory.Take(3).Select(e => e.DisplayName));

                ContextMenu menu = NavigationHistoryMenu.Build(pane, forward: false);
                var items = ((IEnumerable<Control>)menu.ItemsSource).OfType<MenuItem>().ToList();
                Assert.Equal("This PC", items[1].Header);
                items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                for (int i = 0; i < 100 && !pane.IsThisPcView; i++)
                {
                    await Pump(1);
                }

                Assert.True(pane.IsThisPcView); // two steps back, and This PC rather than Home
                Assert.Equal(new[] { "A", "B" }, pane.ForwardHistory.Select(e => e.DisplayName));
                ContextMenu forwardMenu = NavigationHistoryMenu.Build(pane, forward: true);
                Assert.Equal(2, ((IEnumerable<Control>)forwardMenu.ItemsSource).Count());

                await pane.NavigateHistoryAsync(1);
                Assert.Equal(a, pane.CurrentPath);
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ tree reloads

        [AvaloniaFact]
        public async Task Reload_KeepsExpandedSubfolders_AndChangesOnlyWhatChanged()
        {
            var service = new FakeNavigationService();
            service.Set("/r", "alpha", "beta", "gamma");
            service.Set("/r/beta", "inner1", "inner2");
            var dispatcher = new UiDispatcher(Dispatcher.UIThread);
            var root = new NavigationNodeViewModel("r", "/r", NavigationNodeType.Folder, service, null, dispatcher);
            var roots = new System.Collections.ObjectModel.ObservableCollection<NavigationNodeViewModel> { root };
            using var rows = new NavigationTreeRows(roots);

            await root.ExpandAndLoadAsync();
            NavigationNodeViewModel beta = root.FindChild("/r/beta");
            await beta.ExpandAndLoadAsync();
            Assert.Equal(new[] { "r", "alpha", "beta", "inner1", "inner2", "gamma" }, rows.Select(r => r.Node.Name));

            var events = new List<NotifyCollectionChangedEventArgs>();
            rows.CollectionChanged += (_, e) => events.Add(e);
            service.Set("/r", "alpha", "beta", "delta", "gamma0"); // delta and gamma0 new, gamma gone
            root.RequestReload();
            await root.ExpandAndLoadAsync();

            Assert.Same(beta, root.FindChild("/r/beta"));
            Assert.True(beta.IsExpanded);
            Assert.Equal(new[] { "r", "alpha", "beta", "inner1", "inner2", "delta", "gamma0" }, rows.Select(r => r.Node.Name));
            Assert.All(events, e => Assert.NotEqual(NotifyCollectionChangedAction.Reset, e.Action));
            Assert.Equal(3, events.Count); // one removal, two insertions: the rows above stay untouched
        }

        [AvaloniaFact]
        public async Task Reveal_ExpandsDownToTheFolder()
        {
            string deep = Directory.CreateDirectory(Path.Combine(_root, "one", "two", "three")).FullName;
            Directory.CreateDirectory(Path.Combine(_root, "one", "other"));
            var drives = new FakeDriveService(_root);
            var tree = new NavigationViewModel(drives, new FileSystemNavigationService(), null, new UiDispatcher(Dispatcher.UIThread));
            try
            {
                await tree.InitializeAsync();
                Assert.Null(await tree.RevealAsync(deep, expand: false)); // nothing open yet
                NavigationNodeViewModel node = await tree.RevealAsync(deep + Path.DirectorySeparatorChar, expand: true);
                Assert.NotNull(node);
                Assert.Equal("three", node.Name);
                Assert.True(tree.VisibleRows.IndexOf(node) > 0);
                Assert.Same(node, await tree.RevealAsync(deep, expand: false)); // visible now

                Assert.Null(await tree.RevealAsync(Path.Combine(_root, "one", "missing"), expand: true));
                Assert.Null(await tree.RevealAsync("/somewhere/else/entirely" + Guid.NewGuid().ToString("N"), expand: true));

                tree.CollapseAll();
                Assert.Equal(2, tree.VisibleRows.Count); // This PC and its drive
            }
            finally
            {
                tree.Dispose();
            }
        }

        // ------------------------------------------------------------------ the tree in the window

        [AvaloniaFact]
        public async Task Tree_FollowsThePane_AndKeyboardSelectionWaitsBeforeOpening()
        {
            string target = Directory.CreateDirectory(Path.Combine(_root, "Projects", "Alpha")).FullName;
            string sibling = Directory.CreateDirectory(Path.Combine(_root, "Projects", "Beta")).FullName;
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            try
            {
                await Pump(30);
                MainViewModel main = window.ViewModel;
                var paneView = window.GetVisualDescendants().OfType<NavigationPaneView>().Single();
                var list = paneView.FindControl<ListBox>("Rows");
                for (int i = 0; i < 100 && main.NavigationViewModel.ThisPcNode == null; i++)
                {
                    await Pump(1);
                }

                await main.FileListViewModel.LoadFolderAsync(target);
                for (int i = 0; i < 200 && (list.SelectedItem as NavigationTreeRow)?.Node.FullPath != target; i++)
                {
                    await Pump(1);
                }

                Assert.Equal(target, (list.SelectedItem as NavigationTreeRow)?.Node.FullPath);
                Assert.Equal(target, main.FileListViewModel.CurrentPath); // selecting it did not navigate again

                // Down moves to Beta; the pane follows only after the keyboard rests.
                ((Control)list.ContainerFromIndex(list.SelectedIndex)).Focus();
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                await Pump(2);
                Assert.Equal(sibling, (list.SelectedItem as NavigationTreeRow)?.Node.FullPath);
                Assert.Equal(target, main.FileListViewModel.CurrentPath);
                for (int i = 0; i < 100 && main.FileListViewModel.CurrentPath != sibling; i++)
                {
                    await Pump(1);
                }

                Assert.Equal(sibling, main.FileListViewModel.CurrentPath);

                // Typing finds a folder among the visible rows.
                window.KeyTextInput("alph"); // /tmp is busy: a prefix no other temp folder has
                await Pump(2);
                Assert.Equal(target, (list.SelectedItem as NavigationTreeRow)?.Node.FullPath);

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "navigation-tree.png"));
                }
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task TreeKeys_ActOnTheTreeFolder_NotOnTheFileListSelection()
        {
            string shown = Directory.CreateDirectory(Path.Combine(_root, "Shown")).FullName;
            File.WriteAllText(Path.Combine(shown, "keep.txt"), "x");
            var window = new ModernMainWindow { Width = 1300, Height = 900 };
            window.Show();
            try
            {
                await Pump(30);
                MainViewModel main = window.ViewModel;
                var list = window.GetVisualDescendants().OfType<NavigationPaneView>().Single().FindControl<ListBox>("Rows");
                await main.FileListViewModel.LoadFolderAsync(shown);
                for (int i = 0; i < 200 && (list.SelectedItem as NavigationTreeRow)?.Node.FullPath != shown; i++)
                {
                    await Pump(1);
                }

                main.FileListViewModel.SelectAllCommand.Execute(null);
                await Pump(2);
                ((Control)list.ContainerFromIndex(list.SelectedIndex)).Focus();
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
                window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
                await Pump(2);

                (bool ok, IReadOnlyList<string> files, bool isCut) = await ClipboardFileService.TryGetFilesAsync();
                Assert.True(ok);
                Assert.False(isCut);
                Assert.True(files.Count == 1 && PathUtil.AreSame(files[0], shown), string.Join(" | ", files)); // the folder, not keep.txt from the list
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }

        private sealed class FakeNavigationService : IFileSystemNavigationService
        {
            private readonly Dictionary<string, string[]> _children = new Dictionary<string, string[]>();

            public void Set(string path, params string[] names) => _children[path] = names;

            public Task<IReadOnlyList<DirectoryNodeInfo>> GetSubdirectoriesAsync(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken = default)
            {
                IReadOnlyList<DirectoryNodeInfo> result = _children.TryGetValue(path, out string[] names)
                    ? names.Select(n => new DirectoryNodeInfo(n, path + "/" + n)).ToList()
                    : new List<DirectoryNodeInfo>();
                return Task.FromResult(result);
            }
        }

        /// <summary>One "drive": the test's temp folder, so the tree's walk never depends on the machine's mounts.</summary>
        private sealed class FakeDriveService : IDriveEnumerationService
        {
            private readonly string _root;

            public FakeDriveService(string root) => _root = root;

            public Task<IReadOnlyList<DriveNodeInfo>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<DriveNodeInfo>>(new[] { new DriveNodeInfo(_root, "Test", DriveType.Fixed, isReady: true, totalSize: 0, availableFreeSpace: 0) });
        }
    }
}
