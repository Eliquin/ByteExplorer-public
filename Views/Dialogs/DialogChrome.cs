// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>Shared look of the small dialogs: dark, fixed size, centered on the owner, not in the task bar.</summary>
    internal static class DialogChrome
    {
        public static void Apply(Window window, string title, double width)
        {
            window.Title = title;
            window.Width = width;
            window.SizeToContent = SizeToContent.Height;
            window.CanResize = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("ContentBrush").ToBinding();
            window[!TemplatedControl.FontFamilyProperty] = window.GetResourceObservable("UiFont").ToBinding();
            window.FontSize = 13;
        }

        public static Button Button(string text, bool isDefault, bool isCancel, bool accent)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                Padding = new Thickness(14, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (accent)
            {
                button.Classes.Add("accent");
            }

            return button;
        }

        public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, string brushKey = "TextPrimary")
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            block[!TextBlock.ForegroundProperty] = block.GetResourceObservable(brushKey).ToBinding();
            return block;
        }
    }
}
