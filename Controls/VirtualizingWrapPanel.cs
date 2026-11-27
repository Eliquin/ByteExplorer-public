// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace ByteExplorer.Controls
{
    /// <summary>
    /// A virtualizing, recycling panel of equal-size cells for the file lists: a wrap panel for Icons and Tiles (items
    /// flow left to right in rows), or, with <see cref="SingleColumn"/>, a list of full-width rows for Details, List,
    /// Columns and Nexus. Only the containers for rows in (or near) the viewport exist, and scrolling recycles them
    /// instead of building new ones, so a folder of a million items costs the same as a folder of a hundred.
    /// <para>
    /// Every cell has the size of the first realized container (the item templates have fixed sizes, set by the zoom
    /// level), which makes the layout pure arithmetic: an item's position, the extent, the realized range, the row
    /// under a point and the items inside a rubber-band rectangle are all computed without measuring anything else.
    /// When the zoom changes, the container's desired size changes, the panel re-measures and picks up the new size.
    /// </para>
    /// <para>
    /// Recycled containers stay children of the panel (hidden) and keep their item template's controls (see
    /// FileListBox), so reusing one only swaps its data. Avalonia's VirtualizingStackPanel removes and re-adds recycled
    /// containers, which re-attaches them to the logical tree, re-applies styles and rebuilds the item template every
    /// time; in profiles that made a Details row cost several times more to scroll in than a whole Icons tile here.
    /// </para>
    /// <para>
    /// Scrolling is the ScrollViewer's (the panel's desired height is the full extent); the panel follows the viewport
    /// through <see cref="Layoutable.EffectiveViewportChanged"/>, the same model as Avalonia's VirtualizingStackPanel, so
    /// ScrollViewer, scroll bars, wheel and touch scrolling behave exactly as for every other list. The list's current
    /// container is kept realized while it is scrolled out of view, so focus and keyboard navigation survive.
    /// </para>
    /// <para>
    /// Replaces the WPF build's VirtualizingWrapPanel (an IScrollInfo panel without recycling that the WPF views stopped
    /// using) and the row-grouping workaround (RowGroupedFileList) that replaced it: with the list's own selection model
    /// over one container per item, Icons and Tiles get native selection, keyboard navigation and focus like every other
    /// view, instead of a hand-maintained per-tile selection state.
    /// </para>
    /// </summary>
    public sealed class VirtualizingWrapPanel : VirtualizingPanel
    {
        /// <summary>
        /// Defines the <see cref="CacheLength"/> property: how much of a viewport's height is kept realized above and
        /// below the viewport, so small scrolls do not need a layout pass.
        /// </summary>
        public static readonly StyledProperty<double> CacheLengthProperty =
            AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(CacheLength), 0.5, validate: v => v >= 0 && !double.IsInfinity(v));

        /// <summary>
        /// Defines the <see cref="SingleColumn"/> property: one item per row, each row as wide as the panel (or as the
        /// item, when that is wider and the panel can scroll horizontally).
        /// </summary>
        public static readonly StyledProperty<bool> SingleColumnProperty =
            AvaloniaProperty.Register<VirtualizingWrapPanel, bool>(nameof(SingleColumn));

        private static readonly AttachedProperty<object> RecycleKeyProperty =
            AvaloniaProperty.RegisterAttached<VirtualizingWrapPanel, Control, object>("RecycleKey");

        /// <summary>Viewport height assumed before the panel learns its real viewport (first layout, or outside a ScrollViewer).</summary>
        private const double FallbackViewportHeight = 1080;

        // The realized containers, for the contiguous index range [_firstIndex, _firstIndex + _realized.Count).
        private List<Control> _realized = new List<Control>();
        private List<Control> _scratch = new List<Control>();
        private int _firstIndex;

        // The focused container while it is outside the realized range (see class remarks).
        private Control _focusedElement;
        private int _focusedIndex = -1;

        private readonly Dictionary<object, Stack<Control>> _recyclePool = new Dictionary<object, Stack<Control>>();

        private Rect _viewport;
        private Size _natural;
        private Size _cell;
        private double _rowWidth;
        private int _itemsPerRow = 1;
        private int _rowCount;

        // An index ScrollIntoView must have realized even though the viewport has not caught up with the scroll yet.
        private int _pendingScrollIndex = -1;

        static VirtualizingWrapPanel()
        {
            AffectsMeasure<VirtualizingWrapPanel>(CacheLengthProperty, SingleColumnProperty);
        }

        public VirtualizingWrapPanel()
        {
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        /// <summary>See <see cref="CacheLengthProperty"/>.</summary>
        public double CacheLength
        {
            get => GetValue(CacheLengthProperty);
            set => SetValue(CacheLengthProperty, value);
        }

        /// <summary>See <see cref="SingleColumnProperty"/>.</summary>
        public bool SingleColumn
        {
            get => GetValue(SingleColumnProperty);
            set => SetValue(SingleColumnProperty, value);
        }

        /// <summary>Items per row at the current width (at least 1).</summary>
        public int ItemsPerRow => _itemsPerRow;

        /// <summary>The size of one cell (one item's container), or an empty size before the first layout.</summary>
        public Size CellSize => _cell;

        /// <summary>Index of the first realized item, or -1 when nothing is realized.</summary>
        public int FirstRealizedIndex => _realized.Count > 0 ? _firstIndex : -1;

        /// <summary>Index of the last realized item, or -1 when nothing is realized.</summary>
        public int LastRealizedIndex => _realized.Count > 0 ? _firstIndex + _realized.Count - 1 : -1;

        /// <summary>
        /// Calls <paramref name="addRange"/> with each run of consecutive item indices whose cells intersect
        /// <paramref name="rect"/> (in panel coordinates): one run per row. Used by the rubber-band selection, which
        /// therefore also covers rows that are not realized (the band auto-scrolls past the viewport).
        /// </summary>
        public void ForEachIndexRangeIn(Rect rect, Action<int, int> addRange)
        {
            int count = Items.Count;
            if (addRange == null || count == 0 || _cell.Width <= 0 || _cell.Height <= 0 || rect.Width < 0 || rect.Height < 0)
            {
                return;
            }

            // A single-column row spans the whole width, whatever x the rectangle covers.
            int firstColumn = SingleColumn ? 0 : Math.Max(0, (int)Math.Floor(rect.Left / _cell.Width));
            int lastColumn = SingleColumn ? 0 : Math.Min(_itemsPerRow - 1, (int)Math.Floor(rect.Right / _cell.Width));
            int firstRow = Math.Max(0, (int)Math.Floor(rect.Top / _cell.Height));
            int lastRow = Math.Min(_rowCount - 1, (int)Math.Floor(rect.Bottom / _cell.Height));
            if (firstColumn > lastColumn || firstRow > lastRow)
            {
                return;
            }

            for (int row = firstRow; row <= lastRow; row++)
            {
                int start = row * _itemsPerRow + firstColumn;
                if (start >= count)
                {
                    break;
                }

                addRange(start, Math.Min(count - 1, row * _itemsPerRow + lastColumn));
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            IReadOnlyList<object> items = Items;
            int count = items.Count;
            if (count == 0 || ItemContainerGenerator == null)
            {
                RecycleAll();
                _rowCount = 0;
                return default;
            }

            bool finiteWidth = !double.IsInfinity(availableSize.Width);
            double width = finiteWidth ? availableSize.Width : _viewport.Width;

            if (_natural.Width <= 0 || _natural.Height <= 0)
            {
                _natural = MeasureSampleCell(items, count);
            }

            // Two passes at most: the first realized container may report a different size than the cell assumed (the
            // zoom changed), in which case the range is recomputed with the new size before anything is arranged.
            for (int pass = 0; pass < 2; pass++)
            {
                ComputeGrid(width, finiteWidth, count);
                Realize(items, count);

                Size measured = MeasureRealized();
                if (measured == _natural || measured.Width <= 0 || measured.Height <= 0)
                {
                    break;
                }

                _natural = measured;
            }

            ComputeGrid(width, finiteWidth, count);
            double desiredWidth = finiteWidth && width > 0 ? width : _itemsPerRow * _cell.Width;
            return new Size(desiredWidth, _rowCount * _cell.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // A horizontally scrolling viewer arranges the panel at least as wide as its viewport: rows fill that width.
            _rowWidth = SingleColumn ? Math.Max(_cell.Width, finalSize.Width) : _cell.Width;
            for (int i = 0; i < _realized.Count; i++)
            {
                _realized[i].Arrange(CellRect(_firstIndex + i));
            }

            _focusedElement?.Arrange(CellRect(_focusedIndex));
            return finalSize;
        }

        protected override Control ScrollIntoView(int index)
        {
            if (index < 0 || index >= Items.Count || ItemContainerGenerator == null)
            {
                return null;
            }

            if (_cell.Width <= 0 || _cell.Height <= 0 || !IsArrangeValid)
            {
                UpdateLayout();
            }

            _pendingScrollIndex = index;
            try
            {
                InvalidateMeasure();
                UpdateLayout();
                this.BringIntoView(CellRect(index));
                UpdateLayout();
            }
            finally
            {
                _pendingScrollIndex = -1;
            }

            return ContainerFromIndex(index);
        }

        protected override Control ContainerFromIndex(int index)
        {
            if (index >= _firstIndex && index < _firstIndex + _realized.Count)
            {
                return _realized[index - _firstIndex];
            }

            return index == _focusedIndex ? _focusedElement : null;
        }

        protected override int IndexFromContainer(Control container)
        {
            if (container == null)
            {
                return -1;
            }

            int position = _realized.IndexOf(container);
            if (position >= 0)
            {
                return _firstIndex + position;
            }

            return ReferenceEquals(container, _focusedElement) ? _focusedIndex : -1;
        }

        protected override IEnumerable<Control> GetRealizedContainers()
        {
            foreach (Control container in _realized)
            {
                yield return container;
            }

            if (_focusedElement != null)
            {
                yield return _focusedElement;
            }
        }

        /// <summary>
        /// Keyboard navigation in grid terms: Left/Right move one item, Up/Down one row (Down from the next-to-last row
        /// lands on the last item when the last row is shorter, as in Explorer), PageUp/PageDown one viewport of rows,
        /// Home/End the first/last item. Brings the target into view, which realizes it.
        /// </summary>
        protected override IInputElement GetControl(NavigationDirection direction, IInputElement from, bool wrap)
        {
            int count = Items.Count;
            if (count == 0)
            {
                return null;
            }

            int current = from is Control control ? IndexFromContainer(control) : -1;
            int target;
            if (current < 0)
            {
                target = direction == NavigationDirection.Last ? count - 1 : 0;
            }
            else
            {
                int perRow = Math.Max(1, _itemsPerRow);
                int pageRows = _cell.Height > 0 && _viewport.Height > 0 ? Math.Max(1, (int)(_viewport.Height / _cell.Height)) : 1;
                switch (direction)
                {
                    case NavigationDirection.First:
                        target = 0;
                        break;
                    case NavigationDirection.Last:
                        target = count - 1;
                        break;
                    case NavigationDirection.Left when SingleColumn:
                    case NavigationDirection.Right when SingleColumn:
                        return null; // a list has no columns to move between (Columns view uses Left/Right itself)
                    case NavigationDirection.Left:
                    case NavigationDirection.Previous:
                        target = current - 1;
                        break;
                    case NavigationDirection.Right:
                    case NavigationDirection.Next:
                        target = current + 1;
                        break;
                    case NavigationDirection.Up:
                        target = current - perRow;
                        break;
                    case NavigationDirection.Down:
                        target = current + perRow;
                        if (target >= count && current / perRow < (count - 1) / perRow)
                        {
                            target = count - 1;
                        }

                        break;
                    case NavigationDirection.PageUp:
                        target = Math.Max(0, current - pageRows * perRow);
                        break;
                    case NavigationDirection.PageDown:
                        target = Math.Min(count - 1, current + pageRows * perRow);
                        break;
                    default:
                        return null;
                }

                if (target < 0 || target >= count)
                {
                    if (!wrap)
                    {
                        return null;
                    }

                    target = ((target % count) + count) % count;
                }
            }

            return target == current ? null : ScrollIntoView(target);
        }

        /// <summary>
        /// The file list replaces its items wholesale (a new list per folder, filter or sort), so every change is
        /// handled as a reset: all containers are recycled and the next measure realizes the new range. That is also
        /// correct for incremental changes, without tracking container index shifts.
        /// </summary>
        protected override void OnItemsChanged(IReadOnlyList<object> items, NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(items, e);
            RecycleAll();
            if (items == null || items.Count == 0)
            {
                ReleasePool();
            }

            InvalidateMeasure();
        }

        /// <summary>
        /// Removes the pooled containers. The file view empties a list when it hides it (another view or page took over),
        /// and the pool of a hidden list is only memory: hundreds of item containers with their templates and composition
        /// visuals per list (Details, List, Icons and Tiles each kept theirs for the whole session). A folder change in a
        /// visible view replaces the items without emptying them, so the pool is kept where it saves work.
        /// </summary>
        private void ReleasePool()
        {
            foreach (Stack<Control> pool in _recyclePool.Values)
            {
                while (pool.Count > 0)
                {
                    RemoveInternalChild(pool.Pop());
                }
            }

            _recyclePool.Clear();
        }

        private void OnEffectiveViewportChanged(object sender, EffectiveViewportChangedEventArgs e)
        {
            Rect viewport = e.EffectiveViewport;
            if (viewport.Width <= 0 && viewport.Height <= 0)
            {
                return; // hidden or detached: nothing to realize, keep what is there
            }

            bool widthChanged = Math.Abs(viewport.Width - _viewport.Width) > 0.5;
            _viewport = viewport;

            if (widthChanged || _cell.Height <= 0 || _realized.Count == 0)
            {
                InvalidateMeasure();
                return;
            }

            // Re-measure only when the visible rows are no longer all realized; the cache rows absorb small scrolls.
            int lastRow = Math.Max(0, _rowCount - 1);
            int firstVisibleRow = Math.Clamp((int)Math.Floor(viewport.Top / _cell.Height), 0, lastRow);
            int lastVisibleRow = Math.Clamp((int)Math.Floor(Math.Max(viewport.Top, viewport.Bottom - 1) / _cell.Height), 0, lastRow);
            int perRow = Math.Max(1, _itemsPerRow);
            int firstRealizedRow = _firstIndex / perRow;
            int lastRealizedRow = (_firstIndex + _realized.Count - 1) / perRow;
            if (firstVisibleRow < firstRealizedRow || lastVisibleRow > lastRealizedRow)
            {
                InvalidateMeasure();
            }
        }

        /// <summary>
        /// Derives the cell (slot) size and the grid from the item's natural size: a wrap cell is the item's size; a
        /// single-column row is the panel's width, or, when the panel is given unlimited width (it scrolls horizontally),
        /// the item's width, widened to the viewport at arrange time.
        /// </summary>
        private void ComputeGrid(double width, bool finiteWidth, int count)
        {
            if (SingleColumn)
            {
                double rowWidth = finiteWidth && width > 0 ? width : _natural.Width;
                _cell = new Size(Math.Max(1, rowWidth), _natural.Height);
                _itemsPerRow = 1;
            }
            else
            {
                _cell = _natural;
                _itemsPerRow = width > 0 && !double.IsInfinity(width) ? Math.Max(1, (int)(width / _cell.Width)) : 1;
            }

            _rowCount = (count + _itemsPerRow - 1) / _itemsPerRow;
        }

        private Rect CellRect(int index)
        {
            int perRow = Math.Max(1, _itemsPerRow);
            double width = SingleColumn ? Math.Max(_cell.Width, _rowWidth) : _cell.Width;
            return new Rect((index % perRow) * _cell.Width, (index / perRow) * _cell.Height, width, _cell.Height);
        }

        /// <summary>Realizes (or reuses) one container just to learn the cell size, then returns it to the pool.</summary>
        private Size MeasureSampleCell(IReadOnlyList<object> items, int count)
        {
            int index = Math.Clamp(_realized.Count > 0 ? _firstIndex : 0, 0, count - 1);
            Control sample = ContainerFromIndex(index);
            bool borrowed = sample == null;
            if (borrowed)
            {
                sample = GetOrCreateContainer(items, index);
            }

            sample.Measure(Size.Infinity);
            Size size = sample.DesiredSize;
            if (borrowed)
            {
                Recycle(sample, index, force: true);
            }

            return new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        }

        /// <summary>Moves the realized range to the rows the viewport (plus cache) needs, recycling and reusing containers.</summary>
        private void Realize(IReadOnlyList<object> items, int count)
        {
            Rect viewport = _viewport;
            if (viewport.Height <= 0)
            {
                viewport = new Rect(0, 0, _viewport.Width, FallbackViewportHeight);
            }

            double cache = viewport.Height * CacheLength;
            int lastRow = Math.Max(0, _rowCount - 1);
            int firstRow = Math.Clamp((int)Math.Floor((viewport.Top - cache) / _cell.Height), 0, lastRow);
            int endRow = Math.Clamp((int)Math.Floor((viewport.Bottom + cache) / _cell.Height), firstRow, lastRow);

            if (_pendingScrollIndex >= 0 && _pendingScrollIndex < count)
            {
                // ScrollIntoView: realize around the target row; the viewport catches up after the scroll.
                int targetRow = _pendingScrollIndex / _itemsPerRow;
                if (targetRow < firstRow || targetRow > endRow)
                {
                    int span = endRow - firstRow;
                    firstRow = Math.Clamp(targetRow - span / 2, 0, lastRow);
                    endRow = Math.Clamp(firstRow + span, firstRow, lastRow);
                }
            }

            int newFirst = firstRow * _itemsPerRow;
            int newLast = Math.Min(count - 1, (endRow + 1) * _itemsPerRow - 1);

            // The new range is built in _scratch while the old list is detached: a container is cleared only once it is
            // no longer registered (IndexFromContainer returns -1 for it), as the generator contract requires.
            List<Control> old = _realized;
            int oldFirst = _firstIndex;
            int oldCount = old.Count;
            _realized = _scratch;
            _realized.Clear();
            _firstIndex = newFirst;

            // Recycle first, so the containers leaving the range are available for the ones entering it.
            for (int i = 0; i < oldCount; i++)
            {
                int index = oldFirst + i;
                if (index < newFirst || index > newLast)
                {
                    Recycle(old[i], index, force: false);
                }
            }

            if (_focusedElement != null && (_focusedIndex >= count || !IsCurrentContainer(_focusedElement)))
            {
                // Focus moved on (or the item is gone): no reason to keep the container any longer.
                Control stale = _focusedElement;
                _focusedElement = null;
                _focusedIndex = -1;
                Recycle(stale, -1, force: true);
            }

            for (int index = newFirst; index <= newLast; index++)
            {
                bool wasRealized = index >= oldFirst && index < oldFirst + oldCount;
                _realized.Add(wasRealized ? old[index - oldFirst] : GetOrCreateContainer(items, index));
            }

            old.Clear();
            _scratch = old;
        }

        /// <summary>Measures every realized container; returns the first one's natural size (the cell size it asks for).</summary>
        private Size MeasureRealized()
        {
            Size first = default;
            for (int i = 0; i < _realized.Count; i++)
            {
                Control container = _realized[i];
                if (i == 0)
                {
                    container.Measure(Size.Infinity);
                    first = container.DesiredSize;
                }
                else
                {
                    container.Measure(_cell);
                }
            }

            _focusedElement?.Measure(_cell);
            return first;
        }

        private Control GetOrCreateContainer(IReadOnlyList<object> items, int index)
        {
            if (_focusedElement != null && index == _focusedIndex)
            {
                Control focused = _focusedElement;
                _focusedElement = null;
                _focusedIndex = -1;
                return focused;
            }

            object item = items[index];
            var generator = ItemContainerGenerator;

            if (!generator.NeedsContainer(item, index, out object recycleKey))
            {
                // The item is its own container (not the case for the file list, which always uses ListBoxItems).
                var own = (Control)item;
                generator.PrepareItemContainer(own, item, index);
                AddInternalChild(own);
                generator.ItemContainerPrepared(own, item, index);
                return own;
            }

            if (recycleKey != null && _recyclePool.TryGetValue(recycleKey, out Stack<Control> pool) && pool.Count > 0)
            {
                Control recycled = pool.Pop();
                recycled.SetCurrentValue(IsVisibleProperty, true);
                generator.PrepareItemContainer(recycled, item, index);
                generator.ItemContainerPrepared(recycled, item, index);
                return recycled;
            }

            Control container = generator.CreateContainer(item, index, recycleKey);
            container.SetValue(RecycleKeyProperty, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
            return container;
        }

        /// <summary>
        /// The list's current item (the one Tab returns to, normally the focused one) is kept realized while it is
        /// scrolled away, as Avalonia's VirtualizingStackPanel does, so keyboard focus and navigation survive.
        /// </summary>
        private bool IsCurrentContainer(Control container) =>
            container.IsKeyboardFocusWithin ||
            (ItemsControl != null && ReferenceEquals(KeyboardNavigation.GetTabOnceActiveElement(ItemsControl), container));

        /// <summary>
        /// Returns a container to the pool (hidden but still a child, so reuse costs no visual-tree churn). The list's
        /// current container is kept instead, unless <paramref name="force"/>. The caller has already removed the
        /// container from the realized list.
        /// </summary>
        private void Recycle(Control container, int index, bool force)
        {
            if (!force && index >= 0 && IsCurrentContainer(container))
            {
                if (_focusedElement != null && !ReferenceEquals(_focusedElement, container))
                {
                    Control previous = _focusedElement;
                    _focusedElement = null;
                    _focusedIndex = -1;
                    Recycle(previous, -1, force: true);
                }

                _focusedElement = container;
                _focusedIndex = index;
                return;
            }

            object recycleKey = container.GetValue(RecycleKeyProperty);
            if (recycleKey == null)
            {
                RemoveInternalChild(container);
                return;
            }

            ItemContainerGenerator?.ClearItemContainer(container);
            container.SetCurrentValue(IsVisibleProperty, false);
            if (!_recyclePool.TryGetValue(recycleKey, out Stack<Control> pool))
            {
                pool = new Stack<Control>();
                _recyclePool[recycleKey] = pool;
            }

            pool.Push(container);
        }

        /// <summary>How many recycled containers are waiting in the pool (all recycle keys).</summary>
        public int PooledContainerCount
        {
            get
            {
                int total = 0;
                foreach (Stack<Control> pool in _recyclePool.Values)
                {
                    total += pool.Count;
                }

                return total;
            }
        }

        /// <summary>
        /// Creates up to <paramref name="count"/> containers for items just past the realized range, applies their
        /// templates and lays them out, then parks them in the recycle pool. Called on idle time after a folder opens:
        /// the first scroll past the initially realized rows then reuses ready containers instead of building templates
        /// on a visible frame (measured: a one-off 50-165 ms frame on the first scroll in the Icons views). Returns how
        /// many were created.
        /// </summary>
        public int WarmContainerPool(int count)
        {
            IReadOnlyList<object> items = Items;
            var generator = ItemContainerGenerator;
            if (generator == null || items.Count == 0 || count <= 0 || _cell.Width <= 0 || _cell.Height <= 0)
            {
                return 0;
            }

            int created = 0;
            int index = Math.Min(items.Count - 1, Math.Max(0, LastRealizedIndex + 1));
            for (; created < count; created++)
            {
                object item = items[index];
                if (!generator.NeedsContainer(item, index, out object recycleKey) || recycleKey == null)
                {
                    break;
                }

                Control container = generator.CreateContainer(item, index, recycleKey);
                container.SetValue(RecycleKeyProperty, recycleKey);
                generator.PrepareItemContainer(container, item, index);
                AddInternalChild(container);
                generator.ItemContainerPrepared(container, item, index);
                container.Measure(_cell);
                Recycle(container, -1, force: true);
            }

            return created;
        }

        private void RecycleAll()
        {
            List<Control> old = _realized;
            int oldFirst = _firstIndex;
            _realized = _scratch;
            _realized.Clear();
            _firstIndex = 0;
            for (int i = 0; i < old.Count; i++)
            {
                Recycle(old[i], oldFirst + i, force: true);
            }

            old.Clear();
            _scratch = old;

            if (_focusedElement != null)
            {
                Control focused = _focusedElement;
                _focusedElement = null;
                _focusedIndex = -1;
                Recycle(focused, -1, force: true);
            }
        }
    }
}
