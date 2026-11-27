// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// A message or a yes/no question. ShowDialog&lt;bool&gt; returns true for the confirm button; Escape and the close
    /// button return false. A destructive question puts the focus on Cancel, so Enter never deletes by accident.
    /// </summary>
    public sealed class MessageDialog : Window
    {
        public MessageDialog()
            : this("ByteExplorer", string.Empty, "OK", null, false)
        {
        }

        public MessageDialog(string title, string message, string confirmText, string cancelText, bool destructive)
        {
            DialogChrome.Apply(this, title, 440);

            var confirm = DialogChrome.Button(confirmText ?? "OK", isDefault: !destructive, isCancel: cancelText == null, accent: true);
            confirm.Click += (_, _) => Close(true);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(confirm);

            Button cancel = null;
            if (cancelText != null)
            {
                cancel = DialogChrome.Button(cancelText, isDefault: destructive, isCancel: true, accent: false);
                cancel.Click += (_, _) => Close(false);
                buttons.Children.Add(cancel);
            }

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 18 };
            body.Children.Add(DialogChrome.Text(title, 16, FontWeight.SemiBold));
            body.Children.Add(DialogChrome.Text(message, 13, FontWeight.Normal, "TextSecondary"));
            body.Children.Add(buttons);
            Content = body;

            Opened += (_, _) => (destructive && cancel != null ? cancel : confirm).Focus();
        }
    }
}
