// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using ByteExplorer.Core.Security;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>
    /// ZIP files (System.IO.Compression), and the content of an unlocked password-protected item, which is a ZIP
    /// decrypted as it is read (<see cref="ProtectedItems.OpenArchive"/>).
    /// </summary>
    internal sealed class ZipArchiveProvider : ArchiveProvider
    {
        public static readonly ZipArchiveProvider Instance = new ZipArchiveProvider();

        public override string FormatName => "ZIP file";

        public override IReadOnlyList<ArchiveEntryInfo> ReadEntries(string archivePath, int maxEntries, CancellationToken cancellationToken)
        {
            using ZipArchive zip = Open(archivePath);
            var entries = new List<ArchiveEntryInfo>(Math.Min(zip.Entries.Count, maxEntries));
            for (int i = 0; i < zip.Entries.Count && entries.Count < maxEntries; i++)
            {
                ZipArchiveEntry entry = zip.Entries[i];
                bool isFolder = entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                entries.Add(new ArchiveEntryInfo(entry.FullName, isFolder, entry.Length, entry.LastWriteTime.UtcDateTime, i));
            }

            return entries;
        }

        public override void Extract(string archivePath, IReadOnlyList<ArchiveExtraction> items, IProgress<string> progress, CancellationToken cancellationToken)
        {
            using ZipArchive zip = Open(archivePath);
            long lastReport = 0;
            for (int i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchiveExtraction item = items[i];
                int index = item.Entry.Index;
                if (index < 0 || index >= zip.Entries.Count || !string.Equals(zip.Entries[index].FullName, item.Entry.RawName, StringComparison.Ordinal))
                {
                    throw new IOException("The archive changed while it was being read. Try again.");
                }

                Report(progress, ref lastReport, i + 1, items.Count, PathUtil.GetName(item.Target));
                using Stream data = zip.Entries[index].Open();
                WriteFile(data, item.Target, item.Entry.LastWriteUtc);
            }
        }

        /// <summary>Opens a ZIP file, or the content of an unlocked password-protected item (a ZIP inside, decrypted as read).</summary>
        internal static ZipArchive Open(string path) =>
            ProtectedFormat.IsProtectedFile(path) ? ProtectedItems.OpenArchive(path) : ZipFile.OpenRead(path);
    }
}
