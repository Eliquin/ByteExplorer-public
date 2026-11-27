// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Search;
using ByteExplorer.Views.Menus;
using ByteSearcher;
using Serilog;

namespace ByteExplorer.Views.Search
{
    /// <summary>
    /// Code-behind of the results view (see the .axaml).
    /// <list type="bullet">
    /// <item>Double click or Enter opens (a folder in the active pane, a file with its application); Ctrl+Enter (or the
    /// menu's "Open file location") shows the item selected in its folder.</item>
    /// <item>Right-click or the menu key: the file menu for the selected results (open with, copy, cut, delete, rename,
    /// compress, pin, properties...), with "Open file location" and "Copy full path" first.</item>
    /// <item>Selected results can be dragged out (into a folder of the app or another application).</item>
    /// <item>Keys act on the selected results (Ctrl+C, Ctrl+X, Ctrl+Shift+C, Delete, Shift+Delete, Alt+Enter), not on the
    /// hidden file list (see <see cref="IFileKeyScope"/>); Escape asks the window to go back to the search box.</item>
    /// </list>
    /// </summary>
    public partial class SearchResultsView : UserControl, IFileKeyScope
    {
        private Point _pressPoint;
        private bool _pressedOnRow;
        private SearchViewModel _observed;

        public SearchResultsView()
        {
            InitializeComponent();
            ResultsList.DoubleTapped += OnDoubleTapped;
            ResultsList.ContextRequested += OnContextRequested;
            ResultsList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
            ResultsList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            ResultsList.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            ResultsList.AddHandler(PointerReleasedEvent, (_, _) => _pressedOnRow = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        /// <summary>Escape in the results: the window puts the keyboard back in the search box.</summary>
        public event EventHandler SearchBoxFocusRequested;

        private SearchViewModel ViewModel => DataContext as SearchViewModel;

        private MainViewModel Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

        /// <summary>Moves the keyboard into the results (Down in the search box), selecting the first one if none is.</summary>
        public void FocusResults()
        {
            if (ResultsList.ItemCount == 0)
            {
                return;
            }

            if (ResultsList.SelectedIndex < 0)
            {
                ResultsList.SelectedIndex = 0;
            }

            ResultsList.ScrollIntoView(ResultsList.SelectedIndex);
            (ResultsList.ContainerFromIndex(ResultsList.SelectedIndex) as Control ?? ResultsList).Focus(NavigationMethod.Directional);
        }

        /// <summary>The selected result, or the first one (Enter in the search box opens the best match).</summary>
        public SearchResultRow CurrentOrFirst =>
            ResultsList.SelectedItem as SearchResultRow ?? (ViewModel?.Results is SearchResultList list && list.Count > 0 ? list[0] : null);

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_observed != null)
            {
                _observed.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _observed = ViewModel;
            if (_observed != null)
            {
                _observed.PropertyChanged += OnViewModelPropertyChanged;
            }

            UpdateSortGlyphs();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SearchViewModel.Sort) || e.PropertyName == nameof(SearchViewModel.Descending))
            {
                UpdateSortGlyphs();
            }
            else if (e.PropertyName == nameof(SearchViewModel.SelectedRow) && ViewModel?.SelectedRow is SearchResultRow row &&
                     ResultsList.SelectedItems?.Count <= 1)
            {
                ResultsList.ScrollIntoView(row);
            }
        }

        private void UpdateSortGlyphs()
        {
            SearchViewModel vm = ViewModel;
            string Mark(SearchSort sort, string label) => vm != null && vm.Sort == sort ? label + (vm.Descending ? "  ▼" : "  ▲") : label;
            NameHeaderText.Text = Mark(SearchSort.Name, "Name");
            FolderHeaderText.Text = Mark(SearchSort.Folder, "Folder");
            SizeHeaderText.Text = Mark(SearchSort.Size, "Size");
            ModifiedHeaderText.Text = Mark(SearchSort.Modified, "Date modified");
            TypeHeaderText.Text = Mark(SearchSort.Type, "Type");
        }

        // ------------------------------------------------------------------ opening

        private void OnDoubleTapped(object sender, TappedEventArgs e)
        {
            if (RowFromSource(e.Source) is SearchResultRow row)
            {
                OpenAsync(row).Forget("SearchResultsView.OpenAsync");
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
                await main.OpenSearchResultLocationAsync(row);
            }
        }

        // ------------------------------------------------------------------ keyboard

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            SearchResultRow current = ResultsList.SelectedItem as SearchResultRow;
            IReadOnlyList<string> paths = SelectedPaths();
            FileListViewModel pane = Main?.FileListViewModel;
            switch (e.Key)
            {
                case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                    SearchBoxFocusRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.None && current != null:
                    OpenAsync(current).Forget("SearchResultsView.OpenAsync");
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == command && current != null:
                    OpenLocationAsync(current).Forget("SearchResultsView.OpenLocationAsync");
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
                    pane.DeletePathsAsync(paths, permanent: e.KeyModifiers == KeyModifiers.Shift).Forget("DeletePathsAsync");
                    e.Handled = true;
                    return;

                case Key.F2 when e.KeyModifiers == KeyModifiers.None && current != null && pane != null:
                    pane.PromptRenamePathAsync(current.FullPath).Forget("PromptRenamePathAsync");
                    e.Handled = true;
                    return;
            }
        }

        // ------------------------------------------------------------------ menu

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            ListBoxItem item = ItemFromSource(e.Source);
            if (item?.DataContext is not SearchResultRow row)
            {
                return;
            }

            e.Handled = true;
            if (!ResultsList.SelectedItems.Contains(row))
            {
                ResultsList.SelectedItem = row; // right-click outside the selection selects the row, as in the file list
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
                MenuItem("Open file location", Symbol.FolderOpen, () => OpenLocationAsync(row).Forget("OpenLocationAsync"), OperatingSystem.IsMacOS() ? "Cmd+Enter" : "Ctrl+Enter"),
                MenuItem("Copy full path", Symbol.Link, () => ClipboardFileService.SetText(string.Join(Environment.NewLine, paths)), OperatingSystem.IsMacOS() ? "Cmd+Shift+C" : "Ctrl+Shift+C"),
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
                Log.Logger.Warning(ex, "Building the search result menu failed");
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
                Log.Logger.Warning(ex, "Failed to open the search result menu");
            }
        }

        private static MenuItem MenuItem(string header, Symbol symbol, Action action, string gesture)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            try
            {
                item.InputGesture = KeyGesture.Parse(gesture);
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
                    Log.Logger.Warning(ex, "A search result menu command failed");
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
                Log.Logger.Warning(ex, "Dragging search results failed");
            }
        }

        // ------------------------------------------------------------------ helpers

        private IReadOnlyList<string> SelectedPaths()
        {
            var paths = new List<string>();
            if (ResultsList.SelectedItems != null)
            {
                foreach (object selected in ResultsList.SelectedItems)
                {
                    if (selected is SearchResultRow row)
                    {
                        paths.Add(row.FullPath);
                    }

                    if (paths.Count >= 10_000)
                    {
                        break; // a select-all of a huge result set: operations on more are unreasonable from here
                    }
                }
            }

            return paths;
        }

        private static SearchResultRow RowFromSource(object source) => ItemFromSource(source)?.DataContext as SearchResultRow;

        private static ListBoxItem ItemFromSource(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is ListBoxItem item)
                {
                    return item;
                }

                if (visual is ListBox)
                {
                    return null;
                }
            }

            return null;
        }
    }
}
