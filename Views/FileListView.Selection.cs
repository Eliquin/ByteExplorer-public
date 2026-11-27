// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.ViewModels;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Selection: every list uses its own index-based selection model, mirrored into
    /// <see cref="FileListViewModel.SelectedItems"/> (which every command, the status bar and the preview read).
    /// <para>
    /// Index-based selection is what keeps a selection intact across virtualization: VirtualizingFileList discards
    /// off-screen items and re-creates them on the way back, and containers are recycled, but an index range does not
    /// care. The WPF build tracked Icons/Tiles selection by path by hand (RowViewState) because their containers were rows
    /// of several items; with one container per item (VirtualizingWrapPanel) that state is gone.
    /// </para>
    /// </summary>
    public partial class FileListView
    {
        /// <summary>Set while the view itself rewrites a list's selection or items, so the change is mirrored once, at the end.</summary>
        private bool _syncingSelection;

        /// <summary>Modifier keys of the latest pointer press or key press (Avalonia has no global keyboard state).</summary>
        private KeyModifiers _lastModifiers;

        /// <summary>The view model's selection at the moment a list's items are about to be replaced.</summary>
        private sealed class SelectionSnapshot
        {
            public FileListItemViewModel[] Items;
            public FileListItemViewModel Primary;
        }

        private static FileListItemViewModel ItemOf(object data) =>
            data as FileListItemViewModel ?? (data as TimelineRow)?.Item;

        private ListKind KindOf(ListBox list)
        {
            if (list == null)
            {
                return ListKind.None;
            }

            if (ReferenceEquals(list, DetailsList) || ReferenceEquals(list, ListModeList) ||
                ReferenceEquals(list, IconsList) || ReferenceEquals(list, TilesList))
            {
                return ListKind.Flat;
            }

            if (ReferenceEquals(list, TimelineList))
            {
                return ListKind.Timeline;
            }

            if (list.Classes.Contains("columnList"))
            {
                return ListKind.Column;
            }

            return list.Classes.Contains("nexusList") ? ListKind.Nexus : ListKind.None;
        }

        // ------------------------------------------------------------------ list -> view model

        private void OnSelectionModelChangedRouted(object sender, SelectionModelChangedEventArgs e)
        {
            if (_syncingSelection || _vm == null || !(e.Source is ListBox list))
            {
                return;
            }

            switch (KindOf(list))
            {
                case ListKind.Flat:
                    if (ReferenceEquals(list, _activeFlatList))
                    {
                        SyncSelectionFromList(list);
                    }

                    break;
                case ListKind.Timeline:
                    SyncSelectionFromList(list);
                    break;
                case ListKind.Nexus:
                    // A plain click in one card replaces the selection, as in a single list; Ctrl/Shift extend it across
                    // cards. (The WPF Nexus view kept every card's selection, so a plain click added to it.)
                    if (e.ItemsWereSelected && (_lastModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) == 0)
                    {
                        ClearOtherNexusLists(list);
                    }

                    SyncNexusSelection();
                    break;
                case ListKind.Column:
                    if (list.DataContext is ColumnPane pane)
                    {
                        SyncColumnSelectionAsync(pane, list).Forget("SyncColumnSelectionAsync");
                    }

                    break;
            }
        }

        /// <summary>Mirrors one list's selection (Details/List/Icons/Tiles/Timeline) into the view model.</summary>
        private void SyncSelectionFromList(ListBox list)
        {
            if (_vm == null)
            {
                return;
            }

            ISelectionModel selection = list.Selection;
            var selected = new List<FileListItemViewModel>(selection.SelectedIndexes.Count);
            foreach (object data in selection.SelectedItems)
            {
                FileListItemViewModel item = ItemOf(data);
                if (item != null)
                {
                    selected.Add(item);
                }
            }

            _vm.UpdateSelection(selected);

            // The preview follows the item the person last clicked or moved to (the anchor), not the lowest index.
            int anchor = selection.AnchorIndex;
            FileListItemViewModel primary = anchor >= 0 && anchor < list.ItemCount && selection.IsSelected(anchor)
                ? ItemOf(list.Items[anchor])
                : null;
            _vm.SelectedItem = primary ?? (selected.Count > 0 ? selected[0] : null);
        }

        private void SyncNexusSelection()
        {
            var selected = new List<FileListItemViewModel>();
            FileListItemViewModel primary = null;
            foreach (ListBox card in NexusLists())
            {
                ISelectionModel selection = card.Selection;
                foreach (object data in selection.SelectedItems)
                {
                    if (data is FileListItemViewModel item)
                    {
                        selected.Add(item);
                    }
                }

                if (card.IsKeyboardFocusWithin && selection.AnchorIndex >= 0 && selection.IsSelected(selection.AnchorIndex))
                {
                    primary = card.Items[selection.AnchorIndex] as FileListItemViewModel;
                }
            }

            _vm.UpdateSelection(selected);
            _vm.SelectedItem = primary ?? (selected.Count > 0 ? selected[selected.Count - 1] : null);
        }

        private void ClearOtherNexusLists(ListBox keep)
        {
            _syncingSelection = true;
            try
            {
                foreach (ListBox card in NexusLists())
                {
                    if (!ReferenceEquals(card, keep) && card.Selection.SelectedIndexes.Count > 0)
                    {
                        card.Selection.Clear();
                    }
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        /// <summary>The card lists of the Nexus view (at most one per category, so a short walk).</summary>
        private IEnumerable<ListBox> NexusLists()
        {
            foreach (Control container in NexusHost.GetRealizedContainers())
            {
                ListBox list = container.FindDescendantOfType<ListBox>();
                if (list != null)
                {
                    yield return list;
                }
            }
        }

        private async Task SyncColumnSelectionAsync(ColumnPane pane, ListBox list)
        {
            FileListViewModel vm = _vm;
            var selected = new List<FileListItemViewModel>(list.Selection.SelectedIndexes.Count);
            foreach (object data in list.Selection.SelectedItems)
            {
                if (data is FileListItemViewModel item)
                {
                    selected.Add(item);
                }
            }

            try
            {
                // Mirrors the selection synchronously, then drills down into a single selected folder.
                await vm.OnColumnSelectionChangedAsync(pane, selected);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Column selection handling failed for {Path}", pane.Path);
            }
        }

        private void ClearViewModelSelection()
        {
            if (_vm == null || (_vm.SelectedItems.Count == 0 && _vm.SelectedItem == null))
            {
                return;
            }

            _vm.UpdateSelection(Array.Empty<FileListItemViewModel>());
            _vm.SelectedItem = null;
        }

        /// <summary>The view model's selection as paths (what menus, drags and clipboard act on).</summary>
        private List<string> SelectedPaths()
        {
            var paths = new List<string>(_vm?.SelectedItems.Count ?? 0);
            if (_vm != null)
            {
                foreach (FileListItemViewModel item in _vm.SelectedItems)
                {
                    paths.Add(item.FullPath);
                }
            }

            return paths;
        }

        // ------------------------------------------------------------------ carrying selection over

        /// <summary>
        /// The selection to carry into the next set of items: only within the same folder (a filter keystroke, a sort,
        /// a refresh, a view switch), never into another folder. The WPF Icons/Tiles views dropped the selection on every
        /// filter keystroke and on every switch between views.
        /// </summary>
        private SelectionSnapshot CaptureSelection()
        {
            if (_vm == null || _vm.SelectedItems.Count == 0 ||
                !string.Equals(_appliedFolder, _vm.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var items = new FileListItemViewModel[_vm.SelectedItems.Count];
            _vm.SelectedItems.CopyTo(items, 0);
            return new SelectionSnapshot { Items = items, Primary = _vm.SelectedItem };
        }

        /// <summary>Re-selects the snapshot's items in a flat list (by name, O(1) each), as index ranges in one batch.</summary>
        private static void RestoreSelection(ListBox list, VirtualizingFileList items, SelectionSnapshot snapshot)
        {
            ISelectionModel selection = list.Selection;
            if (snapshot == null || items == null)
            {
                selection.Clear();
                return;
            }

            var indices = new List<int>(snapshot.Items.Length);
            foreach (FileListItemViewModel item in snapshot.Items)
            {
                int index = items.IndexOf(item);
                if (index >= 0)
                {
                    indices.Add(index);
                }
            }

            ApplyIndices(selection, indices, snapshot.Primary != null ? items.IndexOf(snapshot.Primary) : -1);
        }

        /// <summary>Re-selects the snapshot's items among the Timeline rows (matched by path).</summary>
        private void RestoreTimelineSelection(List<TimelineRow> rows, SelectionSnapshot snapshot)
        {
            ISelectionModel selection = TimelineList.Selection;
            if (snapshot == null)
            {
                selection.Clear();
                return;
            }

            var indexByPath = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                indexByPath[rows[i].Item.FullPath] = i;
            }

            var indices = new List<int>(snapshot.Items.Length);
            foreach (FileListItemViewModel item in snapshot.Items)
            {
                if (indexByPath.TryGetValue(item.FullPath, out int index))
                {
                    indices.Add(index);
                }
            }

            int primary = snapshot.Primary != null && indexByPath.TryGetValue(snapshot.Primary.FullPath, out int p) ? p : -1;
            ApplyIndices(selection, indices, primary);
        }

        /// <summary>Selects the given indices as runs of consecutive ranges in one batch, and sets the anchor.</summary>
        private static void ApplyIndices(ISelectionModel selection, List<int> indices, int anchor)
        {
            indices.Sort();
            using (selection.BatchUpdate())
            {
                selection.Clear();
                int i = 0;
                while (i < indices.Count)
                {
                    int start = indices[i];
                    int end = start;
                    while (i + 1 < indices.Count && indices[i + 1] <= end + 1)
                    {
                        i++;
                        end = Math.Max(end, indices[i]);
                    }

                    selection.SelectRange(start, end);
                    i++;
                }

                if (anchor >= 0 && selection.IsSelected(anchor))
                {
                    selection.AnchorIndex = anchor;
                }
            }
        }

        // ------------------------------------------------------------------ Select all / Invert selection

        private void OnSelectAllRequested(object sender, EventArgs e)
        {
            switch (_surface)
            {
                case Surface.Details:
                case Surface.List:
                case Surface.Icons:
                case Surface.Tiles:
                    _activeFlatList?.SelectAll();
                    break;
                case Surface.Timeline:
                    TimelineList.SelectAll();
                    break;
                case Surface.Trash:
                    TrashSurface.SelectAll(); // Ctrl+A and the menus' Select all: the trash's own list
                    break;
                case Surface.Nexus:
                    _syncingSelection = true;
                    try
                    {
                        foreach (ListBox card in NexusLists())
                        {
                            card.SelectAll();
                        }
                    }
                    finally
                    {
                        _syncingSelection = false;
                    }

                    SyncNexusSelection();
                    break;
                case Surface.Columns:
                    // Only the focused column: each column is a different folder, so "everything" has no single meaning.
                    ListForPane(_vm?.FocusedColumnPane)?.SelectAll();
                    break;
            }
        }

        /// <summary>Select none: every list of the view lets go of its selection (the column browser's and Nexus's too).</summary>
        /// <summary>
        /// Selects items by name after a bulk rename (their old names, which the refresh carried over, are gone). The flat
        /// lists only: Timeline, Columns and Nexus keep what the refresh restored.
        /// </summary>
        private void OnSelectNamesRequested(object sender, IReadOnlyList<string> names)
        {
            if (!ReferenceEquals(sender, _vm) || names == null || !(_activeFlatList is ListBox list) || !(_activeFlatItems is VirtualizingFileList items) ||
                !ReferenceEquals(list.ItemsSource, items))
            {
                return;
            }

            var indices = new List<int>(names.Count);
            foreach (string name in names)
            {
                int index = items.IndexOfName(name);
                if (index >= 0)
                {
                    indices.Add(index);
                }
            }

            if (indices.Count == 0)
            {
                return;
            }

            int anchor = indices.Min();
            _syncingSelection = true;
            try
            {
                ApplyIndices(list.Selection, indices, anchor);
            }
            finally
            {
                _syncingSelection = false;
            }

            SyncSelectionFromList(list);
            list.ScrollIntoView(anchor);
        }

        private void OnSelectNoneRequested(object sender, EventArgs e)
        {
            _syncingSelection = true;
            try
            {
                foreach (ListBox list in this.GetVisualDescendants().OfType<ListBox>())
                {
                    if (KindOf(list) != ListKind.None)
                    {
                        list.Selection.Clear();
                    }
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            ClearViewModelSelection();
        }

        private void OnInvertSelectionRequested(object sender, EventArgs e)
        {
            switch (_surface)
            {
                case Surface.Details:
                case Surface.List:
                case Surface.Icons:
                case Surface.Tiles:
                    if (_activeFlatList != null)
                    {
                        InvertSelection(_activeFlatList);
                    }

                    break;
                case Surface.Timeline:
                    InvertSelection(TimelineList);
                    break;
                case Surface.Nexus:
                    _syncingSelection = true;
                    try
                    {
                        foreach (ListBox card in NexusLists())
                        {
                            InvertSelection(card);
                        }
                    }
                    finally
                    {
                        _syncingSelection = false;
                    }

                    SyncNexusSelection();
                    break;
                case Surface.Columns:
                    ListBox focused = ListForPane(_vm?.FocusedColumnPane);
                    if (focused != null)
                    {
                        InvertSelection(focused);
                    }

                    break;
            }
        }

        /// <summary>
        /// Selects the complement of the current selection as index ranges, so inverting a large folder costs the number
        /// of selected items, not a pass over (and the creation of) every item as the WPF version did.
        /// </summary>
        private static void InvertSelection(ListBox list)
        {
            ISelectionModel selection = list.Selection;
            int count = list.ItemCount;
            var selected = new List<int>(selection.SelectedIndexes);
            selected.Sort();
            using (selection.BatchUpdate())
            {
                selection.Clear();
                int next = 0;
                foreach (int index in selected)
                {
                    if (index > next)
                    {
                        selection.SelectRange(next, index - 1);
                    }

                    next = Math.Max(next, index + 1);
                }

                if (next < count)
                {
                    selection.SelectRange(next, count - 1);
                }
            }
        }
    }
}
