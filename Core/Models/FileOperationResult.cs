// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// One entry describing a single item that failed within a batch
    /// operation — kept granular (not just a count) so the eventual
    /// error report can name exactly what failed and why.
    /// </summary>
    public sealed class FileOperationFailure
    {
        public FileOperationFailure(string sourcePath, string message)
        {
            SourcePath = sourcePath;
            Message = message;
        }

        public string SourcePath { get; }
        public string Message { get; }

        /// <summary>
        /// Where the item was going (copies and moves), so "Retry failed" can send it there again; null for a delete or a
        /// failure that is not about one item (the destination full).
        /// </summary>
        public string RetryDestination { get; init; }
    }

    /// <summary>
    /// Aggregated outcome of a batch Copy/Move/Delete. Deliberately a
    /// "continue past failures, report at the end" model — one locked
    /// file in a 50-file copy doesn't hold the other 49 hostage,
    /// matching how Explorer itself behaves. FailureCount == 0 and
    /// WasCancelled == false together mean full, clean success.
    /// </summary>
    public sealed class FileOperationResult
    {
        public FileOperationResult(FileOperationKind kind, int successCount, IReadOnlyList<FileOperationFailure> failures, bool wasCancelled, IReadOnlyList<FileOperationFailure> partialMoves = null, int skippedCount = 0)
        {
            SkippedCount = skippedCount;
            Kind = kind;
            SuccessCount = successCount;
            Failures = failures;
            WasCancelled = wasCancelled;
            PartialMoves = partialMoves ?? Array.Empty<FileOperationFailure>();
        }

        public FileOperationKind Kind { get; }
        public int SuccessCount { get; }
        public IReadOnlyList<FileOperationFailure> Failures { get; }
        public bool WasCancelled { get; }

        /// <summary>Items the person chose to skip at a name clash: neither a success nor a failure.</summary>
        public int SkippedCount { get; }

        /// <summary>
        /// Move-specific: items where the destination was successfully
        /// copied, verified, and committed, but the source couldn't be
        /// removed afterward (commonly: still open in another program).
        /// Deliberately NOT counted in Failures — the move's primary
        /// goal succeeded, the file genuinely is at the new location.
        /// Reporting these as plain failures would be actively
        /// misleading, since "nothing happened" and "it worked but
        /// cleanup didn't finish" call for very different next steps
        /// from the person reading the result.
        /// </summary>
        public IReadOnlyList<FileOperationFailure> PartialMoves { get; }

        public bool HasFailures => Failures.Count > 0;
        public bool HasPartialMoves => PartialMoves.Count > 0;
        public bool IsFullSuccess => !HasFailures && !HasPartialMoves && !WasCancelled;
    }
}
