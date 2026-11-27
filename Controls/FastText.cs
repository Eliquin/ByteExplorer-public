// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ByteExplorer.Controls
{
    /// <summary>
    /// A single-run text control for list rows: draws a shared, cached <see cref="TextLayout"/> instead of building one
    /// per control as TextBlock does.
    /// <para>
    /// Why: measured on a 100,000-item folder, changing a recycled row's TextBlock text cost 140–270 µs (TextBlock's inline
    /// processing, two layout builds, invalidation) against ~16 µs for the TextLayout itself, so the text controls were
    /// most of the cost of a page of rows. This control does one cache lookup per measure. The cache is keyed by the text
    /// and its formatting, so the strings rows share (types like "TXT File", sizes, dates, "File folder") are laid out
    /// once for the whole folder, and scrolling back reuses every name already seen.
    /// </para>
    /// <para>
    /// Supports what the file views need: font family/size/weight/style (inherited like TextBlock's), foreground,
    /// left/center/right alignment, end ellipsis, and wrapping to at most <see cref="MaxLines"/> lines (the Icons view's
    /// two-line names). An unconstrained layout is used whenever the text fits, so the width a row happens to have does
    /// not multiply the cache entries.
    /// </para>
    /// </summary>
    public sealed class FastText : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<FastText, string>(nameof(Text));

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner<FastText>();

        public static readonly StyledProperty<double> FontSizeProperty =
            TextElement.FontSizeProperty.AddOwner<FastText>();

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            TextElement.FontWeightProperty.AddOwner<FastText>();

        public static readonly StyledProperty<FontStyle> FontStyleProperty =
            TextElement.FontStyleProperty.AddOwner<FastText>();

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<FastText>();

        public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
            AvaloniaProperty.Register<FastText, TextAlignment>(nameof(TextAlignment), TextAlignment.Left);

        public static readonly StyledProperty<int> MaxLinesProperty =
            AvaloniaProperty.Register<FastText, int>(nameof(MaxLines), 1);

        private TextLayout _layout;

        // A wrapped layout positions its own lines across the full width it was built for (alignment included).
        private bool _wrappedAcrossWidth;
        private double _wrapWidth;

        static FastText()
        {
            AffectsMeasure<FastText>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, FontStyleProperty,
                ForegroundProperty, MaxLinesProperty);
            AffectsRender<FastText>(TextAlignmentProperty);
            ClipToBoundsProperty.OverrideDefaultValue<FastText>(false);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public FontStyle FontStyle
        {
            get => GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        /// <summary>1: one line with an end ellipsis. 2 or more: wrap, then ellipsis on the last line.</summary>
        public int MaxLines
        {
            get => GetValue(MaxLinesProperty);
            set => SetValue(MaxLinesProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            string text = Text;
            if (string.IsNullOrEmpty(text))
            {
                _layout = null;
                return default;
            }

            var typeface = new Typeface(FontFamily ?? FontFamily.Default, FontStyle, FontWeight);
            double fontSize = FontSize > 0 ? FontSize : 12;
            IBrush foreground = Foreground;
            int maxLines = Math.Max(1, MaxLines);

            TextLayout natural = TextLayoutCache.Get(text, typeface, fontSize, foreground, double.PositiveInfinity, 1);
            double available = availableSize.Width;
            _wrappedAcrossWidth = false;
            if (double.IsInfinity(available) || natural.WidthIncludingTrailingWhitespace <= available + 0.5)
            {
                _layout = natural;
            }
            else
            {
                _wrapWidth = Math.Max(1, Math.Floor(available));
                _layout = TextLayoutCache.Get(text, typeface, fontSize, foreground, _wrapWidth, maxLines, TextAlignment);
                _wrappedAcrossWidth = maxLines > 1;
            }

            double width = _wrappedAcrossWidth ? _wrapWidth : _layout.WidthIncludingTrailingWhitespace;
            return new Size(Math.Min(width, double.IsInfinity(available) ? double.MaxValue : available), _layout.Height);
        }

        /// <summary>
        /// A panel may measure with more room than it finally arranges (a Details column narrower than the text); the text
        /// is then trimmed to the arranged width so it never spills into the next column.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            TextLayout layout = _layout;
            if (layout != null && finalSize.Width > 0 && layout.WidthIncludingTrailingWhitespace > finalSize.Width + 0.5)
            {
                var typeface = new Typeface(FontFamily ?? FontFamily.Default, FontStyle, FontWeight);
                _wrapWidth = Math.Max(1, Math.Floor(finalSize.Width));
                _layout = TextLayoutCache.Get(Text, typeface, FontSize > 0 ? FontSize : 12, Foreground, _wrapWidth, Math.Max(1, MaxLines), TextAlignment);
                _wrappedAcrossWidth = MaxLines > 1;
            }

            return finalSize;
        }

        public override void Render(DrawingContext context)
        {
            TextLayout layout = _layout;
            if (layout == null)
            {
                return;
            }

            double x = _wrappedAcrossWidth ? Math.Max(0, (Bounds.Width - _wrapWidth) / 2) : TextAlignment switch
            {
                TextAlignment.Right => Math.Max(0, Bounds.Width - layout.WidthIncludingTrailingWhitespace),
                TextAlignment.Center => Math.Max(0, (Bounds.Width - layout.WidthIncludingTrailingWhitespace) / 2),
                _ => 0
            };
            double y = Math.Max(0, (Bounds.Height - layout.Height) / 2);
            layout.Draw(context, new Point(x, y));
        }
    }

    /// <summary>
    /// The shared text layouts of <see cref="FastText"/>: an LRU of up to <see cref="Capacity"/> layouts, UI thread only.
    /// Evicted layouts are not disposed: a control may still be drawing one, and their glyph data is released by the GC
    /// once no control holds them (the cap bounds the memory either way).
    /// <para>
    /// Each layout keeps its shaped glyphs and a native Skia text blob, about 2 KB in all. The cap was 12,000 (some 25 MB
    /// that stayed for the session); what is ever drawn soon after being laid out is the realized rows plus the two
    /// pages the view prepares ahead and one behind, a few thousand texts in the densest view on a large screen.
    /// </para>
    /// </summary>
    public static class TextLayoutCache
    {
        public const int Capacity = 4_096;

        private readonly struct Key : IEquatable<Key>
        {
            public Key(string text, Typeface typeface, double fontSize, IBrush foreground, double maxWidth, int maxLines, TextAlignment alignment)
            {
                Alignment = alignment;
                Text = text;
                Typeface = typeface;
                FontSize = fontSize;
                Foreground = foreground;
                MaxWidth = maxWidth;
                MaxLines = maxLines;
            }

            public string Text { get; }
            public Typeface Typeface { get; }
            public double FontSize { get; }
            public IBrush Foreground { get; }
            public double MaxWidth { get; }
            public int MaxLines { get; }
            public TextAlignment Alignment { get; }

            public bool Equals(Key other) =>
                FontSize.Equals(other.FontSize) && MaxWidth.Equals(other.MaxWidth) && MaxLines == other.MaxLines && Alignment == other.Alignment &&
                ReferenceEquals(Foreground, other.Foreground) && Typeface.Equals(other.Typeface) &&
                string.Equals(Text, other.Text, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Text, Typeface, FontSize, MaxWidth, MaxLines, Foreground, Alignment);
        }

        private static readonly Dictionary<Key, LinkedListNode<(Key Key, TextLayout Layout)>> Entries =
            new Dictionary<Key, LinkedListNode<(Key, TextLayout)>>(Capacity);
        private static readonly LinkedList<(Key Key, TextLayout Layout)> Lru = new LinkedList<(Key, TextLayout)>();

        /// <summary>How many lookups were served from the cache (diagnostics and tests).</summary>
        public static long Hits { get; private set; }

        /// <summary>How many layouts were built.</summary>
        public static long Misses { get; private set; }

        public static TextLayout Get(string text, Typeface typeface, double fontSize, IBrush foreground, double maxWidth, int maxLines, TextAlignment alignment = TextAlignment.Left)
        {
            // Alignment only matters inside a wrapped (multi-line) layout; single lines are positioned by the control.
            if (maxLines <= 1 || double.IsInfinity(maxWidth))
            {
                alignment = TextAlignment.Left;
            }

            var key = new Key(text, typeface, fontSize, foreground, maxWidth, maxLines, alignment);
            if (Entries.TryGetValue(key, out LinkedListNode<(Key, TextLayout)> node))
            {
                Hits++;
                if (!ReferenceEquals(node, Lru.Last))
                {
                    Lru.Remove(node);
                    Lru.AddLast(node);
                }

                return node.Value.Item2;
            }

            Misses++;
            TextLayout layout = double.IsInfinity(maxWidth)
                ? new TextLayout(text, typeface, fontSize, foreground)
                : new TextLayout(text, typeface, fontSize, foreground,
                    textAlignment: alignment,
                    textWrapping: maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    textTrimming: TextTrimming.CharacterEllipsis,
                    maxWidth: maxWidth,
                    maxLines: maxLines);

            Entries[key] = Lru.AddLast((key, layout));
            if (Entries.Count > Capacity)
            {
                LinkedListNode<(Key Key, TextLayout Layout)> oldest = Lru.First;
                Lru.RemoveFirst();
                Entries.Remove(oldest.Value.Key);
            }

            return layout;
        }
    }
}
