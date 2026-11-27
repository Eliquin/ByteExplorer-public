// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    public enum BackupOutcome
    {
        NotAttempted,     // FileList.BackupBeforeFileOperation is off
        SkippedTooLarge,  // selection exceeds BackupMaxTotalSizeBytes
        Success,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Outcome of a pre-operation backup attempt (see FileBackupService). Backup applies only where data could be at
    /// risk: moves to another drive and permanent deletes. A copy never touches the source, a move within a drive is a
    /// rename, and a delete to the Trash/Recycle Bin is already recoverable.
    /// </summary>
    public sealed class FileBackupResult
    {
        public static FileBackupResult NotAttempted() => new FileBackupResult(BackupOutcome.NotAttempted, null, null);

        public static FileBackupResult SkippedTooLarge() => new FileBackupResult(BackupOutcome.SkippedTooLarge, null, null);

        public static FileBackupResult SuccessAt(string backupFolderPath) => new FileBackupResult(BackupOutcome.Success, backupFolderPath, null);

        public static FileBackupResult Failed(string errorMessage) => new FileBackupResult(BackupOutcome.Failed, null, errorMessage);

        public static FileBackupResult Cancelled() => new FileBackupResult(BackupOutcome.Cancelled, null, null);

        private FileBackupResult(BackupOutcome outcome, string backupFolderPath, string errorMessage)
        {
            Outcome = outcome;
            BackupFolderPath = backupFolderPath;
            ErrorMessage = errorMessage;
        }

        public BackupOutcome Outcome { get; }
        public string BackupFolderPath { get; }
        public string ErrorMessage { get; }

        public bool ProceedWithOperation => Outcome != BackupOutcome.Failed && Outcome != BackupOutcome.Cancelled;
    }
}
