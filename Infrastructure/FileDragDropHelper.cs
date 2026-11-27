// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Shared file drag-and-drop logic for every pane, on Avalonia's cross-platform drag and drop (OLE on Windows, XDND
    /// on X11, NSDragging on macOS). Files travel as storage items (DataFormats.Files), which every platform backend maps
    /// to its native file list, so drags work to and from Explorer, Finder, GNOME Files, Dolphin and the rest.
    /// </summary>
    public static class FileDragDropHelper
    {
        /// <summary>
        /// Pointer travel before a press becomes a drag. Avalonia has no SystemParameters.MinimumDragDistance; 4 device-
        /// independent pixels is the Windows default (SM_CXDRAG) and matches GTK's 8 physical pixels at 2x scale.
        /// </summary>
        public const double DragThreshold = 4;

        public static bool HasMovedPastDragThreshold(Point start, Point current) =>
            Math.Abs(current.X - start.X) >= DragThreshold || Math.Abs(current.Y - start.Y) >= DragThreshold;

        /// <summary>
        /// Starts a drag of the paths from the pointer event that crossed the threshold. Resolves the storage items for
        /// the paths through the top level's storage provider, offers Copy and Move, and returns the effect the target
        /// chose (None when cancelled). Never throws.
        /// </summary>
        /// <summary>
        /// True while a drag started with the right mouse button (in this app) is under way: where it is dropped, a menu
        /// asks what to do (Copy here, Move here, Create shortcuts here, Cancel), as in Explorer. Read it at the start
        /// of a drop handler: it is cleared once the drag ends, which is when the handler's first await lets go.
        /// </summary>
        public static bool IsRightButtonDrag { get; private set; }

        /// <summary>Tests: drops behave as after a right-button drag until disposed (a real drag can't run headless).</summary>
        internal static IDisposable SimulateRightButtonDragForTests()
        {
            IsRightButtonDrag = true;
            return new EndSimulation();
        }

        private sealed class EndSimulation : IDisposable
        {
            public void Dispose() => IsRightButtonDrag = false;
        }

        public static async Task<DragDropEffects> BeginDragAsync(PointerEventArgs trigger, IStorageProvider storage, IReadOnlyList<string> paths, ILogger logger,
            bool rightButton = false)
        {
            ILogger log = logger ?? Log.Logger;
            if (trigger == null || storage == null || paths == null || paths.Count == 0)
            {
                return DragDropEffects.None;
            }

            try
            {
                using DataTransfer data = await ClipboardFileService.BuildTransferAsync(paths, isCut: false, storage);
                if (!((IDataTransfer)data).Contains(DataFormat.File) && !((IDataTransfer)data).Contains(ClipboardFileService.AppPathsFormat))
                {
                    log.Debug("BeginDrag: none of the {Count} path(s) could be resolved", paths.Count);
                    return DragDropEffects.None;
                }

                IsRightButtonDrag = rightButton;
                return await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move | (rightButton ? DragDropEffects.Link : 0));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to start a file drag operation");
                return DragDropEffects.None;
            }
            finally
            {
                IsRightButtonDrag = false;
            }
        }

        /// <summary>The local paths of the files being dragged, or false if the drag carries no files.</summary>
        public static bool TryGetDroppedPaths(IDataTransfer data, out IReadOnlyList<string> paths)
        {
            paths = null;
            if (data != null && data.Contains(ClipboardFileService.AppPathsFormat))
            {
                // From one of ByteExplorer's own windows: the exact paths, phone items included.
                List<string> own = ClipboardFileService.SplitPaths(data.TryGetValue(ClipboardFileService.AppPathsFormat));
                if (own.Count > 0)
                {
                    paths = own;
                    return true;
                }
            }

            if (data == null || !data.Contains(DataFormat.File))
            {
                return false;
            }

            IStorageItem[] items = data.TryGetFiles();
            if (items == null || items.Length == 0)
            {
                return false;
            }

            var result = new List<string>(items.Length);
            foreach (IStorageItem item in items)
            {
                string local = item?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(local))
                {
                    result.Add(local);
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            paths = result;
            return true;
        }

        /// <summary>
        /// The effect a drop would have, with the file managers' shared rules: Ctrl copies, Shift moves, and with
        /// neither, a drop on the same volume moves and a drop on another volume copies. None for an invalid target.
        /// (macOS uses Option for copy; Avalonia reports it as Alt, so Alt also means copy.)
        /// </summary>
        public static DragDropEffects ResolveEffect(DragEventArgs e, string destinationFolder, IReadOnlyList<string> sourcePaths)
        {
            KeyModifiers modifiers = e.KeyModifiers;
            bool copy = (modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
            bool move = (modifiers & KeyModifiers.Shift) != 0;

            if (IsRightButtonDrag && !copy && !move)
            {
                // A right-button drag: the drop asks (Copy here / Move here / Create shortcuts here), so the items' own
                // folder is a target too (a copy there duplicates them); the effect shown is what the menu offers first.
                if (!IsValidDropTarget(destinationFolder, sourcePaths, allowSameFolder: true))
                {
                    return DragDropEffects.None;
                }

                bool sameFolder = sourcePaths.All(p => PathUtil.AreSame(PathUtil.GetParent(PathUtil.Normalize(p)), PathUtil.Normalize(destinationFolder)));
                DragDropEffects offered = sameFolder || !IsSameVolume(destinationFolder, sourcePaths) ? DragDropEffects.Copy : DragDropEffects.Move;
                return (e.DragEffects & offered) != 0 ? offered : (e.DragEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            }

            // Ctrl+drag within the items' own folder duplicates them ("name - Copy"), as in Explorer; a plain drag there
            // does nothing.
            if (!IsValidDropTarget(destinationFolder, sourcePaths, allowSameFolder: copy && !move))
            {
                return DragDropEffects.None;
            }

            if (copy && !move && sourcePaths.All(p => PathUtil.AreSame(PathUtil.GetParent(PathUtil.Normalize(p)), PathUtil.Normalize(destinationFolder))))
            {
                return (e.DragEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            }

            DragDropEffects wanted;
            if (copy && !move)
            {
                wanted = DragDropEffects.Copy;
            }
            else if (move && !copy)
            {
                wanted = DragDropEffects.Move;
            }
            else
            {
                wanted = IsSameVolume(destinationFolder, sourcePaths) ? DragDropEffects.Move : DragDropEffects.Copy;
            }

            // Respect what the source allows (a read-only source offers only Copy).
            if ((e.DragEffects & wanted) != 0)
            {
                return wanted;
            }

            return (e.DragEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private static bool IsSameVolume(string destinationFolder, IReadOnlyList<string> sourcePaths)
        {
            if (Core.Platform.Phones.Phones.IsPhonePath(destinationFolder) || sourcePaths.Any(Core.Platform.Phones.Phones.IsPhonePath))
            {
                // A phone and a drive are never the same volume: copy by default (Explorer's rule); within one phone, move.
                return sourcePaths.All(p => Core.Platform.Phones.Phones.AreOnSamePhone(p, destinationFolder));
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationFolder));
                    foreach (string source in sourcePaths)
                    {
                        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), destinationRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                string destinationMount = MountPoints.Of(destinationFolder);
                foreach (string source in sourcePaths)
                {
                    if (!string.Equals(MountPoints.Of(source), destinationMount, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException)
            {
                // Copy is the safe default when the volumes cannot be compared: it never removes the source.
                return false;
            }
        }

        /// <summary>
        /// False when the drop would do nothing or something impossible: dropping items into the folder they are already
        /// in, a folder onto itself, or a folder into one of its own subfolders.
        /// </summary>
        /// <summary>
        /// What a drop tells the program the files were dragged from, once ByteExplorer takes them. A move is done by
        /// ByteExplorer itself (queued, verified, undoable), so the source is told "copy": told "move", Explorer, Nautilus and
        /// others delete the originals themselves as soon as the drop returns, while the queued move may still be waiting,
        /// fail, or be cancelled. The cursor during the drag still says Move.
        /// </summary>
        public static DragDropEffects EffectToReport(DragDropEffects performed) =>
            performed == DragDropEffects.Move ? DragDropEffects.Copy : performed;

        /// <param name="allowSameFolder">Dropping into the folder the items are in is allowed (a copy duplicates them).</param>
        public static bool IsValidDropTarget(string destinationFolder, IReadOnlyList<string> sourcePaths, bool allowSameFolder = false)
        {
            if (string.IsNullOrEmpty(destinationFolder) || sourcePaths == null || sourcePaths.Count == 0)
            {
                return false;
            }

            if (Core.Services.ArchiveBrowser.IsInsideArchive(destinationFolder))
            {
                return false; // archives are browsed read-only
            }

            string destination = PathUtil.Normalize(destinationFolder);
            foreach (string source in sourcePaths)
            {
                if (string.IsNullOrEmpty(source))
                {
                    continue;
                }

                string normalizedSource = PathUtil.Normalize(source);
                string parent = PathUtil.GetParent(normalizedSource);
                if (parent != null && PathUtil.AreSame(parent, destination) && !allowSameFolder)
                {
                    return false; // already in this folder
                }

                if (PathUtil.IsSameOrInside(destination, normalizedSource))
                {
                    return false; // onto itself or into its own subfolder
                }
            }

            return true;
        }
    }
}
