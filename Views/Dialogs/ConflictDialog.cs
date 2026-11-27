// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.ViewModels.FileOperations;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The name clash question: the item already at the destination and the incoming one side by side (size and date,
    /// the newer and the larger marked), then Replace (Merge for folders), Skip, Keep both or Cancel, and "Do this for
    /// all remaining conflicts". A file can't replace a folder or the reverse, so a type mismatch offers only Skip and
    /// Keep both. Escape or the close button cancels the operation. ShowDialog returns the decision.
    /// </summary>
    public sealed class ConflictDialog : Window
    {
        private readonly CheckBox _applyToAll;

        public ConflictDialog()
            : this(new ConflictInfo(ConflictKind.FileOverFile, "a", "b", 0, default, 0, default, false))
        {
        }

        public ConflictDialog(ConflictInfo conflict)
        {
            string name = PathUtil.GetName(conflict.DestinationPath);
            string title = conflict.Kind switch
            {
                ConflictKind.FolderOverFolder => "Merge folders?",
                ConflictKind.TypeMismatch => "Name already in use",
                _ => "Replace or skip?"
            };
            string message = conflict.Kind switch
            {
                ConflictKind.FolderOverFolder => $"The destination already has a folder named \"{name}\". Merging adds the incoming items to it; you'll be asked about any files with the same name.",
                ConflictKind.TypeMismatch => $"The destination already has {(conflict.DestinationSize < 0 ? "a folder" : "a file")} named \"{name}\".",
                _ => $"The destination already has a file named \"{name}\"."
            };

            DialogChrome.Apply(this, title, 500);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 14 };
            body.Children.Add(DialogChrome.Text(title, 16, FontWeight.SemiBold));
            body.Children.Add(DialogChrome.Text(message, 13, FontWeight.Normal, "TextSecondary"));

            bool bothFiles = conflict.SourceSize >= 0 && conflict.DestinationSize >= 0;
            body.Children.Add(Describe(conflict.IsMove ? "Moving" : "Copying", conflict.SourcePath, conflict.SourceSize, conflict.SourceModified,
                newer: bothFiles && conflict.SourceModified > conflict.DestinationModified,
                larger: bothFiles && conflict.SourceSize > conflict.DestinationSize));
            body.Children.Add(Describe("Already there", conflict.DestinationPath, conflict.DestinationSize, conflict.DestinationModified,
                newer: bothFiles && conflict.DestinationModified > conflict.SourceModified,
                larger: bothFiles && conflict.DestinationSize > conflict.SourceSize));

            _applyToAll = new CheckBox { Content = "Do this for all remaining conflicts" };
            _applyToAll[!ForegroundProperty] = this.GetResourceObservable("TextPrimary").ToBinding();
            body.Children.Add(_applyToAll);

            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (conflict.Kind != ConflictKind.TypeMismatch)
            {
                Button replace = DialogChrome.Button(conflict.Kind == ConflictKind.FolderOverFolder ? "Merge" : "Replace", isDefault: false, isCancel: false, accent: true);
                replace.Click += (_, _) => Answer(ConflictChoice.Replace);
                AddButton(buttons, replace);
            }

            if (conflict.Kind == ConflictKind.FileOverFile)
            {
                // With "for all remaining", this brings a copy up to date: newer files replace, the others are skipped.
                Button newer = DialogChrome.Button("Replace if newer", isDefault: false, isCancel: false, accent: false);
                ToolTip.SetTip(newer, "Replace the existing file only if the incoming one was modified later; otherwise skip it");
                newer.Click += (_, _) => Answer(ConflictChoice.ReplaceIfNewer);
                AddButton(buttons, newer);
            }

            Button skip = DialogChrome.Button("Skip", isDefault: false, isCancel: false, accent: false);
            skip.Click += (_, _) => Answer(ConflictChoice.Skip);
            AddButton(buttons, skip);

            Button keepBoth = DialogChrome.Button("Keep both", isDefault: true, isCancel: false, accent: conflict.Kind == ConflictKind.TypeMismatch);
            keepBoth.Click += (_, _) => Answer(ConflictChoice.KeepBoth);
            AddButton(buttons, keepBoth);

            Button cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Answer(ConflictChoice.Cancel);
            AddButton(buttons, cancel);
            body.Children.Add(buttons);
            Content = body;

            // Enter keeps both: the choice that loses nothing.
            Opened += (_, _) => keepBoth.Focus();
        }

        /// <summary>The answer; the close button counts as Cancel (no answer is no permission to go on).</summary>
        public ConflictDecision Decision { get; private set; } = new ConflictDecision(ConflictChoice.Cancel, false);

        private void Answer(ConflictChoice choice)
        {
            Decision = new ConflictDecision(choice, _applyToAll.IsChecked == true);
            Close(Decision);
        }

        private static void AddButton(WrapPanel panel, Button button)
        {
            button.Margin = new Thickness(8, 0, 0, 6);
            panel.Children.Add(button);
        }

        private Control Describe(string heading, string path, long size, DateTime modified, bool newer, bool larger)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8)
            };
            card[!Border.BorderBrushProperty] = this.GetResourceObservable("BorderLight").ToBinding();

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(DialogChrome.Text(heading, 12, FontWeight.SemiBold, "TextSecondary"));
            TextBlock pathText = DialogChrome.Text(path, 12.5);
            pathText.TextTrimming = TextTrimming.PrefixCharacterEllipsis;
            pathText.TextWrapping = TextWrapping.NoWrap;
            ToolTip.SetTip(pathText, path);
            stack.Children.Add(pathText);

            string sizeText = size < 0 ? "Folder" : FileOperationJob.FormatBytes(size) + (larger ? " (larger)" : string.Empty);
            string dateText = modified == default
                ? string.Empty
                : "Modified " + modified.ToString("g", CultureInfo.CurrentCulture) + (newer ? " (newer)" : string.Empty);
            stack.Children.Add(DialogChrome.Text(string.IsNullOrEmpty(dateText) ? sizeText : sizeText + " · " + dateText, 12, FontWeight.Normal, "TextSecondary"));
            card.Child = stack;
            return card;
        }
    }
}
