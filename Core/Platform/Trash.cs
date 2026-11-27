// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Moves files and folders to the desktop's trash on Linux and macOS, the counterpart of the Windows Recycle Bin
    /// (SHFileOperation with FOF_ALLOWUNDO, which stays the Windows path).
    /// <para>
    /// Linux follows the freedesktop.org Trash specification 1.0 (what GNOME Files, Dolphin, Thunar and Nemo read): an
    /// item on the home folder's volume goes to $XDG_DATA_HOME/Trash (~/.local/share/Trash); an item on another volume
    /// goes to that volume's $topdir/.Trash/$uid if an admin created a sticky, non-symlink $topdir/.Trash, else to
    /// $topdir/.Trash-$uid. Each item is renamed into files/ under a unique name after its .trashinfo (original path,
    /// percent-encoded, and deletion date) is created atomically in info/, so the desktop's "Restore" works. Trashing is
    /// always a rename on the same volume, never a copy: an item that cannot be renamed into a trash is reported as a
    /// failure, and the caller asks before deleting it permanently.
    /// </para>
    /// <para>
    /// macOS: renamed into ~/.Trash on the boot volume, or $volume/.Trashes/$uid on other volumes, the folders Finder's
    /// Trash shows. (Finder's "Put Back" needs metadata only Finder itself writes; the item is still listed and
    /// recoverable by dragging it out.)
    /// </para>
    /// </summary>
    public static partial class Trash
    {
        /// <summary>Moves every path to the trash; false if any of them could not be moved (the rest are still moved).</summary>
        public static bool TryMoveToTrash(IReadOnlyList<string> paths)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows uses the Recycle Bin (SHFileOperation).");
            }

            bool all = true;
            foreach (string path in paths)
            {
                try
                {
                    MoveToTrash(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Debug(ex, "Could not move {Path} to the trash", path);
                    all = false;
                }
            }

            return all;
        }

        /// <summary>
        /// Moves one path to the trash and says where it went, or throws an <see cref="IOException"/> saying why it could
        /// not.
        /// </summary>
        public static TrashedItem MoveToTrash(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows uses the Recycle Bin (SHFileOperation).");
            }

            string fullPath = PathUtil.Normalize(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath) && !Native.UnixFileOperations.IsSymbolicLink(fullPath))
            {
                throw new FileNotFoundException("The item does not exist.", fullPath);
            }

            return OperatingSystem.IsMacOS() ? MoveToMacTrash(fullPath) : MoveToFreedesktopTrash(fullPath);
        }

        /// <summary>
        /// Puts a trashed item back where it was, or, when something new took its place, next to it under a free name
        /// ("name (2)"). Returns the path it was restored to. Throws an <see cref="IOException"/> when the item is no
        /// longer in the trash (emptied, or restored by another program).
        /// </summary>
        public static string Restore(TrashedItem item)
        {
            if (item == null)
            {
                throw new System.ArgumentNullException(nameof(item));
            }

            bool isDirectory = Directory.Exists(item.TrashPath);
            if (!isDirectory && !File.Exists(item.TrashPath) &&
                (OperatingSystem.IsWindows() || !Native.UnixFileOperations.IsSymbolicLink(item.TrashPath)))
            {
                throw new IOException($"\"{PathUtil.GetName(item.OriginalPath)}\" is no longer in the trash.");
            }

            string parent = Path.GetDirectoryName(item.OriginalPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            string target = FreeName(item.OriginalPath);
            Native.NativeFileOperations.MoveFile(item.TrashPath, target, writeThrough: false);

            if (!string.IsNullOrEmpty(item.InfoPath))
            {
                TryDelete(item.InfoPath);
            }

            return target;
        }

        /// <summary>The path itself if nothing is there, else "name (2).ext", "name (3).ext", …</summary>
        internal static string FreeName(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int counter = 2; counter < 10000; counter++)
            {
                string candidate = Path.Combine(folder, $"{name} ({counter}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(folder, $"{name} ({System.Guid.NewGuid():N}){extension}");
        }

        [UnsupportedOSPlatform("windows")]
        private static TrashedItem MoveToFreedesktopTrash(string fullPath)
        {
            string homeTrash = HomeTrashDirectory();
            string itemMount = VolumeOf(fullPath);
            string homeMount = VolumeOf(Path.GetDirectoryName(homeTrash));

            var candidates = new List<(string TrashDir, bool Relative)>();
            if (PathUtil.AreSame(itemMount, homeMount))
            {
                candidates.Add((homeTrash, false));
            }
            else
            {
                uint uid = GetUid();
                string adminTrash = Path.Combine(itemMount, ".Trash");
                if (IsValidAdminTrash(adminTrash))
                {
                    candidates.Add((Path.Combine(adminTrash, uid.ToString(CultureInfo.InvariantCulture)), true));
                }

                candidates.Add((Path.Combine(itemMount, ".Trash-" + uid.ToString(CultureInfo.InvariantCulture)), true));
            }

            IOException last = null;
            foreach ((string trashDir, bool relative) in candidates)
            {
                try
                {
                    // Relative paths in a volume's trash, so its entries stay valid if the volume mounts elsewhere.
                    string recordedPath = relative ? Path.GetRelativePath(itemMount, fullPath) : fullPath;
                    return MoveIntoFreedesktopTrash(fullPath, trashDir, recordedPath);
                }
                catch (IOException ex)
                {
                    last = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    last = new IOException(ex.Message, ex);
                }
            }

            throw last ?? new IOException($"No trash folder is available for {fullPath}.");
        }

        [UnsupportedOSPlatform("windows")]
        private static TrashedItem MoveIntoFreedesktopTrash(string fullPath, string trashDir, string recordedPath)
        {
            string filesDir = Path.Combine(trashDir, "files");
            string infoDir = Path.Combine(trashDir, "info");
            CreatePrivateDirectory(trashDir);
            CreatePrivateDirectory(filesDir);
            CreatePrivateDirectory(infoDir);

            string baseName = PathUtil.GetName(fullPath);
            string infoText = "[Trash Info]\nPath=" + EncodePath(recordedPath) + "\nDeletionDate=" +
                              DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

            for (int attempt = 1; attempt < 10000; attempt++)
            {
                string name = attempt == 1 ? baseName : AddCounter(baseName, attempt);
                string infoPath = Path.Combine(infoDir, name + ".trashinfo");
                FileStream info;
                try
                {
                    // CreateNew is O_EXCL: reserving the name this way is the spec's way to avoid two programs picking it.
                    info = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(infoPath))
                {
                    continue;
                }

                using (info)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(infoText);
                    info.Write(bytes, 0, bytes.Length);
                }

                string target = Path.Combine(filesDir, name);
                try
                {
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        File.Delete(infoPath);
                        continue;
                    }

                    Native.UnixFileOperations.Move(fullPath, target);
                    return new TrashedItem(fullPath, target, infoPath);
                }
                catch
                {
                    TryDelete(infoPath);
                    throw;
                }
            }

            throw new IOException($"Could not find a free name in {trashDir}.");
        }

        [UnsupportedOSPlatform("windows")]
        private static TrashedItem MoveToMacTrash(string fullPath)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string itemVolume = VolumeOf(fullPath);
            string trashDir = PathUtil.AreSame(itemVolume, VolumeOf(home))
                ? Path.Combine(home, ".Trash")
                : Path.Combine(itemVolume, ".Trashes", GetUid().ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(trashDir);

            string baseName = PathUtil.GetName(fullPath);
            for (int attempt = 1; attempt < 10000; attempt++)
            {
                string target = Path.Combine(trashDir, attempt == 1 ? baseName : AddCounter(baseName, attempt));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    continue;
                }

                Native.UnixFileOperations.Move(fullPath, target);
                return new TrashedItem(fullPath, target, null);
            }

            throw new IOException($"Could not find a free name in {trashDir}.");
        }

        private static string VolumeOf(string path) => MountPoints.Of(path);

        /// <summary>The spec's checks for $topdir/.Trash: it exists, is not a symbolic link, and has the sticky bit.</summary>
        [UnsupportedOSPlatform("windows")]
        private static bool IsValidAdminTrash(string adminTrash)
        {
            try
            {
                var info = new DirectoryInfo(adminTrash);
                return info.Exists && info.LinkTarget == null && (info.UnixFileMode & UnixFileMode.StickyBit) != 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        [UnsupportedOSPlatform("windows")]
        private static void CreatePrivateDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        /// <summary>"photo.jpg" → "photo.2.jpg"; "folder" → "folder.2", as GNOME names clashing trash entries.</summary>
        private static string AddCounter(string name, int counter)
        {
            string extension = Path.GetExtension(name);
            string stem = string.IsNullOrEmpty(extension) || extension.Length == name.Length ? name : name.Substring(0, name.Length - extension.Length);
            return string.IsNullOrEmpty(extension) || extension.Length == name.Length
                ? name + "." + counter.ToString(CultureInfo.InvariantCulture)
                : stem + "." + counter.ToString(CultureInfo.InvariantCulture) + extension;
        }

        /// <summary>RFC 2396 escaping of the path, as the spec requires ('/' stays as is).</summary>
        private static string EncodePath(string path)
        {
            var builder = new StringBuilder(path.Length + 16);
            foreach (byte b in Encoding.UTF8.GetBytes(path))
            {
                char c = (char)b;
                bool unreserved = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                                  c == '-' || c == '_' || c == '.' || c == '!' || c == '~' || c == '*' || c == '\'' ||
                                  c == '(' || c == ')' || c == '/';
                if (unreserved)
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Cleanup must not hide the original failure.
            }
        }

        [DllImport("libc", EntryPoint = "getuid")]
        private static extern uint GetUid();
    }
}
