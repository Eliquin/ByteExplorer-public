// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;

namespace ByteExplorer.Views.Omnibar
{
    /// <summary>
    /// Lays out breadcrumbs so the end of the path always shows: the first child is the overflow ("…") button, the
    /// others are the segments in order. When they don't all fit, the leading segments are hidden and the overflow button
    /// appears in their place (its menu lists them), as Explorer and Finder do; the last segment always shows, shortened
    /// if it alone is too wide. A scrolling strip, as before, hid the current folder's name off the end.
    /// <para>
    /// Hidden children are arranged outside the panel (which clips), never toggled invisible: changing visibility during
    /// layout would invalidate it again.
    /// </para>
    /// </summary>
    public sealed class BreadcrumbPanel : Panel
    {
        public static readonly DirectProperty<BreadcrumbPanel, int> HiddenCountProperty =
            AvaloniaProperty.RegisterDirect<BreadcrumbPanel, int>(nameof(HiddenCount), p => p.HiddenCount);

        private int _hiddenCount;
        private int _firstVisible = 1;
        private double _lastWidthLimit = double.PositiveInfinity;

        public BreadcrumbPanel()
        {
            ClipToBounds = true;
        }

        /// <summary>How many leading segments are folded into the overflow button.</summary>
        public int HiddenCount
        {
            get => _hiddenCount;
            private set => SetAndRaise(HiddenCountProperty, ref _hiddenCount, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
            double height = 0;
            foreach (Control child in Children)
            {
                child.Measure(unbounded);
                height = Math.Max(height, child.DesiredSize.Height);
            }

            int count = Children.Count;
            if (count <= 1)
            {
                HiddenCount = 0;
                return new Size(0, height);
            }

            double available = availableSize.Width;
            double overflowWidth = Children[0].DesiredSize.Width;
            double total = 0;
            for (int i = 1; i < count; i++)
            {
                total += Children[i].DesiredSize.Width;
            }

            _lastWidthLimit = double.PositiveInfinity;
            if (total <= available)
            {
                _firstVisible = 1;
                HiddenCount = 0;
                return new Size(total, height);
            }

            // From the end, while the segments and the overflow button fit.
            double used = overflowWidth;
            int first = count;
            for (int i = count - 1; i >= 1; i--)
            {
                double width = Children[i].DesiredSize.Width;
                if (used + width > available && first < count)
                {
                    break;
                }

                used += width;
                first = i;
            }

            if (used > available)
            {
                // Only the last segment, and even it doesn't fit: it gets what's left.
                _lastWidthLimit = Math.Max(0, available - overflowWidth);
                Children[count - 1].Measure(new Size(_lastWidthLimit, availableSize.Height));
                used = available;
            }

            _firstVisible = first;
            HiddenCount = first - 1;
            return new Size(Math.Min(used, available), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int count = Children.Count;
            if (count == 0)
            {
                return finalSize;
            }

            double x = 0;
            Control overflow = Children[0];
            if (_hiddenCount > 0)
            {
                overflow.Arrange(new Rect(0, 0, overflow.DesiredSize.Width, finalSize.Height));
                x = overflow.DesiredSize.Width;
            }
            else
            {
                overflow.Arrange(new Rect(-10000, 0, overflow.DesiredSize.Width, finalSize.Height));
            }

            for (int i = 1; i < count; i++)
            {
                Control child = Children[i];
                if (i < _firstVisible)
                {
                    child.Arrange(new Rect(-10000, 0, child.DesiredSize.Width, finalSize.Height));
                    continue;
                }

                double width = i == count - 1 && !double.IsPositiveInfinity(_lastWidthLimit)
                    ? Math.Min(child.DesiredSize.Width, _lastWidthLimit)
                    : child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }
}
