// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;

namespace ByteExplorer.Infrastructure.Launch
{
    /// <summary>
    /// What ByteExplorer was started to show, from its command line: a folder (the system's file manager hands over
    /// "ByteExplorer.exe --from-shell "C:\Folder""), a file to show selected in its folder, or nothing. Accepts the forms
    /// Explorer, Nautilus and the desktop's MIME handlers pass: a plain or relative path, a <c>file://</c> URI (Linux
    /// desktop files' %U), <c>/select,path</c> and <c>/select path</c>; Explorer's other switches (<c>/e,</c>,
    /// <c>/n</c>, <c>/root,</c>) are ignored, as are shell namespaces (<c>::{CLSID}</c>, <c>shell:</c>) this app can't show.
    /// </summary>
    public sealed class LaunchRequest
    {
        /// <summary>The switch the system's registration adds: this start came from opening a folder or Win+E.</summary>
        public const string FromShellSwitch = "--from-shell";

        /// <summary>Started by the system to open a folder (or by Win+E, then without a folder).</summary>
        public bool FromShell { get; init; }

        /// <summary>The folder to show, as a full path; null for none.</summary>
        public string Folder { get; init; }

        /// <summary>A file (or folder) to select in <see cref="Folder"/>; null for none.</summary>
        public string Select { get; init; }

        public bool HasFolder => !string.IsNullOrEmpty(Folder);

        /// <summary>
        /// Whether a running ByteExplorer should take this request (a new tab there) instead of a second copy of the app
        /// starting: anything the system or a program asked to open. Starting it by hand with no arguments still opens
        /// another copy, as before.
        /// </summary>
        public bool ShouldForward => FromShell || HasFolder;

        public static LaunchRequest Empty { get; } = new LaunchRequest();

        /// <summary>Reads a command line (without the program itself); relative paths are taken from <paramref name="currentDirectory"/>.</summary>
        public static LaunchRequest Parse(IReadOnlyList<string> args, string currentDirectory)
        {
            if (args == null || args.Count == 0)
            {
                return Empty;
            }

            bool fromShell = false;
            string location = null;
            string select = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = (args[i] ?? string.Empty).Trim();
                if (arg.Length == 0)
                {
                    continue;
                }

                if (string.Equals(arg, FromShellSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    fromShell = true;
                }
                else if (arg.StartsWith("/select,", StringComparison.OrdinalIgnoreCase))
                {
                    select ??= Resolve(arg.Substring("/select,".Length), currentDirectory);
                }
                else if (string.Equals(arg, "/select", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                {
                    select ??= Resolve(args[++i].TrimStart(','), currentDirectory);
                }
                else if (arg.StartsWith("--", StringComparison.Ordinal) ||
                         (IsExplorerSwitch(arg) && (OperatingSystem.IsWindows() || !Directory.Exists(arg)))) // "/e" may be a folder on Linux
                {
                    // Another switch: none of them changes what is shown (MaintenanceCommand's switches included: Program
                    // runs those before parsing, and they never reach a window).
                }
                else if (location == null)
                {
                    location = Resolve(arg, currentDirectory);
                }
            }

            string folder = location;
            if (select != null)
            {
                folder = Path.GetDirectoryName(Core.PathUtil.TrimTrailingSeparator(select)) ?? location;
            }
            else if (location != null && File.Exists(location))
            {
                // A file: show it selected in its folder, as "explorer.exe file" does.
                select = location;
                folder = Path.GetDirectoryName(location);
            }

            return new LaunchRequest { FromShell = fromShell, Folder = folder, Select = select };
        }

        /// <summary>Explorer's own switches: "/e,", "/n", "/root,…", "/separate".</summary>
        private static bool IsExplorerSwitch(string arg)
        {
            string lower = arg.ToLowerInvariant();
            return lower is "/e" or "/n" or "/separate" || lower.StartsWith("/e,", StringComparison.Ordinal) ||
                   lower.StartsWith("/n,", StringComparison.Ordinal) || lower.StartsWith("/root,", StringComparison.Ordinal);
        }

        /// <summary>A full local path, or null for what isn't one (a shell namespace, a malformed path, another URI scheme).</summary>
        private static string Resolve(string raw, string currentDirectory)
        {
            string text = (raw ?? string.Empty).Trim().Trim('"').Trim();
            if (text.Length == 0 || text.StartsWith("::", StringComparison.Ordinal) || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || !uri.IsFile)
                {
                    return null;
                }

                text = uri.LocalPath;
            }
            else if (text.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                string full = Path.GetFullPath(text, string.IsNullOrEmpty(currentDirectory) ? Environment.CurrentDirectory : currentDirectory);
                // "C:" alone is a drive's root here (GetFullPath would make it that drive's current directory).
                return OperatingSystem.IsWindows() && text.Length == 2 && text[1] == ':' ? text + "\\" : full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
