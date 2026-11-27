// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using ByteExplorer.Core.Native;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// The trash as the trash view sees it: listing what is in it, putting items back, deleting them permanently and
    /// emptying it, on every system.
    /// <list type="bullet">
    /// <item>Windows: the Recycle Bin, read from the $I records of each drive's $Recycle.Bin\&lt;SID&gt; (see
    /// <see cref="RecycleBin"/>), emptied with SHEmptyRecycleBin.</item>
    /// <item>Linux: the freedesktop.org Trash specification 1.0, the home trash ($XDG_DATA_HOME/Trash) plus the
    /// $topdir/.Trash/$uid and $topdir/.Trash-$uid trashes of the other local disks. Each item is files/NAME with its
    /// record info/NAME.trashinfo (Path= percent-encoded, relative to $topdir in a volume's trash; DeletionDate= local
    /// time). Records without their item (a half-finished trash by another program) are not listed.</item>
    /// <item>macOS: ~/.Trash. Finder keeps the original places in a private store, so they are unknown here and the
    /// items cannot be restored from ByteExplorer (they can be dragged out).</item>
    /// </list>
    /// All of it does file I/O: call it off the UI thread.
    /// </summary>
    public static partial class Trash
    {
        /// <summary>What the system calls its trash.</summary>
        public static string DisplayName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

        /// <summary>
        /// Local disk file systems whose volumes may hold a $topdir trash. Network, FUSE bridges and pseudo file systems
        /// are not probed: a share that stopped answering would hang the listing, and /proc or /sys never hold one.
        /// </summary>
        private static readonly HashSet<string> LocalDiskTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "jfs", "reiserfs", "zfs", "bcachefs", "vfat", "msdos", "exfat",
            "ntfs", "ntfs3", "fuseblk", "hfsplus", "apfs", "udf"
        };

        /// <summary>Everything in the trash (all of the system's trash folders), in no particular order.</summary>
        public static IReadOnlyList<TrashEntry> List(CancellationToken token = default)
        {
            if (OperatingSystem.IsWindows())
            {
                return RecycleBin.ListEntries(token);
            }

            if (OperatingSystem.IsMacOS())
            {
                return ListMac(MacTrashDirectory(), token);
            }

            return ListFreedesktop(TrashDirectoriesOverride ?? FreedesktopTrashDirectories(), token);
        }

        /// <summary>
        /// Puts an item back where it was deleted from; when something has taken its place, next to it under a free name
        /// ("name (2)"). Returns the path it was restored to.
        /// </summary>
        public static string Restore(TrashEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (!entry.CanRestore)
            {
                throw new IOException($"The trash doesn't say where \"{entry.Name}\" came from, so it can't be put back. Drag it out of the trash instead.");
            }

            string target = Restore(entry.ToTrashedItem());
            if (OperatingSystem.IsWindows())
            {
                RecycleBin.NotifyChanged();
            }

            return target;
        }

        /// <summary>Deletes one item from the trash for good: the item (a folder with everything in it) and its record.</summary>
        public static void DeletePermanently(TrashEntry entry, CancellationToken token = default)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            DeleteItem(entry.TrashPath, token);
            if (!string.IsNullOrEmpty(entry.InfoPath) && File.Exists(entry.InfoPath))
            {
                File.Delete(entry.InfoPath);
            }

            if (OperatingSystem.IsWindows())
            {
                RecycleBin.NotifyChanged();
            }
        }

        /// <summary>
        /// Deletes everything in the trash for good. Linux and macOS: every item of every trash folder, listed or not
        /// (orphaned records and items too), and the spec's directorysizes cache. Windows: SHEmptyRecycleBin.
        /// </summary>
        public static void Empty(CancellationToken token = default)
        {
            if (OperatingSystem.IsWindows())
            {
                RecycleBin.Empty(token);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                EmptyFolder(MacTrashDirectory(), token);
                return;
            }

            var trashDirs = new List<string>();
            foreach ((string trashDir, string _) in TrashDirectoriesOverride ?? FreedesktopTrashDirectories())
            {
                trashDirs.Add(trashDir);
            }

            EmptyFreedesktop(trashDirs, token);
        }

        // ------------------------------------------------------------------ freedesktop.org

        /// <summary>
        /// Tests: the freedesktop trash folders <see cref="List"/> and <see cref="Empty"/> use instead of the real ones (the
        /// window tests point the trash view at a fake trash without touching XDG_DATA_HOME, which other tests set too).
        /// </summary>
        internal static IReadOnlyList<(string TrashDir, string TopDir)> TrashDirectoriesOverride { get; set; }

        /// <summary>$XDG_DATA_HOME/Trash (~/.local/share/Trash when the variable is unset or not absolute, as the spec says).</summary>
        internal static string HomeTrashDirectory()
        {
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
            {
                dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            return Path.Combine(dataHome, "Trash");
        }

        /// <summary>
        /// The trash folders that exist: the home trash (TopDir null: its records hold absolute paths), then the trash of
        /// each other local disk with that disk's mount point as TopDir.
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        internal static IReadOnlyList<(string TrashDir, string TopDir)> FreedesktopTrashDirectories()
        {
            var result = new List<(string, string)>();
            string homeTrash = HomeTrashDirectory();
            if (Directory.Exists(homeTrash))
            {
                result.Add((homeTrash, null));
            }

            string homeMount = VolumeOf(Path.GetDirectoryName(homeTrash));
            string uid = GetUid().ToString(CultureInfo.InvariantCulture);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string mount, string type) in MountPoints.CurrentTableLinux())
            {
                if (!LocalDiskTypes.Contains(type) || PathUtil.AreSame(mount, homeMount) || !seen.Add(mount))
                {
                    continue;
                }

                try
                {
                    string adminTrash = Path.Combine(mount, ".Trash");
                    string adminUserTrash = Path.Combine(adminTrash, uid);
                    if (IsValidAdminTrash(adminTrash) && Directory.Exists(adminUserTrash))
                    {
                        result.Add((adminUserTrash, mount));
                    }

                    string userTrash = Path.Combine(mount, ".Trash-" + uid);
                    if (Directory.Exists(userTrash) && new DirectoryInfo(userTrash).LinkTarget == null)
                    {
                        result.Add((userTrash, mount));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Debug(ex, "Could not look for a trash on {Mount}", mount);
                }
            }

            return result;
        }

        /// <summary>The items of the given freedesktop trash folders (testable with a fake trash).</summary>
        internal static List<TrashEntry> ListFreedesktop(IEnumerable<(string TrashDir, string TopDir)> trashDirs, CancellationToken token)
        {
            var entries = new List<TrashEntry>();
            foreach ((string trashDir, string topDir) in trashDirs)
            {
                string infoDir = Path.Combine(trashDir, "info");
                string filesDir = Path.Combine(trashDir, "files");
                if (!Directory.Exists(infoDir))
                {
                    continue;
                }

                IEnumerable<string> infoFiles;
                try
                {
                    infoFiles = Directory.EnumerateFiles(infoDir, "*.trashinfo");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Warning(ex, "Could not read the trash folder {Folder}", trashDir);
                    continue;
                }

                try
                {
                    foreach (string infoPath in infoFiles)
                    {
                        token.ThrowIfCancellationRequested();
                        TrashEntry entry = ReadFreedesktopEntry(infoPath, filesDir, topDir);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Warning(ex, "Could not read the trash folder {Folder}", trashDir);
                }
            }

            return entries;
        }

        private static TrashEntry ReadFreedesktopEntry(string infoPath, string filesDir, string topDir)
        {
            string trashName = Path.GetFileNameWithoutExtension(infoPath);
            string trashPath = Path.Combine(filesDir, trashName);
            FileSystemInfo item = ItemInfo(trashPath);
            if (item == null)
            {
                return null; // a record without its item
            }

            string text;
            try
            {
                text = File.ReadAllText(infoPath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Could not read {Info}", infoPath);
                return null;
            }

            ParseTrashInfo(text, out string recordedPath, out DateTime deletedUtc);
            string originalPath = null;
            if (!string.IsNullOrEmpty(recordedPath))
            {
                if (Path.IsPathRooted(recordedPath))
                {
                    originalPath = recordedPath;
                }
                else if (topDir != null)
                {
                    originalPath = Path.GetFullPath(Path.Combine(topDir, recordedPath));
                }
            }

            string name = originalPath != null ? PathUtil.GetName(originalPath) : trashName;
            bool isDirectory = item is DirectoryInfo;
            long size = item is FileInfo file ? SafeLength(file) : -1;
            return new TrashEntry(name, originalPath, trashPath, infoPath, deletedUtc, size, isDirectory);
        }

        /// <summary>
        /// Reads a .trashinfo record: the [Trash Info] group's Path (percent-decoded) and DeletionDate (local time, as the
        /// spec says, returned in UTC; MinValue when missing or malformed). Keys of other groups are ignored.
        /// </summary>
        internal static void ParseTrashInfo(string text, out string path, out DateTime deletedUtc)
        {
            path = null;
            deletedUtc = DateTime.MinValue;
            bool inGroup = false;
            foreach (string rawLine in (text ?? string.Empty).Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inGroup = string.Equals(line.Trim(), "[Trash Info]", StringComparison.Ordinal);
                    continue;
                }

                if (!inGroup)
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key == "Path" && path == null)
                {
                    path = DecodePath(value);
                }
                else if (key == "DeletionDate" && deletedUtc == DateTime.MinValue &&
                         DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime local))
                {
                    deletedUtc = local.ToUniversalTime();
                }
            }
        }

        /// <summary>Reverses <see cref="EncodePath"/> (any %XX sequence, decoded as UTF-8).</summary>
        private static string DecodePath(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        /// <summary>Deletes everything in the given freedesktop trash folders: files/, info/ and the directorysizes cache.</summary>
        internal static void EmptyFreedesktop(IEnumerable<string> trashDirs, CancellationToken token)
        {
            List<Exception> errors = null;
            foreach (string trashDir in trashDirs)
            {
                // Items before records: interrupted half-way, what is left is still listed with its record.
                foreach (string sub in new[] { "files", "info" })
                {
                    try
                    {
                        EmptyFolder(Path.Combine(trashDir, sub), token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }
                }

                string sizes = Path.Combine(trashDir, "directorysizes");
                if (File.Exists(sizes))
                {
                    TryDelete(sizes);
                }
            }

            if (errors != null)
            {
                throw new IOException("Some items couldn't be deleted from the trash: " + errors[0].Message, errors[0]);
            }
        }

        // ------------------------------------------------------------------ macOS

        private static string MacTrashDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

        /// <summary>Every item in a plain trash folder (macOS): name and size, nothing about where it came from.</summary>
        internal static List<TrashEntry> ListMac(string trashDir, CancellationToken token)
        {
            var entries = new List<TrashEntry>();
            if (!Directory.Exists(trashDir))
            {
                return entries;
            }

            try
            {
                foreach (FileSystemInfo info in new DirectoryInfo(trashDir).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (string.Equals(info.Name, ".DS_Store", StringComparison.Ordinal))
                    {
                        continue; // Finder's own bookkeeping, not something the person deleted
                    }

                    bool isDirectory = info is DirectoryInfo && info.LinkTarget == null;
                    long size = info is FileInfo file ? SafeLength(file) : -1;
                    // Finder records neither the original place nor the deletion time where other programs can read them.
                    entries.Add(new TrashEntry(info.Name, null, info.FullName, null, DateTime.MinValue, size, isDirectory));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Could not read the trash folder {Folder}", trashDir);
            }

            return entries;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The item (a dangling link included), or null when nothing is there.</summary>
        private static FileSystemInfo ItemInfo(string path)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                if (dir.Exists && dir.LinkTarget == null)
                {
                    return dir;
                }

                var file = new FileInfo(path);
                return file.Exists || file.LinkTarget != null || dir.Exists ? file : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        private static long SafeLength(FileInfo file)
        {
            try
            {
                return file.Exists ? file.Length : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return -1;
            }
        }

        /// <summary>Deletes a file, a link (never what it points to) or a whole folder tree; nothing there is fine.</summary>
        internal static void DeleteItem(string path, CancellationToken token)
        {
            if (NativeFileOperations.IsLink(path))
            {
                NativeFileOperations.DeleteLink(path);
            }
            else if (Directory.Exists(path))
            {
                NativeFileOperations.DeleteDirectoryTreePermanently(path, token);
            }
            else if (File.Exists(path))
            {
                NativeFileOperations.DeleteFilePermanently(path);
            }
        }

        /// <summary>Deletes everything inside a folder, keeping the folder.</summary>
        private static void EmptyFolder(string folder, CancellationToken token)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string child in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                DeleteItem(child, token);
            }
        }
    }
}
