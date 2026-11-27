// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Controls;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// "Choose Details": every column the Details view can show, checked when shown, in the order they appear (the shown
    /// ones first, then the others by group). Move up/down reorders the shown ones, a width box sets the selected column's
    /// width, and the line under the list says what the column holds, as Explorer's dialog does. Columns read file by file
    /// say they can't be sorted. ShowDialog&lt;DetailsColumnLayout&gt; returns the new layout, or null when cancelled.
    /// </summary>
    public sealed class ChooseColumnsDialog : Window
    {
        private sealed class Row
        {
            public DetailsColumnInfo Info;
            public bool Shown;
            public double Width;
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly ListBox _list;
        private readonly TextBlock _description;
        private readonly Button _up;
        private readonly Button _down;
        private readonly NumericUpDown _width;
        private bool _updating;

        public ChooseColumnsDialog()
            : this(DetailsColumnLayout.Default)
        {
        }

        public ChooseColumnsDialog(DetailsColumnLayout current)
        {
            current ??= DetailsColumnLayout.Default;
            DialogChrome.Apply(this, "Choose columns", 520);
            LoadRows(current);

            _list = new ListBox { Height = 340, SelectionMode = SelectionMode.Single };
            // Compact rows, as in Explorer's list: the theme's item padding and the check box's touch height would show
            // only six columns at a time.
            _list.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
            {
                Setters =
                {
                    new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(10, 0)),
                    new Avalonia.Styling.Setter(MinHeightProperty, 0d)
                }
            });
            AutomationProperties.SetName(_list, "Columns");
            _list.SelectionChanged += (_, _) => UpdateSelection();
            _list.KeyDown += OnListKeyDown;

            _description = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "TextSecondary");
            _description.MinHeight = 34;

            _up = SideButton("Move up", () => Move(-1));
            _down = SideButton("Move down", () => Move(+1));
            var show = SideButton("Show", () => SetShown(true));
            var hide = SideButton("Hide", () => SetShown(false));
            var reset = SideButton("Reset", ResetRows);
            ToolTip.SetTip(reset, "The default columns: Name, Date modified, Type, Size and Tags");

            _width = new NumericUpDown
            {
                Minimum = (decimal)DetailsColumnLayout.MinColumnWidth,
                Maximum = (decimal)DetailsColumnLayout.MaxColumnWidth,
                Increment = 10,
                FormatString = "0",
                Width = 120
            };
            AutomationProperties.SetName(_width, "Width of the selected column, in pixels");
            _width.ValueChanged += (_, _) =>
            {
                if (!_updating && SelectedRow is Row row && _width.Value is decimal value)
                {
                    row.Width = (double)value;
                }
            };

            var side = new StackPanel { Spacing = 8, Width = 110 };
            side.Children.Add(_up);
            side.Children.Add(_down);
            side.Children.Add(show);
            side.Children.Add(hide);
            side.Children.Add(reset);

            var listAndSide = new DockPanel();
            DockPanel.SetDock(side, Dock.Right);
            side.Margin = new Thickness(12, 0, 0, 0);
            listAndSide.Children.Add(side);
            listAndSide.Children.Add(_list);

            var widthRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            TextBlock widthLabel = DialogChrome.Text("Width of selected column (pixels):");
            widthLabel.VerticalAlignment = VerticalAlignment.Center;
            widthRow.Children.Add(widthLabel);
            widthRow.Children.Add(_width);

            Button ok = DialogChrome.Button("OK", isDefault: true, isCancel: false, accent: true);
            ok.Click += (_, _) => Close(BuildLayout());
            Button cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Close(null);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(DialogChrome.Text("Choose the details you want to show", 16, FontWeight.SemiBold));
            body.Children.Add(DialogChrome.Text("The columns apply to the Details view in every tab and pane. Check a column to show it.", 12, FontWeight.Normal, "TextSecondary"));
            body.Children.Add(listAndSide);
            body.Children.Add(_description);
            body.Children.Add(widthRow);
            body.Children.Add(buttons);
            Content = body;

            RefreshList(0);
            Opened += (_, _) => _list.Focus();
        }

        private Row SelectedRow => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null;

        private void LoadRows(DetailsColumnLayout layout)
        {
            _rows.Clear();
            for (int i = 0; i < layout.Count; i++)
            {
                _rows.Add(new Row { Info = DetailsColumnCatalog.Get(layout.KindAt(i)), Shown = true, Width = layout[i] });
            }

            foreach (string group in new[] { "Basic", "Dates", "File system", "Photos", "Media" })
            {
                foreach (DetailsColumnInfo info in DetailsColumnCatalog.All.Where(c => c.Group == group && !layout.Contains(c.Kind)))
                {
                    _rows.Add(new Row { Info = info, Shown = false, Width = info.DefaultWidth });
                }
            }

            // A column in a group not listed above (none today) still appears.
            foreach (DetailsColumnInfo info in DetailsColumnCatalog.All.Where(c => _rows.All(r => r.Info.Kind != c.Kind)))
            {
                _rows.Add(new Row { Info = info, Shown = false, Width = info.DefaultWidth });
            }
        }

        private void RefreshList(int select)
        {
            _updating = true;
            try
            {
                _list.ItemsSource = _rows.Select(BuildRowView).ToList();
                _list.SelectedIndex = Math.Clamp(select, 0, _rows.Count - 1);
            }
            finally
            {
                _updating = false;
            }

            UpdateSelection();
        }

        private Control BuildRowView(Row row)
        {
            var check = new CheckBox
            {
                IsChecked = row.Shown,
                IsEnabled = row.Info.Kind != DetailsColumnKind.Name,
                Content = row.Info.Title,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 30,
                Padding = new Thickness(6, 0, 0, 0)
            };
            AutomationProperties.SetName(check, row.Info.Title);
            check.IsCheckedChanged += (_, _) =>
            {
                if (!_updating)
                {
                    row.Shown = check.IsChecked == true;
                    // After the click finishes: the shown columns are kept together at the top, so the row may move.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Normalize();
                        RefreshList(_rows.IndexOf(row));
                    });
                }
            };

            TextBlock group = DialogChrome.Text(row.Info.Sort.HasValue ? row.Info.Group : row.Info.Group + " · not sortable", 11, FontWeight.Normal, "TextSecondary");
            group.VerticalAlignment = VerticalAlignment.Center;
            group.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(group, Dock.Right);

            var panel = new DockPanel { Tag = row.Info.Kind };
            panel.Children.Add(group);
            panel.Children.Add(check);
            return panel;
        }

        private void UpdateSelection()
        {
            if (_updating)
            {
                return;
            }

            Row row = SelectedRow;
            int index = _list.SelectedIndex;
            int shownCount = _rows.Count(r => r.Shown);
            _description.Text = row == null
                ? string.Empty
                : row.Info.Description + (row.Info.Sort.HasValue ? "."
                    : row.Info.Needs != Infrastructure.ItemDetailsNeeds.None ? ". Its values are read file by file in the background, so it can't be sorted."
                    : ". It can't be sorted.");
            bool movable = row != null && row.Shown && row.Info.Kind != DetailsColumnKind.Name;
            _up.IsEnabled = movable && index > 1;
            _down.IsEnabled = movable && index < shownCount - 1;
            _updating = true;
            try
            {
                _width.IsEnabled = row != null;
                _width.Value = row == null ? null : (decimal)Math.Round(row.Width);
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && SelectedRow is Row row && row.Info.Kind != DetailsColumnKind.Name)
            {
                e.Handled = true;
                SetShown(!row.Shown);
            }
            else if (e.KeyModifiers == KeyModifiers.Alt && (e.Key == Key.Up || e.Key == Key.Down))
            {
                e.Handled = true;
                Move(e.Key == Key.Up ? -1 : +1);
            }
        }

        /// <summary>Checks or unchecks the selected column; a newly shown one joins the end of the shown ones.</summary>
        private void SetShown(bool shown)
        {
            Row row = SelectedRow;
            if (row == null || row.Info.Kind == DetailsColumnKind.Name || row.Shown == shown)
            {
                return;
            }

            row.Shown = shown;
            Normalize();
            RefreshList(_rows.IndexOf(row));
        }

        /// <summary>Moves the selected shown column one place up or down among the shown ones (Name stays first).</summary>
        private void Move(int direction)
        {
            Row row = SelectedRow;
            Normalize();
            int index = row == null ? -1 : _rows.IndexOf(row);
            int target = index + direction;
            int shownCount = _rows.Count(r => r.Shown);
            if (index <= 0 || !row.Shown || target <= 0 || target >= shownCount)
            {
                RefreshList(index);
                return;
            }

            (_rows[index], _rows[target]) = (_rows[target], _rows[index]);
            RefreshList(target);
        }

        private void ResetRows()
        {
            LoadRows(DetailsColumnLayout.Default);
            RefreshList(0);
        }

        /// <summary>Keeps the shown columns together at the top, in their order (a row checked in place moves up to them).</summary>
        private void Normalize()
        {
            List<Row> shown = _rows.Where(r => r.Shown).ToList();
            List<Row> hidden = _rows.Where(r => !r.Shown).ToList();
            _rows.Clear();
            _rows.AddRange(shown);
            _rows.AddRange(hidden);
        }

        /// <summary>The chosen columns as a layout (internal for the tests).</summary>
        internal DetailsColumnLayout BuildLayout()
        {
            var entries = _rows.Where(r => r.Shown || r.Info.Kind == DetailsColumnKind.Name)
                .Select(r => new DetailsColumnEntry { Key = r.Info.Key, Width = r.Width })
                .ToList();
            DetailsColumnLayout layout = DetailsColumnLayout.FromEntries(entries);
            return SameAsDefault(layout) ? DetailsColumnLayout.Default : layout;
        }

        private static bool SameAsDefault(DetailsColumnLayout layout)
        {
            DetailsColumnLayout d = DetailsColumnLayout.Default;
            if (layout.Count != d.Count)
            {
                return false;
            }

            for (int i = 0; i < d.Count; i++)
            {
                if (layout.KindAt(i) != d.KindAt(i) || Math.Abs(layout[i] - d[i]) > 0.5)
                {
                    return false;
                }
            }

            return true;
        }

        private static Button SideButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(10, 5)
            };
            button.Click += (_, _) => action();
            return button;
        }

        // Test hooks: select a column and check/uncheck or move it as the buttons do.
        internal void SelectForTest(DetailsColumnKind kind) => _list.SelectedIndex = _rows.FindIndex(r => r.Info.Kind == kind);

        internal void SetShownForTest(bool shown) => SetShown(shown);

        internal void MoveForTest(int direction) => Move(direction);
    }
}
