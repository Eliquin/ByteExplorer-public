// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace ByteExplorer.Core.Platform.Phones
{
    /// <summary>
    /// Paths of phones reached through Windows Portable Devices (MTP), which have no drive letter or file system path:
    /// "phone://Galaxy S21/Internal shared storage/DCIM/Camera/IMG_0001.jpg" — the phone, its storage, then folders and
    /// the file. They look and behave like paths everywhere in the app (breadcrumbs, Back, Up, the address bar, history),
    /// and <see cref="PathUtil"/> answers name, parent and comparison questions for them; only reading and writing go
    /// through <see cref="IPhoneProvider"/>.
    /// <para>
    /// The form is the same on every OS and touches no disk: Windows' own path rules must never see it ("phone:" would
    /// read as an alternate data stream of a relative file). Separators are '/', and '\' is accepted too, since code that
    /// joins with Path.Combine on Windows adds one; <see cref="Normalize"/> turns them into '/'. Comparisons ignore case,
    /// as Android's shared storage does.
    /// </para>
    /// </summary>
    public static class PhonePaths
    {
        public const string Prefix = "phone://";

        public static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

        /// <summary>Cheap test, no I/O: does the path use the phone form?</summary>
        public static bool IsPhonePath(string path) =>
            path != null && path.Length > Prefix.Length && path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>"phone://Device/a/b" with '/' only, no doubled or trailing separators; a non-phone path comes back unchanged.</summary>
        public static string Normalize(string path)
        {
            if (!IsPhonePath(path))
            {
                return path;
            }

            string[] segments = Segments(path);
            return segments.Length == 0 ? path : Prefix + string.Join("/", segments);
        }

        /// <summary>The device name and the segments below it ("Internal shared storage", "DCIM", …).</summary>
        public static string[] Segments(string path)
        {
            if (!IsPhonePath(path))
            {
                return Array.Empty<string>();
            }

            return path.Substring(Prefix.Length).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The device root ("phone://Galaxy S21") of a phone path.</summary>
        public static string DeviceRoot(string path)
        {
            string[] segments = Segments(path);
            return segments.Length == 0 ? null : Prefix + segments[0];
        }

        public static string DeviceName(string path)
        {
            string[] segments = Segments(path);
            return segments.Length == 0 ? null : segments[0];
        }

        public static string GetName(string path)
        {
            string[] segments = Segments(path);
            return segments.Length == 0 ? path : segments[segments.Length - 1];
        }

        /// <summary>The containing folder; null for a device root (as for a drive root).</summary>
        public static string GetParent(string path)
        {
            string[] segments = Segments(path);
            if (segments.Length <= 1)
            {
                return null;
            }

            return Prefix + string.Join("/", segments, 0, segments.Length - 1);
        }

        public static string Combine(string folder, string name)
        {
            string normalized = Normalize(folder);
            return string.IsNullOrEmpty(name) ? normalized : normalized + "/" + name.Trim('/', '\\');
        }

        public static string Build(string deviceName, params string[] segments)
        {
            var builder = new StringBuilder(Prefix).Append(deviceName);
            foreach (string segment in segments)
            {
                builder.Append('/').Append(segment);
            }

            return builder.ToString();
        }

        public static bool AreSame(string a, string b) =>
            IsPhonePath(a) && IsPhonePath(b) && string.Equals(Normalize(a), Normalize(b), Comparison);

        public static bool IsInside(string path, string ancestor)
        {
            if (!IsPhonePath(path) || !IsPhonePath(ancestor))
            {
                return false;
            }

            string normalizedPath = Normalize(path);
            string prefix = Normalize(ancestor) + "/";
            return normalizedPath.Length > prefix.Length && normalizedPath.StartsWith(prefix, Comparison);
        }

        /// <summary>
        /// A name that a phone accepts for a new file or folder made from <paramref name="name"/>: MTP names can't hold
        /// '/' (it would read as a folder), and Android's shared storage refuses the characters FAT refuses.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(c < 32 || "/\\:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
            }

            return builder.ToString();
        }

        /// <summary>A device name usable as a path segment, unique among <paramref name="taken"/> ("Galaxy S21 (2)").</summary>
        public static string UniqueDeviceName(string friendlyName, ISet<string> taken)
        {
            string baseName = SanitizeName(string.IsNullOrWhiteSpace(friendlyName) ? "Phone" : friendlyName.Trim());
            string name = baseName;
            for (int n = 2; taken.Contains(name); n++)
            {
                name = $"{baseName} ({n})";
            }

            taken.Add(name);
            return name;
        }
    }
}
