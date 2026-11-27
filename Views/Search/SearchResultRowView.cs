// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Utilities;
using ByteExplorer.Controls;
using ByteExplorer.Icons;
using ByteExplorer.ViewModels.Search;

namespace ByteExplorer.Views.Search
{
    /// <summary>
    /// One search result row, drawn by a single control: the type icon, the name with its matching letters highlighted,
    /// the folder (trimmed from the left so its end stays readable), size, date and type, in the columns of the header
    /// (2.2*, 3*, 96, 150, 120).
    /// <para>
    /// Why not a template of TextBlocks: the row was about ten controls, each built, bound, measured and arranged again
    /// whenever a recycled row showed another result, and the folder column used TextBlock's PrefixCharacterEllipsis,
    /// which re-shapes a long path many times to find where to cut it. Dragging the scroll bar of 94,000 results to the
    /// middle realized a fresh page of rows on every move and kept the UI thread busy for hundreds of milliseconds per
    /// step, so the window stopped responding. This control draws from shared, cached text layouts (TextLayoutCache, as
    /// the file list does): the name's highlighted layout and the trimmed folder are kept with the result, the folder is
    /// trimmed with a binary search over its end (a handful of measurements, once per result and width), and types,
    /// sizes and dates are laid out once for all rows.
    /// </para>
    /// <para>
    /// Hidden results are drawn translucent and system ones in the system-item color, as in the file list.
    /// </para>
    /// </summary>
    public sealed class SearchResultRowView : Control, Infrastructure.SystemIcons.ISystemIconObserver
    {
        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner<SearchResultRowView>();

        private const double RowHeight = 30;
        private const double NameFontSize = 13;
        private const double CellFontSize = 12;
        private const double IconSize = 16;

        void Infrastructure.SystemIcons.ISystemIconObserver.OnSystemIconReady() => InvalidateVisual();
        private const double SizeColumn = 96;
        private const double DateColumn = 150;
        private const double TypeColumn = 120;
        private const double NameStars = 2.2;
        private const double FolderStars = 3;
        private const string Ellipsis = "…";

        private SearchResultRow _row;
        private IBrush _primary;
        private IBrush _secondary;
        private IBrush _accent;
        private IBrush _system;

        static SearchResultRowView()
        {
            AffectsRender<SearchResultRowView>(FontFamilyProperty);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        /// <summary>The column widths for a row this wide: name, folder (sharing what the fixed columns leave), size, date, type.</summary>
        public static (double Name, double Folder, double Size, double Date, double Type) Columns(double width)
        {
            double rest = Math.Max(0, width - SizeColumn - DateColumn - TypeColumn);
            double name = rest * NameStars / (NameStars + FolderStars);
            return (name, rest - name, SizeColumn, DateColumn, TypeColumn);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            _row = DataContext as SearchResultRow;
            AutomationProperties.SetName(this, _row?.Name);
            InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            {
                _primary = _secondary = _accent = _system = null;
                InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(0, RowHeight);

        public override void Render(DrawingContext context)
        {
            SearchResultRow row = _row;
            if (row == null || Bounds.Width <= 0)
            {
                return;
            }

            if (row.IsHidden)
            {
                using (context.PushOpacity(Controls.FileItemView.HiddenItemOpacity))
                {
                    RenderRow(context, row);
                }
            }
            else
            {
                RenderRow(context, row);
            }
        }

        private void RenderRow(DrawingContext context, SearchResultRow row)
        {
            ResolveBrushes();
            IBrush primary = row.IsSystem ? _system : _primary;
            IBrush secondary = row.IsSystem ? _system : _secondary;
            var typeface = new Typeface(FontFamily ?? FontFamily.Default);
            double height = Bounds.Height;
            (double name, double folder, double size, double date, double type) = Columns(Bounds.Width);

            // Name column: icon, then the highlighted name.
            string glyph = SymbolGlyphs.Get(row.IconGlyph, IconVariant.Filled);
            TextLayout icon = TextLayoutCache.Get(glyph, SymbolIcon.IconTypeface, IconSize, row.IconBrush ?? primary, double.PositiveInfinity, 1);
            double iconX = 10;
            using (context.PushClip(new Rect(0, 0, name, height)))
            {
                // Folder options › "Show Windows icons": Explorer's icon once it has arrived (the glyph until then).
                Avalonia.Media.Imaging.Bitmap system = Infrastructure.SystemIcons.SystemIconCache.IsActive
                    ? Infrastructure.SystemIcons.SystemIconCache.TryGet(row.FullPath, row.Name, row.IsDirectory, row.IsSystem, IconSize * (VisualRoot?.RenderScaling ?? 1), this)
                    : null;
                if (system != null)
                {
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                    {
                        context.DrawImage(system, new Rect(system.Size), new Rect(iconX, (height - IconSize) / 2, IconSize, IconSize));
                    }
                }
                else
                {
                    icon.Draw(context, new Point(iconX + (IconSize - icon.WidthIncludingTrailingWhitespace) / 2, (height - icon.Height) / 2));
                }
                double nameX = iconX + IconSize + 8;
                double dots = DrawTagDots(context, row.FullPath, name - 8, height / 2);
                TextLayout nameLayout = NameLayout(row, typeface, primary, Math.Max(1, name - nameX - 6 - dots));
                nameLayout.Draw(context, new Point(nameX, (height - nameLayout.Height) / 2));
                if (row.IsContentMatch)
                {
                    DrawContains(context, row, typeface, secondary, nameX + nameLayout.WidthIncludingTrailingWhitespace + 12, name - 6 - dots, height);
                }
            }

            double x = name;
            DrawCell(context, FolderText(row, typeface, secondary, Math.Max(1, folder - 16)), typeface, secondary, x + 10, folder - 16, height, alignRight: false);
            x += folder;
            DrawCell(context, row.SizeDisplay, typeface, secondary, x + 6, size - 16, height, alignRight: true);
            x += size;
            DrawCell(context, row.ModifiedDisplay, typeface, secondary, x + 10, date - 16, height, alignRight: false);
            x += date;
            DrawCell(context, row.TypeDisplay, typeface, secondary, x + 10, type - 16, height, alignRight: false);
        }

        /// <summary>
        /// "Contains: …the words in their text…" after the name of a result found by its contents, in the room the name
        /// leaves. The snippet is read in the background the first time the row is drawn (<see cref="ContentSnippetLoader"/>).
        /// </summary>
        private void DrawContains(DrawingContext context, SearchResultRow row, Typeface typeface, IBrush brush, double left, double right, double height)
        {
            if (row.Snippet == null)
            {
                ContentSnippetLoader.Request(row, OnSnippetReady);
            }

            string text = row.ContainsText;
            double room = Math.Floor(right - left);
            if (text == null || room < 40)
            {
                return;
            }

            TextLayout layout = TextLayoutCache.Get(text, typeface, CellFontSize, brush, room, 1);
            layout.Draw(context, new Point(left, (height - layout.Height) / 2));
        }

        private void OnSnippetReady(SearchResultRow row)
        {
            if (ReferenceEquals(_row, row))
            {
                InvalidateVisual();
            }
        }

        /// <summary>Up to three dots of the result's tag colors ending at <paramref name="right"/>; the width they took.</summary>
        private static double DrawTagDots(DrawingContext context, string path, double right, double centerY)
        {
            IReadOnlyList<ByteExplorer.Core.Tags.TagDefinition> tags = ByteExplorer.Core.Tags.TagStore.Default.GetTags(path);
            if (tags.Count == 0)
            {
                return 0;
            }

            const double size = 7;
            int shown = Math.Min(3, tags.Count);
            double x = right - size;
            for (int i = 0; i < shown; i++)
            {
                context.DrawEllipse(Controls.FileItemView.TagBrush(tags[i]), null, new Point(x + size / 2, centerY), size / 2, size / 2);
                x -= size - 2;
            }

            return shown * (size - 2) + 8;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ByteExplorer.Core.Tags.TagStore.AnyChanged += OnTagsChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ByteExplorer.Core.Tags.TagStore.AnyChanged -= OnTagsChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTagsChanged(object sender, ByteExplorer.Core.Tags.TagsChangedEventArgs e) => InvalidateVisual();

        private static void DrawCell(DrawingContext context, string text, Typeface typeface, IBrush brush, double x, double width, double height, bool alignRight)
        {
            if (string.IsNullOrEmpty(text) || width <= 0)
            {
                return;
            }

            TextLayout layout = TextLayoutCache.Get(text, typeface, CellFontSize, brush, double.PositiveInfinity, 1);
            if (layout.WidthIncludingTrailingWhitespace > width)
            {
                layout = TextLayoutCache.Get(text, typeface, CellFontSize, brush, Math.Floor(width), 1); // end ellipsis
            }

            double left = alignRight ? x + Math.Max(0, width - layout.WidthIncludingTrailingWhitespace) : x;
            using (context.PushClip(new Rect(x, 0, width, height)))
            {
                layout.Draw(context, new Point(left, (height - layout.Height) / 2));
            }
        }

        /// <summary>The name with its matching letters bold and in the accent color, at this width; kept on the row.</summary>
        private TextLayout NameLayout(SearchResultRow row, Typeface typeface, IBrush foreground, double width)
        {
            int[] positions = row.HighlightPositions;
            if (positions == null || positions.Length == 0)
            {
                TextLayout plain = TextLayoutCache.Get(row.Name, typeface, NameFontSize, foreground, double.PositiveInfinity, 1);
                return plain.WidthIncludingTrailingWhitespace <= width
                    ? plain
                    : TextLayoutCache.Get(row.Name, typeface, NameFontSize, foreground, Math.Floor(width), 1);
            }

            width = Math.Floor(width);
            if (row.NameLayout != null && row.NameLayoutWidth == width && ReferenceEquals(row.NameLayoutForeground, foreground))
            {
                return row.NameLayout;
            }

            var bold = new GenericTextRunProperties(new Typeface(typeface.FontFamily, typeface.Style, FontWeight.Bold), NameFontSize,
                foregroundBrush: _accent);
            var overrides = new List<ValueSpan<TextRunProperties>>();
            int length = row.Name.Length;
            for (int i = 0; i < positions.Length;)
            {
                int start = positions[i];
                int end = start;
                while (i + 1 < positions.Length && positions[i + 1] == end + 1)
                {
                    end++;
                    i++;
                }

                i++;
                if (start >= 0 && start < length)
                {
                    overrides.Add(new ValueSpan<TextRunProperties>(start, Math.Min(end, length - 1) - start + 1, bold));
                }
            }

            var layout = new TextLayout(row.Name, typeface, NameFontSize, foreground,
                textTrimming: TextTrimming.CharacterEllipsis, maxWidth: width, maxLines: 1, textStyleOverrides: overrides);
            row.NameLayout = layout;
            row.NameLayoutWidth = width;
            row.NameLayoutForeground = foreground;
            return layout;
        }

        /// <summary>The narrowest a character of the cell font can be, as a share of its size (a period, an "i").</summary>
        private const double NarrowestCharShare = 0.22;

        /// <summary>Trimmed folders by text and width, shared by the rows of results in the same folder.</summary>
        private static readonly Dictionary<(string Folder, double Width), string> TrimmedFolders = new Dictionary<(string, double), string>();

        /// <summary>
        /// The folder, cut at the start ("…\Projects\2024") so it fits. A row can never show more characters than its
        /// width holds at the narrowest character, so only that much of the end is ever shaped (a 600-character path
        /// costs what a 100-character one does), and a binary search over how much of it to keep needs a handful of
        /// measurements, once per folder and width.
        /// </summary>
        internal static string FolderText(SearchResultRow row, Typeface typeface, IBrush brush, double width)
        {
            string folder = row.FolderPath ?? string.Empty;
            width = Math.Floor(width);
            if (folder.Length == 0)
            {
                return folder;
            }

            if (row.TrimmedFolder != null && row.TrimmedFolderWidth == width)
            {
                return row.TrimmedFolder;
            }

            int maxKeep = (int)Math.Min(folder.Length, Math.Ceiling(width / (CellFontSize * NarrowestCharShare)) + 1);
            string result;
            if (maxKeep >= folder.Length && TextLayoutCache.Get(folder, typeface, CellFontSize, brush, double.PositiveInfinity, 1).WidthIncludingTrailingWhitespace <= width)
            {
                result = folder; // fits: the cached layout is drawn as it is
            }
            else if (!TrimmedFolders.TryGetValue((folder, width), out result))
            {
                int low = 0;
                int high = Math.Min(maxKeep, folder.Length - 1);
                while (low < high)
                {
                    int keep = (low + high + 1) / 2;
                    if (Measure(Ellipsis + folder.Substring(folder.Length - keep), typeface) <= width)
                    {
                        low = keep;
                    }
                    else
                    {
                        high = keep - 1;
                    }
                }

                result = Ellipsis + folder.Substring(folder.Length - low);
                if (TrimmedFolders.Count >= 4096)
                {
                    TrimmedFolders.Clear();
                }

                TrimmedFolders[(folder, width)] = result;
            }

            row.TrimmedFolder = result;
            row.TrimmedFolderWidth = width;
            return result;
        }

        internal static double Measure(string text, Typeface typeface)
        {
            using var layout = new TextLayout(text, typeface, CellFontSize, null);
            return layout.WidthIncludingTrailingWhitespace;
        }

        private void ResolveBrushes()
        {
            if (_primary != null)
            {
                return;
            }

            ThemeVariant theme = ActualThemeVariant;
            _primary = Find("TextPrimary", theme) ?? Brushes.White;
            _secondary = Find("TextSecondary", theme) ?? _primary;
            _accent = Find("SelectedAccent", theme) ?? Brushes.DodgerBlue;
            _system = Find("SystemItemText", theme) ?? Brushes.IndianRed;
        }

        private IBrush Find(string key, ThemeVariant theme) =>
            this.TryFindResource(key, theme, out object value) ? value as IBrush : null;
    }
}
