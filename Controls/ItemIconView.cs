// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure.SystemIcons;

namespace ByteExplorer.Controls
{
    /// <summary>
    /// One item's icon for the template views (Columns, the Nexus list): Explorer's icon when "Show Windows icons" is on
    /// and it has arrived, the app's glyph in its color otherwise. Drawn directly (no child controls), as the file
    /// views draw theirs, so a list of thousands of rows stays cheap.
    /// </summary>
    public sealed class ItemIconView : Control, ISystemIconObserver
    {
        public static readonly StyledProperty<string> PathProperty = AvaloniaProperty.Register<ItemIconView, string>(nameof(Path));
        public static readonly StyledProperty<bool> IsDirectoryProperty = AvaloniaProperty.Register<ItemIconView, bool>(nameof(IsDirectory));
        public static readonly StyledProperty<bool> IsSystemProperty = AvaloniaProperty.Register<ItemIconView, bool>(nameof(IsSystem));
        public static readonly StyledProperty<Symbol> GlyphProperty = AvaloniaProperty.Register<ItemIconView, Symbol>(nameof(Glyph), Symbol.Document);
        public static readonly StyledProperty<IBrush> GlyphBrushProperty = AvaloniaProperty.Register<ItemIconView, IBrush>(nameof(GlyphBrush), Brushes.Gray);
        public static readonly StyledProperty<bool> ShowSystemIconsProperty = AvaloniaProperty.Register<ItemIconView, bool>(nameof(ShowSystemIcons));

        private string _key;

        static ItemIconView()
        {
            AffectsRender<ItemIconView>(PathProperty, IsDirectoryProperty, IsSystemProperty, GlyphProperty, GlyphBrushProperty, ShowSystemIconsProperty);
        }

        public string Path
        {
            get => GetValue(PathProperty);
            set => SetValue(PathProperty, value);
        }

        public bool IsDirectory
        {
            get => GetValue(IsDirectoryProperty);
            set => SetValue(IsDirectoryProperty, value);
        }

        public bool IsSystem
        {
            get => GetValue(IsSystemProperty);
            set => SetValue(IsSystemProperty, value);
        }

        public Symbol Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public IBrush GlyphBrush
        {
            get => GetValue(GlyphBrushProperty);
            set => SetValue(GlyphBrushProperty, value);
        }

        public bool ShowSystemIcons
        {
            get => GetValue(ShowSystemIconsProperty);
            set => SetValue(ShowSystemIconsProperty, value);
        }

        void ISystemIconObserver.OnSystemIconReady() => InvalidateVisual();

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PathProperty || change.Property == IsDirectoryProperty || change.Property == IsSystemProperty)
            {
                _key = null;
            }
        }

        public override void Render(DrawingContext context)
        {
            var box = new Rect(Bounds.Size);
            double side = System.Math.Min(box.Width, box.Height);
            if (side <= 0)
            {
                return;
            }

            box = new Rect((box.Width - side) / 2, (box.Height - side) / 2, side, side);
            if (ShowSystemIcons && !string.IsNullOrEmpty(Path))
            {
                _key ??= SystemIconCache.KeyFor(Path, System.IO.Path.GetFileName(Path), IsDirectory, IsSystem);
                Avalonia.Media.Imaging.Bitmap bitmap = SystemIconCache.TryGet(_key, Path, IsDirectory, side * (VisualRoot?.RenderScaling ?? 1), this);
                if (bitmap != null)
                {
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                    {
                        context.DrawImage(bitmap, new Rect(bitmap.Size), box);
                    }

                    return;
                }
            }

            string glyph = IconGlyphs.Get(Glyph, IconVariant.Filled);
            if (string.IsNullOrEmpty(glyph))
            {
                return;
            }

            TextLayout layout = TextLayoutCache.Get(glyph, IconGlyphs.Typeface, side, GlyphBrush, double.PositiveInfinity, 1);
            layout.Draw(context, new Point(box.X + (box.Width - layout.WidthIncludingTrailingWhitespace) / 2, box.Y + (box.Height - layout.Height) / 2));
        }
    }
}
