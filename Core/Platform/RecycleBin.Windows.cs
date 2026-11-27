// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// The Windows Recycle Bin as the trash view lists it. It is read the way <see cref="FindRecycled"/> already reads it
    /// for undo, from each drive's $Recycle.Bin\&lt;user SID&gt; $I/$R pairs, rather than through the shell namespace
    /// (IShellFolder2 over CSIDL_BITBUCKET): the $I records hold everything the view shows (original path, deletion time,
    /// size), restoring is the rename Explorer's Restore does, and the parser is the one the undo history already relies
    /// on, so there is one way to read the Recycle Bin instead of two. Emptying uses the shell (SHEmptyRecycleBin), so
    /// every drive's bin, including the ones this listing skips, is emptied the way Explorer empties it.
    /// <para>
    /// Not listed: Recycle Bins of network drives and of folders redirected to a server (those are kept by the server,
    /// and the shell shows them through other means), and bins of drives that are not ready.
    /// </para>
    /// </summary>
    public static partial class RecycleBin
    {
        private const uint SHERB_NOCONFIRMATION = 0x1;
        private const uint SHERB_NOPROGRESSUI = 0x2;
        private const uint SHERB_NOSOUND = 0x4;

        /// <summary>Every item in the current user's Recycle Bin on the local drives.</summary>
        [SupportedOSPlatform("windows")]
        public static IReadOnlyList<TrashEntry> ListEntries(CancellationToken token)
        {
            var entries = new List<TrashEntry>();
            string sid = CurrentUserSid();
            if (string.IsNullOrEmpty(sid))
            {
                return entries;
            }

            foreach (string root in LocalDriveRoots())
            {
                token.ThrowIfCancellationRequested();
                string folder = Path.Combine(root, "$Recycle.Bin", sid);
                try
                {
                    if (!Directory.Exists(folder))
                    {
                        continue;
                    }

                    foreach (FileInfo info in new DirectoryInfo(folder).EnumerateFiles("$I*"))
                    {
                        token.ThrowIfCancellationRequested();
                        TrashEntry entry = ReadEntry(info, folder);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Warning(ex, "Could not read the Recycle Bin of {Root}", root);
                }
            }

            return entries;
        }

        /// <summary>
        /// Empties the Recycle Bin of every drive (no shell confirmation, progress or sound: the app asked already). Whatever
        /// the shell left (it fails on an item in use) is deleted item by item, which reports the first failure.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Empty(CancellationToken token)
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr != 0)
            {
                // E_UNEXPECTED when it was already empty; anything else is checked below by listing what is left.
                Log.Logger.Debug("SHEmptyRecycleBin returned 0x{Result:X8}", hr);
            }

            Exception first = null;
            foreach (TrashEntry entry in ListEntries(token))
            {
                try
                {
                    Trash.DeleteItem(entry.TrashPath, token);
                    if (File.Exists(entry.InfoPath))
                    {
                        File.Delete(entry.InfoPath);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    first ??= ex;
                }
            }

            NotifyChanged();
            if (first != null)
            {
                throw new IOException("Some items couldn't be deleted from the Recycle Bin: " + first.Message, first);
            }
        }

        /// <summary>Tells the shell the Recycle Bin changed, so the desktop icon shows it full or empty.</summary>
        [SupportedOSPlatform("windows")]
        public static void NotifyChanged()
        {
            try
            {
                SHUpdateRecycleBinIcon();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                Log.Logger.Debug(ex, "Could not refresh the Recycle Bin icon");
            }
        }

        /// <summary>One $I record and its $R item; null when the record can't be read or its item is gone.</summary>
        [SupportedOSPlatform("windows")]
        private static TrashEntry ReadEntry(FileInfo info, string folder)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(info.FullName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            if (!TryParseInfoFile(data, out string originalPath, out DateTime deletedUtc))
            {
                return null;
            }

            string content = Path.Combine(folder, "$R" + info.Name.Substring(2));
            bool isDirectory = Directory.Exists(content);
            if (!isDirectory && !File.Exists(content))
            {
                return null; // a record whose item was removed by hand
            }

            long size = SizeFromInfoFile(data);
            return new TrashEntry(Path.GetFileName(originalPath), originalPath, content, info.FullName, deletedUtc, size, isDirectory);
        }

        /// <summary>The size a $I record holds (bytes 8–15: the file's size, or a folder's total); -1 if it has none.</summary>
        internal static long SizeFromInfoFile(byte[] data) =>
            data != null && data.Length >= 16 && BitConverter.ToInt64(data, 8) is long size && size >= 0 ? size : -1;

        [SupportedOSPlatform("windows")]
        private static string CurrentUserSid()
        {
            try
            {
                return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not read the current user's SID; the Recycle Bin cannot be listed");
                return null;
            }
        }

        /// <summary>Fixed and removable drives that are ready (removable ones have a Recycle Bin when formatted NTFS).</summary>
        private static IEnumerable<string> LocalDriveRoots()
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Could not list the drives");
                yield break;
            }

            foreach (DriveInfo drive in drives)
            {
                bool ready;
                try
                {
                    ready = (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) && drive.IsReady;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ready = false;
                }

                if (ready)
                {
                    yield return drive.RootDirectory.FullName;
                }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [DllImport("shell32.dll")]
        private static extern void SHUpdateRecycleBinIcon();
    }
}
