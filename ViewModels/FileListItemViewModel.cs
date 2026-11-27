// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One row in the file list. Deliberately built entirely
    /// synchronously — every field here is either copied straight from
    /// a DirectoryIndexEntry (already captured for free by the native
    /// enumerator) or cheap, pure string formatting. There is no
    /// per-row I/O and nothing to await, which is what makes on-demand
    /// hydration in VirtualizingFileList fast enough to happen directly
    /// on the indexer without a placeholder/loading-state dance.
    ///
    /// Immutable: selection lives in each list's index-based selection
    /// model (see FileListView), not on the item, because
    /// VirtualizingFileList evicts and later re-creates the instance for
    /// the same file as its resident window slides. Being immutable also
    /// means bindings to it never subscribe to change notifications.
    /// </summary>
    public sealed class FileListItemViewModel
    {
        public FileListItemViewModel(DirectoryIndexEntry entry, string parentPath)
        {
            Name = entry.Name;
            FullPath = PathUtil.Combine(parentPath, entry.Name);
            IsDirectory = entry.IsDirectory;
            IsHidden = entry.IsHidden;
            IsSystem = entry.IsSystem;
            SizeBytes = entry.SizeBytes;
            LastWriteTimeUtc = entry.LastWriteTimeUtc;
            CreationTimeUtc = entry.CreationTimeUtc;
            LastAccessTimeUtc = entry.LastAccessTimeUtc;
            Attributes = entry.Attributes;
            IconGlyph = FileIconGlyphs.For(entry.Name, entry.IsDirectory);
            if (!entry.IsDirectory)
            {
                _iconColorHex = FileIconGlyphs.ColorHexFor(entry.Name, false);
                _iconColorBrush = Infrastructure.Converters.HexToBrushConverter.BrushFor(_iconColorHex);
            }
        }

        private string _systemIconKey;

        /// <summary>
        /// How this item shares its Windows icon (<see cref="Infrastructure.SystemIcons.SystemIconCache.KeyFor"/>), worked
        /// out once; null for folders, which keep the app's own folder icon (and color).
        /// </summary>
        public string SystemIconKey => IsDirectory ? null : _systemIconKey ??= Infrastructure.SystemIcons.SystemIconCache.KeyFor(FullPath, Name, IsDirectory, IsSystem);

        /// <summary>Hidden attribute (or a dot name on Linux and macOS): the views draw the item translucent, as Explorer does.</summary>
        public bool IsHidden { get; }

        /// <summary>System attribute: the views draw the item's text in the system-item color (SystemItemText).</summary>
        public bool IsSystem { get; }

        /// <summary>
        /// The identity of the list that created this item (0 for items built elsewhere: Timeline, Nexus) and its
        /// position there, so VirtualizingFileList.IndexOf is O(1). An id rather than a reference: recycled list
        /// containers keep their last item for reuse, and a reference would keep that item's whole folder listing
        /// (index, entries, cache) alive after navigating away.
        /// </summary>
        internal int OwnerListId;
        internal int OwnerIndex = -1;

        /// <summary>
        /// Items are equal when they name the same file. VirtualizingFileList evicts off-screen items and creates
        /// a fresh instance when they scroll back in, and the same file appears as different instances in different
        /// lists (a new list per filter, sort or refresh), so identity says nothing about which file an item is.
        /// </summary>
        public override bool Equals(object obj) =>
            obj is FileListItemViewModel other &&
            (ReferenceEquals(this, other) || string.Equals(FullPath, other.FullPath, ByteExplorer.Core.PathUtil.Comparison));

        public override int GetHashCode() => ByteExplorer.Core.PathUtil.Comparer.GetHashCode(FullPath);

        private string _sizeDisplay;
        private string _dateModifiedDisplay;
        private string _typeDisplay;

        /// <summary>Type names by extension ("PDF File"), shared by every item: one string per extension, not per file.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TypeNames =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] SizeUnits = { "bytes", "KB", "MB", "GB", "TB" };

        public string Name { get; }

        private string _displayName;
        private int _displayNameVersion = -1;

        /// <summary>The name as shown: without its extension when Folder options hide extensions of known types.</summary>
        public string DisplayName
        {
            get
            {
                int version = FolderOptions.Version;
                if (_displayNameVersion != version)
                {
                    _displayName = FolderOptions.DisplayNameFor(Name, IsDirectory);
                    _displayNameVersion = version;
                }

                return _displayName;
            }
        }

        public string FullPath { get; }
        public bool IsDirectory { get; }
        public long SizeBytes { get; }
        public DateTime LastWriteTimeUtc { get; }
        // Formatted on first use (the views read them on the UI thread or while preparing the next page's text): an item
        // created for the icon views or for a selection never pays for a date or size string it does not show. A race
        // between two threads formats the same string twice, harmlessly.
        public string SizeDisplay => IsDirectory ? FolderSizeDisplay : _sizeDisplay ??= FormatSize(SizeBytes);

        /// <summary>A folder's counted size ("1.2 GB", "1.2 GB…" while counting), or "" when it hasn't been counted.</summary>
        private string FolderSizeDisplay =>
            Core.Services.FolderSizes.TryGet(FullPath, out Core.Services.FolderSizeInfo info)
                ? (!info.IsComplete && info.Bytes == 0 && info.Files == 0 ? "Calculating…" : Core.Services.FolderSizes.Format(info))
                : string.Empty;
        public string DateModifiedDisplay => _dateModifiedDisplay ??= FormatDate(LastWriteTimeUtc);

        /// <summary>When it was created and last opened (Details › Date created / Date accessed); default when unknown.</summary>
        public DateTime CreationTimeUtc { get; }
        public DateTime LastAccessTimeUtc { get; }

        /// <summary>The file system attributes as read with the folder listing.</summary>
        public FileAttributes Attributes { get; }

        private string _dateCreatedDisplay;
        private string _dateAccessedDisplay;
        private string _attributesDisplay;

        public string DateCreatedDisplay => _dateCreatedDisplay ??= FormatDate(CreationTimeUtc);
        public string DateAccessedDisplay => _dateAccessedDisplay ??= FormatDate(LastAccessTimeUtc);

        /// <summary>".pdf" (Details › File extension); empty for folders and names without one.</summary>
        public string ExtensionDisplay => IsDirectory ? string.Empty : Path.GetExtension(Name);

        /// <summary>The folder the item is in (Details › Folder path).</summary>
        public string FolderPathDisplay => PathUtil.GetParent(FullPath) ?? string.Empty;

        /// <summary>
        /// Explorer's attribute letters (Details › Attributes): R read-only, H hidden, S system, A archive, D folder,
        /// L link, C compressed, E encrypted, O offline.
        /// </summary>
        public string AttributesDisplay => _attributesDisplay ??= AttributeLetters(Attributes);

        internal static string AttributeLetters(FileAttributes attributes)
        {
            if ((int)attributes == -1)
            {
                return string.Empty;
            }

            var letters = new System.Text.StringBuilder(9);
            void Add(FileAttributes flag, char letter)
            {
                if ((attributes & flag) != 0)
                {
                    letters.Append(letter);
                }
            }

            Add(FileAttributes.ReadOnly, 'R');
            Add(FileAttributes.Hidden, 'H');
            Add(FileAttributes.System, 'S');
            Add(FileAttributes.Archive, 'A');
            Add(FileAttributes.Directory, 'D');
            Add(FileAttributes.ReparsePoint, 'L');
            Add(FileAttributes.Compressed, 'C');
            Add(FileAttributes.Encrypted, 'E');
            Add(FileAttributes.Offline, 'O');
            return letters.ToString();
        }
        public string TypeDisplay => _typeDisplay ??= IsDirectory ? "File folder" : TypeNameFor(Name);

        private string _toolTip;

        /// <summary>The hover tip: name, type, size and date, one per line (built on first hover).</summary>
        public string ToolTipText
        {
            get
            {
                int version = FolderOptions.Version;
                if (IsDirectory)
                {
                    // Folders: the counted size, when there is one (it can change at any time: never cached).
                    string size = FolderOptions.Current.ShowSizeInTooltips &&
                                  Core.Services.FolderSizes.TryGet(FullPath, out Core.Services.FolderSizeInfo info) && info.IsComplete
                        ? $"\nSize: {Core.Services.FolderSizes.Format(info)} ({info.Files:N0} files, {info.Folders:N0} folders)"
                        : string.Empty;
                    return $"{Name}\nType: {TypeDisplay}{size}\nDate modified: {DateModifiedDisplay}";
                }

                if (_toolTip == null || _toolTipVersion != version)
                {
                    _toolTip = !FolderOptions.Current.ShowSizeInTooltips
                        ? $"{Name}\nType: {TypeDisplay}\nDate modified: {DateModifiedDisplay}"
                        : $"{Name}\nType: {TypeDisplay}\nSize: {SizeDisplay}\nDate modified: {DateModifiedDisplay}";
                    _toolTipVersion = version;
                }

                return _toolTip;
            }
        }

        private int _toolTipVersion;

        private string _tileSubtitle;

        /// <summary>"Type  Size", the Tiles view's second line; built on first use (most views never ask).</summary>
        public string TileSubtitle => IsDirectory
            ? (string.IsNullOrEmpty(FolderSizeDisplay) ? TypeDisplay : TypeDisplay + "  " + FolderSizeDisplay)
            : _tileSubtitle ??= string.IsNullOrEmpty(SizeDisplay) ? TypeDisplay : TypeDisplay + "  " + SizeDisplay;
        public ByteExplorer.Icons.Symbol IconGlyph { get; }
        private readonly string _iconColorHex;
        private readonly Avalonia.Media.IBrush _iconColorBrush;

        /// <summary>The icon color: the file category's, or for a folder the folder color setting (read live).</summary>
        public string IconColorHex => IsDirectory ? FileIconGlyphs.FolderColorHex : _iconColorHex;

        /// <summary>
        /// A cached, immutable brush for the file category color (see
        /// HexToBrushConverter.BrushFor), bound directly by the icon
        /// templates — no per-row converter call while scrolling.
        /// </summary>
        public Avalonia.Media.IBrush IconColorBrush => IsDirectory ? FileIconGlyphs.FolderBrush : _iconColorBrush;

        private static string FormatSize(long bytes)
        {
            string[] units = SizeUnits;
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{bytes:N0} {units[0]}"
                : $"{size.ToString("N1", CultureInfo.CurrentCulture)} {units[unitIndex]}";
        }

        private static string FormatDate(DateTime lastWriteUtc)
        {
            // Unknown, or the epoch a file system without the time reports (no Unix file system keeps a 1601 time).
            if (lastWriteUtc == DateTime.MinValue || lastWriteUtc.Year <= 1601)
            {
                return string.Empty;
            }

            try
            {
                return lastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }

        private static string TypeNameFor(string name)
        {
            ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());
            if (extension.Length <= 1)
            {
                return "File";
            }

            if (extension.Equals(Core.Security.ProtectedFormat.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return "Password-protected item";
            }

            return TypeNames.GetOrAdd(extension.ToString(), static ext => $"{ext.TrimStart('.').ToUpperInvariant()} File");
        }
    }
}
