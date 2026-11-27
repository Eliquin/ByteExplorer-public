// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// One batch file operation's outcome, in the bounded, rolling
    /// recent-operations log. OperationType is a plain string ("Copy",
    /// "Move", "Delete", "New folder", "Rename") rather than reusing
    /// FileOperationKind — that enum only covers Copy/Move/Delete, and
    /// widening it to also mean "creation" or "rename" would be a
    /// meaning change reaching into FileOperationService and every other
    /// consumer of that type, for a distinction that only matters here.
    /// </summary>
    public sealed class FileOperationLogEntry
    {
        public string OperationType { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public bool WasCancelled { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
