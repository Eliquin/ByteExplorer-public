// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.Controls
{
    /// <summary>The columns the Details view can show (right-click a column header › choose which).</summary>
    public enum DetailsColumnKind
    {
        Name,
        DateModified,
        Type,
        Size,
        Tags,
        DateCreated,
        DateAccessed,
        Extension,
        Attributes,
        FolderPath,
        Owner,
        Dimensions,
        DateTaken,
        Camera,
        Length,
        Artist,
        Album
    }

    /// <summary>What a column shows and how: its title, a description for the chooser, width, alignment, how it sorts.</summary>
    public sealed class DetailsColumnInfo
    {
        public DetailsColumnInfo(DetailsColumnKind kind, string title, string description, string group, double defaultWidth,
            FileListSortColumn? sort = null, bool rightAligned = false, ItemDetailsNeeds needs = ItemDetailsNeeds.None)
        {
            Kind = kind;
            Title = title;
            Description = description;
            Group = group;
            DefaultWidth = defaultWidth;
            Sort = sort;
            RightAligned = rightAligned;
            Needs = needs;
        }

        public DetailsColumnKind Kind { get; }

        /// <summary>The name saved in the settings ("DateCreated").</summary>
        public string Key => Kind.ToString();

        public string Title { get; }
        public string Description { get; }

        /// <summary>The chooser's group: "Basic", "Dates", "Photos", "Media", "File system".</summary>
        public string Group { get; }

        public double DefaultWidth { get; }

        /// <summary>
        /// How a click on the header sorts (null: this column can't sort, as its values are read file by file in the
        /// background and a folder's worth aren't all known).
        /// </summary>
        public FileListSortColumn? Sort { get; }

        public bool RightAligned { get; }

        /// <summary>What has to be read from each file for this column (none: the folder listing has it).</summary>
        public ItemDetailsNeeds Needs { get; }
    }

    /// <summary>Every Details column, in the order the chooser lists them.</summary>
    public static class DetailsColumnCatalog
    {
        public static IReadOnlyList<DetailsColumnInfo> All { get; } = new[]
        {
            new DetailsColumnInfo(DetailsColumnKind.Name, "Name", "The item's name (always shown)", "Basic", 320, FileListSortColumn.Name),
            new DetailsColumnInfo(DetailsColumnKind.DateModified, "Date modified", "When the item was last changed", "Dates", 150, FileListSortColumn.DateModified),
            new DetailsColumnInfo(DetailsColumnKind.Type, "Type", "The kind of file (\"PDF File\", \"File folder\")", "Basic", 130, FileListSortColumn.Type),
            new DetailsColumnInfo(DetailsColumnKind.Size, "Size", "How large the file is (folders once their size is counted)", "Basic", 90, FileListSortColumn.Size, rightAligned: true),
            new DetailsColumnInfo(DetailsColumnKind.Tags, "Tags", "The item's ByteExplorer tags", "Basic", 170, FileListSortColumn.Tags),
            new DetailsColumnInfo(DetailsColumnKind.DateCreated, "Date created", "When the item was created on this drive", "Dates", 150, FileListSortColumn.DateCreated),
            new DetailsColumnInfo(DetailsColumnKind.DateAccessed, "Date accessed", "When the item was last opened (as the file system records it)", "Dates", 150, FileListSortColumn.DateAccessed),
            new DetailsColumnInfo(DetailsColumnKind.Extension, "File extension", "The end of the name that tells the type (\".pdf\")", "Basic", 90, FileListSortColumn.Type),
            new DetailsColumnInfo(DetailsColumnKind.Attributes, "Attributes", "R read-only, H hidden, S system, A archive, D folder, L link, C compressed, E encrypted, O offline", "File system", 90),
            new DetailsColumnInfo(DetailsColumnKind.FolderPath, "Folder path", "The folder the item is in", "File system", 260),
            new DetailsColumnInfo(DetailsColumnKind.Owner, "Owner", "The account that owns the item", "File system", 150, needs: ItemDetailsNeeds.Owner),
            new DetailsColumnInfo(DetailsColumnKind.Dimensions, "Dimensions", "A picture's or video's width × height in pixels", "Photos", 110,
                needs: ItemDetailsNeeds.Image | ItemDetailsNeeds.Media),
            new DetailsColumnInfo(DetailsColumnKind.DateTaken, "Date taken", "When a photo was taken (from its camera data)", "Photos", 150, needs: ItemDetailsNeeds.Image),
            new DetailsColumnInfo(DetailsColumnKind.Camera, "Camera model", "The camera a photo was taken with", "Photos", 150, needs: ItemDetailsNeeds.Image),
            new DetailsColumnInfo(DetailsColumnKind.Length, "Length", "How long a song or video plays", "Media", 80, rightAligned: true, needs: ItemDetailsNeeds.Media),
            new DetailsColumnInfo(DetailsColumnKind.Artist, "Contributing artists", "Who performs a song (from its tags)", "Media", 160, needs: ItemDetailsNeeds.Media),
            new DetailsColumnInfo(DetailsColumnKind.Album, "Album", "The album a song is from (from its tags)", "Media", 160, needs: ItemDetailsNeeds.Media),
        };

        /// <summary>The columns the header's menu offers directly (the rest under "More…"), as Explorer's menu does.</summary>
        public static IReadOnlyList<DetailsColumnKind> InMenu { get; } = new[]
        {
            DetailsColumnKind.Name, DetailsColumnKind.DateModified, DetailsColumnKind.Type, DetailsColumnKind.Size, DetailsColumnKind.Tags,
            DetailsColumnKind.DateCreated, DetailsColumnKind.DateAccessed, DetailsColumnKind.Dimensions, DetailsColumnKind.Length
        };

        public static DetailsColumnInfo Get(DetailsColumnKind kind) => All[(int)kind];

        public static bool TryParse(string key, out DetailsColumnKind kind) =>
            Enum.TryParse(key, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>
    /// The Details view's columns and their widths, shared by the header and every row (Name always first). Immutable: a
    /// change makes a new instance, so each row lays itself out once per change and never sees a half-updated set.
    /// </summary>
    public sealed class DetailsColumnLayout
    {
        /// <summary>Narrowest a column can be dragged; still wide enough for a few characters and the sort arrow.</summary>
        public const double MinColumnWidth = 48;

        /// <summary>Widest a column can be (a dragged-away column comes back usable).</summary>
        public const double MaxColumnWidth = 2000;

        /// <summary>Name, Date modified, Type, Size and Tags, as before columns could be chosen.</summary>
        public static readonly DetailsColumnLayout Default = new DetailsColumnLayout(
            new[] { DetailsColumnKind.Name, DetailsColumnKind.DateModified, DetailsColumnKind.Type, DetailsColumnKind.Size, DetailsColumnKind.Tags },
            new double[] { 320, 150, 130, 90, 170 });

        private readonly DetailsColumnKind[] _kinds;
        private readonly double[] _widths;

        private DetailsColumnLayout(DetailsColumnKind[] kinds, double[] widths)
        {
            _kinds = kinds;
            _widths = widths;
            double total = 0;
            foreach (double width in widths)
            {
                total += width;
            }

            TotalWidth = total;
            Needs = ItemDetailsNeeds.None;
            foreach (DetailsColumnKind kind in kinds)
            {
                Needs |= DetailsColumnCatalog.Get(kind).Needs;
            }
        }

        public int Count => _widths.Length;

        /// <summary>The width of column <paramref name="column"/>.</summary>
        public double this[int column] => _widths[column];

        public DetailsColumnKind KindAt(int column) => _kinds[column];

        public IReadOnlyList<DetailsColumnKind> Kinds => _kinds;

        public double TotalWidth { get; }

        /// <summary>What the shown columns need read from each file (the rows ask only for that).</summary>
        public ItemDetailsNeeds Needs { get; }

        public int IndexOf(DetailsColumnKind kind) => Array.IndexOf(_kinds, kind);

        public bool Contains(DetailsColumnKind kind) => IndexOf(kind) >= 0;

        /// <summary>A copy with one column resized, within <see cref="MinColumnWidth"/> and <see cref="MaxColumnWidth"/>.</summary>
        public DetailsColumnLayout WithWidth(int column, double width)
        {
            var widths = (double[])_widths.Clone();
            widths[column] = Clamp(width);
            return new DetailsColumnLayout(_kinds, widths);
        }

        /// <summary>A copy with <paramref name="kind"/> shown (at the end, at its usual width); the same layout if it is.</summary>
        public DetailsColumnLayout With(DetailsColumnKind kind, double? width = null)
        {
            if (Contains(kind))
            {
                return this;
            }

            return new DetailsColumnLayout(_kinds.Append(kind).ToArray(), _widths.Append(Clamp(width ?? DetailsColumnCatalog.Get(kind).DefaultWidth)).ToArray());
        }

        /// <summary>A copy without <paramref name="kind"/> (Name can't be hidden).</summary>
        public DetailsColumnLayout Without(DetailsColumnKind kind)
        {
            int index = IndexOf(kind);
            if (index <= 0)
            {
                return this;
            }

            return new DetailsColumnLayout(_kinds.Where((_, i) => i != index).ToArray(), _widths.Where((_, i) => i != index).ToArray());
        }

        /// <summary>A copy with <paramref name="kind"/> moved one place left (-1) or right (+1); Name stays first.</summary>
        public DetailsColumnLayout Moved(DetailsColumnKind kind, int direction)
        {
            int index = IndexOf(kind);
            int target = index + Math.Sign(direction);
            if (index <= 0 || target <= 0 || target >= _kinds.Length)
            {
                return this;
            }

            var kinds = (DetailsColumnKind[])_kinds.Clone();
            var widths = (double[])_widths.Clone();
            (kinds[index], kinds[target]) = (kinds[target], kinds[index]);
            (widths[index], widths[target]) = (widths[target], widths[index]);
            return new DetailsColumnLayout(kinds, widths);
        }

        /// <summary>The layout as saved in the settings.</summary>
        public List<DetailsColumnEntry> ToEntries() =>
            _kinds.Select((kind, i) => new DetailsColumnEntry { Key = kind.ToString(), Width = Math.Round(_widths[i]) }).ToList();

        /// <summary>
        /// A layout from the settings: unknown or repeated columns are dropped, Name is put first (added when missing),
        /// widths out of range are mended; nothing usable gives the default.
        /// </summary>
        public static DetailsColumnLayout FromEntries(IEnumerable<DetailsColumnEntry> entries)
        {
            var kinds = new List<DetailsColumnKind>();
            var widths = new List<double>();
            foreach (DetailsColumnEntry entry in entries ?? Enumerable.Empty<DetailsColumnEntry>())
            {
                if (entry == null || !DetailsColumnCatalog.TryParse(entry.Key, out DetailsColumnKind kind) || kinds.Contains(kind))
                {
                    continue;
                }

                double width = double.IsFinite(entry.Width) && entry.Width > 0 ? entry.Width : DetailsColumnCatalog.Get(kind).DefaultWidth;
                if (kind == DetailsColumnKind.Name)
                {
                    kinds.Insert(0, kind);
                    widths.Insert(0, Clamp(width));
                }
                else
                {
                    kinds.Add(kind);
                    widths.Add(Clamp(width));
                }
            }

            if (kinds.Count == 0)
            {
                return Default;
            }

            if (kinds[0] != DetailsColumnKind.Name)
            {
                kinds.Insert(0, DetailsColumnKind.Name);
                widths.Insert(0, DetailsColumnCatalog.Get(DetailsColumnKind.Name).DefaultWidth);
            }

            return new DetailsColumnLayout(kinds.ToArray(), widths.ToArray());
        }

        private static double Clamp(double width) => Math.Clamp(double.IsFinite(width) ? width : MinColumnWidth, MinColumnWidth, MaxColumnWidth);
    }
}
