// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Icons;

namespace ByteExplorer.Controls
{
    /// <summary>A tool tip content that asks its item for the text only when shown: the item's details, then its tags and comment.</summary>
    internal sealed class LazyTip
    {
        public FileListItemViewModel Item { get; set; }

        public override string ToString()
        {
            FileListItemViewModel item = Item;
            if (item == null)
            {
                return string.Empty;
            }

            return item.ToolTipText + TagTipText(item.FullPath);
        }

        /// <summary>"\nTags: Work › Project A (Blue), Urgent (Red)\nComment: …", or empty for an untagged item.</summary>
        public static string TagTipText(string path)
        {
            Core.Tags.TagStore store = Core.Tags.TagStore.Default;
            System.Collections.Generic.IReadOnlyList<Core.Tags.TagDefinition> tags = store.GetTags(path);
            string comment = store.GetComment(path);
            if (tags.Count == 0 && string.IsNullOrEmpty(comment))
            {
                return string.Empty;
            }

            var text = new System.Text.StringBuilder();
            if (tags.Count > 0)
            {
                text.Append("\nTags: ");
                for (int i = 0; i < tags.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(", ");
                    }

                    text.Append(store.FullName(tags[i]).Replace("/", " › ")).Append(" (").Append(tags[i].Color).Append(')');
                    if (!string.IsNullOrEmpty(tags[i].Description))
                    {
                        text.Append(" — ").Append(tags[i].Description);
                    }
                }
            }

            if (!string.IsNullOrEmpty(comment))
            {
                text.Append("\nComment: ").Append(comment);
            }

            return text.ToString();
        }
    }

    /// <summary>How a <see cref="FileItemView"/> lays out its item.</summary>
    public enum FileItemKind
    {
        /// <summary>One row across the Details columns: icon and name, date, type, size.</summary>
        Details,

        /// <summary>One row: icon and name.</summary>
        List,

        /// <summary>A square tile: large icon, two-line centered name.</summary>
        Icon,

        /// <summary>A card: icon on the left, name and "type  size" on the right.</summary>
        Tile
    }

    /// <summary>
    /// One file-list item drawn by a single control: no child controls, no per-field bindings. Its item is its
    /// DataContext (inherited from the list container), and it draws the icon glyph and texts from shared caches
    /// (<see cref="TextLayoutCache"/>, <see cref="IconGlyphs"/>).
    /// <para>
    /// Why: profiling page jumps on a 100,000-item folder showed most layout time in what a templated row does when a
    /// recycled container gets a new item: the DataContext change walking every child, each binding re-publishing, each
    /// TextBlock and SymbolIcon re-measuring. A recycled FileItemView only invalidates itself; its size depends on the
    /// view's settings, not on the item, so a scroll re-measures nothing and only redraws. This is the owner-draw
    /// approach of large-list UIs (Explorer's own list view draws its items).
    /// </para>
    /// <para>
    /// Selection, hover and focus visuals stay on the list container (ListBoxItem), so selection behaviour and styling
    /// are untouched. Used by the Details, List, Icons and Tiles views, which show folders of unlimited size; Timeline,
    /// Columns and Nexus are capped in size and keep their templates.
    /// </para>
    /// </summary>
    public sealed class FileItemView : Control, IThumbnailObserver, Infrastructure.SystemIcons.ISystemIconObserver, IItemDetailsObserver
    {
        public static readonly StyledProperty<FileItemKind> KindProperty =
            AvaloniaProperty.Register<FileItemView, FileItemKind>(nameof(Kind));

        public static readonly StyledProperty<double> IconSizeProperty =
            AvaloniaProperty.Register<FileItemView, double>(nameof(IconSize), 16);

        public static readonly StyledProperty<double> FontSizeProperty =
            TextElement.FontSizeProperty.AddOwner<FileItemView>();

        public static readonly StyledProperty<double> SecondaryFontSizeProperty =
            AvaloniaProperty.Register<FileItemView, double>(nameof(SecondaryFontSize), 11);

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner<FileItemView>();

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<FileItemView>();

        public static readonly StyledProperty<IBrush> SecondaryForegroundProperty =
            AvaloniaProperty.Register<FileItemView, IBrush>(nameof(SecondaryForeground));

        /// <summary>Row height (Details, List) or tile height (Icon, Tile).</summary>
        public static readonly StyledProperty<double> ItemHeightProperty =
            AvaloniaProperty.Register<FileItemView, double>(nameof(ItemHeight), 28);

        /// <summary>Tile width (Icon, Tile); rows stretch to the list instead.</summary>
        public static readonly StyledProperty<double> ItemWidthProperty =
            AvaloniaProperty.Register<FileItemView, double>(nameof(ItemWidth), 96);

        private const double DetailsPadding = 8;
        private const double DetailsCellPadding = 8;
        private const double ListPadding = 9;
        private const double Gap = 8;
        private const double TileIconColumn = 64;
        private const double IconTileNameSize = 11;

        public static readonly StyledProperty<bool> ShowThumbnailsProperty =
            AvaloniaProperty.Register<FileItemView, bool>(nameof(ShowThumbnails), true);

        /// <summary>Draw Explorer's icons (Folder options › "Show Windows icons"); the app's glyphs otherwise, and until each arrives.</summary>
        public static readonly StyledProperty<bool> ShowSystemIconsProperty =
            AvaloniaProperty.Register<FileItemView, bool>(nameof(ShowSystemIcons));

        public bool ShowSystemIcons
        {
            get => GetValue(ShowSystemIconsProperty);
            set => SetValue(ShowSystemIconsProperty, value);
        }

        void Infrastructure.SystemIcons.ISystemIconObserver.OnSystemIconReady() => InvalidateVisual();

        void IItemDetailsObserver.OnItemDetailsReady() => InvalidateVisual();

        /// <summary>A Details cell's text (Name and Tags are drawn on their own); empty while a file-by-file value is read.</summary>
        internal static string CellText(FileListItemViewModel item, DetailsColumnKind kind, ItemDetails details) => kind switch
        {
            DetailsColumnKind.DateModified => item.DateModifiedDisplay,
            DetailsColumnKind.Type => item.TypeDisplay,
            DetailsColumnKind.Size => item.SizeDisplay,
            DetailsColumnKind.DateCreated => item.DateCreatedDisplay,
            DetailsColumnKind.DateAccessed => item.DateAccessedDisplay,
            DetailsColumnKind.Extension => item.ExtensionDisplay,
            DetailsColumnKind.Attributes => item.AttributesDisplay,
            DetailsColumnKind.FolderPath => item.FolderPathDisplay,
            DetailsColumnKind.Owner => details?.Owner ?? string.Empty,
            DetailsColumnKind.Dimensions => details?.DimensionsText ?? string.Empty,
            DetailsColumnKind.DateTaken => details?.DateTakenText ?? string.Empty,
            DetailsColumnKind.Camera => details?.Camera ?? string.Empty,
            DetailsColumnKind.Length => details?.LengthText ?? string.Empty,
            DetailsColumnKind.Artist => details?.Artist ?? string.Empty,
            DetailsColumnKind.Album => details?.Album ?? string.Empty,
            _ => string.Empty
        };

        /// <summary>Icons smaller than this show the type icon even for images (a thumbnail would be unreadable).</summary>
        private const double MinThumbnailSize = 48;

        private FileListItemViewModel _item;
        private readonly LazyTip _tip = new LazyTip();
        private ThumbnailCache.Key _thumbnailKey;
        private bool _thumbnailAcquired;

        static FileItemView()
        {
            AffectsMeasure<FileItemView>(KindProperty, ItemHeightProperty, ItemWidthProperty, DetailsRowPanel.ColumnsProperty, IconSizeProperty);
            AffectsRender<FileItemView>(FontSizeProperty, SecondaryFontSizeProperty, FontFamilyProperty, ForegroundProperty,
                SecondaryForegroundProperty, ShowSystemIconsProperty);
        }

        public FileItemKind Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public double IconSize
        {
            get => GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public double SecondaryFontSize
        {
            get => GetValue(SecondaryFontSizeProperty);
            set => SetValue(SecondaryFontSizeProperty, value);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public IBrush SecondaryForeground
        {
            get => GetValue(SecondaryForegroundProperty);
            set => SetValue(SecondaryForegroundProperty, value);
        }

        /// <summary>Whether image items show a thumbnail (Icon views at 48 px and up, Tiles).</summary>
        public bool ShowThumbnails
        {
            get => GetValue(ShowThumbnailsProperty);
            set => SetValue(ShowThumbnailsProperty, value);
        }

        public double ItemHeight
        {
            get => GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        public double ItemWidth
        {
            get => GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            _item = DataContext as FileListItemViewModel;
            AutomationProperties.SetName(this, _item?.Name);

            // One reusable tip object per view (no allocation per recycle); its text is built only if shown.
            _tip.Item = _item;
            if (ToolTip.GetTip(this) == null && _item != null)
            {
                ToolTip.SetTip(this, _tip);
            }

            ToolTip.SetServiceEnabled(this, FolderOptions.Current.ShowItemTooltips);
            ReleaseThumbnail();

            // The size never depends on the item, so a recycled view only needs a redraw.
            InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            {
                _systemBrush = null;
                InvalidateVisual();
            }

            if (change.Property == IconSizeProperty || change.Property == ShowThumbnailsProperty || change.Property == KindProperty)
            {
                ReleaseThumbnail(); // the next draw asks for the thumbnail at the new size, if any
                InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Core.Tags.TagStore.AnyChanged -= OnTagsChanged;
            Core.Services.FolderSizes.Changed -= OnFolderSizeChanged;
            if (_container != null)
            {
                _container.PropertyChanged -= OnContainerPropertyChanged;
                _container = null;
            }

            ReleaseThumbnail();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Drops this view's thumbnail request (its container was recycled, its item or size changed): a decode that has
        /// not started is skipped. The next draw asks again if the item still needs one.
        /// </summary>
        internal void ReleaseThumbnail()
        {
            if (_thumbnailAcquired)
            {
                ThumbnailCache.Release(_thumbnailKey, this);
                _thumbnailAcquired = false;
            }
        }

        void IThumbnailObserver.OnThumbnailReady(ThumbnailCache.Key key)
        {
            if (_thumbnailAcquired && _thumbnailKey.Equals(key))
            {
                InvalidateVisual();
            }
        }

        /// <summary>The thumbnail to draw for the item, requesting it on first draw; null while decoding or not an image.</summary>
        private Avalonia.Media.Imaging.Bitmap ThumbnailFor(FileListItemViewModel item, double size)
        {
            if (!ShowThumbnails || item.IsDirectory || (Kind == FileItemKind.Icon && size < MinThumbnailSize) ||
                (Kind != FileItemKind.Icon && Kind != FileItemKind.Tile) || !ThumbnailCache.IsSupported(item.FullPath))
            {
                return null;
            }

            ThumbnailCache.Key key = ThumbnailCache.KeyFor(item.FullPath, item.LastWriteTimeUtc, size * (VisualRoot?.RenderScaling ?? 1));
            if (!_thumbnailAcquired || !_thumbnailKey.Equals(key))
            {
                ReleaseThumbnail();
                _thumbnailKey = key;
                ThumbnailCache.Acquire(key, this);
                _thumbnailAcquired = true;
            }

            return ThumbnailCache.TryGet(key);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            switch (Kind)
            {
                case FileItemKind.Details:
                    DetailsColumnLayout columns = GetValue(DetailsRowPanel.ColumnsProperty) ?? DetailsColumnLayout.Default;
                    return new Size(columns.TotalWidth, Math.Max(ItemHeight, IconSize + 6));
                case FileItemKind.List:
                    return new Size(0, ItemHeight);
                default:
                    return new Size(ItemWidth, ItemHeight);
            }
        }

        /// <summary>How opaque a hidden item is drawn: Explorer's faded look, still easy to read.</summary>
        public const double HiddenItemOpacity = 0.55;

        private IBrush _systemBrush;

        /// <summary>
        /// The text color of items with the system attribute (palette SystemItemText: a soft red on the dark theme,
        /// Windows red on the light one). Looked up once per theme, not per draw.
        /// </summary>
        private IBrush SystemBrush
        {
            get
            {
                if (_systemBrush == null)
                {
                    _systemBrush = this.TryFindResource("SystemItemText", ActualThemeVariant, out object value) && value is IBrush brush
                        ? brush
                        : Brushes.IndianRed;
                }

                return _systemBrush;
            }
        }

        /// <summary>The colors an item's text is drawn in: the view's, or the system-item color for system files.</summary>
        private (IBrush Primary, IBrush Secondary) BrushesFor(FileListItemViewModel item)
        {
            if (item.IsSystem)
            {
                IBrush system = SystemBrush;
                return (system, system);
            }

            IBrush primary = Foreground ?? Brushes.White;
            return (primary, SecondaryForeground ?? primary);
        }

        public override void Render(DrawingContext context)
        {
            FileListItemViewModel item = _item;
            if (item == null)
            {
                return;
            }

            if (item.IsHidden || Infrastructure.ClipboardFileService.IsCut(item.FullPath))
            {
                // Hidden items (and dot files) are drawn translucent, icon and text alike, as Explorer shows them; so are
                // items cut and not pasted yet.
                using (context.PushOpacity(HiddenItemOpacity))
                {
                    RenderItem(context, item);
                }
            }
            else
            {
                RenderItem(context, item);
            }
        }

        private void RenderItem(DrawingContext context, FileListItemViewModel item)
        {
            var typeface = new Typeface(FontFamily ?? FontFamily.Default);
            double width = Bounds.Width;
            double height = Bounds.Height;
            double fontSize = FontSize > 0 ? FontSize : 13;
            (IBrush primary, IBrush secondary) = BrushesFor(item);

            switch (Kind)
            {
                case FileItemKind.Details:
                {
                    DetailsColumnLayout columns = GetValue(DetailsRowPanel.ColumnsProperty) ?? DetailsColumnLayout.Default;
                    double icon = IconSize;
                    double left = DetailsPadding + DrawRowCheckBox(context, DetailsPadding, height);
                    DrawIcon(context, item, new Rect(left, (height - icon) / 2, icon, icon));
                    double nameX = left + icon + Gap;
                    TextLayout detailsName = DrawText(context, item.DisplayName, typeface, fontSize, primary, nameX, 0, columns[0] - nameX - DetailsPadding, height, TextAlignment.Left);
                    Underline(context, detailsName, primary, nameX, height);

                    // The other columns, in the order chosen (right-click a column header). Values read file by file
                    // (owner, photo and media facts) are asked for once per draw and come in the background.
                    ItemDetails details = columns.Needs == ItemDetailsNeeds.None
                        ? null
                        : ItemDetailsCache.TryGet(item.FullPath, item.LastWriteTimeUtc, item.IsDirectory, columns.Needs, this);
                    double x = columns[0];
                    for (int i = 1; i < columns.Count; i++)
                    {
                        double column = columns[i];
                        DetailsColumnKind kind = columns.KindAt(i);
                        if (kind == DetailsColumnKind.Tags)
                        {
                            DrawTagPills(context, typeface, item, x + DetailsCellPadding, column - 2 * DetailsCellPadding, height);
                        }
                        else
                        {
                            DrawText(context, CellText(item, kind, details), typeface, fontSize, secondary, x + DetailsCellPadding, 0, column - 2 * DetailsCellPadding, height,
                                DetailsColumnCatalog.Get(kind).RightAligned ? TextAlignment.Right : TextAlignment.Left);
                        }

                        x += column;
                    }

                    break;
                }

                case FileItemKind.List:
                {
                    double icon = IconSize;
                    double left = ListPadding + DrawRowCheckBox(context, ListPadding, height);
                    DrawIcon(context, item, new Rect(left, (height - icon) / 2, icon, icon));
                    double nameX = left + icon + Gap;
                    double dots = DrawTagDots(context, item, width - ListPadding, height / 2);
                    TextLayout listName = DrawText(context, item.DisplayName, typeface, fontSize, primary, nameX, 0, width - nameX - ListPadding - dots, height, TextAlignment.Left);
                    Underline(context, listName, primary, nameX, height);
                    break;
                }

                case FileItemKind.Icon:
                {
                    double icon = IconSize;
                    const double top = 4;
                    DrawIconOrThumbnail(context, item, new Rect((width - icon) / 2, top, icon, icon));
                    DrawTagDots(context, item, width - 6, top + 6);
                    double textTop = top + icon + 2;
                    double nameWidth = Math.Max(1, Math.Floor(width - 8));
                    TextLayout name = Layout(item.DisplayName, typeface, IconTileNameSize, primary, nameWidth, 2, TextAlignment.Center, out bool wrapped);
                    if (name != null)
                    {
                        // A wrapped layout centers its own lines across the width it was built for.
                        double x = 4 + (wrapped ? 0 : (nameWidth - name.WidthIncludingTrailingWhitespace) / 2);
                        using (context.PushClip(new Rect(0, textTop, width, Math.Max(0, height - textTop))))
                        {
                            name.Draw(context, new Point(x, textTop));
                            if (!wrapped)
                            {
                                UnderlineAt(context, name, primary, x, textTop);
                            }
                        }
                    }

                    DrawCornerCheckBox(context);

                    break;
                }

                case FileItemKind.Tile:
                {
                    double icon = IconSize;
                    DrawIconOrThumbnail(context, item, new Rect((TileIconColumn - icon) / 2, (height - icon) / 2, icon, icon));
                    double textX = TileIconColumn + 10;
                    double textWidth = Math.Max(1, width - textX - 8);
                    TextLayout name = Layout(item.DisplayName, new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold), fontSize, primary, textWidth, 1, TextAlignment.Left, out _);
                    TextLayout sub = Layout(item.TileSubtitle, typeface, SecondaryFontSize > 0 ? SecondaryFontSize : 11, secondary, textWidth, 1, TextAlignment.Left, out _);
                    double total = (name?.Height ?? 0) + (sub?.Height ?? 0);
                    double y = (height - total) / 2;
                    DrawTagDots(context, item, width - 8, 10);
                    name?.Draw(context, new Point(textX, y));
                    UnderlineAt(context, name, primary, textX, y);
                    sub?.Draw(context, new Point(textX, y + (name?.Height ?? 0)));
                    DrawCornerCheckBox(context);
                    break;
                }
            }
        }

        private const double PillHeight = 18;
        private const double PillFontSize = 11;
        private const double PillPadding = 7;
        private const double PillGap = 4;
        private const double DotSize = 7;

        private static readonly System.Collections.Generic.Dictionary<string, IBrush> TagBrushes = new System.Collections.Generic.Dictionary<string, IBrush>();

        /// <summary>The fill of a tag color (one brush per color, shared).</summary>
        internal static IBrush TagBrush(Core.Tags.TagDefinition tag)
        {
            string hex = tag.ColorHex;
            if (!TagBrushes.TryGetValue(hex, out IBrush brush))
            {
                brush = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
                TagBrushes[hex] = brush;
            }

            return brush;
        }

        /// <summary>White or near-black text, whichever reads better on the tag's color (yellow takes dark text).</summary>
        internal static IBrush TagTextBrush(Core.Tags.TagDefinition tag)
        {
            Color color = Color.Parse(tag.ColorHex);
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            return luminance > 0.6 ? DarkPillText : Brushes.White;
        }

        private static readonly IBrush DarkPillText = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)).ToImmutable();

        /// <summary>
        /// The Details view's Tags cell: each tag as a colored label, as many as fit, then "+N" for the rest (all of them
        /// are in the tool tip).
        /// </summary>
        private void DrawTagPills(DrawingContext context, Typeface typeface, FileListItemViewModel item, double x, double width, double height)
        {
            System.Collections.Generic.IReadOnlyList<Core.Tags.TagDefinition> tags = Core.Tags.TagStore.Default.GetTags(item.FullPath);
            if (tags.Count == 0 || width <= 8)
            {
                return;
            }

            double top = Math.Round((height - PillHeight) / 2);
            double right = x + width;
            using (context.PushClip(new Rect(x, 0, Math.Max(0, width), height)))
            {
                double left = x;
                for (int i = 0; i < tags.Count; i++)
                {
                    TextLayout label = TextLayoutCache.Get(tags[i].Name, typeface, PillFontSize, TagTextBrush(tags[i]), double.PositiveInfinity, 1);
                    double pill = Math.Min(label.WidthIncludingTrailingWhitespace + 2 * PillPadding, 140);
                    int remaining = tags.Count - i - 1;
                    double reserve = remaining > 0 ? 28 : 0;
                    if (left + pill + reserve > right && i > 0)
                    {
                        DrawMore(context, typeface, tags.Count - i, left, top);
                        break;
                    }

                    context.DrawRectangle(TagBrush(tags[i]), null, new RoundedRect(new Rect(left, top, pill, PillHeight), PillHeight / 2));
                    if (label.WidthIncludingTrailingWhitespace + 2 * PillPadding > pill)
                    {
                        label = TextLayoutCache.Get(tags[i].Name, typeface, PillFontSize, TagTextBrush(tags[i]), Math.Floor(pill - 2 * PillPadding), 1);
                    }

                    label.Draw(context, new Point(left + PillPadding, top + (PillHeight - label.Height) / 2));
                    left += pill + PillGap;
                }
            }
        }

        private void DrawMore(DrawingContext context, Typeface typeface, int count, double x, double top)
        {
            TextLayout more = TextLayoutCache.Get("+" + count, typeface, PillFontSize, SecondaryForeground ?? Foreground ?? Brushes.Gray, double.PositiveInfinity, 1);
            more.Draw(context, new Point(x + 2, top + (PillHeight - more.Height) / 2));
        }

        /// <summary>Up to three colored dots ending at <paramref name="right"/> (List, Icons, Tiles); the width they took.</summary>
        private static double DrawTagDots(DrawingContext context, FileListItemViewModel item, double right, double centerY)
        {
            System.Collections.Generic.IReadOnlyList<Core.Tags.TagDefinition> tags = Core.Tags.TagStore.Default.GetTags(item.FullPath);
            if (tags.Count == 0)
            {
                return 0;
            }

            int shown = Math.Min(3, tags.Count);
            double x = right - DotSize;
            for (int i = 0; i < shown; i++)
            {
                context.DrawEllipse(TagBrush(tags[i]), null, new Point(x + DotSize / 2, centerY), DotSize / 2, DotSize / 2);
                x -= DotSize - 2; // overlapping a little, like Finder's tag dots
            }

            return shown * (DotSize - 2) + 2 + 6;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Core.Tags.TagStore.AnyChanged += OnTagsChanged;
            Core.Services.FolderSizes.Changed += OnFolderSizeChanged;
            AttachContainer();
        }

        /// <summary>Redraws when this folder's counted size changed (Details' Size column, the Tiles subtitle).</summary>
        private void OnFolderSizeChanged(string folder)
        {
            FileListItemViewModel item = _item;
            if (item != null && item.IsDirectory && string.Equals(item.FullPath, folder, StringComparison.Ordinal) &&
                (Kind == FileItemKind.Details || Kind == FileItemKind.Tile))
            {
                InvalidateVisual();
            }
        }

        /// <summary>Redraws when this item's tags (or every item's, after a tag was renamed or recolored) changed.</summary>
        private void OnTagsChanged(object sender, Core.Tags.TagsChangedEventArgs e)
        {
            FileListItemViewModel item = _item;
            if (item == null || !ReferenceEquals(sender, Core.Tags.TagStore.Default))
            {
                return;
            }

            if (e.Paths == null || ContainsPath(e.Paths, item.FullPath))
            {
                InvalidateVisual();
            }
        }

        private static bool ContainsPath(System.Collections.Generic.IReadOnlyCollection<string> paths, string path)
        {
            foreach (string candidate in paths)
            {
                if (Core.PathUtil.AreSame(candidate, path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Where the item's name is drawn, in this view's coordinates (the in-place rename box covers it).</summary>
        public Rect NameBounds()
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            switch (Kind)
            {
                case FileItemKind.Details:
                {
                    DetailsColumnLayout columns = GetValue(DetailsRowPanel.ColumnsProperty) ?? DetailsColumnLayout.Default;
                    double x = DetailsPadding + IconSize + Gap - 4;
                    return new Rect(x, 0, Math.Max(80, columns[0] - x - 4), height);
                }

                case FileItemKind.List:
                {
                    double x = ListPadding + IconSize + Gap - 4;
                    return new Rect(x, 0, Math.Max(80, width - x - 4), height);
                }

                case FileItemKind.Icon:
                {
                    double top = 4 + IconSize + 1;
                    return new Rect(0, top, width, Math.Max(20, height - top));
                }

                default:
                {
                    double x = TileIconColumn + 6;
                    return new Rect(x, height / 2 - 14, Math.Max(80, width - x - 4), 24);
                }
            }
        }

        /// <summary>
        /// Builds (into the shared cache) every text layout this view would need to draw <paramref name="item"/> at its
        /// current size and settings, without drawing. Called on idle time for the items about to scroll into view, so
        /// they appear without shaping their text on the frame that shows them. False when the view has no size yet.
        /// </summary>
        public bool Prewarm(FileListItemViewModel item)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (item == null || width <= 0 || height <= 0)
            {
                return false;
            }

            var typeface = new Typeface(FontFamily ?? FontFamily.Default);
            double fontSize = FontSize > 0 ? FontSize : 13;
            (IBrush primary, IBrush secondary) = BrushesFor(item);
            switch (Kind)
            {
                case FileItemKind.Details:
                {
                    DetailsColumnLayout columns = GetValue(DetailsRowPanel.ColumnsProperty) ?? DetailsColumnLayout.Default;
                    double nameX = DetailsPadding + IconSize + Gap;
                    Layout(item.Name, typeface, fontSize, primary, columns[0] - nameX - DetailsPadding, 1, TextAlignment.Left, out _);
                    for (int i = 1; i < columns.Count; i++)
                    {
                        DetailsColumnKind kind = columns.KindAt(i);
                        if (kind != DetailsColumnKind.Tags && DetailsColumnCatalog.Get(kind).Needs == ItemDetailsNeeds.None)
                        {
                            Layout(CellText(item, kind, null), typeface, fontSize, secondary, columns[i] - 2 * DetailsCellPadding, 1,
                                DetailsColumnCatalog.Get(kind).RightAligned ? TextAlignment.Right : TextAlignment.Left, out _);
                        }
                    }

                    break;
                }

                case FileItemKind.List:
                    Layout(item.Name, typeface, fontSize, primary, width - (ListPadding + IconSize + Gap) - ListPadding, 1, TextAlignment.Left, out _);
                    break;
                case FileItemKind.Icon:
                    Layout(item.Name, typeface, IconTileNameSize, primary, Math.Max(1, Math.Floor(width - 8)), 2, TextAlignment.Center, out _);
                    break;
                case FileItemKind.Tile:
                {
                    double textWidth = Math.Max(1, width - (TileIconColumn + 10) - 8);
                    Layout(item.Name, new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold), fontSize, primary, textWidth, 1, TextAlignment.Left, out _);
                    Layout(item.TileSubtitle, typeface, SecondaryFontSize > 0 ? SecondaryFontSize : 11, secondary, textWidth, 1, TextAlignment.Left, out _);
                    break;
                }
            }

            return true;
        }

        /// <summary>The image's thumbnail fitted into the box (aspect kept, centered) once decoded; the type icon until then.</summary>
        private void DrawIconOrThumbnail(DrawingContext context, FileListItemViewModel item, Rect box)
        {
            Avalonia.Media.Imaging.Bitmap thumbnail = ThumbnailFor(item, box.Width);
            if (thumbnail == null)
            {
                DrawIcon(context, item, box);
                return;
            }

            Size pixels = thumbnail.Size;
            double scale = Math.Min(box.Width / pixels.Width, box.Height / pixels.Height);
            double w = pixels.Width * scale;
            double h = pixels.Height * scale;
            var destination = new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            context.DrawImage(thumbnail, new Rect(pixels), destination);
        }

        /// <summary>
        /// Explorer's icon for the item when "Show Windows icons" is on and it has arrived; otherwise the item's symbol in
        /// its category color, centered in the box (a rounded square if glyphs are unavailable).
        /// </summary>
        private void DrawIcon(DrawingContext context, FileListItemViewModel item, Rect box)
        {
            if (box.Width <= 0)
            {
                return;
            }

            if (ShowSystemIcons)
            {
                double pixels = box.Width * (VisualRoot?.RenderScaling ?? 1);
                Avalonia.Media.Imaging.Bitmap system = Infrastructure.SystemIcons.SystemIconCache.TryGet(item.SystemIconKey, item.FullPath, item.IsDirectory, pixels, this);
                if (system != null)
                {
                    // Made at the next size up (16, 32, 48…) and drawn smaller: filtered, not dropped pixels.
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                    {
                        context.DrawImage(system, new Rect(system.Size), box);
                    }

                    return;
                }
            }

            string glyph = IconGlyphs.Get(item.IconGlyph, IconVariant.Filled);
            if (string.IsNullOrEmpty(glyph))
            {
                double inset = box.Width * 0.15;
                context.DrawRectangle(item.IconColorBrush, null, box.Deflate(inset), box.Width * 0.12, box.Width * 0.12);
                return;
            }

            TextLayout layout = TextLayoutCache.Get(glyph, IconGlyphs.Typeface, box.Width, item.IconColorBrush, double.PositiveInfinity, 1);
            layout.Draw(context, new Point(box.X + (box.Width - layout.WidthIncludingTrailingWhitespace) / 2, box.Y + (box.Height - layout.Height) / 2));
        }

        private static TextLayout DrawText(DrawingContext context, string text, Typeface typeface, double fontSize, IBrush brush,
            double x, double y, double maxWidth, double height, TextAlignment alignment)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 1)
            {
                return null;
            }

            TextLayout layout = Layout(text, typeface, fontSize, brush, maxWidth, 1, alignment, out _);
            double offset = alignment == TextAlignment.Right
                ? Math.Max(0, maxWidth - layout.WidthIncludingTrailingWhitespace)
                : 0;
            layout.Draw(context, new Point(x + offset, y + (height - layout.Height) / 2));
            return layout;
        }

        // ------------------------------------------------------------------ Folder options: single click, check boxes

        private const double CheckBoxSize = 14;
        private const double CheckBoxColumn = 22;
        private ListBoxItem _container;
        private static readonly IBrush CheckFill = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

        /// <summary>Names are underlined under the pointer when one click opens (Explorer's "underline on point").</summary>
        private bool UnderlineName => IsPointerOver && FolderOptions.Current.OpenWithSingleClick;

        private void Underline(DrawingContext context, TextLayout layout, IBrush brush, double x, double rowHeight)
        {
            if (layout != null)
            {
                UnderlineAt(context, layout, brush, x, (rowHeight - layout.Height) / 2);
            }
        }

        private void UnderlineAt(DrawingContext context, TextLayout layout, IBrush brush, double x, double top)
        {
            if (layout == null || !UnderlineName)
            {
                return;
            }

            double y = Math.Round(top + layout.Baseline + 2) + 0.5;
            context.DrawLine(new Pen(brush, 1), new Point(x, y), new Point(x + Math.Min(layout.WidthIncludingTrailingWhitespace, Bounds.Width - x), y));
        }

        private bool IsSelectedItem => _container?.IsSelected == true;

        /// <summary>Details and List: a check box column before the icon (its width, or 0 without check boxes).</summary>
        private double DrawRowCheckBox(DrawingContext context, double x, double rowHeight)
        {
            if (!FolderOptions.Current.UseCheckBoxes)
            {
                return 0;
            }

            DrawCheckBox(context, new Rect(x, Math.Round((rowHeight - CheckBoxSize) / 2), CheckBoxSize, CheckBoxSize));
            return CheckBoxColumn;
        }

        /// <summary>Icons and Tiles: a check box in the top-left corner, shown when pointed at or selected (as Explorer).</summary>
        private void DrawCornerCheckBox(DrawingContext context)
        {
            if (FolderOptions.Current.UseCheckBoxes && (IsPointerOver || IsSelectedItem))
            {
                DrawCheckBox(context, CornerCheckBoxRect);
            }
        }

        private Rect CornerCheckBoxRect => new Rect(4, 4, CheckBoxSize, CheckBoxSize);

        private void DrawCheckBox(DrawingContext context, Rect box)
        {
            bool selected = IsSelectedItem;
            IBrush border = SecondaryForeground ?? Foreground ?? Brushes.Gray;
            context.DrawRectangle(selected ? CheckFill : Brushes.Transparent, new Pen(selected ? CheckFill : border, 1.2), box, 3, 3);
            if (selected)
            {
                var pen = new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    g.BeginFigure(new Point(box.X + 3.2, box.Y + 7.2), false);
                    g.LineTo(new Point(box.X + 6, box.Y + 10));
                    g.LineTo(new Point(box.X + 11, box.Y + 4.2));
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, pen, geometry);
            }
        }

        /// <summary>
        /// Whether a point (in this view's coordinates) is on the item's check box, where a press toggles the item's
        /// selection without touching the others.
        /// </summary>
        public bool IsOnCheckBox(Point point)
        {
            if (!FolderOptions.Current.UseCheckBoxes)
            {
                return false;
            }

            Rect box = Kind == FileItemKind.Details || Kind == FileItemKind.List
                ? new Rect(Kind == FileItemKind.Details ? DetailsPadding : ListPadding, 0, CheckBoxColumn, Bounds.Height)
                : CornerCheckBoxRect.Inflate(4);
            return box.Contains(point);
        }

        private void AttachContainer()
        {
            _container = this.FindAncestorOfType<ListBoxItem>();
            if (_container != null)
            {
                _container.PropertyChanged += OnContainerPropertyChanged;
            }
        }

        private void OnContainerPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ListBoxItem.IsSelectedProperty && FolderOptions.Current.UseCheckBoxes)
            {
                InvalidateVisual();
            }
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            if (FolderOptions.Current.OpenWithSingleClick || FolderOptions.Current.UseCheckBoxes)
            {
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (FolderOptions.Current.OpenWithSingleClick || FolderOptions.Current.UseCheckBoxes)
            {
                InvalidateVisual();
            }
        }

        /// <summary>A folder option changed: re-read it (tips on or off) and redraw.</summary>
        internal void ApplyFolderOptions()
        {
            ToolTip.SetServiceEnabled(this, FolderOptions.Current.ShowItemTooltips);
            InvalidateVisual();
        }

        /// <summary>
        /// The cached layout of the text: the natural (untrimmed) one when it fits, otherwise one trimmed or wrapped to
        /// the width.
        /// </summary>
        private static TextLayout Layout(string text, Typeface typeface, double fontSize, IBrush brush, double maxWidth, int maxLines,
            TextAlignment alignment, out bool constrained)
        {
            constrained = false;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // Measuring the natural width costs a full shaping; when the text is clearly too long for the width (a name
            // in a narrow icon tile) go straight to the constrained layout. 0.4 em per character is below the average
            // of UI fonts; a rare text of very narrow characters that would have fit just gets a constrained layout that
            // draws identically (nothing to trim or wrap).
            if (text.Length * fontSize * 0.4 <= maxWidth)
            {
                TextLayout natural = TextLayoutCache.Get(text, typeface, fontSize, brush, double.PositiveInfinity, 1);
                if (natural.WidthIncludingTrailingWhitespace <= maxWidth + 0.5)
                {
                    return natural;
                }
            }

            constrained = true;
            return TextLayoutCache.Get(text, typeface, fontSize, brush, Math.Max(1, Math.Floor(maxWidth)), maxLines, alignment);
        }
    }
}
