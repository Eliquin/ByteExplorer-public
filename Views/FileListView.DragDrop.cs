// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Files dropped on the pane. One set of handlers on the whole view resolves the target from the event source: a
    /// folder row/tile drops into that folder (and is highlighted while hovered), a Columns pane drops into that pane's
    /// folder, and anywhere else in a folder view drops into the current folder (the focused column in Columns). This
    /// PC, Home and error pages accept nothing.
    /// </summary>
    public partial class FileListView
    {
        private ListBoxItem _dropHighlight;

        private void InitializeDropHandling()
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOverRouted);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeaveRouted);
            AddHandler(DragDrop.DropEvent, OnDropRouted);
        }

        /// <summary>The folder a drop at <paramref name="source"/> goes into, and the folder item to highlight (if any).</summary>
        private string ResolveDropTarget(object source, out ListBoxItem folderItem)
        {
            folderItem = null;
            if (_vm == null)
            {
                return null;
            }

            for (Visual visual = source as Visual; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            {
                if (visual is ListBoxItem container && ItemOf(container.DataContext) is FileListItemViewModel item && item.IsDirectory)
                {
                    folderItem = container;
                    return item.FullPath;
                }

                if (visual is ListBox list && list.DataContext is ColumnPane pane && KindOf(list) == ListKind.Column)
                {
                    return pane.Path;
                }
            }

            switch (_surface)
            {
                case Surface.Details:
                case Surface.List:
                case Surface.Icons:
                case Surface.Tiles:
                case Surface.Timeline:
                case Surface.Columns:
                case Surface.Nexus:
                    return _vm.EffectiveTargetPath;
                default:
                    return null;
            }
        }

        private void OnDragOverRouted(object sender, DragEventArgs e)
        {
            e.Handled = true;
            string folder = ResolveDropTarget(e.Source, out ListBoxItem folderItem);
            if (folder == null || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
            {
                e.DragEffects = DragDropEffects.None;
                SetDropHighlight(null);
                return;
            }

            e.DragEffects = FileDragDropHelper.ResolveEffect(e, folder, paths);
            SetDropHighlight(e.DragEffects != DragDropEffects.None ? folderItem : null);
        }

        private void OnDragLeaveRouted(object sender, DragEventArgs e) => SetDropHighlight(null);

        private async void OnDropRouted(object sender, DragEventArgs e)
        {
            try
            {
                e.Handled = true;
                SetDropHighlight(null);
                FileListViewModel vm = _vm;
                string folder = ResolveDropTarget(e.Source, out _);
                if (vm == null || folder == null || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
                {
                    return;
                }

                // Copy or move by the keys, or (a right-button drag) Explorer's Copy here / Move here / Create shortcuts here.
                await Menus.DropChoice.RunAsync(this, e, folder, paths, vm);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Dropping files failed");
            }
        }

        /// <summary>The highlight is a style class on the container, so it follows the theme and never outlives a recycle.</summary>
        private void SetDropHighlight(ListBoxItem container)
        {
            if (ReferenceEquals(_dropHighlight, container))
            {
                return;
            }

            _dropHighlight?.Classes.Remove("dropTarget");
            _dropHighlight = container;
            _dropHighlight?.Classes.Add("dropTarget");
        }
    }
}
