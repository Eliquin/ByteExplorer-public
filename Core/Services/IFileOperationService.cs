// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services.FileOperations;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Copy, move, delete and undo. Every method runs off the UI thread, continues past individual failures and reports
    /// them in the result, asks about name clashes through the context's resolver (keeping both when there is none,
    /// never overwriting unasked), reports progress, honours pause and cancellation, and returns an undo record for the
    /// reversible changes it made. See <see cref="FileOperationService"/>.
    /// </summary>
    public interface IFileOperationService
    {
        /// <summary>Copy or move (<paramref name="kind"/>) the items into a folder.</summary>
        Task<FileOperationOutcome> TransferAsync(FileOperationKind kind, IReadOnlyList<string> sourcePaths, string destinationFolder, FileOperationContext context);

        /// <summary>To the trash (undoable), or permanently.</summary>
        Task<FileOperationOutcome> DeleteAsync(IReadOnlyList<string> paths, bool permanent, FileOperationContext context);

        /// <summary>Reverses an earlier operation's record.</summary>
        Task<FileOperationOutcome> UndoAsync(FileOperationRecord record, FileOperationContext context);
    }
}
