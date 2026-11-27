// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.VisualTree;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Menus;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Right-click (and keyboard context-menu key / Shift+F10) menus, resolved from the event source in one handler:
    /// a list item gets the selection's menu, a This PC/Home entry gets a menu for its own path, and the empty area of a
    /// folder view gets the background menu (New, Paste, ...). The WPF build wired a separate handler per view mode.
    /// </summary>
    public partial class FileListView
    {
        private void OnContextRequestedRouted(object sender, ContextRequestedEventArgs e)
        {
            if (_vm == null)
            {
                return;
            }

            if (_rightArmedList == null && System.Environment.TickCount64 - _rightDragEndedAt < 500)
            {
                e.Handled = true; // the release that ended a right-button drag: its drop menu is the menu
                return;
            }

            if (TryGetHeaderColumn(e.Source as Visual, out ByteExplorer.Controls.DetailsColumnKind? headerColumn))
            {
                // A Details column header: hide it, choose others (Explorer's header menu).
                e.Handled = true;
                ShowColumnMenu(e.Source as Control ?? DetailsHeader, headerColumn);
                return;
            }

            for (Visual visual = e.Source as Visual; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            {
                switch (visual)
                {
                    case ScrollBar _:
                        return;
                    case ListBoxItem container when container.FindAncestorOfType<ListBox>() is ListBox owner && KindOf(owner) != ListKind.None:
                        e.Handled = true;
                        ShowItemMenu(owner, container);
                        return;
                    case ListBox list when KindOf(list) != ListKind.None:
                        e.Handled = true;
                        if (list.DataContext is ColumnPane pane)
                        {
                            _vm.OnColumnFocused(pane);
                        }

                        FileContextMenu.ShowForBackground(list, _vm);
                        return;
                    case Button button:
                        if (TryGetEntryPath(button, out string path))
                        {
                            // A path the entry stands for, not the list's selection.
                            e.Handled = true;
                            if (path != null)
                            {
                                FileContextMenu.ShowForItems(button, _vm, new[] { path }, pathsAreSelection: false);
                            }

                            return;
                        }

                        break;
                }
            }

            // Empty area outside any list (between columns or cards). Home has no folder to act on.
            if (!_vm.IsHomeView)
            {
                e.Handled = true;
                FileContextMenu.ShowForBackground(this, _vm);
            }
        }

        /// <summary>
        /// The menu for a list item: a right-click on an item outside the selection first makes it the selection (as in
        /// Explorer), then the menu acts on the whole selection.
        /// </summary>
        private void ShowItemMenu(ListBox list, ListBoxItem container)
        {
            FileListItemViewModel item = ItemOf(container.DataContext);
            if (item == null)
            {
                return;
            }

            if (!container.IsSelected)
            {
                int index = list.IndexFromContainer(container);
                if (index >= 0)
                {
                    if (KindOf(list) == ListKind.Nexus)
                    {
                        ClearOtherNexusLists(list);
                    }

                    ISelectionModel selection = list.Selection;
                    using (selection.BatchUpdate())
                    {
                        selection.Clear();
                        selection.Select(index);
                        selection.AnchorIndex = index;
                    }
                }

                container.Focus(NavigationMethod.Pointer);
            }

            List<string> paths = SelectedPaths();
            if (paths.Count > 0)
            {
                FileContextMenu.ShowForItems(container, _vm, paths, pathsAreSelection: true);
            }
            else
            {
                FileContextMenu.ShowForItems(container, _vm, new[] { item.FullPath }, pathsAreSelection: false);
            }
        }

        /// <summary>
        /// True when the button is a This PC or Home entry; <paramref name="path"/> is what its menu acts on, or null for
        /// an entry with no menu: a drive that is not responding (asking it for anything could block, as in the WPF build).
        /// </summary>
        private static bool TryGetEntryPath(Button button, out string path)
        {
            switch (button.DataContext)
            {
                case ThisPcDriveItem drive:
                    path = drive.IsUnreachable || string.IsNullOrEmpty(drive.RootPath) ? null : drive.RootPath;
                    return true;
                case ThisPcRecentItem recent:
                    path = string.IsNullOrEmpty(recent.FullPath) ? null : recent.FullPath;
                    return true;
                case FavoriteFolderRowViewModel folder:
                    path = string.IsNullOrEmpty(folder.FullPath) ? null : folder.FullPath;
                    return true;
                case RecentFileRowViewModel file:
                    path = string.IsNullOrEmpty(file.FullPath) ? null : file.FullPath;
                    return true;
            }

            path = ResolveQuickFolder(button.Tag as string);
            return path != null;
        }
    }
}
