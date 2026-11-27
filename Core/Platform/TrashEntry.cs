// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// One item in the trash / Recycle Bin, as the trash view lists it: the name it had, where it came from, when it was
    /// deleted and how big it is, plus where the trash keeps it (the renamed item and its record), which is what restoring
    /// and deleting it permanently work on.
    /// </summary>
    public sealed class TrashEntry
    {
        public TrashEntry(string name, string originalPath, string trashPath, string infoPath, DateTime deletedUtc, long size, bool isDirectory)
        {
            Name = name ?? string.Empty;
            OriginalPath = string.IsNullOrEmpty(originalPath) ? null : originalPath;
            TrashPath = trashPath ?? throw new ArgumentNullException(nameof(trashPath));
            InfoPath = infoPath;
            DeletedUtc = deletedUtc;
            Size = size;
            IsDirectory = isDirectory;
        }

        /// <summary>The item's name before it was deleted (not the unique name the trash renamed it to).</summary>
        public string Name { get; }

        /// <summary>The full path it was deleted from; null where the trash keeps no record of it (macOS).</summary>
        public string OriginalPath { get; }

        /// <summary>The item itself, inside the trash.</summary>
        public string TrashPath { get; }

        /// <summary>The trash's record of the item (.trashinfo, $I file); null where there is none (macOS).</summary>
        public string InfoPath { get; }

        /// <summary>When it was deleted (UTC); <see cref="DateTime.MinValue"/> when unknown.</summary>
        public DateTime DeletedUtc { get; }

        /// <summary>Bytes; -1 when unknown (a folder whose size the trash does not record).</summary>
        public long Size { get; }

        public bool IsDirectory { get; }

        /// <summary>Only an item whose original place is known can be put back.</summary>
        public bool CanRestore => OriginalPath != null;

        /// <summary>The folder it was deleted from; null when unknown.</summary>
        public string OriginalFolder => OriginalPath == null ? null : Path.GetDirectoryName(OriginalPath);

        /// <summary>The same item in the shape the undo history and <see cref="Trash.Restore(TrashedItem)"/> use.</summary>
        public TrashedItem ToTrashedItem() => new TrashedItem(OriginalPath, TrashPath, InfoPath);
    }
}
