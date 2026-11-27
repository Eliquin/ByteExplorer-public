// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>One entry of an archive, as its format (or tool) lists it.</summary>
    internal sealed class ArchiveEntryInfo
    {
        public ArchiveEntryInfo(string rawName, bool isFolder, long length, DateTime lastWriteUtc, int index, bool isEncrypted = false)
        {
            RawName = rawName;
            IsFolder = isFolder;
            Length = length;
            LastWriteUtc = lastWriteUtc;
            Index = index;
            IsEncrypted = isEncrypted;
        }

        /// <summary>The name exactly as stored (or as the tool printed it): what finds the entry again when extracting.</summary>
        public string RawName { get; }

        public bool IsFolder { get; }
        public long Length { get; }

        /// <summary>UTC; <c>default</c> when the format or tool doesn't say.</summary>
        public DateTime LastWriteUtc { get; }

        /// <summary>The entry's position in the archive (sequential formats find it again by counting).</summary>
        public int Index { get; }

        /// <summary>The entry's data is encrypted (known for ZIP-less formats only when 7-Zip lists them).</summary>
        public bool IsEncrypted { get; }
    }

    /// <summary>One file entry to extract and the (already checked) path it goes to.</summary>
    internal readonly record struct ArchiveExtraction(ArchiveEntryInfo Entry, string Target);

    /// <summary>
    /// An encrypted archive (or entry) that can't be read without its password. ByteExplorer asks for passwords only for
    /// its own protected items; for others the message says which program can extract them.
    /// </summary>
    public sealed class ArchivePasswordException : IOException
    {
        public ArchivePasswordException()
            : base("This archive is password protected; extract it with 7-Zip.")
        {
        }
    }

    /// <summary>
    /// One archive format: how to list its entries and extract some of them. <see cref="ArchiveBrowser"/> builds the
    /// folder tree, the paths, the cache and the zip-slip checks on top, so a format only reads and writes bytes. All
    /// formats but ZIP are read-only in the app (browse, open, copy out, extract); ZIP is read-only too while browsing.
    /// </summary>
    internal abstract class ArchiveProvider
    {
        private const int ProgressThrottleMs = 200;

        /// <summary>The format's name in messages ("ZIP file", "TAR archive", "7z archive").</summary>
        public abstract string FormatName { get; }

        /// <summary>Lists the archive's entries (at most <paramref name="maxEntries"/>). Throws for a damaged archive.</summary>
        public abstract IReadOnlyList<ArchiveEntryInfo> ReadEntries(string archivePath, int maxEntries, CancellationToken cancellationToken);

        /// <summary>
        /// Writes the given file entries to their targets (whose folders exist; the targets don't). Entries come from
        /// <see cref="ReadEntries"/> of the same, unchanged archive.
        /// </summary>
        public abstract void Extract(string archivePath, IReadOnlyList<ArchiveExtraction> items, IProgress<string> progress, CancellationToken cancellationToken);

        /// <summary>
        /// Extracts everything into <paramref name="destination"/> (an empty folder). An entry whose name would leave the
        /// folder fails the whole extraction (<see cref="InvalidDataException"/>), as "Extract all" of a ZIP does; links
        /// and devices are not extracted.
        /// </summary>
        public virtual void ExtractAll(string archivePath, string destination, IProgress<string> progress, CancellationToken cancellationToken)
        {
            IReadOnlyList<ArchiveEntryInfo> entries = ReadEntries(archivePath, int.MaxValue, cancellationToken);
            var files = new List<ArchiveExtraction>();
            var taken = new HashSet<string>(PathUtil.Comparer);
            foreach (ArchiveEntryInfo entry in entries)
            {
                string name = ArchiveBrowser.NormalizeInner(entry.RawName);
                if (ArchiveBrowser.IsUnsafeName(entry.RawName) && name.Length > 0)
                {
                    throw new InvalidDataException($"The archive contains an unsafe path: {entry.RawName}");
                }

                if (name.Length == 0)
                {
                    continue;
                }

                string target = ArchiveBrowser.SafeTarget(destination, name);
                if (entry.IsFolder)
                {
                    Directory.CreateDirectory(target);
                }
                else if (taken.Add(target))
                {
                    if (entry.IsEncrypted)
                    {
                        throw new ArchivePasswordException();
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    files.Add(new ArchiveExtraction(entry, target));
                }
            }

            Extract(archivePath, files, progress, cancellationToken);
        }

        /// <summary>Copies an entry's data to a new file (never over an existing one) and gives it the entry's date.</summary>
        protected static void WriteFile(Stream data, string target, DateTime lastWriteUtc)
        {
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
            {
                data?.CopyTo(output, 256 * 1024);
            }

            SetTime(target, lastWriteUtc);
        }

        /// <summary>Gives an extracted file its archived date; a date the file system can't store keeps the extraction time.</summary>
        protected internal static void SetTime(string target, DateTime lastWriteUtc)
        {
            if (lastWriteUtc == default)
            {
                return;
            }

            try
            {
                File.SetLastWriteTimeUtc(target, lastWriteUtc);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException)
            {
                Log.Logger.Debug(ex, "Kept the extraction time of {File}", target);
            }
        }

        /// <summary>Progress text at most every 200 ms (and for the last item), so a million small files don't flood the UI.</summary>
        protected static void Report(IProgress<string> progress, ref long lastReport, int done, int total, string name)
        {
            long now = Environment.TickCount64;
            if (progress != null && (done == total || now - lastReport >= ProgressThrottleMs))
            {
                lastReport = now;
                progress.Report(total > 1 ? $"Extracting {done:N0} of {total:N0}: {name}" : name);
            }
        }
    }
}
