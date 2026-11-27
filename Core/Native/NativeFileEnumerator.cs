// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// Streams directory entries with name, size, attributes and timestamps from one directory read, on every OS.
    /// <para>
    /// Built on <see cref="FileSystemEnumerable{TResult}"/>, which reads the entries' metadata from the same kernel call
    /// that lists them (NtQueryDirectoryFile on Windows, readdir + one lstat per entry on Unix) and hands it over as a
    /// <see cref="FileSystemEntry"/> struct — no FileInfo per entry, no second stat. This replaced the Windows-only
    /// FindFirstFileEx enumerator when the app became cross-platform; on Windows the managed enumerator is as fast (it
    /// uses a large buffer, like FIND_FIRST_EX_LARGE_FETCH, and needs no \\?\ prefix for long paths since .NET Core).
    /// </para>
    /// <para>
    /// Errors keep the old contract: a missing folder throws <see cref="DirectoryNotFoundException"/>, a denied one
    /// <see cref="UnauthorizedAccessException"/>, anything else an <see cref="IOException"/>; an empty folder yields
    /// nothing. Entries that cannot be read individually (a broken symlink's target, a file deleted mid-listing) are
    /// skipped rather than aborting the whole listing (<see cref="EnumerationOptions.IgnoreInaccessible"/> is off, so a
    /// denied root still throws).
    /// </para>
    /// <para>
    /// Hidden entries: on Unix .NET reports a leading-dot name as <see cref="FileAttributes.Hidden"/>, so the existing
    /// "show hidden files" setting filters dotfiles there with no extra code.
    /// </para>
    /// </summary>
    internal static class NativeFileEnumerator
    {
        private static readonly EnumerationOptions Options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            // Include hidden and system entries; callers filter by the settings.
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
            BufferSize = 64 * 1024
        };

        public static IEnumerable<DirectoryIndexEntry> Enumerate(string directoryPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new DirectoryNotFoundException(directoryPath);
            }

            var enumerable = new FileSystemEnumerable<DirectoryIndexEntry>(directoryPath, ToEntry, Options);

            // The enumerator itself is created lazily by the foreach below, so the first MoveNext (where a missing or
            // denied folder throws) happens on the caller's thread, exactly where the old FindFirstFileEx call did.
            foreach (DirectoryIndexEntry entry in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }

        private static DirectoryIndexEntry ToEntry(ref FileSystemEntry entry)
        {
            bool isDirectory = entry.IsDirectory;
            FileAttributes attributes = entry.Attributes;
            long size = 0;
            DateTime lastWriteUtc = DateTime.MinValue;
            DateTime createdUtc = default;
            DateTime accessedUtc = default;

            try
            {
                if (!isDirectory)
                {
                    size = entry.Length;
                }

                lastWriteUtc = entry.LastWriteTimeUtc.UtcDateTime;
                // The same directory read (Windows) or stat (Linux) as the date above: no extra disk access.
                createdUtc = entry.CreationTimeUtc.UtcDateTime;
                accessedUtc = entry.LastAccessTimeUtc.UtcDateTime;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException)
            {
                // A dangling symlink or an entry removed between readdir and its stat: list it with no size/date
                // rather than failing the whole folder.
            }

            return new DirectoryIndexEntry(entry.FileName.ToString(), isDirectory, size, lastWriteUtc, attributes, createdUtc, accessedUtc);
        }
    }
}
