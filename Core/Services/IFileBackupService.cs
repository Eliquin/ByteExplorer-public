// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Pre-operation safety net for moves to another drive and permanent
    /// deletes (see FileBackupResult's remarks): copies the original files to a
    /// temp location before the real, potentially destructive operation
    /// runs, so a sudden interruption (power loss, app crash, network
    /// drop) during the operation itself leaves a recoverable copy
    /// behind rather than lost data.
    ///
    /// Honest limitation: a backup on the same physical drive as the
    /// source does not protect against that drive failing — only
    /// against interruption. Point FileList.BackupFolderPath at a
    /// different physical drive for that specific protection.
    /// </summary>
    public interface IFileBackupService
    {
        /// <summary>
        /// Recursively sums file sizes for the given paths, stopping
        /// early the moment the running total exceeds stopIfExceeds —
        /// no value in fully walking a huge tree just to confirm what's
        /// already known to be over the threshold.
        /// </summary>
        Task<long> ComputeTotalSizeAsync(IReadOnlyList<string> paths, long stopIfExceeds, CancellationToken cancellationToken = default);

        Task<FileBackupResult> CreateBackupAsync(IReadOnlyList<string> paths, IProgress<string> progress = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Called after the real operation completes fully successfully
        /// — no reason to keep the backup once it's not needed.
        /// </summary>
        void DeleteBackup(string backupFolderPath);

        /// <summary>
        /// Best-effort startup maintenance: removes backup folders older
        /// than maxAge, left behind by a session that ended before its
        /// own cleanup could run (e.g. crashed, or the operation failed
        /// and the backup was deliberately kept).
        /// </summary>
        void CleanupOldBackups(TimeSpan maxAge);
    }
}
