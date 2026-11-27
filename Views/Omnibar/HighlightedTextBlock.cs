// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ByteExplorer.Views.Omnibar
{
    /// <summary>
    /// A text with some characters emphasised (the ones that matched what was typed): bold, in the accent color. Runs
    /// are built only when the text or the positions change, merging adjacent positions into one run.
    /// </summary>
    public sealed class HighlightedTextBlock : TextBlock
    {
        public static readonly StyledProperty<string> SourceTextProperty =
            AvaloniaProperty.Register<HighlightedTextBlock, string>(nameof(SourceText));

        public static readonly StyledProperty<int[]> HighlightsProperty =
            AvaloniaProperty.Register<HighlightedTextBlock, int[]>(nameof(Highlights));

        public static readonly StyledProperty<IBrush> HighlightBrushProperty =
            AvaloniaProperty.Register<HighlightedTextBlock, IBrush>(nameof(HighlightBrush));

        static HighlightedTextBlock()
        {
            SourceTextProperty.Changed.AddClassHandler<HighlightedTextBlock>((block, _) => block.Rebuild());
            HighlightsProperty.Changed.AddClassHandler<HighlightedTextBlock>((block, _) => block.Rebuild());
            HighlightBrushProperty.Changed.AddClassHandler<HighlightedTextBlock>((block, _) => block.Rebuild());
        }

        public string SourceText
        {
            get => GetValue(SourceTextProperty);
            set => SetValue(SourceTextProperty, value);
        }

        public int[] Highlights
        {
            get => GetValue(HighlightsProperty);
            set => SetValue(HighlightsProperty, value);
        }

        public IBrush HighlightBrush
        {
            get => GetValue(HighlightBrushProperty);
            set => SetValue(HighlightBrushProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(TextBlock);

        private void Rebuild()
        {
            string text = SourceText ?? string.Empty;
            int[] positions = Highlights;
            Inlines?.Clear();
            if (positions == null || positions.Length == 0)
            {
                Text = text;
                return;
            }

            Text = null;
            var inlines = Inlines ?? (Inlines = new InlineCollection());
            int index = 0;
            int p = 0;
            while (index < text.Length)
            {
                bool highlighted = p < positions.Length && positions[p] == index;
                int start = index;
                if (highlighted)
                {
                    while (index < text.Length && p < positions.Length && positions[p] == index)
                    {
                        index++;
                        p++;
                    }
                }
                else
                {
                    int next = p < positions.Length ? Math.Min(positions[p], text.Length) : text.Length;
                    index = Math.Max(next, index + 1);
                }

                var run = new Run(text.Substring(start, index - start));
                if (highlighted)
                {
                    run.FontWeight = FontWeight.Bold;
                    if (HighlightBrush != null)
                    {
                        run.Foreground = HighlightBrush;
                    }
                }

                inlines.Add(run);
            }
        }
    }
}
