// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;

namespace ByteExplorer.Controls
{
    /// <summary>
    /// Lays out its children side by side at the widths of the inherited <see cref="ColumnsProperty"/> layout: child i
    /// gets column i. Used for the Details header and for each row, so they line up without a Grid per row or shared size
    /// groups (which re-measure every realized row on any change and do not scale to a virtualized list). Setting the
    /// layout on the list re-lays out every realized row through property inheritance.
    /// </summary>
    public sealed class DetailsRowPanel : Panel
    {
        /// <summary>The column layout; inherited, so it is set once on the list (and the header) for all rows.</summary>
        public static readonly AttachedProperty<DetailsColumnLayout> ColumnsProperty =
            AvaloniaProperty.RegisterAttached<DetailsRowPanel, Control, DetailsColumnLayout>("Columns", DetailsColumnLayout.Default, inherits: true);

        static DetailsRowPanel()
        {
            AffectsMeasure<DetailsRowPanel>(ColumnsProperty);
        }

        public static DetailsColumnLayout GetColumns(Control control) => control.GetValue(ColumnsProperty);

        public static void SetColumns(Control control, DetailsColumnLayout value) => control.SetValue(ColumnsProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            DetailsColumnLayout columns = GetValue(ColumnsProperty) ?? DetailsColumnLayout.Default;
            double height = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                double width = i < columns.Count ? columns[i] : 0;
                Control child = Children[i];
                child.Measure(new Size(width, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(columns.TotalWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            DetailsColumnLayout columns = GetValue(ColumnsProperty) ?? DetailsColumnLayout.Default;
            double x = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                double width = i < columns.Count ? columns[i] : 0;
                Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }
}
