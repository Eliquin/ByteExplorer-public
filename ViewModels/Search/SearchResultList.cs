// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteSearcher;

namespace ByteExplorer.ViewModels.Search
{
    /// <summary>One search result as the results view shows it, built when its row scrolls into view.</summary>
    public sealed class SearchResultRow
    {
        private string _sizeDisplay;
        private string _modifiedDisplay;
        private string _typeDisplay;

        internal SearchResultRow(SearchHit hit, int index, SearchResultList owner)
        {
            Index = index;
            Owner = owner;
            Name = hit.Name ?? string.Empty;
            FullPath = hit.FullPath ?? string.Empty;
            FolderPath = hit.FolderPath;
            IsDirectory = hit.IsDirectory;
            IsHidden = hit.IsHidden;
            IsSystem = hit.IsSystem;
            Size = hit.Size;
            ModifiedUtc = hit.ModifiedUtc;
            Kind = hit.Kind;
            IconGlyph = FileIconGlyphs.For(Name, IsDirectory);
            _iconBrush = IsDirectory ? null : Infrastructure.Converters.HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(Name, false));
            HighlightPositions = ToPositions(hit.NameHighlights, Name.Length);
            IsContentMatch = hit.IsContentMatch;
        }

        internal int Index { get; }

        internal SearchResultList Owner { get; }

        public string Name { get; }

        public string FullPath { get; }

        public string FolderPath { get; }

        public bool IsDirectory { get; }

        public bool IsHidden { get; }

        /// <summary>System attribute (Windows): drawn in the system-item color.</summary>
        public bool IsSystem { get; }

        public long Size { get; }

        // Text the row view prepared for this result, kept with the row so scrolling back to it costs nothing: the name
        // with its highlights at a width, and the folder trimmed from the left to a width.
        internal Avalonia.Media.TextFormatting.TextLayout NameLayout;
        internal double NameLayoutWidth = -1;
        internal IBrush NameLayoutForeground;
        internal string TrimmedFolder;
        internal double TrimmedFolderWidth = -1;

        public DateTime ModifiedUtc { get; }

        public FileKind Kind { get; }

        public Symbol IconGlyph { get; }

        private readonly IBrush _iconBrush;

        /// <summary>The icon color: the file category's, or for a folder the folder color setting (read live).</summary>
        public IBrush IconBrush => IsDirectory ? FileIconGlyphs.FolderBrush : _iconBrush;

        /// <summary>Found by its contents: the row shows "Contains: …" with a snippet of the text.</summary>
        public bool IsContentMatch { get; }

        /// <summary>The words the snippet looks for.</summary>
        public IReadOnlyList<string> ContentWords => Owner?.Results.ContentWords ?? Array.Empty<string>();

        /// <summary>The line of text around the match, once read (<see cref="ContentSnippetLoader"/>); empty when none was found.</summary>
        public string Snippet { get; internal set; }

        internal bool SnippetRequested;

        /// <summary>What the row shows after the name for a content match.</summary>
        public string ContainsText => !IsContentMatch || Snippet == null
            ? null
            : Snippet.Length > 0 ? "Contains: " + Snippet : "Contains: " + string.Join(" ", ContentWords);

        /// <summary>The characters of <see cref="Name"/> that match the search words (for the highlighted name).</summary>
        public int[] HighlightPositions { get; }

        public string SizeDisplay => _sizeDisplay ??= IsDirectory ? string.Empty : FormatSize(Size);

        public string ModifiedDisplay => _modifiedDisplay ??= ModifiedUtc.Ticks <= DateTime.MinValue.Ticks + TimeSpan.TicksPerDay
            ? string.Empty
            : ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        public string TypeDisplay => _typeDisplay ??= IsDirectory ? "File folder" : TypeOf(Name);

        /// <summary>The hover tip: the full path, then size and date.</summary>
        public string ToolTip => IsDirectory
            ? $"{FullPath}\nModified: {ModifiedDisplay}"
            : $"{FullPath}\n{TypeDisplay}, {SizeDisplay}\nModified: {ModifiedDisplay}" + (ContainsText != null ? "\n" + ContainsText : string.Empty);

        private static int[] ToPositions(IReadOnlyList<TextRange> ranges, int length)
        {
            if (ranges == null || ranges.Count == 0)
            {
                return Array.Empty<int>();
            }

            int count = 0;
            foreach (TextRange range in ranges)
            {
                count += range.Length;
            }

            var positions = new List<int>(count);
            foreach (TextRange range in ranges)
            {
                for (int i = range.Start; i < range.Start + range.Length && i < length; i++)
                {
                    positions.Add(i);
                }
            }

            return positions.ToArray();
        }

        private static string TypeOf(string name)
        {
            string extension = System.IO.Path.GetExtension(name);
            return extension.Length <= 1 ? "File" : extension.Substring(1).ToUpperInvariant() + " File";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
            }

            string[] units = { "KB", "MB", "GB", "TB" };
            double size = bytes / 1024d;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return size.ToString(size < 10 ? "N1" : "N0", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    /// <summary>
    /// The results as a read-only list for a virtualizing ListBox: rows are built from <see cref="SearchResults"/> only when
    /// the list asks for them (the visible ones and a little more), and the last few thousand are kept. So every match can
    /// be scrolled through, a million as fast as ten; the previous view materialized at most 500 entries. A new search
    /// replaces the whole list. UI thread only.
    /// </summary>
    public sealed class SearchResultList : IList, IReadOnlyList<SearchResultRow>, INotifyCollectionChanged
    {
        private const int CacheLimit = 4096;
        private readonly Dictionary<int, SearchResultRow> _rows = new Dictionary<int, SearchResultRow>();

        public SearchResultList(SearchResults results)
        {
            Results = results ?? SearchResults.Empty;
        }

        public SearchResults Results { get; }

        public int Count => Results.Count;

        public SearchResultRow this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Results.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (!_rows.TryGetValue(index, out SearchResultRow row))
                {
                    if (_rows.Count >= CacheLimit)
                    {
                        _rows.Clear(); // the list asks again for what is on screen; cheap to rebuild
                    }

                    row = new SearchResultRow(Results.GetHit(index), index, this);
                    _rows[index] = row;
                }

                return row;
            }
        }

        /// <summary>The row of <paramref name="fullPath"/> among the first <paramref name="limit"/> results, or -1.</summary>
        public int IndexOfPath(string fullPath, int limit = 5000)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return -1;
            }

            int end = Math.Min(Count, limit);
            for (int i = 0; i < end; i++)
            {
                if (string.Equals(this[i].FullPath, fullPath, PathUtil.Comparison))
                {
                    return i;
                }
            }

            return -1;
        }

        // The list never changes after construction (a new search makes a new list), so this is never raised.
        public event NotifyCollectionChangedEventHandler CollectionChanged { add { } remove { } }

        object IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => true;
        object ICollection.SyncRoot => this;
        bool ICollection.IsSynchronized => false;

        int IList.Add(object value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object value) => ((IList)this).IndexOf(value) >= 0;
        int IList.IndexOf(object value) => value is SearchResultRow row && ReferenceEquals(row.Owner, this) ? row.Index : -1;
        void IList.Insert(int index, object value) => throw new NotSupportedException();
        void IList.Remove(object value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator<SearchResultRow> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
