// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace ByteExplorer.Views.Tabs
{
    /// <summary>
    /// Lays out the tabs, the way the major browsers do.
    /// <list type="bullet">
    /// <item>Horizontal: every unpinned tab gets the same width, between <see cref="MinTabWidth"/> and
    /// <see cref="MaxTabWidth"/>, sharing the room there is; pinned tabs are <see cref="PinnedTabWidth"/> wide. Past the
    /// minimum the row scrolls (<see cref="ScrollOffset"/>, by the wheel; the selected tab is kept in view).</item>
    /// <item>While tabs are closed with the mouse the widths stay as they were (<see cref="FreezeTabWidth"/>), so the next
    /// tab's close button comes under the pointer and several tabs close with clicks on one spot; the row settles when the
    /// pointer leaves (Chrome's behaviour).</item>
    /// <item>Vertical: rows of <see cref="RowHeight"/>, the whole width.</item>
    /// <item>A dragged tab follows the pointer (<see cref="DragChild"/>, <see cref="DragPosition"/>); the others slide to
    /// their new places (<see cref="AnimateMoves"/>).</item>
    /// </list>
    /// Children are the tabs in order; whether a child is pinned comes from <see cref="IsPinnedSelector"/>.
    /// </summary>
    public sealed class TabStripPanel : Panel
    {
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TabStripPanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

        public static readonly StyledProperty<double> ScrollOffsetProperty =
            AvaloniaProperty.Register<TabStripPanel, double>(nameof(ScrollOffset));

        private readonly Dictionary<Control, double> _arrangedAt = new Dictionary<Control, double>();
        private double _extent;
        private double _viewport;

        static TabStripPanel()
        {
            AffectsMeasure<TabStripPanel>(OrientationProperty);
            AffectsArrange<TabStripPanel>(ScrollOffsetProperty);
        }

        public TabStripPanel()
        {
            ClipToBounds = true;
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        /// <summary>How far the horizontal row is scrolled (0 … <see cref="MaxScrollOffset"/>).</summary>
        public double ScrollOffset
        {
            get => GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, Math.Clamp(value, 0, MaxScrollOffset));
        }

        public double MinTabWidth { get; set; } = 96;
        public double MaxTabWidth { get; set; } = 232;
        public double PinnedTabWidth { get; set; } = 44;
        public double TabHeight { get; set; } = 34;
        public double TabSpacing { get; set; } = 2;
        public double RowHeight { get; set; } = 38;

        /// <summary>Vertical: the space between pinned and other tabs (the bar draws a line in it, at <see cref="GroupLineY"/>).</summary>
        public double GroupGap { get; set; } = 9;


        /// <summary>Tells whether a child is a pinned tab.</summary>
        public Func<Control, bool> IsPinnedSelector { get; set; }

        /// <summary>A width kept for unpinned tabs while tabs are closed with the mouse (NaN: none).</summary>
        public double FreezeTabWidth { get; set; } = double.NaN;

        /// <summary>The tab being dragged (arranged at <see cref="DragPosition"/> instead of its slot), or null.</summary>
        public Control DragChild { get; set; }

        /// <summary>Where the dragged tab's leading edge is, in the panel's coordinates (scrolling included).</summary>
        public double DragPosition { get; set; }

        /// <summary>Tabs slide to new places instead of jumping (off in tests and while the row is resized).</summary>
        public bool AnimateMoves { get; set; } = true;

        /// <summary>The width every unpinned tab has now (horizontal).</summary>
        public double CurrentTabWidth { get; private set; }

        public double MaxScrollOffset => Math.Max(0, _extent - _viewport);

        /// <summary>True when the tabs need more room than there is (the row scrolls).</summary>
        public bool IsOverflowing => _extent > _viewport + 0.5;

        public event EventHandler OverflowChanged;

        private bool IsPinned(Control child) => IsPinnedSelector?.Invoke(child) ?? false;

        private int CountPinned()
        {
            int pinned = 0;
            foreach (Control child in Children)
            {
                if (IsPinned(child))
                {
                    pinned++;
                }
            }

            return pinned;
        }

        /// <summary>Vertical: the gap after the pinned tabs, when there are both pinned and other tabs.</summary>
        private double VerticalGap(int pinned) => pinned > 0 && pinned < Children.Count ? GroupGap : 0;

        /// <summary>The leading edge and size of each child's slot, in order (scroll offset not applied).</summary>
        public (double Start, double Size) SlotOf(int index)
        {
            if (Orientation == Orientation.Vertical)
            {
                int pinned = CountPinned();
                return (index * RowHeight + (index >= pinned ? VerticalGap(pinned) : 0), RowHeight);
            }

            double x = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                double width = IsPinned(Children[i]) ? PinnedTabWidth : CurrentTabWidth;
                if (i == index)
                {
                    return (x, width);
                }

                x += width + TabSpacing;
            }

            return (x, CurrentTabWidth);
        }

        /// <summary>The slot index whose middle is nearest a position (dragging).</summary>
        public int IndexAt(double position)
        {
            int count = Children.Count;
            for (int i = 0; i < count; i++)
            {
                (double start, double size) = SlotOf(i);
                if (position < start + size)
                {
                    return i;
                }
            }

            return Math.Max(0, count - 1);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int count = Children.Count;
            if (Orientation == Orientation.Vertical)
            {
                double rowWidth = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
                foreach (Control child in Children)
                {
                    child.Measure(new Size(rowWidth, RowHeight));
                }

                _extent = count * RowHeight + VerticalGap(CountPinned());
                _viewport = _extent;
                return new Size(rowWidth, _extent);
            }

            int pinned = 0;
            foreach (Control child in Children)
            {
                if (IsPinned(child))
                {
                    pinned++;
                }
            }

            int unpinned = count - pinned;
            double available = double.IsInfinity(availableSize.Width) ? count * MaxTabWidth : availableSize.Width;
            double spacing = Math.Max(0, count - 1) * TabSpacing;
            double width = unpinned == 0
                ? MaxTabWidth
                : Math.Clamp((available - pinned * PinnedTabWidth - spacing) / unpinned, MinTabWidth, MaxTabWidth);
            if (!double.IsNaN(FreezeTabWidth) && unpinned > 0)
            {
                width = FreezeTabWidth; // as wide as when the closing began: the next close button lands under the pointer
            }

            CurrentTabWidth = width;
            foreach (Control child in Children)
            {
                child.Measure(new Size(IsPinned(child) ? PinnedTabWidth : width, TabHeight));
            }

            bool wasOverflowing = IsOverflowing;
            _extent = pinned * PinnedTabWidth + unpinned * width + spacing;
            _viewport = Math.Min(available, _extent);
            if (wasOverflowing != IsOverflowing)
            {
                Dispatcher.UIThread.Post(() => OverflowChanged?.Invoke(this, EventArgs.Empty));
            }

            return new Size(_viewport, TabHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool vertical = Orientation == Orientation.Vertical;
            _viewport = vertical ? finalSize.Height : finalSize.Width;
            if (GetValue(ScrollOffsetProperty) > MaxScrollOffset)
            {
                SetCurrentValue(ScrollOffsetProperty, MaxScrollOffset);
            }

            double offset = vertical ? 0 : ScrollOffset;
            double position = 0;
            int pinnedCount = vertical ? CountPinned() : 0;
            double gap = vertical ? VerticalGap(pinnedCount) : 0;
            int index = 0;
            foreach (Control child in Children)
            {
                if (vertical && index++ == pinnedCount)
                {
                    position += gap;
                }

                double size = vertical ? RowHeight : IsPinned(child) ? PinnedTabWidth : CurrentTabWidth;
                double slot = position - offset;
                double at = ReferenceEquals(child, DragChild) ? DragPosition - offset : slot;
                child.ZIndex = ReferenceEquals(child, DragChild) ? 10 : 0;
                Rect rect = vertical ? new Rect(0, at, finalSize.Width, size) : new Rect(at, 0, size, finalSize.Height);

                if (AnimateMoves && !ReferenceEquals(child, DragChild) && _arrangedAt.TryGetValue(child, out double previous) &&
                    Math.Abs(previous - slot) > 0.5 && Math.Abs(previous - slot) < size * 3 + RowHeight)
                {
                    Slide(child, previous - slot, vertical);
                }

                _arrangedAt[child] = ReferenceEquals(child, DragChild) ? at : slot;
                child.Arrange(rect);
                position += size + (vertical ? 0 : TabSpacing);
            }

            if (_arrangedAt.Count > Children.Count * 2)
            {
                var alive = new HashSet<Control>(Children);
                foreach (Control gone in new List<Control>(_arrangedAt.Keys))
                {
                    if (!alive.Contains(gone))
                    {
                        _arrangedAt.Remove(gone);
                    }
                }
            }

            return finalSize;
        }

        /// <summary>Vertical: where the line between pinned and other tabs goes (NaN: no line).</summary>
        public double GroupLineY
        {
            get
            {
                int pinned = CountPinned();
                return Orientation == Orientation.Vertical && VerticalGap(pinned) > 0 ? Math.Round(pinned * RowHeight + GroupGap / 2) : double.NaN;
            }
        }

        /// <summary>Starts a child where it was and lets it slide to its new slot (only the moves of other tabs while dragging, reordering, closing).</summary>
        private static void Slide(Control child, double from, bool vertical)
        {
            child.Transitions = null;
            child.RenderTransform = TransformOperations.Parse(vertical ? $"translateY({from:0.##}px)" : $"translateX({from:0.##}px)");
            Dispatcher.UIThread.Post(() =>
            {
                child.Transitions = new Transitions
                {
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() }
                };
                child.RenderTransform = TransformOperations.Identity;
            }, DispatcherPriority.Render);
        }

        /// <summary>Scrolls the horizontal row so the child at <paramref name="index"/> is fully in view.</summary>
        public void BringIntoView(int index)
        {
            if (Orientation == Orientation.Vertical || index < 0 || index >= Children.Count)
            {
                return;
            }

            (double start, double size) = SlotOf(index);
            if (start < ScrollOffset)
            {
                ScrollOffset = start;
            }
            else if (start + size > ScrollOffset + _viewport)
            {
                ScrollOffset = start + size - _viewport;
            }
        }
    }
}
