// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// Asks for a new name. ShowDialog&lt;string&gt; returns the trimmed name, or null when cancelled. The name is checked
    /// against the OS's rules while typing (FileNameRules), with the reason shown under the box and Rename disabled, so a
    /// bad name never reaches the file system. For a file, only the part before the extension is selected, as in
    /// Explorer, Finder and GNOME Files.
    /// </summary>
    public sealed class RenameDialog : Window
    {
        private readonly TextBox _nameBox;
        private readonly TextBlock _problem;
        private readonly Button _ok;
        private readonly string _currentName;

        public RenameDialog()
            : this(string.Empty, false)
        {
        }

        public RenameDialog(string currentName, bool isDirectory)
        {
            _currentName = currentName ?? string.Empty;
            DialogChrome.Apply(this, "Rename", 460);

            _nameBox = new TextBox { Text = _currentName, MinHeight = 32 };
            _nameBox.TextChanged += (_, _) => Validate();
            _nameBox.KeyDown += OnNameKeyDown;

            _problem = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "DangerBrush");
            _problem.IsVisible = false;

            _ok = DialogChrome.Button("Rename", isDefault: true, isCancel: false, accent: true);
            _ok.Click += (_, _) => Accept();
            var cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Close(null);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(DialogChrome.Text(isDirectory ? "Rename folder" : "Rename file", 16, FontWeight.SemiBold));
            body.Children.Add(_nameBox);
            body.Children.Add(_problem);
            body.Children.Add(buttons);
            Content = body;

            Opened += (_, _) =>
            {
                _nameBox.Focus();
                string extension = Path.GetExtension(_currentName);
                int end = !isDirectory && !string.IsNullOrEmpty(extension) && _currentName.Length > extension.Length
                    ? _currentName.Length - extension.Length
                    : _currentName.Length;
                _nameBox.SelectionStart = 0;
                _nameBox.SelectionEnd = end;
            };
        }

        private void Validate()
        {
            string name = _nameBox.Text?.Trim();
            string problem = string.IsNullOrEmpty(name) ? null : FileNameRules.Validate(name);
            _problem.Text = problem ?? string.Empty;
            _problem.IsVisible = problem != null;
            _ok.IsEnabled = !string.IsNullOrEmpty(name) && problem == null;
        }

        private void Accept()
        {
            string name = _nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name) || FileNameRules.Validate(name) != null)
            {
                Validate();
                return;
            }

            Close(name);
        }

        private void OnNameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        }
    }
}
