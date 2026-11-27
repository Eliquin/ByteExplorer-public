// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The "..." menu. Most rows bind the active pane's commands (a MenuFlyout closes itself after a click); here: the pin
    /// row's text follows the selection ("Pin to Quick access" / "Unpin from Quick access"), and Settings opens the
    /// window's settings.
    /// </summary>
    public partial class MoreOptionsSelector : UserControl
    {
        public MoreOptionsSelector()
        {
            InitializeComponent();
        }

        private void OnMenuOpening(object sender, EventArgs e)
        {
            FileListViewModel pane = (DataContext as MainViewModel)?.FileListViewModel;
            PinItem.Header = pane?.PinSelectionText ?? "Pin to Quick access";
            UpdatePasswordItem(pane);
            Infrastructure.CommandManager.InvalidateRequerySuggested(); // the rows' enabled state follows the selection
        }

        /// <summary>
        /// The Password submenu for the selected items: the same one the right-click menu has (FileContextMenu.
        /// BuildPasswordMenu), so both offer the same choices: "Protect with password…" for files and folders, or Open,
        /// Lock now, Change password and Remove protection for a protected item. Greyed, with the reason, when nothing is
        /// selected or the selection can't be protected (a phone, inside an archive, protected and unprotected items mixed).
        /// </summary>
        internal void UpdatePasswordItem(FileListViewModel pane)
        {
            var paths = pane?.SelectedItems.Select(i => i.FullPath).ToList() ?? new List<string>();
            MenuItem built = paths.Count > 0 ? Menus.FileContextMenu.BuildPasswordMenu(pane, paths) : null;
            var children = (built?.ItemsSource as IEnumerable<Control>)?.ToList();
            if (built != null)
            {
                built.ItemsSource = null; // the rows move to this menu
            }

            PasswordItem.ItemsSource = children ?? new List<Control>();
            PasswordItem.IsEnabled = children != null && children.Count > 0;
            ToolTip.SetTip(PasswordItem, PasswordItem.IsEnabled ? null
                : paths.Count == 0 ? "Select files or folders to protect them with a password"
                : "The selected items can't be protected together");
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e) =>
            (TopLevel.GetTopLevel(this) as MainWindowBase)?.OpenSettings();
    }
}
