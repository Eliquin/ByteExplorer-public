// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services.FileOperations;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The error question of a file operation, as Total Commander asks it: which item failed and why, then Retry (after
    /// closing the program that holds it, reconnecting the drive, …), Skip, Skip all (no more questions for this
    /// operation) or Cancel. Escape or the close button cancels. The answer is <see cref="Choice"/>.
    /// </summary>
    public sealed class ErrorDialog : Window
    {
        public ErrorDialog()
            : this(new ErrorInfo(FileOperationKind.Copy, "file.txt", "The file is in use."))
        {
        }

        public ErrorDialog(ErrorInfo error)
        {
            string verb = error.Kind switch
            {
                FileOperationKind.Move => "moved",
                FileOperationKind.Delete => "deleted",
                _ => "copied"
            };
            string name = PathUtil.GetName(error.Path);
            string title = $"\"{name}\" couldn't be {verb}";
            DialogChrome.Apply(this, "Problem with an item", 480);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(DialogChrome.Text(title, 16, FontWeight.SemiBold));
            body.Children.Add(DialogChrome.Text(error.Message, 13, FontWeight.Normal, "TextPrimary"));
            body.Children.Add(DialogChrome.Text(error.Path, 12, FontWeight.Normal, "TextSecondary"));
            body.Children.Add(DialogChrome.Text("Retry after fixing the cause (close the program using it, reconnect the drive, free some space), or skip it: a skipped item can be retried from the operations panel when the rest is done.",
                12, FontWeight.Normal, "TextMuted"));

            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            Button retry = Add(buttons, "Retry", ErrorChoice.Retry, accent: true, isDefault: true);
            Add(buttons, "Skip", ErrorChoice.Skip, accent: false, isDefault: false);
            Button skipAll = Add(buttons, "Skip all", ErrorChoice.SkipAll, accent: false, isDefault: false);
            ToolTip.SetTip(skipAll, "Skip this and every other item that fails in this operation, without asking again");
            Add(buttons, "Cancel", ErrorChoice.Cancel, accent: false, isDefault: false, isCancel: true);
            body.Children.Add(buttons);
            Content = body;
            Opened += (_, _) => retry.Focus();
        }

        /// <summary>The answer; the close button counts as Cancel.</summary>
        public ErrorChoice Choice { get; private set; } = ErrorChoice.Cancel;

        private Button Add(WrapPanel panel, string text, ErrorChoice choice, bool accent, bool isDefault, bool isCancel = false)
        {
            Button button = DialogChrome.Button(text, isDefault, isCancel, accent);
            button.Margin = new Thickness(8, 0, 0, 6);
            button.Click += (_, _) =>
            {
                Choice = choice;
                Close(choice);
            };
            panel.Children.Add(button);
            return button;
        }
    }
}
