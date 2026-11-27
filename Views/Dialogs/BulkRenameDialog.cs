// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// "Rename items": renames several items at once with rules that apply top to bottom (find and replace, remove
    /// characters, a new name with numbering, added text, the modified date, letter case, the extension), with a live
    /// preview of every old and new name. Names that can't be used (the OS's rules, two items getting the same name, a name
    /// already taken in the folder) are shown in red with the reason, and Rename stays disabled until none is left, so a
    /// bad batch never reaches the disk. It opens in Explorer's quick mode: numbering is on with the first item's name
    /// selected, so typing "Holiday" and pressing Enter gives "Holiday (1)", "Holiday (2)"… as Explorer's F2 does.
    /// ShowDialog&lt;IReadOnlyList&lt;BulkRenameMove&gt;&gt; returns the renames, or null when cancelled. The rules live
    /// in <see cref="BulkRename"/>, which the preview and the rename share.
    /// </summary>
    public sealed class BulkRenameDialog : Window
    {
        private readonly IReadOnlyList<BulkRenameItem> _items;
        private readonly Func<string, string, bool> _existsInFolder;

        private readonly CheckBox _findOn = Rule("Find and replace");
        private readonly TextBox _find = Field("Find", 170);
        private readonly TextBox _replace = Field("Replace with", 170);
        private readonly CheckBox _regex = Option("Regular expression");
        private readonly CheckBox _matchCase = Option("Match case");
        private readonly CheckBox _firstOnly = Option("First match only");
        private readonly CheckBox _includeExtension = Option("Include extension");

        private readonly CheckBox _removeOn = Rule("Remove characters");
        private readonly NumericUpDown _removeAt = Number(1, 1, 255);
        private readonly NumericUpDown _removeCount = Number(1, 1, 255);

        private readonly CheckBox _numberOn = Rule("New name with numbering");
        private readonly TextBox _pattern = Field("Holiday ({n})", 200);
        private readonly NumericUpDown _start = Number(1, 0, 999999999);
        private readonly NumericUpDown _step = Number(1, 1, 99999);
        private readonly NumericUpDown _digits = Number(1, 1, 10);
        private readonly CheckBox _keepExtension = Option("Keep extension");

        private readonly CheckBox _addOn = Rule("Add text");
        private readonly TextBox _prefix = Field("Before the name", 170);
        private readonly TextBox _suffix = Field("After the name", 170);

        private readonly CheckBox _dateOn = Rule("Insert modified date");
        private readonly ComboBox _datePosition = Choice("Before the name", "After the name");
        private readonly TextBox _dateFormat = Field("yyyy-MM-dd", 130);

        private readonly CheckBox _caseOn = Rule("Change case");
        private readonly ComboBox _case = Choice("lower case", "UPPER CASE", "Title Case", "Sentence case");

        private readonly CheckBox _extensionOn = Rule("Extension");
        private readonly ComboBox _extensionMode = Choice("Change to", "lower case", "UPPER CASE", "Remove");
        private readonly TextBox _newExtension = Field("txt", 90);

        private readonly ListBox _preview;
        private readonly TextBlock _summary;
        private readonly TextBlock _error;
        private readonly Button _ok;
        private bool _loading = true;

        public BulkRenameDialog()
            : this(Array.Empty<BulkRenameItem>(), null)
        {
        }

        public BulkRenameDialog(IReadOnlyList<BulkRenameItem> items, Func<string, string, bool> existsInFolder)
        {
            _items = items ?? Array.Empty<BulkRenameItem>();
            _existsInFolder = existsInFolder;
            DialogChrome.Apply(this, "Rename items", 860);

            _keepExtension.IsChecked = true;
            _dateFormat.Text = "yyyy-MM-dd";
            _numberOn.IsChecked = true;
            _pattern.Text = _items.Count > 0 ? BulkRename.Split(_items[0].Name, _items[0].IsDirectory).Stem : string.Empty;
            ToolTip.SetTip(_pattern, "{n} is the number, {name} the current name. Without {n}: \"Holiday\" gives Holiday (1), Holiday (2)…");
            ToolTip.SetTip(_find, "Text to find; with Regular expression, a .NET pattern ($1 in the replacement is the first group)");
            ToolTip.SetTip(_dateFormat, "yyyy year, MM month, dd day, HH hour, mm minute");

            var rules = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 14 };
            AddRule(rules, _findOn, _find, Label("with"), _replace, _regex, _matchCase, _firstOnly, _includeExtension);
            AddRule(rules, _removeOn, Label("From character"), _removeAt, Label("count"), _removeCount);
            AddRule(rules, _numberOn, _pattern, Label("Start"), _start, Label("Step"), _step, Label("Digits"), _digits, _keepExtension);
            AddRule(rules, _addOn, Label("Prefix"), _prefix, Label("Suffix"), _suffix);
            AddRule(rules, _dateOn, _datePosition, Label("Format"), _dateFormat);
            AddRule(rules, _caseOn, _case);
            AddRule(rules, _extensionOn, _extensionMode, _newExtension);

            _preview = new ListBox
            {
                Height = 240,
                SelectionMode = SelectionMode.Single,
                ItemTemplate = new FuncDataTemplate<BulkRenameRow>((row, _) => BuildRowView(row))
            };
            _preview.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
            {
                Setters =
                {
                    new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(10, 3)),
                    new Avalonia.Styling.Setter(MinHeightProperty, 0d)
                }
            });
            AutomationProperties.SetName(_preview, "Preview of the new names");

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*"), Margin = new Thickness(10, 0) };
            TextBlock oldHeader = DialogChrome.Text("Current name", 12, FontWeight.SemiBold, "TextSecondary");
            TextBlock newHeader = DialogChrome.Text("New name", 12, FontWeight.SemiBold, "TextSecondary");
            Grid.SetColumn(newHeader, 2);
            header.Children.Add(oldHeader);
            header.Children.Add(newHeader);

            _summary = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "TextSecondary");
            _error = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "DangerBrush");
            _error.IsVisible = false;

            _ok = DialogChrome.Button("Rename", isDefault: true, isCancel: false, accent: true);
            _ok.Click += (_, _) => Accept();
            Button cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Close(null);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(DialogChrome.Text(_items.Count == 1 ? "Rename 1 item" : $"Rename {_items.Count} items", 16, FontWeight.SemiBold));
            body.Children.Add(DialogChrome.Text("Switch on the rules you need; they apply from top to bottom. Numbering follows the order of the list.", 12, FontWeight.Normal, "TextSecondary"));
            body.Children.Add(rules);
            body.Children.Add(header);
            body.Children.Add(_preview);
            body.Children.Add(_summary);
            body.Children.Add(_error);
            body.Children.Add(buttons);
            Content = body;

            HookChanges();
            _loading = false;
            Refresh();

            Opened += (_, _) =>
            {
                _pattern.Focus();
                _pattern.SelectAll();
            };
        }

        /// <summary>The preview as it stands (internal for the tests).</summary>
        internal BulkRenamePlan CurrentPlan { get; private set; }

        internal bool CanAccept => _ok.IsEnabled;

        // Test hooks: the rule controls, changed as the person would.
        internal CheckBox FindRule => _findOn;
        internal TextBox FindBox => _find;
        internal TextBox ReplaceBox => _replace;
        internal CheckBox RegexBox => _regex;
        internal CheckBox NumberingRule => _numberOn;
        internal TextBox PatternBox => _pattern;
        internal NumericUpDown DigitsBox => _digits;
        internal CheckBox CaseRule => _caseOn;
        internal ComboBox CaseChoice => _case;
        internal ListBox PreviewList => _preview;

        internal void AcceptForTest() => Accept();

        /// <summary>The options as the controls show them.</summary>
        internal BulkRenameOptions ReadOptions() => new BulkRenameOptions
        {
            FindEnabled = _findOn.IsChecked == true,
            Find = _find.Text ?? string.Empty,
            Replace = _replace.Text ?? string.Empty,
            UseRegex = _regex.IsChecked == true,
            MatchCase = _matchCase.IsChecked == true,
            FirstOnly = _firstOnly.IsChecked == true,
            IncludeExtension = _includeExtension.IsChecked == true,

            RemoveEnabled = _removeOn.IsChecked == true,
            RemovePosition = ValueOf(_removeAt, 1),
            RemoveCount = ValueOf(_removeCount, 1),

            NumberingEnabled = _numberOn.IsChecked == true,
            Pattern = _pattern.Text ?? string.Empty,
            Start = ValueOf(_start, 1),
            Step = ValueOf(_step, 1),
            Digits = ValueOf(_digits, 1),
            KeepExtension = _keepExtension.IsChecked == true,

            AddTextEnabled = _addOn.IsChecked == true,
            Prefix = _prefix.Text ?? string.Empty,
            Suffix = _suffix.Text ?? string.Empty,

            DateEnabled = _dateOn.IsChecked == true,
            DatePosition = _datePosition.SelectedIndex == 1 ? BulkRenameDatePosition.AfterName : BulkRenameDatePosition.BeforeName,
            DateFormat = _dateFormat.Text ?? string.Empty,

            CaseEnabled = _caseOn.IsChecked == true,
            Case = (BulkRenameCase)Math.Max(0, _case.SelectedIndex),

            ExtensionEnabled = _extensionOn.IsChecked == true,
            ExtensionMode = (BulkRenameExtensionMode)Math.Max(0, _extensionMode.SelectedIndex),
            NewExtension = _newExtension.Text ?? string.Empty
        };

        private void HookChanges()
        {
            foreach (CheckBox box in new[] { _findOn, _regex, _matchCase, _firstOnly, _includeExtension, _removeOn, _numberOn, _keepExtension, _addOn, _dateOn, _caseOn, _extensionOn })
            {
                box.IsCheckedChanged += (_, _) => Refresh();
            }

            foreach (TextBox box in new[] { _find, _replace, _pattern, _prefix, _suffix, _dateFormat, _newExtension })
            {
                // The Text property rather than TextChanged, which Avalonia raises later: the preview follows each keystroke at once.
                box.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        Refresh();
                    }
                };
            }

            foreach (NumericUpDown box in new[] { _removeAt, _removeCount, _start, _step, _digits })
            {
                box.ValueChanged += (_, _) => Refresh();
            }

            foreach (ComboBox box in new[] { _datePosition, _case, _extensionMode })
            {
                box.SelectionChanged += (_, _) => Refresh();
            }
        }

        /// <summary>Recomputes the preview from the controls (every change: the rules are cheap, the list virtualized).</summary>
        private void Refresh()
        {
            if (_loading)
            {
                return;
            }

            _newExtension.IsEnabled = _extensionMode.SelectedIndex == (int)BulkRenameExtensionMode.ChangeTo;
            BulkRenamePlan plan = BulkRename.Plan(_items, ReadOptions(), _existsInFolder);
            CurrentPlan = plan;
            _preview.ItemsSource = plan.Rows;
            _ok.IsEnabled = plan.CanApply;
            _error.Text = plan.Error ?? string.Empty;
            _error.IsVisible = plan.Error != null;

            string changes = plan.ChangedCount == 1 ? "1 name changes" : $"{plan.ChangedCount} names change";
            string problems = plan.ProblemCount == 0 ? string.Empty
                : plan.ProblemCount == 1 ? " · 1 name can't be used" : $" · {plan.ProblemCount} names can't be used";
            _summary.Text = $"{_items.Count} items · {changes}{problems}";
        }

        private void Accept()
        {
            Refresh();
            if (CurrentPlan == null || !CurrentPlan.CanApply)
            {
                return;
            }

            Close(CurrentPlan.Moves);
        }

        private static Control BuildRowView(BulkRenameRow row)
        {
            if (row == null)
            {
                return new TextBlock();
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
            TextBlock oldName = DialogChrome.Text(row.Item.Name, 13, FontWeight.Normal, row.IsChanged ? "TextSecondary" : "TextPrimary");
            oldName.TextWrapping = TextWrapping.NoWrap;
            oldName.TextTrimming = TextTrimming.CharacterEllipsis;
            oldName.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(oldName);

            TextBlock arrow = DialogChrome.Text("→", 13, FontWeight.Normal, "TextSecondary");
            arrow.HorizontalAlignment = HorizontalAlignment.Center;
            arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock newName = DialogChrome.Text(row.NewName, 13, row.IsChanged ? FontWeight.SemiBold : FontWeight.Normal,
                row.Problem != null ? "DangerBrush" : "TextPrimary");
            newName.TextWrapping = TextWrapping.NoWrap;
            newName.TextTrimming = TextTrimming.CharacterEllipsis;
            right.Children.Add(newName);
            if (row.Problem != null)
            {
                TextBlock problem = DialogChrome.Text(row.Problem, 11, FontWeight.Normal, "DangerBrush");
                right.Children.Add(problem);
                ToolTip.SetTip(grid, row.Problem);
            }

            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            return grid;
        }

        private static void AddRule(Grid grid, CheckBox toggle, params Control[] controls)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(toggle, row);
            grid.Children.Add(toggle);

            var panel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (Control control in controls)
            {
                control.Margin = new Thickness(0, 2, 10, 2);
                control.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(control);
            }

            // The controls of a rule that is off stay as typed but greyed out, so switching it back on restores them.
            panel.IsEnabled = toggle.IsChecked == true;
            toggle.IsCheckedChanged += (_, _) => panel.IsEnabled = toggle.IsChecked == true;
            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, 1);
            grid.Children.Add(panel);
        }

        private static CheckBox Rule(string title)
        {
            var box = new CheckBox { Content = title, FontWeight = FontWeight.SemiBold, MinWidth = 190, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, title);
            return box;
        }

        private static CheckBox Option(string title)
        {
            var box = new CheckBox { Content = title };
            AutomationProperties.SetName(box, title);
            return box;
        }

        private static TextBox Field(string placeholder, double width)
        {
            var box = new TextBox { Watermark = placeholder, Width = width, MinHeight = 30 };
            AutomationProperties.SetName(box, placeholder);
            return box;
        }

        private static NumericUpDown Number(int value, int minimum, int maximum) => new NumericUpDown
        {
            Value = value,
            Minimum = minimum,
            Maximum = maximum,
            Increment = 1,
            FormatString = "0",
            Width = 110
        };

        private static ComboBox Choice(params string[] choices) => new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 140 };

        private static TextBlock Label(string text)
        {
            TextBlock label = DialogChrome.Text(text, 12, FontWeight.Normal, "TextSecondary");
            label.TextWrapping = TextWrapping.NoWrap;
            return label;
        }

        private static int ValueOf(NumericUpDown box, int fallback) =>
            box.Value is decimal value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : fallback;
    }
}
