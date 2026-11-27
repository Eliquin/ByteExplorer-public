// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The View menu: sets the active pane's FileListViewModel.SelectedPreset. Re-picking the current preset does nothing
    /// (the WPF version re-applied and re-saved it).
    /// </summary>
    public partial class ViewModeSelector : UserControl
    {
        public ViewModeSelector()
        {
            InitializeComponent();
        }

        /// <summary>Applies the clicked preset to the active pane (the radio check follows the view model).</summary>
        private void OnPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string name } && DataContext is MainViewModel { FileListViewModel: { } fileList } &&
                Enum.TryParse(name, out FileListViewPreset preset) && fileList.SelectedPreset != preset)
            {
                fileList.SelectedPreset = preset;
            }
        }
    }
}
