// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// Asks for one line of text, checked while typing: the validator returns null when the text is acceptable, else the
    /// reason, shown under the box with the OK button disabled. ShowDialog&lt;string&gt; returns the trimmed text, or null
    /// when cancelled.
    /// </summary>
    public sealed class TextPromptDialog : Window
    {
        private readonly TextBox _box;
        private readonly TextBlock _problem;
        private readonly Button _ok;
        private readonly Func<string, string> _validate;

        public TextPromptDialog()
            : this("ByteExplorer", string.Empty, string.Empty, null, "OK", null)
        {
        }

        public TextPromptDialog(string title, string message, string initialText, string placeholder, string okText, Func<string, string> validate)
        {
            _validate = validate;
            DialogChrome.Apply(this, title, 480);

            _box = new TextBox { Text = initialText ?? string.Empty, Watermark = placeholder, MinHeight = 32 };
            _box.TextChanged += (_, _) => Validate();
            _box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Accept();
                }
            };

            _problem = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "DangerBrush");
            _problem.IsVisible = false;

            _ok = DialogChrome.Button(okText ?? "OK", isDefault: true, isCancel: false, accent: true);
            _ok.Click += (_, _) => Accept();
            var cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Close(null);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(DialogChrome.Text(title, 16, FontWeight.SemiBold));
            if (!string.IsNullOrEmpty(message))
            {
                body.Children.Add(DialogChrome.Text(message, 13, FontWeight.Normal, "TextSecondary"));
            }

            body.Children.Add(_box);
            body.Children.Add(_problem);
            body.Children.Add(buttons);
            Content = body;

            Opened += (_, _) =>
            {
                _box.Focus();
                _box.SelectAll();
            };
            Validate();
        }

        private string Problem(string text) => string.IsNullOrWhiteSpace(text) ? string.Empty : _validate?.Invoke(text.Trim());

        private void Validate()
        {
            string problem = Problem(_box.Text);
            _ok.IsEnabled = problem == null;
            _problem.Text = problem ?? string.Empty;
            _problem.IsVisible = !string.IsNullOrEmpty(problem);
        }

        private void Accept()
        {
            if (Problem(_box.Text) == null)
            {
                Close(_box.Text.Trim());
            }
        }
    }
}
