// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Icons;
using Serilog;

namespace ByteExplorer.Views.Menus
{
    /// <summary>
    /// The app's own right-click menus, identical on Windows, Linux and macOS. They replace the Windows shell context
    /// menu (IContextMenu) of the WPF build, which has no counterpart on other systems.
    /// <para>
    /// Two menus: <see cref="ForItems"/> for one or more files/folders (the list's selection, a tree node, a sidebar
    /// folder, a drive) and <see cref="ForBackground"/> for the empty area of a folder. Menus are built on demand when
    /// opened, from what is true at that moment (clipboard contents, single vs. multiple, file vs. folder, zip or not),
    /// so nothing stale is ever shown and nothing is kept alive between openings. Every action runs through the file
    /// list view model (the same code as the toolbar and keyboard shortcuts) and is wrapped so a failure is logged and
    /// reported, never thrown into the UI loop.
    /// </para>
    /// <para>
    /// Items of the list's selection use the view model's selection commands (Cut, Copy, Rename, Delete); items that are
    /// not the selection (a tree node) use the path-based operations, so right-clicking a folder in the tree never acts
    /// on whatever happens to be selected in the list.
    /// </para>
    /// </summary>
    public static partial class FileContextMenu
    {
        /// <summary>
        /// Opens the menu for <paramref name="paths"/> at the pointer. <paramref name="pathsAreSelection"/> is true when the
        /// paths are exactly the list's current selection.
        /// </summary>
        public static void ShowForItems(Control anchor, FileListViewModel viewModel, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            if (anchor == null || viewModel == null || paths == null || paths.Count == 0)
            {
                return;
            }

            // The Windows menu instead (Right-click menu: Windows, or Shift+right-click in Hybrid), when the shell can make one.
            if (TryShowShellMenu(anchor, viewModel, paths, pathsAreSelection))
            {
                return;
            }

            Open(anchor, ForItems(anchor, viewModel, paths, pathsAreSelection));
        }

        /// <summary>Opens the menu for the empty area of the current folder at the pointer.</summary>
        public static void ShowForBackground(Control anchor, FileListViewModel viewModel)
        {
            if (anchor == null || viewModel == null || string.IsNullOrEmpty(viewModel.CurrentPath))
            {
                return;
            }

            if (TryShowShellBackground(anchor, viewModel, viewModel.EffectiveTargetPath ?? viewModel.CurrentPath))
            {
                return;
            }

            Open(anchor, ForBackground(viewModel, anchor));
        }

        public static ContextMenu ForItems(Control anchor, FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            if (ArchiveBrowser.IsInsideArchive(PathUtil.GetParent(paths[0])))
            {
                return ForArchiveItems(vm, paths, pathsAreSelection);
            }

            if (Core.Platform.Phones.Phones.IsPhonePath(paths[0]))
            {
                return ForPhoneItems(anchor, vm, paths, pathsAreSelection);
            }

            bool single = paths.Count == 1;
            string first = paths[0];
            // The selection already knows which items are folders: asking the disk again for each one made the menu wait
            // on a large selection or a slow network share. A tree folder (not a selection) is one quick check.
            Func<string, bool> isFolder = pathsAreSelection ? FolderLookup(vm) : Directory.Exists;
            bool firstIsFolder = isFolder(first);
            bool allFolders = paths.All(isFolder);
            var items = new List<Control>();

            // ----- Open -----
            var open = Item(single && firstIsFolder ? "Open" : single ? "Open" : $"Open {paths.Count} items", Symbol.Open, () => OpenAsync(vm, paths), "Enter");
            open.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            items.Add(open);

            if (single && !firstIsFolder)
            {
                items.Add(BuildOpenWith(anchor, vm, first));
            }

            AddOpenInNewTab(items, vm, allFolders ? paths : null);

            if (single && firstIsFolder)
            {
                items.Add(Item("Open in terminal", Symbol.WindowConsole, () => Run(vm, () => ShellLauncher.OpenTerminal(first), "Couldn't open a terminal here.")));
            }

            items.Add(Item(OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in Windows Explorer" : "Show in system file manager",
                Symbol.FolderOpen, () => Run(vm, () => ShellLauncher.RevealInSystemFileManager(first), "Couldn't open the system file manager.")));

            if (vm.Pins != null && allFolders)
            {
                items.Add(PinItem(vm, paths));
            }

            AddCalculateSize(items, vm, paths.Where(Directory.Exists).ToList(), pathsAreSelection);

            items.Add(new Separator());

            // ----- Clipboard -----
            items.Add(Item("Cut", Symbol.Cut, () =>
            {
                if (pathsAreSelection)
                {
                    Execute(vm.CutCommand);
                }
                else
                {
                    ClipboardFileService.SetFilesForCut(paths);
                }
            }, Gesture("X")));
            items.Add(Item("Copy", Symbol.Copy, () =>
            {
                if (pathsAreSelection)
                {
                    Execute(vm.CopyCommand);
                }
                else
                {
                    ClipboardFileService.SetFilesForCopy(paths);
                }
            }, Gesture("C")));

            if (single && firstIsFolder)
            {
                var paste = Item("Paste into folder", Symbol.ClipboardPaste, () => PasteIntoAsync(vm, first));
                paste.IsEnabled = ClipboardFileService.HasFiles();
                items.Add(paste);
            }

            items.Add(Item("Copy as path", Symbol.Link, () =>
                ClipboardFileService.SetText(string.Join(Environment.NewLine, paths.Select(p => paths.Count == 1 ? p : "\"" + p + "\""))),
                OperatingSystem.IsWindows() ? "Ctrl+Shift+C" : null));

            items.Add(new Separator());

            // ----- Organize -----
            if (single || (pathsAreSelection && vm.SelectedItems.Count > 1))
            {
                // Several selected items: "Rename…" opens the Rename items dialog (bulk rename).
                var rename = Item(single ? "Rename" : "Rename…", Symbol.Rename, () =>
                {
                    if (pathsAreSelection)
                    {
                        Execute(vm.RenameCommand);
                    }
                    else
                    {
                        Fire(vm, vm.PromptRenamePathAsync(first)); // a tree folder: asked in a dialog
                    }
                }, "F2");
                rename.IsEnabled = pathsAreSelection || PathUtil.GetParent(first) != null;
                items.Add(rename);
            }

            if (single && !firstIsFolder)
            {
                AddExtractItems(items, anchor, vm, first);
            }

            items.Add(Item(single ? "Compress to ZIP file" : $"Compress {paths.Count} items to ZIP file", Symbol.FolderZip, () => Fire(vm, vm.CompressToZipAsync(paths))));

            MenuItem password = BuildPasswordMenu(vm, paths);
            if (password != null)
            {
                items.Add(password);
            }

            items.Add(new Separator());

            var delete = Item(OperatingSystem.IsWindows() ? "Delete" : "Move to Trash", Symbol.Delete, () => Fire(vm, vm.DeletePathsAsync(paths, permanent: false)), "Delete");
            items.Add(delete);

            var deletePermanent = Item("Delete permanently", Symbol.DeleteDismiss, () => Fire(vm, vm.DeletePathsAsync(paths, permanent: true)), "Shift+Delete");
            deletePermanent.Classes.Add("danger");
            items.Add(deletePermanent);

            items.Add(new Separator());

            // Tags: the quick way (check tags here) and the detailed one (the tag editor, with a comment).
            items.Add(Tags.TagActions.BuildItemsMenu(paths, Core.Tags.TagStore.Default, vm.DialogService, () => TopLevel.GetTopLevel(anchor) as Window));

            items.Add(Item("Properties", Symbol.Info, () => vm.DialogService?.ShowProperties(paths), "Alt+Enter"));
            AddShowMoreOptions(items, anchor, vm, paths, pathsAreSelection);

            return new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        }

        /// <summary>Whether a selected item is a folder, from what the list already knows (the disk for anything else).</summary>
        private static Func<string, bool> FolderLookup(FileListViewModel vm)
        {
            var known = new Dictionary<string, bool>(PathUtil.Comparer);
            foreach (FileListItemViewModel item in vm.SelectedItems)
            {
                known[item.FullPath] = item.IsDirectory;
            }

            return path => known.TryGetValue(path, out bool folder) ? folder : Directory.Exists(path);
        }

        /// <summary>
        /// The Password submenu: for one protected item (.bxlock), Open, Lock (when unlocked), Change password and Remove
        /// protection; for ordinary files and folders, "Protect with password…". Null when neither applies (a mix of
        /// protected and ordinary items, or items that can't be protected, such as a drive).
        /// </summary>
        internal static MenuItem BuildPasswordMenu(FileListViewModel vm, IReadOnlyList<string> paths)
        {
            if (vm.DialogService == null || paths.Count == 0)
            {
                return null;
            }

            var menu = new MenuItem { Header = "Password", Icon = Icon(Symbol.Password) };
            var children = new List<Control>();
            if (paths.Count == 1 && FileListViewModel.IsProtectedItem(paths[0]))
            {
                string path = paths[0];
                bool unlocked = Core.Security.ProtectedItems.IsUnlocked(path);
                var open = Item(unlocked ? "Open" : "Open with password…", Symbol.LockOpen, () => Fire(vm, vm.OpenProtectedAsync(path)));
                open.FontWeight = Avalonia.Media.FontWeight.SemiBold;
                children.Add(open);
                if (unlocked)
                {
                    var lockItem = Item("Lock now", Symbol.LockClosed, () => vm.LockProtected(path));
                    ToolTip.SetTip(lockItem, "Forget the password until it is typed again, and delete the files opened from it");
                    children.Add(lockItem);
                }

                children.Add(new Separator());
                children.Add(Item("Change password…", Symbol.KeyReset, () => Fire(vm, vm.ChangeProtectedPasswordAsync(path))));
                var remove = Item("Remove protection…", Symbol.LockOpen, () => Fire(vm, vm.RemoveProtectionAsync(path)));
                ToolTip.SetTip(remove, "Decrypt it back into the original file or folder");
                children.Add(remove);
            }
            else if (paths.Any(Core.Security.ProtectedFormat.IsProtectedFile))
            {
                return null;
            }
            else
            {
                var protect = Item(paths.Count == 1 ? "Protect with password…" : $"Protect {paths.Count} items with password…", Symbol.LockClosed,
                    () => Fire(vm, vm.ProtectAsync(paths)));
                string reason = paths.Select(Core.Security.ProtectedItems.CannotProtectReason).FirstOrDefault(r => r != null);
                if (reason != null)
                {
                    protect.IsEnabled = false;
                    ToolTip.SetTip(protect, reason);
                }
                else
                {
                    ToolTip.SetTip(protect, "Encrypt into a .bxlock file that only opens with the password");
                }

                children.Add(protect);
            }

            menu.ItemsSource = children;
            return menu;
        }

        /// <summary>"Calculate size" for folders (counted and shown in the Size column, as Total Commander's Space does).</summary>
        private static void AddCalculateSize(List<Control> items, FileListViewModel vm, IReadOnlyList<string> folders, bool pathsAreSelection)
        {
            if (folders.Count == 0)
            {
                return;
            }

            items.Add(Item(folders.Count == 1 ? "Calculate size" : $"Calculate size of {folders.Count} folders", Symbol.Storage,
                () => Fire(vm, vm.CalculateFolderSizesAsync(folders, announce: true))));
        }

        /// <summary>
        /// "Open in new tab" (one folder: the tab is shown, as in Explorer) or "Open in new tabs" (several: they open behind,
        /// in order), then "Open in new window" for one folder; nothing for files, or where the pane has no tabs to open.
        /// </summary>
        private static void AddOpenInNewTab(List<Control> items, FileListViewModel vm, IReadOnlyList<string> folders)
        {
            if (folders == null || folders.Count == 0 || vm.OpenInNewTabs == null)
            {
                return;
            }

            var paths = folders.ToList();
            items.Add(Item(paths.Count == 1 ? "Open in new tab" : $"Open in {paths.Count} new tabs", Symbol.TabAdd,
                () => vm.OpenInNewTabs(paths, paths.Count == 1).Forget("open in new tab"),
                Infrastructure.TabOptions.MiddleClickOpensOtherPane ? null : "Middle-click"));

            // One folder only: a window per folder of a large selection would bury the screen (the Windows menu's verb
            // still opens one each, as Explorer does).
            if (paths.Count == 1 && vm.OpenInNewWindows != null)
            {
                items.Add(Item("Open in new window", Symbol.Window, () => vm.OpenInNewWindows(paths).Forget("open in new window")));
            }
        }

        /// <summary>
        /// Items on a phone: open, cut, copy, paste into a folder, copy as path, rename, delete (permanent: phones have no
        /// recycle bin) and tags. Not "Open with", compressing, pinning, or, on Windows, Properties and the terminal: they
        /// need a file path, which items of a Windows phone don't have.
        /// </summary>
        private static ContextMenu ForPhoneItems(Control anchor, FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            bool single = paths.Count == 1;
            string first = paths[0];
            bool windowsPhone = Core.Platform.Phones.PhonePaths.IsPhonePath(first);
            // A tree node is always a folder; a selection says what each item is (the phone isn't asked again).
            bool firstIsFolder = !pathsAreSelection || vm.SelectedItems.Any(i => PathUtil.AreSame(i.FullPath, first) && i.IsDirectory);
            var items = new List<Control>();

            var open = Item(single ? "Open" : $"Open {paths.Count} items", Symbol.Open, () => OpenAsync(vm, paths), "Enter");
            open.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            items.Add(open);
            bool allPhoneFolders = pathsAreSelection
                ? paths.All(p => vm.SelectedItems.Any(i => PathUtil.AreSame(i.FullPath, p) && i.IsDirectory))
                : firstIsFolder;
            AddOpenInNewTab(items, vm, allPhoneFolders ? paths : null);
            if (!windowsPhone)
            {
                AddCalculateSize(items, vm, pathsAreSelection
                    ? vm.SelectedItems.Where(i => i.IsDirectory).Select(i => i.FullPath).ToList()
                    : paths, pathsAreSelection);
                items.Add(Item(OperatingSystem.IsMacOS() ? "Show in Finder" : "Show in system file manager",
                    Symbol.FolderOpen, () => Run(vm, () => ShellLauncher.RevealInSystemFileManager(first), "Couldn't open the system file manager.")));
            }

            items.Add(new Separator());
            items.Add(Item("Cut", Symbol.Cut, () =>
            {
                if (pathsAreSelection)
                {
                    Execute(vm.CutCommand);
                }
                else
                {
                    ClipboardFileService.SetFilesForCut(paths);
                }
            }, Gesture("X")));
            items.Add(Item("Copy", Symbol.Copy, () =>
            {
                if (pathsAreSelection)
                {
                    Execute(vm.CopyCommand);
                }
                else
                {
                    ClipboardFileService.SetFilesForCopy(paths);
                }
            }, Gesture("C")));

            if (single && firstIsFolder)
            {
                var paste = Item("Paste into folder", Symbol.ClipboardPaste, () => PasteIntoAsync(vm, first));
                paste.IsEnabled = ClipboardFileService.HasFiles();
                items.Add(paste);
            }

            items.Add(Item("Copy as path", Symbol.Link, () =>
                ClipboardFileService.SetText(string.Join(Environment.NewLine, paths.Select(p => paths.Count == 1 ? p : "\"" + p + "\"")))));
            items.Add(new Separator());

            if (single)
            {
                var rename = Item("Rename", Symbol.Rename, () =>
                {
                    if (pathsAreSelection)
                    {
                        Execute(vm.RenameCommand);
                    }
                    else
                    {
                        Fire(vm, vm.PromptRenamePathAsync(first));
                    }
                }, "F2");
                rename.IsEnabled = PathUtil.GetParent(first) != null && (!windowsPhone || Core.Platform.Phones.PhonePaths.Segments(first).Length > 2);
                items.Add(rename);
            }

            var delete = Item("Delete from phone", Symbol.DeleteDismiss, () => Fire(vm, vm.DeletePathsAsync(paths, permanent: true)), "Delete");
            delete.Classes.Add("danger");
            ToolTip.SetTip(delete, "Phones have no recycle bin: this can't be undone");
            items.Add(delete);

            items.Add(new Separator());
            items.Add(Tags.TagActions.BuildItemsMenu(paths, Core.Tags.TagStore.Default, vm.DialogService, () => TopLevel.GetTopLevel(anchor) as Window));
            if (!windowsPhone)
            {
                items.Add(Item("Properties", Symbol.Info, () => vm.DialogService?.ShowProperties(paths), "Alt+Enter"));
            }

            return new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        }

        /// <summary>
        /// Items inside an archive (read-only): open (from a temporary copy), copy (extracted first), extract beside the
        /// archive, copy as path. Nothing that would write into the archive.
        /// </summary>
        private static ContextMenu ForArchiveItems(FileListViewModel vm, IReadOnlyList<string> paths, bool pathsAreSelection)
        {
            var items = new List<Control>();
            var open = Item(paths.Count == 1 ? "Open" : $"Open {paths.Count} items", Symbol.Open, () => OpenAsync(vm, paths), "Enter");
            open.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            items.Add(open);
            items.Add(new Separator());
            var copy = Item("Copy", Symbol.Copy, () => Execute(vm.CopyCommand), Gesture("C"));
            copy.IsEnabled = pathsAreSelection;
            items.Add(copy);
            items.Add(Item("Copy as path", Symbol.Link, () =>
                ClipboardFileService.SetText(string.Join(Environment.NewLine, paths.Select(p => paths.Count == 1 ? p : "\"" + p + "\"")))));
            items.Add(new Separator());
            ArchiveBrowser.TryResolve(paths[0], out string archive, out _);
            items.Add(Item($"Extract to \"{PathUtil.GetName(PathUtil.GetParent(archive))}\"", Symbol.FolderZip, () => Fire(vm, vm.ExtractFromArchiveAsync(paths))));
            items.Add(new Separator());
            items.Add(ReadOnlyArchiveItem("Rename", Symbol.Rename, "F2"));
            items.Add(ReadOnlyArchiveItem(OperatingSystem.IsWindows() ? "Delete" : "Move to Trash", Symbol.Delete, "Delete"));
            return new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        }

        /// <summary>A writing command shown disabled inside an archive, its tip saying why (archives are browsed read-only).</summary>
        private static MenuItem ReadOnlyArchiveItem(string header, Symbol symbol, string gesture)
        {
            MenuItem item = Item(header, symbol, () => { }, gesture);
            item.IsEnabled = false;
            ToolTip.SetTip(item, "Archives are read-only here: open, copy or extract their items; to change them, extract the archive first");
            ToolTip.SetShowOnDisabled(item, true);
            return item;
        }

        /// <summary>
        /// "Extract all…" (into a folder you choose) and "Extract here" (beside the archive), each making a folder named
        /// after the archive, for every format the app can read (ZIP, TAR, and 7z, RAR, ISO, CAB… with an archiver). A
        /// known archive type nothing installed can read gets a disabled "Extract here" whose tip says what to install.
        /// </summary>
        private static void AddExtractItems(List<Control> items, Control anchor, FileListViewModel vm, string archive)
        {
            if (ArchiveService.CanExtract(archive))
            {
                var all = Item("Extract all…", Symbol.FolderZip, () => Fire(vm, ExtractToChosenFolderAsync(anchor, vm, archive)));
                ToolTip.SetTip(all, "Choose a folder; the archive is extracted into a new folder there named after it");
                items.Add(all);
                var here = Item("Extract here", Symbol.FolderZip, () => Fire(vm, vm.ExtractArchiveAsync(archive)));
                ToolTip.SetTip(here, $"Extract into a new folder \"{Core.Services.Archives.ArchiveFormats.StemOf(archive)}\" beside the archive");
                items.Add(here);
                return;
            }

            string hint = Core.Services.Archives.ArchiveFormats.MissingToolHint(archive);
            if (hint != null && !Core.Security.ProtectedFormat.IsProtectedFile(archive))
            {
                var unavailable = Item("Extract here", Symbol.FolderZip, () => { });
                unavailable.IsEnabled = false;
                ToolTip.SetTip(unavailable, hint);
                ToolTip.SetShowOnDisabled(unavailable, true);
                items.Add(unavailable);
            }
        }

        private static async Task ExtractToChosenFolderAsync(Control anchor, FileListViewModel vm, string archive)
        {
            TopLevel top = (anchor == null ? null : TopLevel.GetTopLevel(anchor))
                ?? (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            IStorageProvider storage = top?.StorageProvider;
            if (storage == null || !storage.CanPickFolder)
            {
                await vm.ExtractArchiveAsync(archive); // no picker (headless, some Linux desktops): beside the archive
                return;
            }

            IStorageFolder start = await storage.TryGetFolderFromPathAsync(PathUtil.GetParent(archive) ?? archive);
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Extract {PathUtil.GetName(archive)} to",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });
            string destination = folders?.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(destination))
            {
                await vm.ExtractArchiveAsync(archive, destination);
            }
        }

        public static ContextMenu ForBackground(FileListViewModel vm, Control anchor = null)
        {
            string folder = vm.EffectiveTargetPath ?? vm.CurrentPath;
            var items = new List<Control>();

            // View
            var view = new MenuItem { Header = "View", Icon = Icon(Symbol.Grid) };
            view.ItemsSource = vm.ViewPresetOptions.Select(option =>
            {
                var item = new MenuItem
                {
                    Header = option.DisplayName,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = vm.SelectedPreset == option.Value,
                    GroupName = "view"
                };
                item.Click += (_, _) => Guard(() => vm.SelectedPreset = option.Value);
                return item;
            }).ToList();
            items.Add(view);

            // Sort by
            var sort = new MenuItem { Header = "Sort by", Icon = Icon(Symbol.ArrowSort) };
            var sortItems = new List<Control>();
            foreach ((FileListSortColumn column, string label) in new[]
                     {
                         (FileListSortColumn.Name, "Name"), (FileListSortColumn.DateModified, "Date modified"),
                         (FileListSortColumn.Type, "Type"), (FileListSortColumn.Size, "Size"), (FileListSortColumn.Tags, "Tags"),
                         (FileListSortColumn.DateCreated, "Date created"), (FileListSortColumn.DateAccessed, "Date accessed")
                     })
            {
                var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = vm.SortColumn == column, GroupName = "sortcol" };
                item.Click += (_, _) => Guard(() => vm.SetSort(column, vm.SortColumn == column ? vm.SortAscending : column == FileListSortColumn.Name));
                sortItems.Add(item);
            }

            sortItems.Add(new Separator());
            var ascending = new MenuItem { Header = "Ascending", ToggleType = MenuItemToggleType.Radio, IsChecked = vm.SortAscending, GroupName = "sortdir" };
            ascending.Click += (_, _) => Guard(() => vm.SetSort(vm.SortColumn, true));
            var descending = new MenuItem { Header = "Descending", ToggleType = MenuItemToggleType.Radio, IsChecked = !vm.SortAscending, GroupName = "sortdir" };
            descending.Click += (_, _) => Guard(() => vm.SetSort(vm.SortColumn, false));
            sortItems.Add(ascending);
            sortItems.Add(descending);
            sort.ItemsSource = sortItems;
            items.Add(sort);

            items.Add(Item("Refresh", Symbol.ArrowClockwise, () => Execute(vm.RefreshCommand), "F5"));
            // Not inside a password-protected item: a pin would write the names inside it into the settings file.
            if (vm.Pins != null && !string.IsNullOrEmpty(vm.CurrentPath) && !Core.Security.ProtectedFormat.IsProtectedPath(vm.CurrentPath))
            {
                items.Add(PinItem(vm, new[] { vm.CurrentPath }, Gesture("D")));
            }

            items.Add(new Separator());

            if (vm.IsInsideArchive)
            {
                // Read-only: no paste, new items, undo or terminal here; extracting the whole archive instead.
                if (ArchiveBrowser.TryResolve(vm.CurrentPath, out string archive, out _))
                {
                    if (Core.Security.ProtectedFormat.IsProtectedFile(archive))
                    {
                        // Inside an unlocked protected item: lock it (the pane goes back to its folder), or decrypt it for good.
                        items.Add(Item("Lock now", Symbol.LockClosed, () => vm.LockProtected(archive)));
                        items.Add(Item("Remove protection…", Symbol.LockOpen, () => Fire(vm, vm.RemoveProtectionAsync(archive))));
                    }
                    else
                    {
                        AddExtractItems(items, anchor, vm, archive);
                        items.Add(ReadOnlyArchiveItem("Paste", Symbol.ClipboardPaste, Gesture("V")));
                    }
                }

                return new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
            }

            // Undo the last file operation (any pane), as Explorer's background menu offers it.
            var undo = Item(vm.Operations?.UndoText ?? "Undo", Symbol.ArrowUndo, () => Execute(vm.Operations?.UndoCommand), Gesture("Z"));
            undo.IsEnabled = vm.Operations?.CanUndo == true;
            items.Add(undo);
            if (vm.Operations?.CanRedo == true)
            {
                items.Add(Item(vm.Operations.RedoText, Symbol.ArrowRedo, () => Execute(vm.Operations.RedoCommand), Gesture("Y")));
            }

            var paste = Item("Paste", Symbol.ClipboardPaste, () => Execute(vm.PasteCommand), Gesture("V"));
            paste.IsEnabled = vm.PasteCommand.CanExecute(null);
            items.Add(paste);

            // The same New items as the toolbar's New button: Folder, Shortcut, Text Document, the system's file kinds.
            var newMenu = new MenuItem { Header = "New", Icon = Icon(Symbol.AddCircle) };
            newMenu.ItemsSource = NewItemSelector.BuildItems(vm, null); // built whole now: rows swapped in later may not show
            items.Add(newMenu);

            items.Add(new Separator());
            if (!vm.IsOnWindowsPhone)
            {
                items.Add(Item("Open in terminal", Symbol.WindowConsole, () => Execute(vm.OpenTerminalCommand)));
            }

            items.Add(Item("Select all", Symbol.SelectAllOn, () => Execute(vm.SelectAllCommand), Gesture("A")));
            if (!vm.IsOnWindowsPhone)
            {
                items.Add(Item("Calculate folder sizes", Symbol.Storage, () => Execute(vm.CalculateAllFolderSizesCommand), "Alt+Shift+Enter"));
            }

            var hidden = new MenuItem { Header = "Show hidden items", ToggleType = MenuItemToggleType.CheckBox, IsChecked = vm.ShowHiddenItems };
            hidden.Click += (_, _) => Execute(vm.ToggleShowHiddenItemsCommand);
            items.Add(hidden);

            if (!vm.IsOnWindowsPhone)
            {
                items.Add(new Separator());
                items.Add(Item("Properties", Symbol.Info, () =>
                {
                    if (!string.IsNullOrEmpty(folder))
                    {
                        vm.DialogService?.ShowProperties(new[] { folder });
                    }
                }));
            }

            AddShowMoreOptionsForBackground(items, anchor, vm, folder);
            return new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        }

        /// <summary>
        /// "Open with": the registered applications on Linux (default first, marked), and "Choose another app…" — Windows'
        /// own dialog there, an application picker on macOS (Applications folder), and a program picker on Linux.
        /// </summary>
        /// <summary>
        /// "Pin to Quick access" for folders not all pinned yet (pins the rest), else "Unpin from Quick access".
        /// </summary>
        private static MenuItem PinItem(FileListViewModel vm, IReadOnlyList<string> folders, string gesture = null)
        {
            bool allPinned = folders.All(vm.Pins.Contains);
            string title = allPinned
                ? "Unpin from Quick access"
                : folders.Count == 1 ? "Pin to Quick access" : $"Pin {folders.Count} folders to Quick access";
            return Item(title, Symbol.Pin, () =>
            {
                if (allPinned)
                {
                    foreach (string folder in folders)
                    {
                        Fire(vm, vm.Pins.UnpinAsync(folder));
                    }
                }
                else
                {
                    Fire(vm, vm.Pins.PinAsync(folders));
                }
            }, gesture);
        }

        /// <summary>The menu of a saved network location in the sidebar: open it, or remove it from the sidebar.</summary>
        public static void ShowForNetworkLocation(Control anchor, Func<Task> open, Func<Task> remove)
        {
            var items = new List<Control>
            {
                Item("Open", Symbol.Open, open),
                new Separator(),
                Item("Remove from sidebar", Symbol.Dismiss, remove)
            };
            Open(anchor, new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer });
        }

        private static MenuItem BuildOpenWith(Control anchor, FileListViewModel vm, string path)
        {
            var menu = new MenuItem { Header = "Open with", Icon = Icon(Symbol.AppsList) };
            var children = new List<Control>();
            foreach (OpenWithApplication app in OpenWithApplications.For(path))
            {
                var item = new MenuItem { Header = app.IsDefault ? app.Name + " (default)" : app.Name };
                item.Click += (_, _) => Run(vm, () => ShellLauncher.OpenWith(path, app), $"Couldn't open the file with {app.Name}.");
                children.Add(item);
            }

            if (children.Count > 0)
            {
                children.Add(new Separator());
            }

            children.Add(Item("Choose another app…", Symbol.Apps, () => Fire(vm, ChooseApplicationAsync(anchor, vm, path))));
            menu.ItemsSource = children;
            return menu;
        }

        private static async Task ChooseApplicationAsync(Control anchor, FileListViewModel vm, string path)
        {
            if (ShellLauncher.TryShowSystemOpenWithDialog(path))
            {
                return;
            }

            IStorageProvider storage = TopLevel.GetTopLevel(anchor)?.StorageProvider;
            if (storage == null)
            {
                return;
            }

            IStorageFolder start = await storage.TryGetFolderFromPathAsync(OperatingSystem.IsMacOS() ? "/Applications" : "/usr/bin");
            IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an application to open " + PathUtil.GetName(path),
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            string program = picked?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(program))
            {
                return;
            }

            OpenWithApplication app = OperatingSystem.IsMacOS() && program.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? OpenWithApplications.ForMacBundle(program)
                : new OpenWithApplication(program, PathUtil.GetName(program), program, new[] { OpenWithApplication.PathPlaceholder }, isDefault: false);
            Run(vm, () => ShellLauncher.OpenWith(path, app), "Couldn't open the file with that application.");
        }

        private static async Task OpenAsync(FileListViewModel vm, IReadOnlyList<string> paths)
        {
            // One folder opens in this pane; files (and several items) go to their applications, as a double-click does.
            if (paths.Count == 1 && Directory.Exists(paths[0]))
            {
                await vm.LoadFolderAsync(paths[0]);
                return;
            }

            foreach (string path in paths.Take(32)) // a runaway "open 5,000 files" would fork 5,000 processes
            {
                if (Directory.Exists(path))
                {
                    continue;
                }

                vm.OpenRecentFile(path, PathUtil.GetName(path));
            }
        }

        /// <summary>Pastes the files on the clipboard into <paramref name="folder"/> (cut files are moved, then the clipboard is cleared).</summary>
        public static async Task PasteIntoAsync(FileListViewModel vm, string folder)
        {
            (bool ok, IReadOnlyList<string> files, bool isCut) = await ClipboardFileService.TryGetFilesAsync();
            if (!ok || files.Count == 0)
            {
                return;
            }

            bool done = await vm.ExecuteDropAsync(files, folder, isCut);
            if (isCut && done)
            {
                // Only once everything moved (a failed or cancelled move keeps the cut for another try), and only if the
                // clipboard still holds this cut (something copied meanwhile stays).
                await ClipboardFileService.ClearCutIfUnchangedAsync(files);
            }
        }

        private static void Open(Control anchor, ContextMenu menu)
        {
            // A ContextMenu opened from code needs an owner in the visual tree for its resources, theme and placement.
            menu.PlacementTarget = anchor;
            menu.Closed += (_, _) => menu.ItemsSource = null; // release the closures (and the view models they capture)
            menu.Open(anchor);
        }

        private static MenuItem Item(string header, Symbol symbol, Action action, string gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = Icon(symbol) };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture);
                }
                catch (ArgumentException)
                {
                    ToolTip.SetTip(item, gesture); // "Middle-click" isn't a key gesture: shown as a tip instead
                }
            }

            item.Click += (_, _) => Guard(action);
            return item;
        }

        private static MenuItem Item(string header, Symbol symbol, Func<Task> action, string gesture = null) =>
            Item(header, symbol, () => Fire(null, action()), gesture); // no view model here: logged

        /// <summary>"Ctrl+X" on Windows/Linux, "Cmd+X" on macOS, as each platform shows its shortcuts.</summary>
        private static string Gesture(string keys) => (OperatingSystem.IsMacOS() ? "Cmd+" : "Ctrl+") + keys;

        private static SymbolIcon Icon(Symbol symbol) => new SymbolIcon { Symbol = symbol, FontSize = 16 };

        private static void Execute(ICommand command)
        {
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        private static void Run(FileListViewModel vm, Action action, string failureMessage)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Logger.Warning(ex, "Context menu action failed");
                vm.ReportFailure(failureMessage + "\n\n" + ex.Message);
            }
        }

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Context menu action failed");
            }
        }

        /// <summary>Runs a menu action; a failure is shown to the person (not only logged), as the toolbar's commands do.</summary>
        private static async void Fire(FileListViewModel vm, Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the person: nothing to report.
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Context menu action failed");
                try
                {
                    if (vm?.DialogService != null)
                    {
                        await vm.DialogService.ShowMessageAsync("That didn't work", Infrastructure.FriendlyErrors.Explain(ex));
                    }
                }
                catch (Exception dialogEx)
                {
                    Log.Logger.Warning(dialogEx, "Could not show the failure of a context menu action");
                }
            }
        }
    }
}
