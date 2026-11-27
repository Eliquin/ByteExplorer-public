// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// One entry in a directory's lightweight index. Deliberately a
    /// readonly struct, not a class: for a folder with a million
    /// entries, avoiding per-entry object-header overhead and keeping
    /// entries contiguous in memory (cache-friendly for the sort pass)
    /// is the difference between an index that costs tens of MB and
    /// one that costs hundreds.
    ///
    /// This struct holds only what a single native FindFirstFileEx/
    /// FindNextFile call already returns for free — no second per-file
    /// stat call is ever made to populate it.
    /// </summary>
    public readonly struct DirectoryIndexEntry
    {
        public DirectoryIndexEntry(string name, bool isDirectory, long sizeBytes, DateTime lastWriteTimeUtc, FileAttributes attributes,
            DateTime creationTimeUtc = default, DateTime lastAccessTimeUtc = default)
        {
            Name = name;
            IsDirectory = isDirectory;
            SizeBytes = sizeBytes;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Attributes = attributes;
            CreationTimeUtc = creationTimeUtc;
            LastAccessTimeUtc = lastAccessTimeUtc;
        }

        /// <summary>
        /// When it was created (Details › Date created): part of the same directory read on Windows; on Linux the stat
        /// already made for the size and date (the birth time where the file system keeps one, else the change time).
        /// Default when unknown (entries not read from a folder: archives, phones).
        /// </summary>
        public DateTime CreationTimeUtc { get; }

        /// <summary>When it was last opened (Details › Date accessed), as the file system records it; default when unknown.</summary>
        public DateTime LastAccessTimeUtc { get; }

        public string Name { get; }
        public bool IsDirectory { get; }
        public long SizeBytes { get; }
        public DateTime LastWriteTimeUtc { get; }
        public FileAttributes Attributes { get; }

        /// <summary>
        /// Hidden attribute set (on Linux and macOS .NET sets it for dot names), or a copy engine temporary file: those
        /// carry no Hidden attribute on Windows and would otherwise show up in the list while a copy runs.
        /// </summary>
        public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0 ||
                                (!IsDirectory && Services.FileOperations.OperationTemporaries.IsTemporaryName(Name));
        public bool IsSystem => (Attributes & FileAttributes.System) != 0;
    }
}
