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
    /// The Sort menu: column and direction through FileListViewModel.SetSort, each keeping the other half of the
    /// sort. Re-picking the current column or direction does nothing (the WPF version re-sorted the whole folder).
    /// </summary>
    public partial class SortSelector : UserControl
    {
        public SortSelector()
        {
            InitializeComponent();
        }

        private void OnSortColumnClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string name } && DataContext is MainViewModel { FileListViewModel: { } fileList } &&
                Enum.TryParse(name, out FileListSortColumn column) && column != fileList.SortColumn)
            {
                fileList.SetSort(column, fileList.SortAscending);
            }
        }

        private void OnSortDirectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string text } && DataContext is MainViewModel { FileListViewModel: { } fileList } &&
                bool.TryParse(text, out bool ascending) && ascending != fileList.SortAscending)
            {
                fileList.SetSort(fileList.SortColumn, ascending);
            }
        }
    }
}
