// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Pointer gestures on the lists, handled once for every list on tunnelling events (keyed off the event source)
    /// instead of per element: double-click to open, drag out, and the rubber band.
    /// <para>
    /// A press on an item never interferes with the list's own click selection (single, Ctrl, Shift), with one exception
    /// taken from Explorer: a plain press on an item that is already part of a multi-selection keeps the selection, so
    /// the whole selection can be dragged; it collapses to that item on release if no drag started.
    /// </para>
    /// </summary>
    public partial class FileListView
    {
        /// <summary>Auto-scroll interval of a rubber band held past the top or bottom edge.</summary>
        private static readonly TimeSpan BandScrollInterval = TimeSpan.FromMilliseconds(30);

        /// <summary>Most a band auto-scroll moves per tick; the speed grows with the distance past the edge.</summary>
        private const double BandScrollMaxStep = 60;

        private DispatcherTimer _bandScrollTimer;

        // A press on an item that may become a drag once the pointer crosses the threshold.
        private ListBox _armedList;
        private Point _armPoint;
        private int _collapseIndex = -1;

        // Folder options: the item a plain press was on, opened on release when one click opens (no drag in between).
        private ListBoxItem _armedContainer;
        private ListBoxItem _middleContainer;
        private Button _middleTile;

        // Ctrl+press on a selected item: the selection is kept for a Ctrl+drag (a copy); without a drag, the release
        // takes the item out of the selection, as Ctrl+click does (Explorer's order, so a Ctrl+drag never loses its item).
        private int _toggleOnReleaseIndex = -1;

        // A right-button press on an item, which becomes a right-button drag (the drop asks what to do).
        private ListBox _rightArmedList;
        private Point _rightArmPoint;

        // After a right-button drag, the right-click menu the button's release may still bring is not opened.
        private long _rightDragEndedAt = long.MinValue / 2;

        private BandState _band;

        /// <summary>
        /// One rubber-band gesture, in the coordinates of the list's items panel (that is, of the content, so it survives
        /// scrolling). The panel maps the rectangle to item index ranges by its cell geometry.
        /// </summary>
        private sealed class BandState
        {
            public ListBox List;
            public VirtualizingWrapPanel Panel;
            public ScrollViewer Scroller;
            public IPointer Pointer;
            public Point Start;
            public Point LastPointer;
            public List<(int Start, int End)> Base;
            public List<(int Start, int End)> Current = new List<(int Start, int End)>();
            public List<(int Start, int End)> Applied = new List<(int Start, int End)>();

            public BandState()
            {
                AddCurrent = (start, end) => Current.Add((start, end));
            }

            /// <summary>Allocated once per gesture, not per pointer move.</summary>
            public readonly Action<int, int> AddCurrent;
        }

        private void InitializePointerHandling()
        {
            _bandScrollTimer = new DispatcherTimer { Interval = BandScrollInterval };
            _bandScrollTimer.Tick += OnBandScrollTick;

            AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
            AddHandler(PointerCaptureLostEvent, OnPointerCaptureLostRouted, RoutingStrategies.Bubble);
        }

        /// <summary>
        /// The list of ours that <paramref name="source"/> is in, the item container it is in (if any), and whether it is
        /// on the list's chrome (a scroll bar), where a press must stay a scroll-bar press.
        /// </summary>
        private ListBox FindList(Visual source, out ListBoxItem container, out bool onChrome)
        {
            container = null;
            onChrome = false;
            for (Visual visual = source; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            {
                switch (visual)
                {
                    case ScrollBar _:
                        onChrome = true;
                        break;
                    case ListBoxItem item when container == null:
                        container = item;
                        break;
                    case ListBox list:
                        return KindOf(list) != ListKind.None ? list : null;
                }
            }

            container = null;
            return null;
        }

        /// <summary>
        /// The folder a Home or This PC tile under the pointer opens (a drive that's ready, a known folder, a frequent
        /// folder), or null.
        /// </summary>
        private static string TileFolderAt(Visual source, out Button tile)
        {
            for (Visual visual = source; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Button button && button.Classes.Contains("tile"))
                {
                    tile = button;
                    switch (button.DataContext)
                    {
                        case ThisPcDriveItem drive when drive.IsReady && !string.IsNullOrEmpty(drive.RootPath) && !drive.IsUnreachable:
                            return drive.RootPath;
                        case FavoriteFolderRowViewModel folder when !string.IsNullOrEmpty(folder.FullPath):
                            return folder.FullPath;
                    }

                    return button.Tag is string key ? ResolveQuickFolder(key) : null;
                }
            }

            tile = null;
            return null;
        }

        private void OnPointerPressedTunnel(object sender, PointerPressedEventArgs e)
        {
            _lastModifiers = e.KeyModifiers;
            _middleContainer = null;
            _middleTile = null;
            PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (_vm?.OpenWithMiddleClick != null && kind == PointerUpdateKind.MiddleButtonPressed &&
                TileFolderAt(e.Source as Visual, out Button tile) != null)
            {
                // A folder or drive tile on Home or This PC: a middle click opens it in a new tab too.
                _middleTile = tile;
                e.Handled = true;
                return;
            }

            if (_vm != null && kind == PointerUpdateKind.MiddleButtonPressed && _vm.OpenWithMiddleClick != null &&
                FindList(e.Source as Visual, out ListBoxItem middle, out bool middleOnChrome) != null && !middleOnChrome &&
                ItemOf(middle?.DataContext) is FileListItemViewModel { IsDirectory: true })
            {
                // A middle click on a folder opens it in a new tab (or the other pane), as in Explorer and browsers: on release.
                _middleContainer = middle;
                e.Handled = true;
                return;
            }

            _rightArmedList = null;
            if (_vm != null && kind == PointerUpdateKind.RightButtonPressed)
            {
                // Not handled: the list still selects the item under a right press. Moving with the button down drags.
                ListBox rightList = FindList(e.Source as Visual, out ListBoxItem rightContainer, out bool rightOnChrome);
                if (rightList != null && !rightOnChrome && rightContainer != null && KindOf(rightList) != ListKind.None)
                {
                    _rightArmedList = rightList;
                    _rightArmPoint = e.GetPosition(this);
                }

                return;
            }

            if (_vm == null || kind != PointerUpdateKind.LeftButtonPressed)
            {
                return;
            }

            ListBox list = FindList(e.Source as Visual, out ListBoxItem container, out bool onChrome);
            if (list == null || onChrome)
            {
                return;
            }

            if (container == null)
            {
                if (KindOf(list) == ListKind.Flat)
                {
                    StartBand(list, e);
                }

                return;
            }

            const KeyModifiers selectionModifiers = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta;
            int index = list.IndexFromContainer(container);
            if (IsCheckBoxPress(container, e))
            {
                // The item's check box: toggles just this item, as Ctrl+click does, and never opens it.
                e.Handled = true;
                ResetArm();
                ToggleSelection(list, index);
                return;
            }

            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.ClickCount < 2 && container.IsSelected && (e.KeyModifiers & command) != 0 && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                // Ctrl+press on a selected item: keep the selection, so Ctrl+drag copies it (into its own folder: a
                // duplicate); a Ctrl+click without a drag deselects it on release.
                e.Handled = true;
                container.Focus(NavigationMethod.Pointer);
                _toggleOnReleaseIndex = index;
                _armedList = list;
                _armPoint = e.GetPosition(this);
                return;
            }

            bool singleClick = FolderOptions.Current.OpenWithSingleClick;
            if (e.ClickCount >= 2)
            {
                e.Handled = true;
                ResetArm();
                if (!singleClick) // with one click opening, the first click already opened it
                {
                    OpenFromListAsync(list, ItemOf(container.DataContext)).Forget("OpenFromListAsync");
                }

                return;
            }

            if (singleClick && (e.KeyModifiers & selectionModifiers) == 0)
            {
                _armedContainer = container;
            }

            if (container.IsSelected && (e.KeyModifiers & selectionModifiers) == 0 && list.Selection.SelectedIndexes.Count > 1)
            {
                e.Handled = true;
                container.Focus(NavigationMethod.Pointer);
                _collapseIndex = index;
            }

            _armedList = list;
            _armPoint = e.GetPosition(this);
        }

        private void OnPointerMovedTunnel(object sender, PointerEventArgs e)
        {
            if (_rightArmedList != null)
            {
                if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                {
                    _rightArmedList = null;
                }
                else if (FileDragDropHelper.HasMovedPastDragThreshold(_rightArmPoint, e.GetPosition(this)))
                {
                    _rightArmedList = null;
                    List<string> dragged = SelectedPaths();
                    if (dragged.Count > 0 && (_vm == null || !_vm.IsInsideArchive))
                    {
                        BeginDragOutAsync(e, dragged, rightButton: true).Forget("right-button drag");
                    }
                }

                return;
            }

            if (_band != null)
            {
                _band.LastPointer = e.GetPosition(_band.List);
                UpdateBand();
                return;
            }

            if (_armedList == null)
            {
                return;
            }

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                ResetArm();
                return;
            }

            if (!FileDragDropHelper.HasMovedPastDragThreshold(_armPoint, e.GetPosition(this)))
            {
                return;
            }

            ResetArm();
            List<string> paths = SelectedPaths();
            if (paths.Count > 0)
            {
                // Items inside a ZIP file aren't files yet: they leave the archive by Copy or Extract, not by dragging.
                if (_vm == null || !_vm.IsInsideArchive)
                {
                    BeginDragOutAsync(e, paths).Forget("BeginDragOutAsync");
                }
            }
        }

        private void OnPointerReleasedTunnel(object sender, PointerReleasedEventArgs e)
        {
            if (_band != null)
            {
                EndBand();
                return;
            }

            Button middleTile = _middleTile;
            _middleTile = null;
            if (middleTile != null && e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                if (TileFolderAt(e.Source as Visual, out Button released) is string tileFolder && ReferenceEquals(released, middleTile) && _vm?.OpenWithMiddleClick != null)
                {
                    _vm.OpenWithMiddleClick(tileFolder).Forget("open a tile with a middle click");
                }

                return;
            }

            ListBoxItem middle = _middleContainer;
            _middleContainer = null;
            if (middle != null && e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                if (middle.Bounds.Contains(e.GetPosition(middle.GetVisualParent() as Visual ?? middle)) &&
                    ItemOf(middle.DataContext) is FileListItemViewModel { IsDirectory: true } folder && _vm?.OpenWithMiddleClick != null)
                {
                    _vm.OpenWithMiddleClick(folder.FullPath).Forget("open a folder with a middle click");
                }

                return;
            }

            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                _rightArmedList = null;
            }

            ListBox list = _armedList;
            int collapse = _collapseIndex;
            int toggle = _toggleOnReleaseIndex;
            ListBoxItem clicked = _armedContainer;
            ResetArm();
            if (list != null && toggle >= 0 && toggle < list.ItemCount && e.InitialPressMouseButton == MouseButton.Left)
            {
                // Ctrl+click on a selected item, no drag: it leaves the selection now.
                list.Selection.Deselect(toggle);
                return;
            }

            if (list != null && clicked != null && e.InitialPressMouseButton == MouseButton.Left &&
                (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) == 0 &&
                clicked.Bounds.Contains(e.GetPosition(clicked.GetVisualParent() as Visual ?? clicked)) &&
                ItemOf(clicked.DataContext) is FileListItemViewModel item)
            {
                // One click opens (Folder options), on release: a press that became a drag never gets here.
                OpenFromListAsync(list, item).Forget("OpenFromListAsync");
                return;
            }

            if (list != null && collapse >= 0 && collapse < list.ItemCount && e.InitialPressMouseButton == MouseButton.Left)
            {
                // A click (no drag) on an item of a multi-selection selects just that item.
                ISelectionModel selection = list.Selection;
                using (selection.BatchUpdate())
                {
                    selection.Clear();
                    selection.Select(collapse);
                    selection.AnchorIndex = collapse;
                }
            }
        }

        private void OnPointerCaptureLostRouted(object sender, PointerCaptureLostEventArgs e)
        {
            if (_band != null && ReferenceEquals(e.Source, _band.List))
            {
                EndBand();
            }
        }

        private void ResetArm()
        {
            _armedList = null;
            _collapseIndex = -1;
            _armedContainer = null;
            _toggleOnReleaseIndex = -1;
        }

        /// <summary>A press on an item's check box (Folder options): the owner-drawn views' box, or the templates' itemCheck area.</summary>
        private static bool IsCheckBoxPress(ListBoxItem container, PointerPressedEventArgs e)
        {
            if (!FolderOptions.Current.UseCheckBoxes)
            {
                return false;
            }

            for (Visual visual = e.Source as Visual; visual != null && !ReferenceEquals(visual, container); visual = visual.GetVisualParent())
            {
                if (visual is StyledElement styled && styled.Classes.Contains("itemCheck"))
                {
                    return true;
                }
            }

            FileItemView view = container.FindDescendantOfType<FileItemView>();
            return view != null && view.IsOnCheckBox(e.GetPosition(view));
        }

        private static void ToggleSelection(ListBox list, int index)
        {
            if (index < 0)
            {
                return;
            }

            ISelectionModel selection = list.Selection;
            if (selection.IsSelected(index))
            {
                selection.Deselect(index);
            }
            else
            {
                selection.Select(index);
            }

            selection.AnchorIndex = index;
        }

        /// <summary>Drags the selection out as files (to another pane, another app, or onto a folder in this list).</summary>
        private async Task BeginDragOutAsync(PointerEventArgs trigger, IReadOnlyList<string> paths, bool rightButton = false)
        {
            try
            {
                await FileDragDropHelper.BeginDragAsync(trigger, TopLevel.GetTopLevel(this)?.StorageProvider, paths, Log.Logger, rightButton);
                if (rightButton)
                {
                    _rightDragEndedAt = Environment.TickCount64;
                }
            }
            catch (Exception ex)
            {
                // BeginDragAsync never throws by contract; this guards the top of an async chain all the same.
                Log.Logger.Warning(ex, "File drag failed");
            }
        }

        // ------------------------------------------------------------------ rubber band

        /// <summary>
        /// Starts a rubber band on the empty area of a flat list. Plain: replaces the selection; Ctrl (Cmd): adds to it.
        /// Unlike the WPF version (which hit-tested realized containers each move), the band selects by geometry, so it
        /// keeps working past the viewport while the list auto-scrolls under it.
        /// </summary>
        private void StartBand(ListBox list, PointerPressedEventArgs e)
        {
            if (!(list.ItemsPanelRoot is VirtualizingWrapPanel panel))
            {
                return;
            }

            bool additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            _band = new BandState
            {
                List = list,
                Panel = panel,
                Scroller = list.Scroll as ScrollViewer,
                Pointer = e.Pointer,
                Start = e.GetPosition(panel),
                LastPointer = e.GetPosition(list),
                Base = additive ? SelectedRanges(list.Selection) : null,
            };

            if (!additive)
            {
                list.Selection.Clear();
            }

            e.Handled = true;
            e.Pointer.Capture(list);
            list.Focus(NavigationMethod.Pointer);
            _bandScrollTimer.Start();
            UpdateBand();
        }

        private void EndBand()
        {
            BandState band = _band;
            if (band == null)
            {
                return;
            }

            _band = null;
            _bandScrollTimer.Stop();
            SelectionBand.IsVisible = false;
            if (ReferenceEquals(band.Pointer.Captured, band.List))
            {
                band.Pointer.Capture(null);
            }
        }

        private void UpdateBand()
        {
            BandState band = _band;
            Point? current = band?.List.TranslatePoint(band.LastPointer, band.Panel);
            if (current == null)
            {
                return;
            }

            Rect rect = RectFromCorners(band.Start, current.Value);
            DrawBand(band, rect);
            ApplyBandSelection(band, rect);
        }

        private void DrawBand(BandState band, Rect rect)
        {
            Point? topLeft = band.Panel.TranslatePoint(rect.TopLeft, BandLayer);
            Point? bottomRight = band.Panel.TranslatePoint(rect.BottomRight, BandLayer);
            if (topLeft == null || bottomRight == null)
            {
                return;
            }

            Rect visible = RectFromCorners(topLeft.Value, bottomRight.Value);
            Control viewport = (Control)band.Scroller ?? band.List;
            Point? viewportOrigin = viewport.TranslatePoint(default, BandLayer);
            if (viewportOrigin != null)
            {
                Size viewportSize = band.Scroller?.Viewport ?? viewport.Bounds.Size;
                visible = visible.Intersect(new Rect(viewportOrigin.Value, viewportSize));
            }

            Canvas.SetLeft(SelectionBand, visible.X);
            Canvas.SetTop(SelectionBand, visible.Y);
            SelectionBand.Width = Math.Max(0, visible.Width);
            SelectionBand.Height = Math.Max(0, visible.Height);
            SelectionBand.IsVisible = true;
        }

        /// <summary>Selects the items under the band (plus the Ctrl base selection), touching the selection only when the covered set changes.</summary>
        private void ApplyBandSelection(BandState band, Rect rect)
        {
            band.Current.Clear();
            band.Panel.ForEachIndexRangeIn(rect, band.AddCurrent);

            if (SameRanges(band.Current, band.Applied))
            {
                return;
            }

            ISelectionModel selection = band.List.Selection;
            using (selection.BatchUpdate())
            {
                selection.Clear();
                if (band.Base != null)
                {
                    foreach ((int start, int end) in band.Base)
                    {
                        selection.SelectRange(start, end);
                    }
                }

                foreach ((int start, int end) in band.Current)
                {
                    selection.SelectRange(start, end);
                }
            }

            (band.Applied, band.Current) = (band.Current, band.Applied);
        }

        /// <summary>While the pointer is held above or below the list, scrolls toward it and extends the band.</summary>
        private void OnBandScrollTick(object sender, EventArgs e)
        {
            BandState band = _band;
            ScrollViewer scroller = band?.Scroller;
            if (scroller == null)
            {
                return;
            }

            Point? pointer = band.List.TranslatePoint(band.LastPointer, scroller);
            if (pointer == null)
            {
                return;
            }

            double height = scroller.Viewport.Height;
            double distance = pointer.Value.Y < 0 ? pointer.Value.Y : (pointer.Value.Y > height ? pointer.Value.Y - height : 0);
            if (distance == 0)
            {
                return;
            }

            double step = Math.Clamp(distance / 2, -BandScrollMaxStep, BandScrollMaxStep);
            double maxOffset = Math.Max(0, scroller.Extent.Height - height);
            double offset = Math.Clamp(scroller.Offset.Y + step, 0, maxOffset);
            if (offset != scroller.Offset.Y)
            {
                scroller.Offset = new Vector(scroller.Offset.X, offset);
                UpdateBand();
            }
        }

        private static Rect RectFromCorners(Point a, Point b) =>
            new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        private static bool SameRanges(List<(int Start, int End)> a, List<(int Start, int End)> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The selection as runs of consecutive indices (the Ctrl+band base), compact even for a select-all.</summary>
        private static List<(int Start, int End)> SelectedRanges(ISelectionModel selection)
        {
            var indices = new List<int>(selection.SelectedIndexes);
            indices.Sort();
            var ranges = new List<(int Start, int End)>();
            foreach (int index in indices)
            {
                if (ranges.Count > 0 && ranges[ranges.Count - 1].End >= index - 1)
                {
                    (int start, int end) = ranges[ranges.Count - 1];
                    ranges[ranges.Count - 1] = (start, Math.Max(end, index));
                }
                else
                {
                    ranges.Add((index, index));
                }
            }

            return ranges;
        }
    }
}
