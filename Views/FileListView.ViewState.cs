// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Where the person was in each folder. Leaving a folder saves its scroll position and selection; Back/Forward to it
    /// restore both, and going up to the parent selects the folder just left and brings it into view, as Explorer and
    /// Finder do. Kept for the last <see cref="MaxSavedFolders"/> folders, per pane; selection is saved by name, so it
    /// survives the folder being re-read (items added, removed, re-sorted).
    /// <para>
    /// The memory belongs to the pane (its view model), not to the view: one view shows every tab's pane in turn, and each
    /// tab keeps its own folders' states, plus where its list was scrolled when another tab was selected (selection lives
    /// in the view model already), so coming back to a tab shows it exactly as it was left, as browsers and Explorer do.
    /// </para>
    /// </summary>
    public partial class FileListView
    {
        private const int MaxSavedFolders = 64;
        private const int MaxSavedSelection = 2000;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FileListViewModel, PaneMemory> Memories =
            new System.Runtime.CompilerServices.ConditionalWeakTable<FileListViewModel, PaneMemory>();

        private PaneMemory _memory = new PaneMemory();

        /// <summary>A pane's view memory (kept with the view model, collected with it).</summary>
        private sealed class PaneMemory
        {
            public readonly Dictionary<string, SavedFolderState> States = new Dictionary<string, SavedFolderState>(PathUtil.Comparer);
            public readonly LinkedList<string> Order = new LinkedList<string>();

            /// <summary>Where the list was when the view moved to another pane (another tab was selected).</summary>
            public string DetachedFolder;
            public double DetachedOffsetY;
        }

        /// <summary>The view is about to show another pane: the one it showed keeps where its list was scrolled.</summary>
        private void RememberDetachedPane()
        {
            if (_vm == null)
            {
                return;
            }

            ScrollViewer scroller = _activeFlatList?.Scroll as ScrollViewer;
            _memory.DetachedFolder = scroller != null ? _appliedFolder : null;
            _memory.DetachedOffsetY = scroller?.Offset.Y ?? 0;
        }

        /// <summary>The view shows <paramref name="viewModel"/> now: its memory comes with it.</summary>
        private void TakePaneMemory(FileListViewModel viewModel)
        {
            _memory = viewModel != null ? Memories.GetValue(viewModel, _ => new PaneMemory()) : new PaneMemory();

            // The pane's own folder counts as already shown: its selection (kept by the view model) is put back on the list
            // rather than read from the fresh list, which would clear it.
            _appliedFolder = viewModel?.CurrentPath;
        }

        /// <summary>After showing a pane it showed before, scrolls its list back to where it was left.</summary>
        private void RestoreDetachedScroll()
        {
            string folder = _memory.DetachedFolder;
            double offset = _memory.DetachedOffsetY;
            _memory.DetachedFolder = null;
            ListBox list = _activeFlatList;
            if (folder == null || list == null || _vm == null || !PathUtil.AreSame(folder, _vm.CurrentPath) || offset <= 0)
            {
                return;
            }

            object items = list.ItemsSource;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(list, _activeFlatList) && ReferenceEquals(list.ItemsSource, items) && list.Scroll is ScrollViewer scroller)
                {
                    scroller.Offset = new Vector(scroller.Offset.X, Math.Min(offset, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
                }
            }, DispatcherPriority.Loaded);
        }

        private sealed class SavedFolderState
        {
            public double OffsetY;
            public string[] SelectedNames;
            public string AnchorName;
        }

        /// <summary>Saves the state of the folder the list shows now (called before it shows another folder).</summary>
        private void SaveFolderState(ListBox list, string folder)
        {
            if (list == null || string.IsNullOrEmpty(folder) || !(list.ItemsSource is VirtualizingFileList items))
            {
                return;
            }

            ScrollViewer scroller = list.Scroll as ScrollViewer;
            IReadOnlyList<int> selected = list.Selection.SelectedIndexes;
            var names = new List<string>(Math.Min(selected.Count, MaxSavedSelection));
            foreach (int index in selected)
            {
                if (names.Count == MaxSavedSelection)
                {
                    break;
                }

                string name = items.NameAt(index);
                if (name != null)
                {
                    names.Add(name);
                }
            }

            var state = new SavedFolderState
            {
                OffsetY = scroller?.Offset.Y ?? 0,
                SelectedNames = names.ToArray(),
                AnchorName = list.Selection.AnchorIndex >= 0 ? items.NameAt(list.Selection.AnchorIndex) : null
            };

            if (_memory.States.ContainsKey(folder))
            {
                _memory.Order.Remove(folder);
            }

            _memory.States[folder] = state;
            _memory.Order.AddLast(folder);
            while (_memory.Order.Count > MaxSavedFolders)
            {
                _memory.States.Remove(_memory.Order.First.Value);
                _memory.Order.RemoveFirst();
            }
        }

        /// <summary>
        /// After a new folder's items are in the list: select a file being revealed, restore the folder's saved state on
        /// Back/Forward, or select the child the person came up from. Runs after layout so the scroll extent is known.
        /// </summary>
        private void RestoreFolderState(ListBox list, string folder, string previousFolder, bool historyNavigation)
        {
            if (list == null || string.IsNullOrEmpty(folder) || !(list.ItemsSource is VirtualizingFileList items))
            {
                return;
            }

            SavedFolderState state = null;
            // A file asked to be shown wins over everything else. Taken only once it is in the list: a list that arrives
            // before the folder's items are all in (empty or stale while loading) would otherwise use the request up, and
            // nothing would be selected when the real list comes.
            string revealed = _vm?.PeekRevealName(folder);
            string cameFrom = revealed != null && items.IndexOfName(revealed) >= 0 ? _vm.TakeRevealName(folder) : null;
            if (revealed != null && cameFrom == null)
            {
                return; // the request waits for the next list of this folder
            }
            if (cameFrom == null && historyNavigation)
            {
                _memory.States.TryGetValue(folder, out state);
            }

            if (cameFrom == null && state == null && previousFolder != null && PathUtil.AreSame(PathUtil.GetParent(previousFolder), folder))
            {
                cameFrom = PathUtil.GetName(previousFolder);
            }

            if (state == null && cameFrom == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(list.ItemsSource, items))
                {
                    // A refresh of the same folder (live change, F5) replaced the list meanwhile: apply to the new one.
                    // Anything else means the person navigated again.
                    if (list.ItemsSource is VirtualizingFileList refreshed && PathUtil.AreSame(_vm?.CurrentPath, folder))
                    {
                        items = refreshed;
                    }
                    else
                    {
                        return;
                    }
                }

                int anchor;
                _syncingSelection = true;
                try
                {
                    list.Selection.BeginBatchUpdate();
                    list.Selection.Clear();
                    if (state != null)
                    {
                        foreach (string name in state.SelectedNames)
                        {
                            int index = items.IndexOfName(name);
                            if (index >= 0)
                            {
                                list.Selection.Select(index);
                            }
                        }

                        anchor = items.IndexOfName(state.AnchorName);
                    }
                    else
                    {
                        anchor = items.IndexOfName(cameFrom);
                        if (anchor >= 0)
                        {
                            list.Selection.Select(anchor);
                        }
                    }

                    if (anchor >= 0)
                    {
                        list.Selection.AnchorIndex = anchor;
                    }

                    list.Selection.EndBatchUpdate();
                }
                finally
                {
                    _syncingSelection = false;
                }

                SyncSelectionFromList(list);

                if (state != null && list.Scroll is ScrollViewer scroller)
                {
                    scroller.Offset = new Vector(scroller.Offset.X, Math.Min(state.OffsetY, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
                }
                else if (anchor >= 0)
                {
                    list.ScrollIntoView(anchor);
                }

                if (anchor >= 0 && list.IsKeyboardFocusWithin)
                {
                    list.ContainerFromIndex(anchor)?.Focus(NavigationMethod.Directional);
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
