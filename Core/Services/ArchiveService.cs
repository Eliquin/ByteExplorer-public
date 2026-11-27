// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Services.Archives;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// "Compress to ZIP file" and "Extract all" / "Extract here" for the context menu, on every OS. Compressing makes a
    /// ZIP (System.IO.Compression); extracting reads every format <see cref="ArchiveFormats"/> can browse (ZIP and TAR
    /// built in, others through an installed archiver). Both run on a background thread, report progress as text, stop
    /// promptly on cancellation and never leave a half-written result behind: the archive (or the extraction folder) is
    /// built under a temporary name and only renamed into place once complete.
    /// </summary>
    public sealed class ArchiveService
    {
        private const int ProgressThrottleMs = 200;
        private readonly ILogger _logger;

        public ArchiveService(ILogger logger = null)
        {
            _logger = (logger ?? Log.Logger).ForContext<ArchiveService>();
        }

        /// <summary>A zip path next to the first item, named after it (or after the folder for several items), numbered if taken.</summary>
        public static string SuggestArchivePath(IReadOnlyList<string> paths)
        {
            string first = PathUtil.TrimTrailingSeparator(paths[0]);
            string folder = PathUtil.GetParent(first) ?? first;
            string stem = paths.Count == 1 ? Path.GetFileNameWithoutExtension(PathUtil.GetName(first)) : PathUtil.GetName(folder);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "Archive";
            }

            return FreeName(folder, stem, ".zip");
        }

        /// <summary>
        /// Writes the items (files and whole folder trees) into a new zip at <paramref name="destinationZip"/>. Entry
        /// names are relative to each item's parent, with '/' separators as the ZIP specification requires; symbolic
        /// links are stored as the files they point to, never followed into folders (no cycles). Returns the archive path.
        /// </summary>
        public Task<string> CompressAsync(IReadOnlyList<string> paths, string destinationZip, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Count == 0)
            {
                throw new ArgumentException("Nothing to compress.", nameof(paths));
            }

            return Task.Run(() =>
            {
                string temporary = destinationZip + ".partial-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                long lastReport = 0;
                int count = 0;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        foreach (string item in paths)
                        {
                            string root = PathUtil.TrimTrailingSeparator(item);
                            string baseName = PathUtil.GetName(root);
                            if (Directory.Exists(root) && !IsLink(root))
                            {
                                AddDirectory(archive, root, baseName, ref count, ref lastReport, progress, cancellationToken);
                            }
                            else if (File.Exists(root))
                            {
                                AddFile(archive, root, baseName, ref count, ref lastReport, progress, cancellationToken);
                            }
                        }
                    }

                    File.Move(temporary, destinationZip, overwrite: false);
                    _logger.Information("Compressed {Count} file(s) into {Archive}", count, destinationZip);
                    return destinationZip;
                }
                catch
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Extracts an archive into a new folder next to it named after the archive (numbered if taken) and returns that
        /// folder. Every entry's destination is checked to lie inside the target folder, so a crafted archive with
        /// "../" or absolute entry names ("zip slip") cannot write anywhere else; such entries fail the extraction.
        /// </summary>
        public Task<string> ExtractAsync(string zipPath, IProgress<string> progress, CancellationToken cancellationToken) =>
            ExtractAsync(zipPath, null, progress, cancellationToken);

        /// <summary>
        /// Extracts an archive into a new folder named after it (numbered if taken) inside <paramref name="destinationParent"/>
        /// (null: beside the archive) and returns that folder. ZIP files are read here; other formats by their
        /// <see cref="ArchiveProvider"/>, with the same refusal of names that leave the folder.
        /// </summary>
        public Task<string> ExtractAsync(string zipPath, string destinationParent, IProgress<string> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                string parent = destinationParent ?? PathUtil.GetParent(zipPath) ?? throw new IOException("The archive has no parent folder.");
                string target = FreeName(parent, ArchiveFormats.StemOf(zipPath), string.Empty);
                string staging = Path.Combine(parent, "." + PathUtil.GetName(target) + ".extracting-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(staging);

                try
                {
                    ArchiveProvider provider = ArchiveFormats.GetProvider(zipPath);
                    if (provider != null && !(provider is ZipArchiveProvider))
                    {
                        progress?.Report($"Extracting {PathUtil.GetName(zipPath)}");
                        provider.ExtractAll(zipPath, staging, progress, cancellationToken);
                    }
                    else
                    {
                        ExtractZip(zipPath, staging, progress, cancellationToken);
                    }

                    Directory.Move(staging, target);
                    _logger.Information("Extracted {Archive} into {Folder}", zipPath, target);
                    return target;
                }
                catch
                {
                    try
                    {
                        Directory.Delete(staging, recursive: true);
                    }
                    catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException)
                    {
                        _logger.Debug(cleanup, "Could not remove the partial extraction {Folder}", staging);
                    }

                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>Writes every entry of a ZIP into the (empty) staging folder; an entry naming a path outside it fails the whole extraction.</summary>
        private static void ExtractZip(string zipPath, string staging, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string stagingPrefix = PathUtil.WithTrailingSeparator(Path.GetFullPath(staging));
            long lastReport = 0;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                int total = archive.Entries.Count;
                int done = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('\\', '/');
                    string destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(stagingPrefix, PathUtil.Comparison) && !PathUtil.AreSame(destination, staging))
                    {
                        throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
                    }

                    if (relative.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (Stream input = entry.Open())
                        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
                        {
                            input.CopyTo(output, 256 * 1024);
                        }

                        try
                        {
                            File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
                        }
                        catch (Exception ex) when (ex is IOException || ex is ArgumentOutOfRangeException || ex is UnauthorizedAccessException)
                        {
                            // Timestamps are a nicety; a file system that refuses them is not a failure.
                        }
                    }

                    done++;
                    Report(progress, ref lastReport, $"Extracting {done:N0} of {total:N0}: {entry.Name}", done == total);
                }
            }
        }

        public static bool IsZip(string path) =>
            !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

        /// <summary>
        /// True for an archive file "Extract all" / "Extract here" can unpack on this machine: a ZIP or TAR always, other
        /// formats when an archiver is installed (<see cref="ArchiveFormats.MissingToolHint"/> says what otherwise).
        /// Password-protected items have their own commands.
        /// </summary>
        public static bool CanExtract(string path) =>
            !string.IsNullOrEmpty(path) && !Security.ProtectedFormat.IsProtectedFile(path) && ArchiveFormats.IsBrowsable(path) && File.Exists(path);

        private static void AddDirectory(ZipArchive archive, string directory, string entryPrefix, ref int count, ref long lastReport, IProgress<string> progress, CancellationToken cancellationToken)
        {
            bool any = false;
            foreach (var entry in NativeFileEnumerator.Enumerate(directory, cancellationToken))
            {
                any = true;
                string child = Path.Combine(directory, entry.Name);
                string childEntry = entryPrefix + "/" + entry.Name;
                bool isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (entry.IsDirectory && !isLink)
                {
                    AddDirectory(archive, child, childEntry, ref count, ref lastReport, progress, cancellationToken);
                }
                else if (!entry.IsDirectory)
                {
                    AddFile(archive, child, childEntry, ref count, ref lastReport, progress, cancellationToken);
                }
            }

            if (!any)
            {
                archive.CreateEntry(entryPrefix + "/"); // keep empty folders
            }
        }

        private static void AddFile(ZipArchive archive, string file, string entryName, ref int count, ref long lastReport, IProgress<string> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            try
            {
                entry.LastWriteTime = File.GetLastWriteTime(file);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Dates outside 1980-2107 cannot be stored in a ZIP; keep the default.
            }

            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.SequentialScan))
            using (Stream output = entry.Open())
            {
                input.CopyTo(output, 256 * 1024);
            }

            count++;
            Report(progress, ref lastReport, $"Compressing {count:N0}: {Path.GetFileName(file)}", force: false);
        }

        private static void Report(IProgress<string> progress, ref long lastReport, string text, bool force)
        {
            long now = Environment.TickCount64;
            if (progress != null && (force || now - lastReport >= ProgressThrottleMs))
            {
                lastReport = now;
                progress.Report(text);
            }
        }

        private static bool IsLink(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string FreeName(string folder, string stem, string extension)
        {
            string candidate = Path.Combine(folder, stem + extension);
            for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            {
                candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            }

            return candidate;
        }
    }
}
