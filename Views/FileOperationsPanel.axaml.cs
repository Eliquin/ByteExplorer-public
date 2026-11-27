// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia.Controls;
using Avalonia.Interactivity;
using ByteExplorer.Icons;

namespace ByteExplorer.Views
{
    /// <summary>The operations panel. Everything is bound to the queue and its jobs; the code only collapses the list.</summary>
    public partial class FileOperationsPanel : UserControl
    {
        public FileOperationsPanel()
        {
            InitializeComponent();
        }

        private void OnCollapseClick(object sender, RoutedEventArgs e)
        {
            bool collapse = JobsScroller.IsVisible;
            JobsScroller.IsVisible = !collapse;
            CollapseIcon.Symbol = collapse ? Symbol.ChevronUp : Symbol.ChevronDown;
            ToolTip.SetTip(CollapseButton, collapse ? "Expand" : "Collapse");
        }
    }
}
