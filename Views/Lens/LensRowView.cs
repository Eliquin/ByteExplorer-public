// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ByteExplorer.Controls;
using ByteExplorer.Core.Tags;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure.SystemIcons;
using ByteExplorer.ViewModels.Lens;
using ByteExplorer.ViewModels.Search;

namespace ByteExplorer.Views.Lens
{
    /// <summary>
    /// One row of a lens, drawn directly (no child controls, as the file views do), so a lens of a million photos scrolls
    /// as smoothly as a folder: either a time group's heading ("THIS AFTERNOON — 12 files" and a line), or a file: its icon
    /// (Explorer's with "Show Windows icons"), name, tag chips, the folder it is in, size and time.
    /// </summary>
    public sealed class LensRowView : Control, ISystemIconObserver
    {
        public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<LensRowView>();

        private const double IconSize = 18;
        private const double SizeColumn = 84;
        private const double TimeColumn = 90;
        private const double FolderShare = 0.38;
        private static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, JetBrains Mono, DejaVu Sans Mono, monospace");

        private IBrush _primary;
        private IBrush _secondary;
        private IBrush _muted;
        private IBrush _line;
        private IBrush _system;

        static LensRowView()
        {
            AffectsRender<LensRowView>(FontFamilyProperty);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        void ISystemIconObserver.OnSystemIconReady() => InvalidateVisual();

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            {
                _primary = null;
                InvalidateVisual();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TagStore.AnyChanged += OnTagsChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TagStore.AnyChanged -= OnTagsChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTagsChanged(object sender, EventArgs e) => InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, DataContext is LensGroupHeader ? 40 : 36);

        private void ResolveBrushes()
        {
            if (_primary != null)
            {
                return;
            }

            IBrush Find(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out object value) && value is IBrush brush ? brush : fallback;
            _primary = Find("TextPrimary", Brushes.Black);
            _secondary = Find("TextSecondary", Brushes.DimGray);
            _muted = Find("TextMuted", Brushes.Gray);
            _line = Find("BorderLight", Brushes.LightGray);
            _system = Find("SystemItemText", _primary);
        }

        public override void Render(DrawingContext context)
        {
            ResolveBrushes();
            var typeface = new Typeface(FontFamily ?? FontFamily.Default);
            switch (DataContext)
            {
                case LensGroupHeader header:
                    RenderHeader(context, header, typeface);
                    break;
                case SearchResultRow row:
                    RenderRow(context, row, typeface);
                    break;
            }
        }

        private void RenderHeader(DrawingContext context, LensGroupHeader header, Typeface typeface)
        {
            double width = Bounds.Width;
            double baseline = Bounds.Height - 12;
            var bold = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold);
            TextLayout label = TextLayoutCache.Get(header.Label.ToUpper(CultureInfo.CurrentCulture), bold, 11, _secondary, Math.Max(1, width / 2), 1);
            double y = baseline - label.Height + 3;
            label.Draw(context, new Point(12, y));
            double x = 12 + label.WidthIncludingTrailingWhitespace + 10;
            TextLayout count = TextLayoutCache.Get(header.CountText, typeface, 11, _muted, Math.Max(1, width / 3), 1);
            count.Draw(context, new Point(x, y));
            x += count.WidthIncludingTrailingWhitespace + 12;
            double lineY = Math.Round(y + label.Height / 2) + 0.5;
            if (width - 12 > x)
            {
                context.DrawLine(new Pen(_line, 1), new Point(x, lineY), new Point(width - 12, lineY));
            }
        }

        private void RenderRow(DrawingContext context, SearchResultRow row, Typeface typeface)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            IBrush primary = row.IsSystem ? _system : _primary;
            double timeX = width - 12 - TimeColumn;
            double sizeX = timeX - SizeColumn;
            double folderWidth = Math.Max(0, (sizeX - 60) * FolderShare);
            double folderX = sizeX - 12 - folderWidth;
            double nameRight = folderX - 16;

            // Icon.
            var iconBox = new Rect(14, (height - IconSize) / 2, IconSize, IconSize);
            Avalonia.Media.Imaging.Bitmap system = SystemIconCache.IsActive
                ? SystemIconCache.TryGet(row.FullPath, row.Name, false, row.IsSystem, IconSize * (VisualRoot?.RenderScaling ?? 1), this)
                : null;
            if (system != null)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                {
                    context.DrawImage(system, new Rect(system.Size), iconBox);
                }
            }
            else
            {
                string glyph = SymbolGlyphs.Get(row.IconGlyph, IconVariant.Regular);
                TextLayout icon = TextLayoutCache.Get(glyph, SymbolIcon.IconTypeface, IconSize, row.IconBrush ?? primary, double.PositiveInfinity, 1);
                icon.Draw(context, new Point(iconBox.X + (IconSize - icon.WidthIncludingTrailingWhitespace) / 2, (height - icon.Height) / 2));
            }

            // Name, then its tags as chips.
            double nameX = iconBox.Right + 12;
            IReadOnlyList<TagDefinition> tags = TagStore.Default.GetTags(row.FullPath);
            double chips = tags.Count == 0 ? 0 : Math.Min(nameRight - nameX - 60, 96 * Math.Min(2, tags.Count));
            using (context.PushClip(new Rect(0, 0, Math.Max(0, nameRight), height)))
            {
                TextLayout name = TextLayoutCache.Get(row.Name, typeface, 13, primary, Math.Max(1, nameRight - nameX - Math.Max(0, chips)), 1);
                name.Draw(context, new Point(nameX, (height - name.Height) / 2));
                double chipX = nameX + name.WidthIncludingTrailingWhitespace + 10;
                for (int i = 0; i < tags.Count && i < 2 && chips > 0; i++)
                {
                    chipX = DrawChip(context, tags[i], typeface, chipX, height, nameRight);
                }
            }

            // Folder (monospaced: paths read better aligned), size, time.
            if (folderWidth > 40)
            {
                TextLayout folder = TextLayoutCache.Get(row.FolderPath ?? string.Empty, new Typeface(Mono), 11.5, _secondary, folderWidth, 1);
                using (context.PushClip(new Rect(folderX, 0, folderWidth, height)))
                {
                    folder.Draw(context, new Point(folderX, (height - folder.Height) / 2));
                }
            }

            TextLayout size = TextLayoutCache.Get(row.SizeDisplay, typeface, 12, _secondary, SizeColumn - 8, 1, TextAlignment.Right);
            size.Draw(context, new Point(sizeX + SizeColumn - 8 - size.WidthIncludingTrailingWhitespace, (height - size.Height) / 2));
            TextLayout time = TextLayoutCache.Get(TimeText(row.ModifiedUtc), typeface, 12, _muted, TimeColumn, 1, TextAlignment.Right);
            time.Draw(context, new Point(timeX + TimeColumn - time.WidthIncludingTrailingWhitespace, (height - time.Height) / 2));
        }

        private static double DrawChip(DrawingContext context, TagDefinition tag, Typeface typeface, double x, double height, double right)
        {
            IBrush color = FileItemView.TagBrush(tag);
            TextLayout text = TextLayoutCache.Get(tag.Name, typeface, 11, color, 80, 1);
            double w = text.WidthIncludingTrailingWhitespace + 16;
            if (x + w > right)
            {
                return x;
            }

            var box = new Rect(x, (height - 20) / 2, w, 20);
            IBrush fill = color is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.16) : color;
            context.DrawRectangle(fill, null, box, 10, 10);
            text.Draw(context, new Point(x + 8, box.Y + (20 - text.Height) / 2));
            return x + w + 6;
        }

        /// <summary>"15:40" today, "Yesterday 09:15", "Mon 14:02" this week, "14 Jun" this year, else the date.</summary>
        internal static string TimeText(DateTime modifiedUtc)
        {
            if (modifiedUtc.Ticks <= DateTime.MinValue.Ticks + TimeSpan.TicksPerDay)
            {
                return string.Empty;
            }

            DateTime local = modifiedUtc.ToLocalTime();
            DateTime today = DateTime.Today;
            CultureInfo culture = CultureInfo.CurrentCulture;
            string clock = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
            if (local >= today)
            {
                return clock;
            }

            if (local >= today.AddDays(-1))
            {
                return "Yesterday " + clock;
            }

            if (local >= today.AddDays(-6))
            {
                return local.ToString("ddd", culture) + " " + clock;
            }

            return local.Year == today.Year ? local.ToString("d MMM", culture) : local.ToString("d MMM yyyy", culture);
        }
    }
}
