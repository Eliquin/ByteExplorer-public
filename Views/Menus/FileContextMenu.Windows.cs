// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Shell;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views.Menus
{
    /// <summary>
    /// The Windows shell menu's half of the right-click menu (Settings › Folder options › Right-click menu):
    /// <list type="bullet">
    /// <item><b>ByteExplorer</b>: the app's menu only.</item>
    /// <item><b>Windows</b>: the shell's menu (<see cref="ShellContextMenu"/>), with every program's entries.</item>
    /// <item><b>Hybrid</b> (default on Windows): the app's menu, ending in "Show more options", which opens the shell's
    /// menu at the same place, as Windows 11 does; Shift+right-click opens the shell's menu directly.</item>
    /// </list>
    /// From the shell's menu, the entries the app does better are done by the app: Rename (the shell's only works inside
    /// Explorer's view), Delete (undoable, in the operation queue; Shift: permanently), Cut/Copy (the cut items dim),
    /// Paste (the queue, with the name-clash dialog), Undo, opening a folder (in this pane, or a tab, not another window),
    /// Refresh. Everything else (Send to, Open with, Properties, Share, 7-Zip, Git…) is the shell's. Where the shell
    /// can't make a menu (items of different folders, inside an archive, a phone, a protected item, Linux and macOS),
    /// the app's menu is shown.
    /// </summary>
    public static partial class FileContextMenu
    {
        /// <summary>The menu style in effect: the setting on Windows, the app's menu elsewhere.</summary>
        internal static string MenuStyle =>
            ShellContextMenu.IsSupported ? FolderOptions.Current?.ContextMenu ?? FolderOptionsSettings.ContextMenuHybrid : FolderOptionsSettings.ContextMenuByteExplorer;

        /// <summary>Whether a right-click opens the shell's menu instead of the app's (Windows style, or Shift in Hybrid).</summary>
        private static bool WantsShellMenu() =>
            MenuStyle == FolderOptionsSettings.ContextMenuWindows ||
            (MenuStyle == FolderOptionsSettings.ContextMenuHybrid && ShellContextMenu.IsShiftDown());

        /// <summary>
        /// For the views that build their own menu around the file menu (search results, Lens, the tree): opens the shell's
        /// menu instead when the style asks for it and the shell can make one. True when it did.
        /// </summary>
        public static bool TryShowShellMenu(Control anchor, FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            if (anchor == null || vm == null || !WantsShellMenu() || !ShellContextMenu.CanShowFor(paths))
            {
                return false;
            }

            return ShowShellForItems(anchor, vm, paths, pathsAreSelection, MenuPoint(anchor));
        }

        private static bool TryShowShellBackground(Control anchor, FileListViewModel vm, string folder)
        {
            if (!WantsShellMenu() || !ShellContextMenu.CanShowForBackground(folder))
            {
                return false;
            }

            return ShowShellForBackground(anchor, vm, folder, MenuPoint(anchor));
        }

        private static bool ShowShellForItems(Control anchor, FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection, PixelPoint at) =>
            TopLevel.GetTopLevel(anchor) is TopLevel owner &&
            ShellContextMenu.ShowForItems(owner, paths, at, verb => RunItemVerb(vm, paths, pathsAreSelection, verb));

        private static bool ShowShellForBackground(Control anchor, FileListViewModel vm, string folder, PixelPoint at) =>
            TopLevel.GetTopLevel(anchor) is TopLevel owner &&
            ShellContextMenu.ShowForBackground(owner, folder, at, verb => RunBackgroundVerb(vm, folder, verb));

        /// <summary>Hybrid: "Show more options" at the end of the app's item menu (none where the shell can't make a menu).</summary>
        private static void AddShowMoreOptions(List<Control> items, Control anchor, FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            if (MenuStyle != FolderOptionsSettings.ContextMenuHybrid || anchor == null || !ShellContextMenu.CanShowFor(paths))
            {
                return;
            }

            PixelPoint at = MenuPoint(anchor); // where the app's menu opened: the shell's opens there too
            items.Add(new Separator());
            items.Add(ShowMoreItem(() => ShowShellForItems(anchor, vm, paths, pathsAreSelection, at)));
        }

        private static void AddShowMoreOptionsForBackground(List<Control> items, Control anchor, FileListViewModel vm, string folder)
        {
            if (MenuStyle != FolderOptionsSettings.ContextMenuHybrid || anchor == null || !ShellContextMenu.CanShowForBackground(folder))
            {
                return;
            }

            PixelPoint at = MenuPoint(anchor);
            items.Add(new Separator());
            items.Add(ShowMoreItem(() => ShowShellForBackground(anchor, vm, folder, at)));
        }

        private static MenuItem ShowMoreItem(Func<bool> show)
        {
            var item = new MenuItem { Header = "Show more options", Icon = new SymbolIcon { Symbol = Symbol.MoreHorizontal, FontSize = 16 } };
            ToolTip.SetTip(item, "The Windows menu, with every program's entries (or Shift+right-click)");
            // After the app's menu has closed: the shell's menu is modal and must not open inside the click.
            item.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    show();
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Show more options failed");
                }
            }, DispatcherPriority.Background);
            return item;
        }

        /// <summary>
        /// Where a menu opens: the pointer, when it is over the anchor's window (a right-click); otherwise (the menu key,
        /// Shift+F10) the anchor's left edge, as Explorer places a keyboard-opened menu.
        /// </summary>
        private static PixelPoint MenuPoint(Control anchor)
        {
            PixelPoint cursor = ShellContextMenu.CursorPosition();
            if (TopLevel.GetTopLevel(anchor) is TopLevel top)
            {
                PixelPoint topLeft = top.PointToScreen(default);
                PixelPoint bottomRight = top.PointToScreen(new Point(top.Bounds.Width, top.Bounds.Height));
                if (cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y)
                {
                    return cursor;
                }
            }

            return anchor.PointToScreen(new Point(Math.Min(24, anchor.Bounds.Width / 2), anchor.Bounds.Height / 2));
        }

        /// <summary>
        /// A verb chosen in the shell's item menu that the app runs itself; false lets the shell run it. Internal for the
        /// tests.
        /// </summary>
        internal static bool RunItemVerb(FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection, string verb)
        {
            if (string.IsNullOrEmpty(verb) || vm == null || paths == null || paths.Count == 0)
            {
                return false;
            }

            string first = paths[0];
            bool allFolders = paths.All(Directory.Exists);
            switch (verb.ToLowerInvariant())
            {
                case "rename":
                    if (pathsAreSelection)
                    {
                        Execute(vm.RenameCommand);
                    }
                    else
                    {
                        Fire(vm, vm.PromptRenamePathAsync(first));
                    }

                    return true;
                case "delete":
                    Fire(vm, vm.DeletePathsAsync(paths, permanent: ShellContextMenu.IsShiftDown()));
                    return true;
                case "cut":
                    if (pathsAreSelection)
                    {
                        Execute(vm.CutCommand);
                    }
                    else
                    {
                        ClipboardFileService.SetFilesForCut(paths);
                    }

                    return true;
                case "copy":
                    if (pathsAreSelection)
                    {
                        Execute(vm.CopyCommand);
                    }
                    else
                    {
                        ClipboardFileService.SetFilesForCopy(paths);
                    }

                    return true;
                case "paste":
                    if (paths.Count == 1 && allFolders)
                    {
                        PasteIntoAsync(vm, first).Forget("paste into folder");
                        return true;
                    }

                    return false;
                case "open":
                case "explore":
                    OpenAsync(vm, paths).Forget("open from the Windows menu");
                    return true;
                case "opennewwindow":
                case "opennewprocess":
                    if (allFolders && vm.OpenInNewWindows != null)
                    {
                        // A window of this process: a new process would build the search index and write the settings
                        // again ("new process" included: one ByteExplorer process serves every window).
                        vm.OpenInNewWindows(paths.ToList()).Forget("open in new window");
                        return true;
                    }

                    goto case "opennewtab"; // no window to open one from (a pane without its window): a tab
                case "opennewtab":
                    if (allFolders && vm.OpenInNewTabs != null)
                    {
                        vm.OpenInNewTabs(paths.ToList(), true).Forget("open in new tab");
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        /// <summary>A verb chosen in the shell's background menu that the app runs itself; false lets the shell run it.</summary>
        internal static bool RunBackgroundVerb(FileListViewModel vm, string folder, string verb)
        {
            if (string.IsNullOrEmpty(verb) || vm == null)
            {
                return false;
            }

            switch (verb.ToLowerInvariant())
            {
                case "paste":
                    if (PathUtil.AreSame(folder, vm.EffectiveTargetPath ?? vm.CurrentPath))
                    {
                        Execute(vm.PasteCommand);
                    }
                    else
                    {
                        PasteIntoAsync(vm, folder).Forget("paste into folder");
                    }

                    return true;
                case "refresh":
                    Execute(vm.RefreshCommand);
                    return true;
                case "newfolder":
                    Execute(vm.NewFolderCommand); // the app's: named at once, in place
                    return true;
                case "undo":
                    Execute(vm.Operations?.UndoCommand);
                    return true;
                case "redo":
                    Execute(vm.Operations?.RedoCommand);
                    return true;
                default:
                    return false;
            }
        }
    }
}
