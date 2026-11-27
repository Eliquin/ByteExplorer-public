// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.Views.Dialogs;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The Details header: one button per chosen column (click sorts, the right edge resizes) built from
    /// <see cref="DetailsColumnsState"/>, and its menu (right-click a header): hide this column, show or hide the usual
    /// ones, "More…" for every column (<see cref="ChooseColumnsDialog"/>), size to fit and reset, as in Explorer.
    /// </summary>
    public partial class FileListView
    {
        private readonly Dictionary<DetailsColumnKind, SymbolIcon> _sortGlyphs = new Dictionary<DetailsColumnKind, SymbolIcon>();

        /// <summary>The layout the header was built for (rebuilt only when the columns themselves change, not a width).</summary>
        private IReadOnlyList<DetailsColumnKind> _headerKinds;

        private void InitializeColumns()
        {
            BuildHeader();
        }

        private void OnDetailsColumnsChanged(object sender, EventArgs e) => ApplyColumns(DetailsColumnsState.Current);

        /// <summary>Shows <paramref name="layout"/> in the header and every row of this view.</summary>
        private void ApplyColumns(DetailsColumnLayout layout)
        {
            if (layout == null || ReferenceEquals(layout, _columns))
            {
                return;
            }

            _columns = layout;
            DetailsRowPanel.SetColumns(DetailsHost, layout);
            if (!SameKinds(_headerKinds, layout.Kinds))
            {
                BuildHeader();
            }
        }

        private static bool SameKinds(IReadOnlyList<DetailsColumnKind> a, IReadOnlyList<DetailsColumnKind> b)
        {
            if (a == null || b == null || a.Count != b.Count)
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

        private void BuildHeader()
        {
            DetailsHeader.Children.Clear();
            _sortGlyphs.Clear();
            _headerKinds = _columns.Kinds;
            for (int i = 0; i < _columns.Count; i++)
            {
                DetailsHeader.Children.Add(BuildHeaderCell(DetailsColumnCatalog.Get(_columns.KindAt(i)), i));
            }

            UpdateSortGlyphs();
        }

        private Control BuildHeaderCell(DetailsColumnInfo info, int index)
        {
            var glyph = new SymbolIcon { IsVisible = false, Name = "SortGlyph" + info.Key };
            glyph.Classes.Add("sortGlyph");
            DockPanel.SetDock(glyph, Dock.Right);
            _sortGlyphs[info.Kind] = glyph;

            var title = new TextBlock
            {
                Text = info.Title,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                HorizontalAlignment = info.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var content = new DockPanel();
            content.Children.Add(glyph);
            content.Children.Add(title);

            var button = new Button { Content = content, Tag = info.Kind };
            button.Classes.Add("colHeader");
            ToolTip.SetTip(button, info.Sort.HasValue ? info.Description
                : info.Needs != ItemDetailsNeeds.None ? info.Description + " (can't be sorted: read file by file)"
                : info.Description + " (can't be sorted)");
            AutomationProperties.SetName(button, info.Title);
            button.Click += OnColumnHeaderClick;

            var grip = new Thumb { Tag = index };
            grip.Classes.Add("colGrip");
            grip.DragDelta += OnColumnGripDragDelta;
            grip.DoubleTapped += OnColumnGripDoubleTapped;
            ToolTip.SetTip(grip, "Drag to resize · double-click to fit");

            var cell = new Panel();
            cell.Children.Add(button);
            cell.Children.Add(grip);
            return cell;
        }

        private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (_vm != null && sender is Control header && header.Tag is DetailsColumnKind kind &&
                DetailsColumnCatalog.Get(kind).Sort is FileListSortColumn column)
            {
                _vm.SetSortColumn(column);
            }
        }

        /// <summary>
        /// Resizes a Details column. The grip moves with the column edge, so each delta is relative to the previous one.
        /// The grip is a sibling of the sort button, so a resize can never also trigger a sort. Every pane follows (the
        /// columns are one app-wide set) and the widths are saved once the drag pauses.
        /// </summary>
        private void OnColumnGripDragDelta(object sender, VectorEventArgs e)
        {
            if (!(sender is Control grip) || !(grip.Tag is int column) || column < 0 || column >= _columns.Count)
            {
                return;
            }

            DetailsColumnsState.Set(_columns.WithWidth(column, _columns[column] + e.Vector.X));
            e.Handled = true;
        }

        private void OnColumnGripDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is Control grip && grip.Tag is int column && column >= 0 && column < _columns.Count)
            {
                e.Handled = true;
                SizeColumnToFit(_columns.KindAt(column));
            }
        }

        /// <summary>The sort column's header shows the direction; Type sorting also marks File extension (it sorts the same).</summary>
        private void UpdateSortGlyphs()
        {
            FileListSortColumn? sorted = _vm?.SortColumn;
            Symbol glyph = (_vm?.SortAscending ?? true) ? Symbol.ChevronUp : Symbol.ChevronDown;
            foreach (KeyValuePair<DetailsColumnKind, SymbolIcon> pair in _sortGlyphs)
            {
                bool visible = sorted.HasValue && DetailsColumnCatalog.Get(pair.Key).Sort == sorted;
                pair.Value.IsVisible = visible;
                if (visible)
                {
                    pair.Value.Symbol = glyph;
                }
            }
        }

        // ------------------------------------------------------------------ the header's menu

        /// <summary>The kind of column the header element <paramref name="source"/> belongs to; null outside a column.</summary>
        private bool TryGetHeaderColumn(Visual source, out DetailsColumnKind? kind)
        {
            kind = null;
            for (Visual visual = source; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Button button && button.Classes.Contains("colHeader") && button.Tag is DetailsColumnKind k)
                {
                    kind = k;
                }

                if (ReferenceEquals(visual, DetailsHeader) || ReferenceEquals(visual, DetailsHeaderStrip))
                {
                    return true;
                }

                if (ReferenceEquals(visual, this))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>Opens the column menu for a right-click on <paramref name="kind"/>'s header (null: the header's empty part).</summary>
        internal void ShowColumnMenu(Control anchor, DetailsColumnKind? kind)
        {
            var menu = new ContextMenu { ItemsSource = BuildColumnMenuItems(kind), Placement = PlacementMode.Pointer, PlacementTarget = anchor };
            menu.Closed += (_, _) => menu.ItemsSource = null;
            menu.Open(anchor);
        }

        /// <summary>The menu's items (internal for the tests).</summary>
        internal List<Control> BuildColumnMenuItems(DetailsColumnKind? kind)
        {
            var items = new List<Control>();
            DetailsColumnLayout layout = _columns;
            if (kind is DetailsColumnKind clicked)
            {
                DetailsColumnInfo info = DetailsColumnCatalog.Get(clicked);
                var hide = new MenuItem
                {
                    Header = "Hide “" + info.Title + "”",
                    Icon = MenuIcon(Symbol.EyeOff),
                    IsEnabled = clicked != DetailsColumnKind.Name
                };
                hide.Click += (_, _) => Guard(() => DetailsColumnsState.Set(DetailsColumnsState.Current.Without(clicked)));
                items.Add(hide);

                var fit = new MenuItem { Header = "Size column to fit", Icon = MenuIcon(Symbol.LayoutColumnThree) };
                fit.Click += (_, _) => Guard(() => SizeColumnToFit(clicked));
                items.Add(fit);
            }

            var fitAll = new MenuItem { Header = "Size all columns to fit", Icon = MenuIcon(Symbol.LayoutColumnThree) };
            fitAll.Click += (_, _) => Guard(SizeAllColumnsToFit);
            items.Add(fitAll);
            items.Add(new Separator());

            foreach (DetailsColumnKind menuKind in DetailsColumnCatalog.InMenu)
            {
                DetailsColumnKind column = menuKind;
                var item = new MenuItem
                {
                    Header = DetailsColumnCatalog.Get(column).Title,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = layout.Contains(column),
                    IsEnabled = column != DetailsColumnKind.Name
                };
                item.Click += (_, _) => Guard(() =>
                {
                    DetailsColumnLayout current = DetailsColumnsState.Current;
                    DetailsColumnsState.Set(current.Contains(column) ? current.Without(column) : current.With(column));
                });
                items.Add(item);
            }

            items.Add(new Separator());
            var more = new MenuItem { Header = "More…", Icon = MenuIcon(Symbol.Settings) };
            more.Click += (_, _) => Guard(() => ChooseColumnsAsync().Forget("choose the Details columns"));
            items.Add(more);
            var reset = new MenuItem
            {
                Header = "Reset columns",
                Icon = MenuIcon(Symbol.ArrowReset),
                IsEnabled = !ReferenceEquals(layout, DetailsColumnLayout.Default)
            };
            reset.Click += (_, _) => Guard(DetailsColumnsState.Reset);
            items.Add(reset);
            return items;
        }

        private static SymbolIcon MenuIcon(Symbol symbol) => new SymbolIcon { Symbol = symbol, FontSize = 16 };

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "A Details column menu command failed");
            }
        }

        private async System.Threading.Tasks.Task ChooseColumnsAsync()
        {
            if (!(TopLevel.GetTopLevel(this) is Window owner))
            {
                return;
            }

            var dialog = new ChooseColumnsDialog(DetailsColumnsState.Current);
            DetailsColumnLayout chosen = await dialog.ShowDialog<DetailsColumnLayout>(owner);
            if (chosen != null)
            {
                DetailsColumnsState.Set(chosen);
            }
        }

        // ------------------------------------------------------------------ size to fit

        /// <summary>Widens or narrows a column to its widest cell among the loaded items (and its title), as Explorer does.</summary>
        internal void SizeColumnToFit(DetailsColumnKind kind)
        {
            DetailsColumnLayout current = DetailsColumnsState.Current;
            int index = current.IndexOf(kind);
            if (index < 0)
            {
                return;
            }

            DetailsColumnsState.Set(current.WithWidth(index, MeasureColumn(kind)));
        }

        private void SizeAllColumnsToFit()
        {
            DetailsColumnLayout layout = DetailsColumnsState.Current;
            for (int i = 0; i < layout.Count; i++)
            {
                layout = layout.WithWidth(i, MeasureColumn(layout.KindAt(i)));
            }

            DetailsColumnsState.Set(layout);
        }

        /// <summary>
        /// The width <paramref name="kind"/>'s cells need: the title (with the sort arrow) and the longest text among up to
        /// the first 5000 items, measured with the row font. Tags measure their pills' text; per-file columns only what is
        /// read so far (the rest show a dash until read).
        /// </summary>
        private double MeasureColumn(DetailsColumnKind kind)
        {
            const double CellPadding = 16; // the cell's 8 px margins
            const double HeaderExtra = 30; // header padding and the sort arrow
            DetailsColumnInfo info = DetailsColumnCatalog.Get(kind);
            double fontSize = 13;
            Avalonia.Media.Typeface typeface = new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default);
            double Measure(string text, double size)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return 0;
                }

                var formatted = new Avalonia.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                    Avalonia.Media.FlowDirection.LeftToRight, typeface, size, null);
                return formatted.WidthIncludingTrailingWhitespace;
            }

            double width = Measure(info.Title, 12) + HeaderExtra;
            if (_vm?.Items is System.Collections.IList items)
            {
                int count = Math.Min(items.Count, 5000);
                for (int i = 0; i < count; i++)
                {
                    if (!(items[i] is ByteExplorer.ViewModels.FileListItemViewModel item))
                    {
                        continue;
                    }

                    double cell;
                    if (kind == DetailsColumnKind.Name)
                    {
                        cell = Measure(item.DisplayName, fontSize) + 40; // icon and its gap
                    }
                    else if (kind == DetailsColumnKind.Tags)
                    {
                        cell = 0;
                        foreach (Core.Tags.TagDefinition tag in Core.Tags.TagStore.Default.GetTags(item.FullPath))
                        {
                            cell += Measure(tag.Name, 11) + 18; // pill padding and gap
                        }
                    }
                    else
                    {
                        ItemDetails details = DetailsColumnCatalog.Get(kind).Needs != ItemDetailsNeeds.None
                            ? ItemDetailsCache.Peek(item.FullPath, item.LastWriteTimeUtc)
                            : null;
                        cell = Measure(FileItemView.CellText(item, kind, details), fontSize);
                    }

                    width = Math.Max(width, cell + CellPadding);
                }
            }

            return Math.Ceiling(width);
        }
    }
}
