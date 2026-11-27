// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core
{
    /// <summary>
    /// Path helpers that behave the same on Windows, Linux and macOS. The WPF version hard-coded '\' as the separator
    /// and case-insensitive comparison everywhere; on Unix '/' is the separator, '\' is a legal file-name character, and
    /// Linux file systems are case-sensitive. Every place that trims, joins or compares paths by hand goes through here.
    /// </summary>
    public static class PathUtil
    {
        /// <summary>The platform's separator: '\' on Windows, '/' elsewhere.</summary>
        public static readonly char Separator = Path.DirectorySeparatorChar;

        /// <summary>
        /// How two paths compare: case-insensitive on Windows and macOS (NTFS, APFS and HFS+ are by default),
        /// case-sensitive on Linux. Used for "same folder" checks, ancestor checks and path-keyed dictionaries.
        /// </summary>
        public static readonly StringComparison Comparison =
            OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>The <see cref="StringComparer"/> matching <see cref="Comparison"/>.</summary>
        public static readonly StringComparer Comparer =
            Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        /// <summary>True for a path separator on this OS ('/' on every OS, '\' on Windows only).</summary>
        public static bool IsSeparator(char c) => c == '/' || (c == '\\' && OperatingSystem.IsWindows());

        /// <summary>
        /// Removes trailing separators but never reduces a root to nothing: "C:\Temp\" becomes "C:\Temp", "C:\" stays
        /// "C:\" (on Windows "C:" alone means "the current folder on drive C", not the root), "/home/u/" becomes
        /// "/home/u", and "/" stays "/".
        /// </summary>
        public static string TrimTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                return Platform.Phones.PhonePaths.Normalize(path);
            }

            int end = path.Length;
            while (end > 0 && IsSeparator(path[end - 1]))
            {
                end--;
            }

            if (end == path.Length)
            {
                return path;
            }

            string trimmed = path.Substring(0, end);
            if (trimmed.Length == 0)
            {
                return path.Substring(0, 1); // "/" (or "\\" on Windows): the root itself
            }

            // "C:" -> "C:\"
            if (OperatingSystem.IsWindows() && trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed + "\\";
            }

            return trimmed;
        }

        /// <summary>The path with exactly one trailing separator, for prefix ("is inside") checks.</summary>
        public static string WithTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string trimmed = TrimTrailingSeparator(path);
            if (Platform.Phones.PhonePaths.IsPhonePath(trimmed))
            {
                return trimmed + "/"; // phone paths use '/' on every OS
            }

            return IsSeparator(trimmed[trimmed.Length - 1]) ? trimmed : trimmed + Separator;
        }

        /// <summary>The last segment of a path that may end with a separator ("C:\Temp\" gives "Temp").</summary>
        public static string GetName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                return Platform.Phones.PhonePaths.GetName(path);
            }

            string trimmed = TrimTrailingSeparator(path);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        /// <summary>The full path with no trailing separator, or the input unchanged if it cannot be resolved.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                return Platform.Phones.PhonePaths.Normalize(path); // never Path.GetFullPath: Windows would read "phone:" as a stream name
            }

            try
            {
                return TrimTrailingSeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return TrimTrailingSeparator(path);
            }
        }

        /// <summary>True if both paths name the same location (after normalization, compared per OS).</summary>
        public static bool AreSame(string a, string b) =>
            a != null && b != null &&
            (Platform.Phones.PhonePaths.IsPhonePath(a) || Platform.Phones.PhonePaths.IsPhonePath(b)
                ? Platform.Phones.PhonePaths.AreSame(a, b)
                : string.Equals(Normalize(a), Normalize(b), Comparison));

        /// <summary>True if <paramref name="path"/> is strictly inside <paramref name="ancestor"/> (not equal to it).</summary>
        public static bool IsInside(string path, string ancestor)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ancestor))
            {
                return false;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path) || Platform.Phones.PhonePaths.IsPhonePath(ancestor))
            {
                return Platform.Phones.PhonePaths.IsInside(path, ancestor);
            }

            string normalizedPath = Normalize(path);
            string prefix = WithTrailingSeparator(Normalize(ancestor));
            return normalizedPath.Length > prefix.Length && normalizedPath.StartsWith(prefix, Comparison);
        }

        /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> or inside it.</summary>
        public static bool IsSameOrInside(string path, string ancestor) => AreSame(path, ancestor) || IsInside(path, ancestor);

        /// <summary>
        /// Splits a path into its root ("C:\", "\\server\share\", "/") and the remaining segments, for breadcrumbs.
        /// </summary>
        public static (string Root, string[] Segments) Split(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return (string.Empty, Array.Empty<string>());
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                string[] phoneSegments = Platform.Phones.PhonePaths.Segments(path);
                return phoneSegments.Length == 0
                    ? (string.Empty, Array.Empty<string>())
                    : (Platform.Phones.PhonePaths.Prefix + phoneSegments[0] + "/", phoneSegments[1..]);
            }

            string root = Path.GetPathRoot(path) ?? string.Empty;
            string remainder = path.Substring(root.Length);
            char[] separators = OperatingSystem.IsWindows() ? new[] { '\\', '/' } : new[] { '/' };
            return (root, remainder.Split(separators, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The parent folder, or null at a root.</summary>
        /// <summary>Path.Combine that keeps phone paths in their own form ('/' separators on every OS).</summary>
        public static string Combine(string folder, string name) =>
            Platform.Phones.PhonePaths.IsPhonePath(folder) ? Platform.Phones.PhonePaths.Combine(folder, name) : Path.Combine(folder, name);

        public static string GetParent(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                return Platform.Phones.PhonePaths.GetParent(path);
            }

            string parent = Path.GetDirectoryName(TrimTrailingSeparator(path));
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
    }
}
