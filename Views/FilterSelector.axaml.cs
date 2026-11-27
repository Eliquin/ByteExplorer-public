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
    /// The Filter menu: sets the active pane's FileListViewModel.CategoryFilter (which ignores an unchanged value).
    /// </summary>
    public partial class FilterSelector : UserControl
    {
        public FilterSelector()
        {
            InitializeComponent();
        }

        /// <summary>Applies the clicked category to the active pane (the radio check follows the view model).</summary>
        private void OnCategoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string name } && DataContext is MainViewModel { FileListViewModel: { } fileList } &&
                Enum.TryParse(name, out FileCategoryFilter category))
            {
                fileList.CategoryFilter = category;
            }
        }
    }
}
