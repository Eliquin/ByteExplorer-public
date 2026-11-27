// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// "Create shortcuts here" (a right-button drag): on Windows a shell shortcut, "report.pdf - Shortcut.lnk", as Explorer
    /// names it; on Linux and macOS a symbolic link, "Link to report.pdf", as GNOME Files names it. A name already taken
    /// gets " (2)", " (3)"…
    /// </summary>
    public static class Shortcuts
    {
        /// <summary>The verb for menus: "shortcut" on Windows, "link" elsewhere.</summary>
        public static string Noun => OperatingSystem.IsWindows() ? "shortcut" : "link";

        /// <summary>Whether a shortcut to <paramref name="source"/> can be made in <paramref name="folder"/> (not on a phone, not inside an archive).</summary>
        public static bool CanCreate(string source, string folder) =>
            !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(folder) &&
            !Phones.Phones.IsPhonePath(source) && !Phones.Phones.IsPhonePath(folder) &&
            !Services.ArchiveBrowser.IsInsideArchive(source) && !Services.ArchiveBrowser.IsInsideArchive(folder);

        /// <summary>The path the shortcut to <paramref name="source"/> gets in <paramref name="folder"/>: a name not taken there.</summary>
        public static string PathFor(string source, string folder)
        {
            string name = PathUtil.GetName(PathUtil.TrimTrailingSeparator(source));
            if (string.IsNullOrEmpty(name))
            {
                name = PathUtil.TrimTrailingSeparator(source).TrimEnd(':', '\\', '/'); // a drive root: "C"
            }

            for (int n = 1; ; n++)
            {
                string suffix = n == 1 ? string.Empty : $" ({n})";
                string candidate = OperatingSystem.IsWindows()
                    ? Path.Combine(folder, $"{name} - Shortcut{suffix}.lnk")
                    : Path.Combine(folder, $"Link to {name}{suffix}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate) && !IsDanglingLink(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool IsDanglingLink(string path)
        {
            try
            {
                return new FileInfo(path).LinkTarget != null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Makes a shortcut to <paramref name="source"/> in <paramref name="folder"/> and returns its path. Blocks: call off the UI thread.</summary>
        public static string Create(string source, string folder)
        {
            if (!CanCreate(source, folder))
            {
                throw new IOException("A shortcut can't be made here.");
            }

            string path = PathFor(source, folder);
            if (OperatingSystem.IsWindows())
            {
                CreateShellLink(source, path);
            }
            else
            {
                File.CreateSymbolicLink(path, source);
            }

            return path;
        }

        [SupportedOSPlatform("windows")]
        private static void CreateShellLink(string target, string path)
        {
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellLink, throwOnError: true));
            try
            {
                var link = (IShellLinkW)instance;
                link.SetPath(target);
                string parent = PathUtil.GetParent(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    link.SetWorkingDirectory(parent);
                }

                ((IPersistFile)instance).Save(path, true);
            }
            catch (COMException ex)
            {
                throw new IOException($"Windows couldn't make the shortcut ({ex.Message}).", ex);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }

        private static readonly Guid ClsidShellLink = new Guid("00021401-0000-0000-C000-000000000046");

        /// <summary>
        /// IShellLinkW, every method in vtable order as shobjidl.h declares it (only SetPath and SetWorkingDirectory are
        /// called). Strings are LPWSTR, stated on each: a COM interface's string defaults to BSTR, a different layout.
        /// </summary>
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath(IntPtr file, int fileLength, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription(IntPtr name, int nameLength);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory(IntPtr directory, int directoryLength);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments(IntPtr arguments, int argumentsLength);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out ushort hotkey);
            void SetHotkey(ushort hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation(IntPtr iconPath, int iconPathLength, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
