// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One row in the This PC "Recent" list. Paths may be real files/folders
    /// or .lnk shortcuts under the shell Recent folder.
    /// </summary>
    public sealed class ThisPcRecentItem
    {
        public ThisPcRecentItem(
            string displayName,
            string fullPath,
            bool isDirectory,
            DateTime lastAccessUtc,
            long sizeBytes)
        {
            DisplayName = displayName ?? string.Empty;
            FullPath = fullPath ?? string.Empty;
            IsDirectory = isDirectory;
            LastAccessUtc = lastAccessUtc;
            SizeBytes = sizeBytes;
        }

        public string DisplayName { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public DateTime LastAccessUtc { get; }
        public long SizeBytes { get; }

        public string TypeDisplay => IsDirectory ? "File folder" : "File";

        public string DateAccessDisplay
        {
            get
            {
                if (LastAccessUtc == DateTime.MinValue) return string.Empty;
                try
                {
                    return LastAccessUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        public string SizeDisplay
        {
            get
            {
                if (IsDirectory || SizeBytes < 0) return string.Empty;
                string[] units = { "bytes", "KB", "MB", "GB", "TB" };
                double size = SizeBytes;
                int unit = 0;
                while (size >= 1024 && unit < units.Length - 1)
                {
                    size /= 1024;
                    unit++;
                }

                return unit == 0
                    ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", SizeBytes, units[0])
                    : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", size, units[unit]);
            }
        }

        /// <summary>Folder or document icon (Fluent UI System Icons; the WPF build's Segoe MDL2 glyphs exist only on Windows).</summary>
        public ByteExplorer.Icons.Symbol Glyph => IsDirectory ? ByteExplorer.Icons.Symbol.Folder : ByteExplorer.Icons.Symbol.Document;
    }
}
