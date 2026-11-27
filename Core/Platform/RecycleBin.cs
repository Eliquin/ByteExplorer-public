// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Finds items the shell just put in the Windows Recycle Bin, so they can be restored (undo). SHFileOperation does
    /// not say where an item went; the Recycle Bin of each drive is the folder X:\$Recycle.Bin\&lt;user SID&gt;, where
    /// every recycled item is a pair: $Rxxxxxx (the item itself, renamed) and $Ixxxxxx (its record: original path,
    /// size and deletion time). Restoring is renaming $R back and removing $I, which is what Explorer's Restore does.
    /// <para>
    /// $I format: Windows Vista to 8.1 write version 1 (8-byte version, 8-byte size, 8-byte FILETIME, then the path
    /// in a fixed 520-byte UTF-16 field); Windows 10 and later write version 2 (the same header, then a 4-byte length
    /// in characters, including the terminating null, and the path). Unknown versions are skipped.
    /// </para>
    /// </summary>
    public static partial class RecycleBin
    {
        /// <summary>Clock slack between the deletion and the $I file's timestamps (FAT-formatted drives round to 2 s).</summary>
        private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The recycled entries for the given original paths that were deleted at or after <paramref name="notBeforeUtc"/>
        /// (the newest per path). Only $I files written since then are read, so a large Recycle Bin costs one listing per
        /// drive. Paths not found are missing from the result (the drive has no Recycle Bin, or the shell deleted the item
        /// permanently because it was too large).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static Dictionary<string, TrashedItem> FindRecycled(IReadOnlyCollection<string> originalPaths, DateTime notBeforeUtc)
        {
            var found = new Dictionary<string, TrashedItem>(StringComparer.OrdinalIgnoreCase);
            if (originalPaths == null || originalPaths.Count == 0)
            {
                return found;
            }

            var wanted = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
            var newest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string sid;
            try
            {
                sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not read the current user's SID; recycled items cannot be located");
                return found;
            }

            if (string.IsNullOrEmpty(sid))
            {
                return found;
            }

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in wanted)
            {
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    roots.Add(root);
                }
            }

            DateTime threshold = notBeforeUtc - Slack;
            foreach (string root in roots)
            {
                string folder = Path.Combine(root, "$Recycle.Bin", sid);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    foreach (FileInfo info in new DirectoryInfo(folder).EnumerateFiles("$I*"))
                    {
                        if (info.LastWriteTimeUtc < threshold)
                        {
                            continue;
                        }

                        if (!TryParseInfoFile(File.ReadAllBytes(info.FullName), out string originalPath, out DateTime deletedUtc) ||
                            deletedUtc < threshold || !wanted.Contains(originalPath))
                        {
                            continue;
                        }

                        if (newest.TryGetValue(originalPath, out DateTime previous) && previous >= deletedUtc)
                        {
                            continue;
                        }

                        string content = Path.Combine(folder, "$R" + info.Name.Substring(2));
                        newest[originalPath] = deletedUtc;
                        found[originalPath] = new TrashedItem(originalPath, content, info.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Warning(ex, "Could not read the Recycle Bin of {Root}", root);
                }
            }

            return found;
        }

        /// <summary>Parses a $I record (version 1 or 2). Public for tests; the format is documented on the class.</summary>
        public static bool TryParseInfoFile(byte[] data, out string originalPath, out DateTime deletedUtc)
        {
            originalPath = null;
            deletedUtc = default;
            if (data == null || data.Length < 24)
            {
                return false;
            }

            long version = BitConverter.ToInt64(data, 0);
            long fileTime = BitConverter.ToInt64(data, 16);
            try
            {
                deletedUtc = DateTime.FromFileTimeUtc(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            int offset;
            int maxChars;
            if (version == 1)
            {
                offset = 24;
                maxChars = Math.Min(260, (data.Length - offset) / 2);
            }
            else if (version == 2)
            {
                if (data.Length < 28)
                {
                    return false;
                }

                int length = BitConverter.ToInt32(data, 24);
                offset = 28;
                if (length <= 0 || offset + (long)length * 2 > data.Length)
                {
                    return false;
                }

                maxChars = length;
            }
            else
            {
                return false;
            }

            string text = Encoding.Unicode.GetString(data, offset, maxChars * 2);
            int nul = text.IndexOf('\0');
            originalPath = nul >= 0 ? text.Substring(0, nul) : text;
            return originalPath.Length > 0;
        }
    }
}
