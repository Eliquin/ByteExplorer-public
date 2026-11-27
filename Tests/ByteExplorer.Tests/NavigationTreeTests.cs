// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.ViewModels;
using ByteExplorer.Views;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class NavigationTreeTests
    {
        // FullPath null: these nodes never start a disk load; their children are set by the test.
        private static NavigationNodeViewModel Node(string name) =>
            new NavigationNodeViewModel(name, null, NavigationNodeType.Folder, null, null, null, hasChildrenHint: false);

        private static NavigationNodeViewModel WithChildren(string name, int count)
        {
            NavigationNodeViewModel node = Node(name);
            node.Children.ReplaceAll(Enumerable.Range(0, count).Select(i => Node($"{name}-{i}")));
            return node;
        }

        // A node is a view model: its property changes post a command requery to the UI dispatcher, which must be the
        // headless session's (a plain test thread touching Dispatcher.UIThread first broke later window tests at random).
        [AvaloniaFact]
        public void Rows_FollowExpandAndCollapse()
        {
            NavigationNodeViewModel a = WithChildren("a", 2);
            NavigationNodeViewModel b = WithChildren("b", 3);
            var root = Node("root");
            root.Children.ReplaceAll(new[] { a, b });
            var roots = new ObservableCollection<NavigationNodeViewModel> { root };
            using var rows = new NavigationTreeRows(roots);

            Assert.Equal(new[] { "root" }, rows.Select(r => r.Node.Name));
            root.IsExpanded = true;
            Assert.Equal(new[] { "root", "a", "b" }, rows.Select(r => r.Node.Name));

            b.IsExpanded = true;
            a.IsExpanded = true;
            Assert.Equal(new[] { "root", "a", "a-0", "a-1", "b", "b-0", "b-1", "b-2" }, rows.Select(r => r.Node.Name));
            Assert.Equal(new[] { 0, 1, 2, 2, 1, 2, 2, 2 }, rows.Select(r => r.Depth));

            // Collapsing a keeps b's expanded block; collapsing root hides everything, re-expanding restores the state.
            a.IsExpanded = false;
            Assert.Equal(new[] { "root", "a", "b", "b-0", "b-1", "b-2" }, rows.Select(r => r.Node.Name));
            root.IsExpanded = false;
            Assert.Single(rows);
            root.IsExpanded = true;
            Assert.Equal(6, rows.Count);
            Assert.Same(root, rows.ParentOf(rows[1]).Node);
        }

        // A node is a view model: its property changes post a command requery to the UI dispatcher, which must be the
        // headless session's (a plain test thread touching Dispatcher.UIThread first broke later window tests at random).
        [AvaloniaFact]
        public void Rows_FollowChildReplacementWithRangedNotifications()
        {
            NavigationNodeViewModel folder = WithChildren("f", 1);
            var roots = new ObservableCollection<NavigationNodeViewModel> { folder };
            using var rows = new NavigationTreeRows(roots);
            folder.IsExpanded = true;

            var events = new List<NotifyCollectionChangedEventArgs>();
            rows.CollectionChanged += (_, e) => events.Add(e);

            var stopwatch = Stopwatch.StartNew();
            folder.Children.ReplaceAll(Enumerable.Range(0, 50_000).Select(i => Node("n" + i)));
            stopwatch.Stop();

            Assert.Equal(50_001, rows.Count);
            Assert.Equal(2, events.Count); // one ranged Remove, one ranged Add: not 50,000 notifications
            Assert.Equal(50_000, events[1].NewItems.Count);
            Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"took {stopwatch.ElapsedMilliseconds} ms");
            Assert.True(rows[0].HasChildren);
        }

        [AvaloniaFact]
        public void Pane_RealizesOnlyVisibleRows()
        {
            NavigationNodeViewModel big = WithChildren("big", 50_000);
            var viewModel = new NavigationViewModel(null, null, null, new Infrastructure.UiDispatcher(Dispatcher.UIThread));
            viewModel.RootNodes.Add(big);
            big.IsExpanded = true;

            var pane = new NavigationPaneView { DataContext = viewModel };
            var window = new Window { Width = 300, Height = 600, Content = pane };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int realized = pane.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(realized, 1, 100);
            window.Close();
            viewModel.Dispose();
        }

        /// <summary>
        /// The Modern left panel: the tree scrolls with the other sections in one scroller and still creates only the rows
        /// the panel shows; scrolling the panel brings other rows, and the keyboard keeps the selected row in view.
        /// </summary>
        [AvaloniaFact]
        public void Pane_InThePanelsScroller_StaysVirtualized_AndKeepsTheSelectionInView()
        {
            NavigationNodeViewModel big = WithChildren("big", 50_000);
            var viewModel = new NavigationViewModel(null, null, null, new Infrastructure.UiDispatcher(Dispatcher.UIThread));
            viewModel.RootNodes.Add(big);
            big.IsExpanded = true;

            var pane = new NavigationPaneView { DataContext = viewModel, ScrollsWithPanel = true };
            var above = new Border { Height = 400 }; // the sections above the tree
            var stack = new StackPanel();
            stack.Children.Add(above);
            stack.Children.Add(pane);
            stack.Children.Add(new Border { Height = 200 }); // and below it
            var scroller = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            var window = new Window { Width = 300, Height = 600, Content = scroller };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int realized = pane.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(realized, 1, 60); // 200 px of tree shows
            Assert.True(scroller.Extent.Height > 50_000 * 30, $"extent {scroller.Extent.Height}: the panel scrolls the whole tree");

            // Scrolled far down: the rows there exist, the first ones don't.
            scroller.Offset = new Avalonia.Vector(0, 400 + 25_000 * 30);
            Dispatcher.UIThread.RunJobs();
            var shown = pane.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsVisible && i.Bounds.Height > 0)
                .Select(i => ((NavigationTreeRow)i.DataContext).Node.Name).ToList();
            Assert.InRange(shown.Count, 1, 80);
            Assert.Contains(shown, n => n.StartsWith("big-249") || n.StartsWith("big-250"));

            // The keyboard: End selects the last row and the panel shows it; Home goes back to the top.
            var list = pane.FindControl<ListBox>("Rows");
            scroller.Offset = new Avalonia.Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            (list.ContainerFromIndex(0) as Control)?.Focus();
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.End });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(50_000, list.SelectedIndex);
            Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 250, $"offset {scroller.Offset.Y} of {scroller.Extent.Height}");
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.PageUp });
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(list.SelectedIndex, 49_970, 49_999);
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Home });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, list.SelectedIndex);
            Assert.True(scroller.Offset.Y <= 400 + 30, $"offset {scroller.Offset.Y}");
            Assert.InRange(pane.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);

            window.Close();
            viewModel.Dispose();
        }
    }
}
