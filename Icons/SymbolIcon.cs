// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ByteExplorer.Controls;

namespace ByteExplorer.Icons
{
    /// <summary>
    /// An icon from the bundled Fluent icon font. Same properties as the FluentIcons SymbolIcon it replaces (Symbol,
    /// IconVariant, FontSize, Foreground), so XAML only changes its namespace.
    /// <para>
    /// Desired size is FontSize by FontSize (unless Width/Height are set); the glyph is drawn centered from the shared
    /// text layout cache, so a thousand identical icons cost one layout.
    /// </para>
    /// </summary>
    public sealed class SymbolIcon : Control
    {
        /// <summary>The bundled icon font.</summary>
        public static readonly Typeface IconTypeface = new Typeface(new FontFamily("avares://ByteExplorer/Assets/Fonts#Seagull Fluent Icons"));

        public static readonly StyledProperty<Symbol> SymbolProperty =
            AvaloniaProperty.Register<SymbolIcon, Symbol>(nameof(Symbol));

        public static readonly StyledProperty<IconVariant> IconVariantProperty =
            AvaloniaProperty.Register<SymbolIcon, IconVariant>(nameof(IconVariant));

        public static readonly StyledProperty<double> FontSizeProperty =
            AvaloniaProperty.Register<SymbolIcon, double>(nameof(FontSize), 20);

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<SymbolIcon>();

        static SymbolIcon()
        {
            AffectsMeasure<SymbolIcon>(FontSizeProperty);
            AffectsRender<SymbolIcon>(SymbolProperty, IconVariantProperty, ForegroundProperty);
        }

        public Symbol Symbol
        {
            get => GetValue(SymbolProperty);
            set => SetValue(SymbolProperty, value);
        }

        public IconVariant IconVariant
        {
            get => GetValue(IconVariantProperty);
            set => SetValue(IconVariantProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = Math.Max(0, FontSize);
            return new Size(Math.Min(size, availableSize.Width), Math.Min(size, availableSize.Height));
        }

        public override void Render(DrawingContext context)
        {
            string glyph = SymbolGlyphs.Get(Symbol, IconVariant);
            double size = Math.Min(FontSize, Math.Min(Bounds.Width, Bounds.Height));
            if (string.IsNullOrEmpty(glyph) || size <= 0)
            {
                return;
            }

            TextLayout layout = TextLayoutCache.Get(glyph, IconTypeface, size, Foreground ?? Brushes.White, double.PositiveInfinity, 1);
            layout.Draw(context, new Point((Bounds.Width - layout.WidthIncludingTrailingWhitespace) / 2, (Bounds.Height - layout.Height) / 2));
        }
    }
}
