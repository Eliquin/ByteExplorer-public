// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Lens;
using ByteExplorer.ViewModels.Search;
using ByteExplorer.Views.Menus;
using Serilog;

namespace ByteExplorer.Views.Lens
{
    /// <summary>
    /// The lens view. Files behave as in the search results: double-click or Enter opens, Ctrl+Enter shows the file in its
    /// folder, the right-click menu is the file list's (plus "Open file location"), Ctrl+C / Ctrl+X / Delete / F2 / Alt+Enter
    /// work, files can be dragged out. Headings can't be selected. The river follows the scroll and jumps on a click.
    /// </summary>
    public partial class LensView : UserControl
    {
        private ListBox _rows;
        private ScrollViewer _scroller;
        private Point _pressPoint;
        private bool _pressedOnRow;

        /// <summary>The row a river bar jumped to, while the list can't scroll it to the top (it is near the end).</summary>
        private int _jumpRow = -1;

        public LensView()
        {
            AvaloniaXamlLoader.Load(this);
            _rows = this.FindControl<ListBox>("Rows");
            _rows.DoubleTapped += OnDoubleTapped;
            _rows.ContainerPrepared += OnContainerPrepared;
            _rows.SelectionChanged += OnSelectionChanged;
            _rows.ContextRequested += OnContextRequested;
            _rows.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
            _rows.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _rows.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _rows.TemplateApplied += (_, _) => HookScroller();
            AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Bubble);
        }

        private LensViewModel ViewModel => DataContext as LensViewModel;

        private MainViewModel Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

        /// <summary>The rows list (tests).</summary>
        internal ListBox RowsForTests => _rows;

        private void HookScroller()
        {
            ScrollViewer scroller = _rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller == null || ReferenceEquals(scroller, _scroller))
            {
                return;
            }

            if (_scroller != null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
            }

            _scroller = scroller;
            _scroller.ScrollChanged += OnScrollChanged;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // After a jump to a period near the end, the list stops at its bottom before that period reaches the top:
            // the bar that was clicked stays lit rather than a newer one.
            bool atBottom = _scroller != null && _scroller.Offset.Y >= _scroller.Extent.Height - _scroller.Viewport.Height - 1;
            if (_jumpRow >= 0 && atBottom)
            {
                ViewModel?.SetTopRow(_jumpRow);
                return;
            }

            _jumpRow = -1;
            ViewModel?.SetTopRow(TopRow());
        }

        /// <summary>The list's padding above the first row.</summary>
        private const double TopPadding = 4;

        /// <summary>The row at the top edge, from the scroll offset (rows have fixed heights, so no layout is needed).</summary>
        private int TopRow() =>
            _scroller == null || _rows.ItemsSource is not LensRowList list ? 0 : Math.Max(0, list.RowAtOffset(_scroller.Offset.Y - TopPadding + 1));

        // ------------------------------------------------------------------ headings

        private void OnContainerPrepared(object sender, ContainerPreparedEventArgs e)
        {
            bool heading = _rows.ItemsSource is LensRowList list && e.Index >= 0 && e.Index < list.Count && list[e.Index] is LensGroupHeader;
            e.Container.Classes.Set("heading", heading);
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // A heading never stays selected (Ctrl+A, Shift+click across groups).
            foreach (object added in e.AddedItems)
            {
                if (added is LensGroupHeader)
                {
                    _rows.SelectedItems?.Remove(added);
                }
            }
        }

        // ------------------------------------------------------------------ river

        private void OnRiverBarClick(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.Tag is LensRiverBucket bucket && bucket.FirstRow >= 0)
            {
                ScrollToTop(bucket.FirstRow);
            }
        }

        /// <summary>Brings a row to the top of the list (a river jump), not just into view.</summary>
        internal void ScrollToTop(int row)
        {
            if (_rows.ItemsSource is not LensRowList list || row < 0 || row >= list.Count)
            {
                return;
            }

            _jumpRow = row;
            HookScroller();
            if (_scroller != null)
            {
                double target = TopPadding + list.OffsetOfRow(row);
                double max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
                _scroller.Offset = new Vector(_scroller.Offset.X, max > 0 ? Math.Min(target, max) : target);
            }

            ViewModel?.SetTopRow(row);
        }

        // ------------------------------------------------------------------ opening

        private void OnDoubleTapped(object sender, TappedEventArgs e)
        {
            if (RowFromSource(e.Source) is SearchResultRow row)
            {
                OpenAsync(row).Forget("open a lens file");
            }
        }

        private async Task OpenAsync(SearchResultRow row)
        {
            MainViewModel main = Main;
            if (main != null)
            {
                await main.OpenSearchResultAsync(row);
            }
        }

        private async Task OpenLocationAsync(SearchResultRow row)
        {
            MainViewModel main = Main;
            if (main != null)
            {
                await main.OpenSearchResultLocationAsync(row); // opens the folder: the lens gives way to it
            }
        }

        // ------------------------------------------------------------------ keyboard

        private void OnViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && ViewModel != null)
            {
                ViewModel.RefreshAsync().Forget("refresh the lens");
                e.Handled = true;
            }
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            SearchResultRow current = _rows.SelectedItem as SearchResultRow;
            IReadOnlyList<string> paths = SelectedPaths();
            FileListViewModel pane = Main?.FileListViewModel;
            switch (e.Key)
            {
                case Key.Escape when e.KeyModifiers == KeyModifiers.None && paths.Count > 0:
                    _rows.SelectedItems?.Clear();
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.None && current != null:
                    foreach (SearchResultRow row in _rows.SelectedItems.OfType<SearchResultRow>().Take(32).ToList())
                    {
                        OpenAsync(row).Forget("open a lens file"); // every selected file, as Explorer does
                    }

                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == command && current != null:
                    OpenLocationAsync(current).Forget("show a lens file in its folder");
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.Alt && paths.Count > 0:
                    pane?.DialogService?.ShowProperties(paths);
                    e.Handled = true;
                    return;

                case Key.C when e.KeyModifiers == command && paths.Count > 0:
                    ClipboardFileService.SetFilesForCopy(paths);
                    e.Handled = true;
                    return;

                case Key.X when e.KeyModifiers == command && paths.Count > 0:
                    ClipboardFileService.SetFilesForCut(paths);
                    e.Handled = true;
                    return;

                case Key.C when e.KeyModifiers == (command | KeyModifiers.Shift) && paths.Count > 0:
                    ClipboardFileService.SetText(string.Join(Environment.NewLine, paths));
                    e.Handled = true;
                    return;

                case Key.Delete when paths.Count > 0 && pane != null && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift):
                    DeleteAsync(pane, paths, e.KeyModifiers == KeyModifiers.Shift).Forget("delete lens files");
                    e.Handled = true;
                    return;

                case Key.F2 when e.KeyModifiers == KeyModifiers.None && current != null && pane != null:
                    pane.PromptRenamePathAsync(current.FullPath).Forget("rename a lens file");
                    e.Handled = true;
                    return;
            }
        }

        /// <summary>Deletes through the pane (confirmation, Recycle Bin, Undo), then shows the lens again without them.</summary>
        private async Task DeleteAsync(FileListViewModel pane, IReadOnlyList<string> paths, bool permanent)
        {
            await pane.DeletePathsAsync(paths, permanent);
            if (ViewModel != null)
            {
                await ViewModel.RefreshAsync();
            }
        }

        // ------------------------------------------------------------------ menu

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            ListBoxItem item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is not SearchResultRow row)
            {
                return;
            }

            e.Handled = true;
            if (!_rows.SelectedItems.Contains(row))
            {
                _rows.SelectedItem = row; // right-click outside the selection selects the row, as in the file list
            }

            IReadOnlyList<string> paths = SelectedPaths();
            FileListViewModel pane = Main?.FileListViewModel;
            if (paths.Count == 0 || pane == null)
            {
                return;
            }

            if (FileContextMenu.TryShowShellMenu(item, pane, paths, pathsAreSelection: false))
            {
                return; // Right-click menu: Windows (or Shift+right-click in Hybrid)
            }

            var items = new List<Control>
            {
                MenuItem("Open file location", Symbol.FolderOpen, () => OpenLocationAsync(row).Forget("show a lens file in its folder"),
                    OperatingSystem.IsMacOS() ? "Cmd+Enter" : "Ctrl+Enter"),
                MenuItem("Copy full path", Symbol.Link, () => ClipboardFileService.SetText(string.Join(Environment.NewLine, paths)),
                    OperatingSystem.IsMacOS() ? "Cmd+Shift+C" : "Ctrl+Shift+C"),
                new Separator()
            };

            try
            {
                ContextMenu fileMenu = FileContextMenu.ForItems(item, pane, paths, pathsAreSelection: false);
                if (fileMenu?.ItemsSource is IEnumerable<Control> fileItems)
                {
                    items.AddRange(fileItems.ToList());
                    fileMenu.ItemsSource = null;
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Building the lens menu failed");
            }

            var menu = new ContextMenu
            {
                ItemsSource = items,
                Placement = e.TryGetPosition(item, out _) ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft,
                PlacementTarget = item
            };
            menu.Closed += (_, _) => menu.ItemsSource = null;
            try
            {
                menu.Open(item);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to open the lens menu");
            }
        }

        private static MenuItem MenuItem(string header, Symbol symbol, Action action, string gesture)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            try
            {
                item.InputGesture = KeyGesture.Parse(gesture.Replace("Cmd", "Meta", StringComparison.Ordinal));
            }
            catch (ArgumentException)
            {
                // Display only.
            }

            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "A lens menu command failed");
                }
            };
            return item;
        }

        // ------------------------------------------------------------------ dragging out

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            _pressPoint = e.GetPosition(this);
            _pressedOnRow = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && RowFromSource(e.Source) != null;
        }

        private async void OnPointerMoved(object sender, PointerEventArgs e)
        {
            try
            {
                if (!_pressedOnRow || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                    !FileDragDropHelper.HasMovedPastDragThreshold(_pressPoint, e.GetPosition(this)))
                {
                    return;
                }

                _pressedOnRow = false;
                IReadOnlyList<string> paths = SelectedPaths();
                TopLevel top = TopLevel.GetTopLevel(this);
                if (paths.Count > 0 && top?.StorageProvider != null)
                {
                    await FileDragDropHelper.BeginDragAsync(e, top.StorageProvider, paths, Log.Logger);
                }
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Dragging lens files failed");
            }
        }

        // ------------------------------------------------------------------ helpers

        private IReadOnlyList<string> SelectedPaths() =>
            _rows.SelectedItems?.OfType<SearchResultRow>().Select(r => r.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();

        private static SearchResultRow RowFromSource(object source) =>
            (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SearchResultRow;
    }
}
