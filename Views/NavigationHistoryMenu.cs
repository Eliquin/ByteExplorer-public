// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The Back and Forward buttons' history menus: right-clicking one (or the menu key on it) lists the places behind
    /// or ahead, nearest first, as in a browser or Windows Explorer; picking one jumps there in one step, keeping the
    /// places passed over in the other list. Attached in XAML with <c>views:NavigationHistoryMenu.Direction="Back"</c>
    /// (or "Forward"); the history is the active pane's (the window's MainViewModel.FileListViewModel).
    /// </summary>
    public static class NavigationHistoryMenu
    {
        /// <summary>The most places one menu lists; older ones stay reachable step by step.</summary>
        public const int MaxEntries = 16;

        public static readonly AttachedProperty<string> DirectionProperty =
            AvaloniaProperty.RegisterAttached<Control, string>("Direction", typeof(NavigationHistoryMenu));

        static NavigationHistoryMenu()
        {
            DirectionProperty.Changed.AddClassHandler<Control>((control, e) =>
            {
                control.ContextRequested -= OnContextRequested;
                if (e.NewValue is string direction && direction.Length > 0)
                {
                    control.ContextRequested += OnContextRequested;
                }
            });
        }

        public static string GetDirection(Control control) => control.GetValue(DirectionProperty);

        public static void SetDirection(Control control, string value) => control.SetValue(DirectionProperty, value);

        /// <summary>The menu items for <paramref name="pane"/>'s history in one direction (null when it is empty).</summary>
        public static ContextMenu Build(FileListViewModel pane, bool forward)
        {
            IReadOnlyList<HistoryEntry> entries = forward ? pane.ForwardHistory : pane.BackHistory;
            if (entries.Count == 0)
            {
                return null;
            }

            var items = new List<Control>(Math.Min(entries.Count, MaxEntries));
            for (int i = 0; i < entries.Count && i < MaxEntries; i++)
            {
                HistoryEntry entry = entries[i];
                int steps = forward ? i + 1 : -(i + 1);
                var item = new MenuItem
                {
                    Header = entry.DisplayName,
                    Icon = new SymbolIcon { Symbol = IconOf(entry), FontSize = 16 }
                };
                if (entry.Place == HistoryPlace.Folder)
                {
                    ToolTip.SetTip(item, entry.Path);
                }

                item.Click += (_, _) => pane.NavigateHistoryAsync(steps).Forget("NavigateHistoryAsync");
                items.Add(item);
            }

            return new ContextMenu { ItemsSource = items };
        }

        private static Symbol IconOf(HistoryEntry entry) => entry.Place switch
        {
            HistoryPlace.Home => Symbol.Home,
            HistoryPlace.ThisPc => IconGlyphs.Computer,
            HistoryPlace.Trash => Symbol.Delete,
            _ => Symbol.Folder
        };

        private static void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is not Control control || TopLevel.GetTopLevel(control)?.DataContext is not MainViewModel main ||
                main.FileListViewModel is not FileListViewModel pane)
            {
                return;
            }

            e.Handled = true;
            bool forward = string.Equals(GetDirection(control), "Forward", StringComparison.OrdinalIgnoreCase);
            ContextMenu menu = Build(pane, forward);
            if (menu == null)
            {
                return;
            }

            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementTarget = control;
            menu.Closed += (_, _) => menu.ItemsSource = null;
            try
            {
                menu.Open(control);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to open the navigation history menu");
            }
        }
    }
}
