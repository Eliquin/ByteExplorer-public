// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The details pane shared by both layouts. Besides its bindings it tells the preview how large an image it can
    /// show: the pane's width in device pixels (with some room for zoom-free sharpness), so a 300-pixel pane decodes a
    /// photo at about 700 pixels instead of the 33 MB a 4K decode costs.
    /// </summary>
    public partial class DetailsPaneView : UserControl
    {
        /// <summary>Headroom over the pane's width: the preview is letterboxed in a box a little narrower than the pane, never wider.</summary>
        private const double ImageSideFactor = 1.25;

        public DetailsPaneView()
        {
            InitializeComponent();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateImageBudget();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            UpdateImageBudget();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
            {
                UpdateImageBudget();
            }
        }

        private void UpdateImageBudget()
        {
            FilePreviewViewModel preview = (DataContext as MainViewModel)?.FileListViewModel?.Preview;
            double width = Bounds.Width;
            if (preview == null || width <= 0)
            {
                return;
            }

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            preview.MaxImageSide = (int)Math.Ceiling(width * scaling * ImageSideFactor);
        }
    }
}
