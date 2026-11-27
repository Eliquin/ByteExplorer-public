// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One row of the trash view: an item in the trash with where it came from, when it was deleted and its size. A
    /// folder's size is filled in after the listing (the trash records it on Windows only; elsewhere it is counted in the
    /// background), so <see cref="SizeBytes"/> notifies.
    /// </summary>
    public sealed class TrashItemViewModel : ViewModelBase
    {
        private long _sizeBytes;

        public TrashItemViewModel(TrashEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _sizeBytes = entry.Size;
            IconGlyph = FileIconGlyphs.For(entry.Name, entry.IsDirectory);
            IconColorBrush = entry.IsDirectory
                ? FileIconGlyphs.FolderBrush
                : Infrastructure.Converters.HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(entry.Name, false));
        }

        public TrashEntry Entry { get; }

        public string Name => Entry.Name;

        public bool IsDirectory => Entry.IsDirectory;

        public bool CanRestore => Entry.CanRestore;

        /// <summary>The folder it was deleted from, as the "Original location" column shows it.</summary>
        public string OriginalLocation => Entry.OriginalFolder ?? (Entry.OriginalPath != null ? Entry.OriginalPath : "Unknown");

        public DateTime DeletedUtc => Entry.DeletedUtc;

        public string DeletedDisplay
        {
            get
            {
                if (Entry.DeletedUtc == DateTime.MinValue)
                {
                    return string.Empty;
                }

                try
                {
                    return Entry.DeletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>Bytes; -1 while unknown (a folder not counted yet, or one that couldn't be read).</summary>
        public long SizeBytes
        {
            get => _sizeBytes;
            internal set
            {
                if (SetProperty(ref _sizeBytes, value))
                {
                    RaisePropertyChanged(nameof(SizeDisplay));
                }
            }
        }

        public string SizeDisplay => _sizeBytes < 0 ? string.Empty : FolderSizes.FormatBytes(_sizeBytes);

        public string TypeDisplay
        {
            get
            {
                if (Entry.IsDirectory)
                {
                    return "File folder";
                }

                string extension = Path.GetExtension(Entry.Name);
                return string.IsNullOrEmpty(extension) || extension.Length == Entry.Name.Length
                    ? "File"
                    : extension.TrimStart('.').ToUpperInvariant() + " File";
            }
        }

        public Icons.Symbol IconGlyph { get; }

        public Avalonia.Media.IBrush IconColorBrush { get; }

        public string ToolTip => Entry.OriginalPath != null
            ? $"{Name}\nOriginal location: {OriginalLocation}\nDeleted: {DeletedDisplay}"
            : $"{Name}\nThe original location isn't known: {Trash.DisplayName} on this system doesn't record it.";
    }
}
