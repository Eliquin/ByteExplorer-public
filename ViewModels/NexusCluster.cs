// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One type-based cluster in the Nexus view (Documents, Images, Media, …).
    /// Carries display metadata so the UI can render accent color, glyph,
    /// and aggregate size without further computation.
    /// </summary>
    public sealed class NexusCluster
    {
        public NexusCluster(
            string key,
            string title,
            ByteExplorer.Icons.Symbol glyph,
            string accentBrushKey,
            IReadOnlyList<FileListItemViewModel> items,
            long totalSizeBytes)
        {
            Key = key;
            Title = title;
            Glyph = glyph;
            AccentBrushKey = accentBrushKey;
            Items = items;
            TotalSizeBytes = totalSizeBytes;
        }

        public string Key { get; }
        public string Title { get; }
        public ByteExplorer.Icons.Symbol Glyph { get; }
        public string AccentBrushKey { get; }
        public IReadOnlyList<FileListItemViewModel> Items { get; }
        public int Count => Items.Count;
        public long TotalSizeBytes { get; }

        public string SizeDisplay
        {
            get
            {
                double b = TotalSizeBytes;
                if (b < 1024) return $"{b:0} B";
                if (b < 1024 * 1024) return $"{b / 1024:0.0} KB";
                if (b < 1024L * 1024 * 1024) return $"{b / (1024 * 1024):0.0} MB";
                return $"{b / (1024L * 1024 * 1024):0.00} GB";
            }
        }
    }
}
