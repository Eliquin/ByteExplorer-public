// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// \\?\ opts out of the legacy 260-character MAX_PATH limit for
    /// local paths — but UNC paths need a different, specific form:
    /// \\?\UNC\server\share\... , not just \\?\ glued onto a \\server\
    /// path (which is not a valid extended path and Windows will
    /// reject or mishandle). Centralized here because getting this
    /// wrong silently breaks long-path support specifically for
    /// network shares, exactly the environment where MAX_PATH
    /// limitations bite hardest and are easiest to overlook.
    /// </summary>
    internal static class ExtendedPath
    {
        private const string LocalPrefix = "\\\\?\\";
        private const string UncPrefix = "\\\\?\\UNC\\";

        internal static string Prefix(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // \\?\ turns off Windows' own path parsing, so "/" is no longer a separator there: a path with forward
            // slashes (valid everywhere else in Windows) would name a file that can't exist. "/" can't be part of a name.
            string normalized = path.Replace('/', '\\');

            if (normalized.StartsWith(LocalPrefix, StringComparison.Ordinal))
            {
                return normalized; // already prefixed, either form
            }

            // A trailing separator is dropped, except on a drive root: "\\?\C:" is the volume device, not its root folder.
            if (!(normalized.Length == 3 && normalized[1] == ':'))
            {
                normalized = normalized.TrimEnd('\\');
            }

            if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
            {
                // \\server\share\... -> \\?\UNC\server\share\...
                // (strip the leading \\ , the UNC prefix supplies its own)
                return UncPrefix + normalized.Substring(2);
            }

            return LocalPrefix + normalized;
        }
    }
}
