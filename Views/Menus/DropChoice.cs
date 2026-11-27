// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views.Menus
{
    /// <summary>What a drop does.</summary>
    public enum DropAction
    {
        None,
        Copy,
        Move,
        Link
    }

    /// <summary>
    /// What every drop target in the app does with files dropped on a folder (the file list, the folder tree, Quick access,
    /// the sidebar's rows, the breadcrumbs, the tabs): Ctrl copies, Shift moves, otherwise the same drive moves and another
    /// copies; and after a right-button drag, Explorer's menu: Copy here, Move here, Create shortcuts here, Cancel, with
    /// what a left drag would have done in bold. Shortcuts made here can be undone (Ctrl+Z) like any operation.
    /// </summary>
    public static class DropChoice
    {
        /// <summary>
        /// Handles a drop of <paramref name="paths"/> on <paramref name="folder"/>. Sets the effect the source is told
        /// before anything is awaited (the source reads it as soon as the handler yields). The copy or move runs through
        /// <paramref name="pane"/> (its operations queue, its error messages); <paramref name="transfer"/> replaces that
        /// for targets with their own route (Quick access).
        /// </summary>
        public static async Task RunAsync(Control anchor, DragEventArgs e, string folder, IReadOnlyList<string> paths,
            FileListViewModel pane, Func<bool, Task> transfer = null)
        {
            bool rightButton = FileDragDropHelper.IsRightButtonDrag; // cleared once the drag ends: read it first
            DragDropEffects effect = FileDragDropHelper.ResolveEffect(e, folder, paths);
            e.DragEffects = FileDragDropHelper.EffectToReport(effect);
            if (effect == DragDropEffects.None)
            {
                return;
            }

            DropAction action = effect == DragDropEffects.Move ? DropAction.Move : DropAction.Copy;
            if (rightButton)
            {
                action = await ChooseAsync(anchor, e.GetPosition(anchor), folder, paths, action);
            }

            await ExecuteAsync(action, folder, paths, pane, transfer);
        }

        /// <summary>Runs a drop's action: the copy or move, or the shortcuts.</summary>
        public static async Task ExecuteAsync(DropAction action, string folder, IReadOnlyList<string> paths, FileListViewModel pane, Func<bool, Task> transfer = null)
        {
            switch (action)
            {
                case DropAction.Copy:
                case DropAction.Move:
                    bool move = action == DropAction.Move;
                    if (transfer != null)
                    {
                        await transfer(move);
                    }
                    else if (pane != null)
                    {
                        await pane.ExecuteDropAsync(paths, folder, move);
                    }

                    break;
                case DropAction.Link:
                    await CreateShortcutsAsync(paths, folder, pane);
                    break;
            }
        }

        /// <summary>
        /// The right-drag menu at <paramref name="at"/>: Copy here, Move here, Create shortcuts here, Cancel. Move is offered
        /// only where it would do something (not into the items' own folder); shortcuts only for files and folders on a
        /// drive (not phones, not inside a ZIP). Closing the menu any other way is Cancel.
        /// </summary>
        public static Task<DropAction> ChooseAsync(Control anchor, Point at, string folder, IReadOnlyList<string> paths, DropAction preferred)
        {
            var done = new TaskCompletionSource<DropAction>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool sameFolder = paths.All(p => PathUtil.AreSame(PathUtil.GetParent(PathUtil.Normalize(p)), PathUtil.Normalize(folder)));
            bool canLink = paths.All(p => Shortcuts.CanCreate(p, folder));
            string noun = Shortcuts.Noun + (paths.Count == 1 ? string.Empty : "s");

            MenuItem Item(string header, Symbol symbol, DropAction action, bool enabled)
            {
                var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 }, IsEnabled = enabled };
                if (action == preferred && enabled)
                {
                    item.FontWeight = Avalonia.Media.FontWeight.SemiBold; // what a left drag would have done
                }

                item.Click += (_, _) => done.TrySetResult(action);
                return item;
            }

            var cancel = new MenuItem { Header = "Cancel", Icon = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 16 } };
            cancel.Click += (_, _) => done.TrySetResult(DropAction.None);
            var menu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    Item("Copy here", Symbol.Copy, DropAction.Copy, true),
                    Item("Move here", Symbol.Cut, DropAction.Move, !sameFolder),
                    Item($"Create {noun} here", Symbol.Link, DropAction.Link, canLink),
                    new Separator(),
                    cancel
                },
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
                PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
                PlacementRect = new Rect(at, new Size(1, 1)) // where the files were dropped (the pointer isn't tracked during a drag)
            };
            menu.Closed += (_, _) => done.TrySetResult(DropAction.None);
            OpenMenu?.Invoke(menu, anchor);
            if (OpenMenu == null)
            {
                menu.Open(anchor);
            }

            return done.Task;
        }

        /// <summary>Opens the right-drag menu (tests replace it to choose an item without a pointer).</summary>
        internal static Action<ContextMenu, Control> OpenMenu { get; set; }

        /// <summary>
        /// Makes a shortcut (Windows) or link (Linux, macOS) to each item in <paramref name="folder"/>, off the UI thread,
        /// recorded as one operation that Ctrl+Z removes and Ctrl+Y makes again. Failures are reported together.
        /// </summary>
        public static async Task<IReadOnlyList<string>> CreateShortcutsAsync(IReadOnlyList<string> paths, string folder, FileListViewModel pane)
        {
            var created = new List<string>();
            var failures = new List<string>();
            await Task.Run(() =>
            {
                foreach (string source in paths)
                {
                    try
                    {
                        created.Add(Shortcuts.Create(source, folder));
                    }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is InvalidCastException ||
                                               ex is System.Runtime.InteropServices.COMException || ex is NotSupportedException || ex is ArgumentException)
                    {
                        Log.Logger.Warning(ex, "Creating a shortcut to {Source} in {Folder} failed", source, folder);
                        failures.Add($"\"{PathUtil.GetName(source)}\": {ex.Message}");
                    }
                }
            }).ConfigureAwait(true);

            if (created.Count > 0 && pane?.Operations != null)
            {
                string description = created.Count == 1 ? $"Create {Shortcuts.Noun}" : $"Create {created.Count} {Shortcuts.Noun}s";
                pane.Operations.Record(new FileOperationRecord(description, created.Select(p => (UndoAction)new CreatedAction(p)).ToList(), new[] { folder })
                {
                    Redo = () => CreateShortcutsAsync(paths, folder, pane)
                });
            }

            if (created.Count > 0 && pane != null)
            {
                await pane.NotifyExternalChangeAsync(folder).ConfigureAwait(true); // shown at once, not at the watcher's pace
            }

            if (failures.Count > 0)
            {
                string message = $"Couldn't create {(failures.Count == 1 ? "a " + Shortcuts.Noun : failures.Count + " " + Shortcuts.Noun + "s")}:\n\n" + string.Join("\n", failures);
                if (pane != null)
                {
                    pane.ReportFailure(message);
                }
            }

            return created;
        }
    }
}
