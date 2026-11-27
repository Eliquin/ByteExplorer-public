// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Enumerates subdirectories for tree expansion. Uses
    /// Directory.EnumerateDirectories (streaming, backed by
    /// FindFirstFile/FindNextFile) rather than Directory.GetDirectories
    /// (which materializes the full array before returning anything) —
    /// at this scale the difference is small, but the habit matters
    /// once Phase 3's file list is enumerating folders with hundreds of
    /// thousands of entries.
    ///
    /// Hidden and system entries are filtered out here rather than in
    /// the view model, so "what counts as visible" stays a single,
    /// testable decision point.
    ///
    /// Network-path enumeration runs under
    /// <see cref="BlockingOperationRunner"/> on a dedicated thread. The
    /// caller (NavigationNodeViewModel) already arms a deadline on the
    /// token it passes in — that ownership of the actual timeout VALUE
    /// doesn't change here — but a plain CancellationToken can only be
    /// observed between steps, and Directory.EnumerateDirectories'
    /// first FindFirstFile call against an unresponsive UNC path blocks
    /// before any step-checking code ever runs. Running the whole
    /// enumeration on a dedicated thread is what makes the caller's
    /// timeout actually bounded in wall-clock time instead of merely
    /// requested. HardBackstopTimeoutMs is a second, independent,
    /// deliberately generous ceiling — comfortably above the maximum
    /// value NavigationSettings.NetworkDriveTimeoutMs can be sanitized
    /// to (60,000ms) — that exists purely so this method can never hang
    /// truly indefinitely even if some future caller passes a token
    /// with no deadline of its own; in ordinary operation the caller's
    /// own token fires first and this backstop is never reached.
    /// </summary>
    public sealed class FileSystemNavigationService : IFileSystemNavigationService
    {
        private const int HardBackstopTimeoutMs = 120000;

        private readonly ILogger _logger;

        public FileSystemNavigationService(ILogger logger = null)
        {
            _logger = (logger ?? Log.Logger).ForContext<FileSystemNavigationService>();
        }

        public Task<IReadOnlyList<DirectoryNodeInfo>> GetSubdirectoriesAsync(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Task.FromResult((IReadOnlyList<DirectoryNodeInfo>)Array.Empty<DirectoryNodeInfo>());
            }

            if (Platform.Phones.Phones.IsPhonePath(path))
            {
                return GetPhoneSubfoldersAsync(path, includeHiddenAndSystem, cancellationToken);
            }

            if (PathClassifier.IsNetworkPath(path))
            {
                return GetSubdirectoriesOverNetworkAsync(path, includeHiddenAndSystem, cancellationToken);
            }

            return Task.Run(() => EnumerateSubdirectoriesBlocking(path, includeHiddenAndSystem, cancellationToken), cancellationToken);
        }

        /// <summary>A phone folder's subfolders for the tree, from its provider (dedicated thread, phone timeout).</summary>
        private async Task<IReadOnlyList<DirectoryNodeInfo>> GetPhoneSubfoldersAsync(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken)
        {
            Platform.Phones.IPhoneProvider phone = Platform.Phones.Phones.For(path);
            if (phone == null)
            {
                return Array.Empty<DirectoryNodeInfo>();
            }

            DirectoryIndex index;
            try
            {
                index = await BlockingOperationRunner.RunAsync(token => phone.List(path, token), DirectoryIndexService.PhoneListTimeoutMs,
                    cancellationToken, threadName: "ByteExplorer.PhoneTree").ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new OperationCanceledException("The phone didn't answer in time.", ex, CancellationToken.None);
            }

            var result = new List<DirectoryNodeInfo>();
            if (!index.HasError)
            {
                foreach (DirectoryIndexEntry entry in index.Entries)
                {
                    if (entry.IsDirectory && (includeHiddenAndSystem || !entry.IsHidden))
                    {
                        result.Add(new DirectoryNodeInfo(entry.Name, PathUtil.Combine(path, entry.Name), entry.IsHidden, entry.IsSystem));
                    }
                }
            }

            result.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
            return result;
        }

        private async Task<IReadOnlyList<DirectoryNodeInfo>> GetSubdirectoriesOverNetworkAsync(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken)
        {
            try
            {
                return await BlockingOperationRunner.RunAsync(
                    token => EnumerateSubdirectoriesBlocking(path, includeHiddenAndSystem, token),
                    HardBackstopTimeoutMs,
                    cancellationToken,
                    threadName: "ByteExplorer.TreeEnum").ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                // Only reachable if the caller's own (much shorter, in
                // practice) deadline somehow never fired — the caller's
                // catch (OperationCanceledException) handling treats
                // this identically to its normal network-timeout path,
                // so it's translated here rather than surfaced as a
                // distinct exception type the caller doesn't expect.
                _logger.Warning(
                    ex,
                    "Subdirectory listing for {Path} exceeded the {TimeoutMs}ms hard backstop — the underlying request is still running in the background and may complete later",
                    path, HardBackstopTimeoutMs);
                throw new OperationCanceledException("Timed out reading this location.", ex, CancellationToken.None);
            }
        }

        private IReadOnlyList<DirectoryNodeInfo> EnumerateSubdirectoriesBlocking(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken)
        {
            // One FindFirstFileEx pass returns the attributes with each name (Directory.EnumerateDirectories plus a
            // DirectoryInfo per entry cost a second file-system call per folder). Errors raised mid-enumeration are
            // caught too: they used to escape and leave the node stuck on "Loading...".
            var result = new List<DirectoryNodeInfo>();
            try
            {
                foreach (DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(path, cancellationToken))
                {
                    if (!entry.IsDirectory)
                    {
                        continue;
                    }

                    if (!includeHiddenAndSystem && (entry.IsHidden || entry.IsSystem))
                    {
                        continue;
                    }

                    result.Add(new DirectoryNodeInfo(entry.Name, Path.Combine(path, entry.Name), entry.IsHidden, entry.IsSystem));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Debug(ex, "Access denied enumerating {Path}", path);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.Debug(ex, "Directory not found: {Path}", path);
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception)
            {
                _logger.Warning(ex, "I/O error enumerating {Path}", path);
            }

            result.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
            return result;
        }
    }
}
