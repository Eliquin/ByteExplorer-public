// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core
{
    /// <summary>
    /// Validates a single file or folder name before a rename or create, with the rules of the OS the app runs on, so
    /// the person gets a clear message instead of a raw I/O exception.
    /// <list type="bullet">
    /// <item>Everywhere: not empty or whitespace, no path separator, not "." or "..", at most 255 UTF-16 code units
    /// (NTFS, ext4 and APFS all cap a name at 255).</item>
    /// <item>Windows: none of \ / : * ? " &lt; &gt; | or control characters, no trailing space or dot (Win32 strips
    /// them silently), not a reserved device name (CON, PRN, AUX, NUL, COM0-9, LPT0-9, with or without an
    /// extension).</item>
    /// <item>macOS: no ':' (Finder shows it as '/', and the Carbon layer treats it as a separator).</item>
    /// <item>Linux: only '/' and NUL are forbidden by the kernel.</item>
    /// </list>
    /// </summary>
    public static class FileNameRules
    {
        private const int MaxNameLength = 255;
        private static readonly char[] WindowsInvalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        private static readonly string[] WindowsReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Null when the name is valid, otherwise a message saying what is wrong.</summary>
        public static string Validate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "The name can't be empty.";
            }

            if (name == "." || name == "..")
            {
                return "\".\" and \"..\" are reserved names.";
            }

            if (name.Length > MaxNameLength)
            {
                return $"The name is too long ({name.Length} characters; the limit is {MaxNameLength}).";
            }

            if (name.IndexOf('\0') >= 0 || name.IndexOf('/') >= 0)
            {
                return "A name can't contain \"/\".";
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (char c in name)
                {
                    if (c < 32 || Array.IndexOf(WindowsInvalid, c) >= 0)
                    {
                        return "A name can't contain any of the following characters:\n\\ / : * ? \" < > |";
                    }
                }

                if (name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                {
                    return "A name can't end with a space or a period.";
                }

                string stem = Path.GetFileNameWithoutExtension(name.TrimEnd());
                foreach (string reserved in WindowsReservedNames)
                {
                    if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"\"{reserved}\" is reserved by Windows and can't be used as a name.";
                    }
                }
            }
            else if (OperatingSystem.IsMacOS() && name.IndexOf(':') >= 0)
            {
                return "A name can't contain \":\".";
            }

            return null;
        }
    }
}
