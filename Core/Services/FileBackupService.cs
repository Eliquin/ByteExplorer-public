// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using Microsoft.VisualBasic.FileIO;
using Serilog;

namespace ByteExplorer.Core.Services
{
    public sealed class FileBackupService : IFileBackupService
    {
        private const string BackupRootFolderName = "ByteExplorer_Backups";

        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;

        public FileBackupService(ISettingsService settingsService, ILogger logger = null)
        {
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<FileBackupService>();
        }

        /// <summary>
        /// Live read from settings, same reasoning as other live-read
        /// settings properties in this project — settings.json hasn't
        /// necessarily finished loading at construction time. Falls
        /// back to the system temp folder when BackupFolderPath isn't
        /// configured; an explicitly configured path is the only way to
        /// get protection against the source drive itself failing, not
        /// just against interruption.
        /// </summary>
        private string BackupRoot
        {
            get
            {
                string configured = _settingsService?.Current?.FileList?.BackupFolderPath;
                string baseFolder = string.IsNullOrWhiteSpace(configured)
                    ? Path.GetTempPath()
                    : configured;
                return Path.Combine(baseFolder, BackupRootFolderName);
            }
        }

        public Task<long> ComputeTotalSizeAsync(IReadOnlyList<string> paths, long stopIfExceeds, CancellationToken cancellationToken = default)
        {
            if (paths == null || paths.Count == 0)
            {
                return Task.FromResult(0L);
            }

            return Task.Run(() =>
            {
                long total = 0;

                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (File.Exists(path))
                        {
                            total += new FileInfo(path).Length;
                        }
                        else if (Directory.Exists(path))
                        {
                            foreach (string file in Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                try
                                {
                                    total += new FileInfo(file).Length;
                                }
                                catch (Exception ex)
                                {
                                    // A single unreadable file (permissions,
                                    // deleted mid-walk) shouldn't abort the
                                    // whole size calculation — count it as
                                    // zero-sized and keep going.
                                    _logger.Debug(ex, "Couldn't stat {File} while computing backup size", file);
                                }

                                if (total > stopIfExceeds)
                                {
                                    return total;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Couldn't compute size for {Path}", path);
                    }

                    if (total > stopIfExceeds)
                    {
                        return total;
                    }
                }

                return total;
            }, cancellationToken);
        }

        public Task<FileBackupResult> CreateBackupAsync(IReadOnlyList<string> paths, IProgress<string> progress = null, CancellationToken cancellationToken = default)
        {
            if (paths == null || paths.Count == 0)
            {
                return Task.FromResult(FileBackupResult.NotAttempted());
            }

            return Task.Run(() =>
            {
                // A fresh, uniquely-named subfolder per backup — never
                // collides with a previous or concurrent backup, and the
                // timestamp prefix makes CleanupOldBackups' age check
                // straightforward without needing to persist any extra
                // metadata.
                string backupFolder = Path.Combine(BackupRoot, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");

                try
                {
                    Directory.CreateDirectory(backupFolder);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Couldn't create backup folder {Folder}", backupFolder);
                    return FileBackupResult.Failed($"Couldn't create the backup folder: {ex.Message}");
                }

                for (int i = 0; i < paths.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return FileBackupResult.Cancelled();
                    }

                    string sourcePath = paths[i];
                    string itemName = PathUtil.GetName(sourcePath);
                    progress?.Report($"Backing up {i + 1} of {paths.Count}: {itemName}");

                    string destinationPath = Path.Combine(backupFolder, itemName);

                    try
                    {
                        // overwrite: false is really just a safety
                        // assertion here — destinationPath lives inside a
                        // folder we just created fresh, so a name
                        // collision would indicate something unexpected
                        // rather than a normal case to silently handle.
                        if (Directory.Exists(sourcePath))
                        {
                            FileSystem.CopyDirectory(sourcePath, destinationPath, overwrite: false);
                        }
                        else if (File.Exists(sourcePath))
                        {
                            FileSystem.CopyFile(sourcePath, destinationPath, overwrite: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Deliberately does NOT continue past this failure
                        // the way FileOperationService does for the real
                        // operation — an incomplete backup is not a safety
                        // net at all, so the whole backup attempt (and the
                        // real operation waiting on it) should stop here
                        // rather than proceed on a false sense of safety.
                        _logger.Warning(ex, "Backup failed for {Path}", sourcePath);
                        return FileBackupResult.Failed($"Couldn't back up \"{itemName}\": {ex.Message}");
                    }
                }

                _logger.Information("Backup created at {Folder} for {Count} item(s)", backupFolder, paths.Count);
                return FileBackupResult.SuccessAt(backupFolder);
            }, cancellationToken);
        }

        public void DeleteBackup(string backupFolderPath)
        {
            if (string.IsNullOrEmpty(backupFolderPath))
            {
                return;
            }

            try
            {
                if (Directory.Exists(backupFolderPath))
                {
                    Directory.Delete(backupFolderPath, recursive: true);
                    _logger.Debug("Deleted backup folder {Folder}", backupFolderPath);
                }
            }
            catch (Exception ex)
            {
                // Failing to clean up a backup is a disk-hygiene
                // annoyance, not a correctness problem — the real
                // operation already succeeded by the time this runs, so
                // there's nothing to roll back or retry here.
                _logger.Warning(ex, "Couldn't delete backup folder {Folder}", backupFolderPath);
            }
        }

        public void CleanupOldBackups(TimeSpan maxAge)
        {
            try
            {
                string root = BackupRoot;
                if (!Directory.Exists(root))
                {
                    return;
                }

                DateTime cutoffUtc = DateTime.UtcNow - maxAge;

                foreach (string subfolder in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (Directory.GetCreationTimeUtc(subfolder) < cutoffUtc)
                        {
                            Directory.Delete(subfolder, recursive: true);
                            _logger.Debug("Cleaned up old backup folder {Folder}", subfolder);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Couldn't evaluate/clean up backup folder {Folder}", subfolder);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Backup cleanup pass failed");
            }
        }
    }
}
