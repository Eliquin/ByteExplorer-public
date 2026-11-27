// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// The complete, sorted, lightweight index for one folder — the
    /// layer that makes Count and "what's at index N" instant and O(1),
    /// no matter how large the folder is. This is what the virtualizing
    /// file list is built on top of; it holds no per-row UI state.
    /// </summary>
    public sealed class DirectoryIndex
    {
        public DirectoryIndex(string parentPath, IReadOnlyList<DirectoryIndexEntry> entries, bool hasError, string errorMessage)
        {
            ParentPath = parentPath;
            Entries = entries ?? Array.Empty<DirectoryIndexEntry>();
            HasError = hasError;
            ErrorMessage = errorMessage;
        }

        public string ParentPath { get; }
        public IReadOnlyList<DirectoryIndexEntry> Entries { get; }
        public int Count => Entries.Count;
        public bool HasError { get; }
        public string ErrorMessage { get; }

        /// <summary>
        /// True when Entries are in the default order (folders first, then natural name order, ascending), so the
        /// file list can skip sorting for the default view. Set by DirectoryIndexService.
        /// </summary>
        public bool IsSortedByName { get; init; }

        public static DirectoryIndex Empty(string parentPath) =>
            new DirectoryIndex(parentPath, Array.Empty<DirectoryIndexEntry>(), false, null);

        public static DirectoryIndex Error(string parentPath, string message) =>
            new DirectoryIndex(parentPath, Array.Empty<DirectoryIndexEntry>(), true, message);
    }
}
