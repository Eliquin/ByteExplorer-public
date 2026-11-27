// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Which mount point a Unix path lives on: the longest mount point (from every mount, pseudo file systems included)
    /// that contains the path. Two paths on the same mount point can be renamed into each other; two on different ones
    /// cannot (EXDEV). This is what drive-letter comparison answered on Windows. The mount table is cached for two
    /// seconds: a move of many items asks once per item, and mounts change far less often than that.
    /// </summary>
    public static class MountPoints
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
        private static string[] _cached = Array.Empty<string>();
        private static long _cachedAtTicks;

        public static string Of(string path)
        {
            string full = PathUtil.Normalize(path);
            string[] mounts = GetMounts();
            string best = "/";
            foreach (string mount in mounts)
            {
                if (mount.Length > best.Length && PathUtil.IsSameOrInside(full, mount))
                {
                    best = mount;
                }
            }

            return best;
        }

        private static string[] GetMounts()
        {
            long now = Environment.TickCount64;
            string[] cached = Volatile.Read(ref _cached);
            if (cached.Length > 0 && now - Interlocked.Read(ref _cachedAtTicks) < (long)CacheLifetime.TotalMilliseconds)
            {
                return cached;
            }

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                var mounts = new string[drives.Length];
                for (int i = 0; i < drives.Length; i++)
                {
                    mounts[i] = PathUtil.TrimTrailingSeparator(drives[i].Name);
                }

                Volatile.Write(ref _cached, mounts);
                Interlocked.Exchange(ref _cachedAtTicks, now);
                return mounts;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return cached;
            }
        }

        // ------------------------------------------------------------------ network mounts (Linux)

        /// <summary>
        /// File system types reached over a network (or through a user-space bridge to one). Besides the volume list's
        /// network types, GVfs's FUSE mount: GNOME Files puts browsed SMB/SFTP shares under /run/user/&lt;uid&gt;/gvfs.
        /// </summary>
        private static readonly HashSet<string> NetworkTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nfs", "nfs4", "cifs", "smb", "smbfs", "smb2", "smb3", "afpfs", "webdav", "davfs", "fuse.davfs", "fuse.sshfs",
            "sshfs", "9p", "fuse.rclone", "fuse.gvfsd-fuse", "ceph", "glusterfs", "fuse.glusterfs", "afs"
        };

        private static (string MountPoint, string Type)[] _types = Array.Empty<(string, string)>();
        private static long _typesAtTicks;

        /// <summary>
        /// Whether a Linux path lives on a network file system (NFS, SMB/CIFS, SSHFS, GVfs, …). Read from /proc/self/mounts,
        /// which the kernel writes from memory: unlike statfs (what DriveInfo.DriveType calls), reading it never blocks
        /// on a share that stopped answering — the very case the answer is needed for. False on other systems and when
        /// the table can't be read.
        /// </summary>
        public static bool IsOnNetworkFileSystemLinux(string path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path) || path[0] != '/')
            {
                return false;
            }

            return IsOnNetworkFileSystem(path, GetTypes());
        }

        /// <summary>Linux: the file system type of the mount a path lives on ("ext4", "fuse.jmtpfs"), or null.</summary>
        public static string FileSystemTypeLinux(string path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path) || path[0] != '/')
            {
                return null;
            }

            return FileSystemType(path, GetTypes()).Type;
        }

        internal static (string MountPoint, string Type) FileSystemType(string path, (string MountPoint, string Type)[] table)
        {
            string full = PathUtil.Normalize(path);
            string bestMount = null;
            string bestType = null;
            foreach ((string mount, string type) in table)
            {
                if ((bestMount == null || mount.Length > bestMount.Length) && PathUtil.IsSameOrInside(full, mount))
                {
                    bestMount = mount;
                    bestType = type;
                }
            }

            return (bestMount, bestType);
        }

        /// <summary>The kernel's mount table (Linux; cached for two seconds), empty elsewhere.</summary>
        internal static (string MountPoint, string Type)[] CurrentTableLinux() =>
            OperatingSystem.IsLinux() ? GetTypes() : Array.Empty<(string, string)>();

        /// <summary>The mount points of the given types (Linux), from the kernel's table.</summary>
        public static IReadOnlyList<string> MountPointsOfTypesLinux(ISet<string> types)
        {
            var result = new List<string>();
            if (!OperatingSystem.IsLinux())
            {
                return result;
            }

            foreach ((string mount, string type) in GetTypes())
            {
                if (types.Contains(type))
                {
                    result.Add(mount);
                }
            }

            return result;
        }

        /// <summary>The longest mount point in <paramref name="table"/> containing the path decides (testable without a share).</summary>
        internal static bool IsOnNetworkFileSystem(string path, (string MountPoint, string Type)[] table)
        {
            string full = PathUtil.Normalize(path);
            string bestMount = null;
            string bestType = null;
            foreach ((string mount, string type) in table)
            {
                if ((bestMount == null || mount.Length > bestMount.Length) && PathUtil.IsSameOrInside(full, mount))
                {
                    bestMount = mount;
                    bestType = type;
                }
            }

            return bestType != null && NetworkTypes.Contains(bestType);
        }

        private static (string MountPoint, string Type)[] GetTypes()
        {
            long now = Environment.TickCount64;
            (string, string)[] cached = Volatile.Read(ref _types);
            if (cached.Length > 0 && now - Interlocked.Read(ref _typesAtTicks) < (long)CacheLifetime.TotalMilliseconds)
            {
                return cached;
            }

            try
            {
                (string, string)[] parsed = ParseMountTable(File.ReadAllText("/proc/self/mounts"));
                Volatile.Write(ref _types, parsed);
                Interlocked.Exchange(ref _typesAtTicks, now);
                return parsed;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return cached;
            }
        }

        /// <summary>
        /// Parses a mounts table (fstab format: device, mount point, type, options, …). The kernel writes a space, tab,
        /// newline or backslash in a mount point as an octal escape ("\040" for a space).
        /// </summary>
        internal static (string MountPoint, string Type)[] ParseMountTable(string table)
        {
            var result = new List<(string, string)>();
            foreach (string line in table.Split('\n'))
            {
                string[] fields = line.Split(' ');
                if (fields.Length < 3 || fields[1].Length == 0 || fields[1][0] != '/')
                {
                    continue;
                }

                result.Add((PathUtil.TrimTrailingSeparator(Unescape(fields[1])), fields[2]));
            }

            return result.ToArray();
        }

        private static string Unescape(string field)
        {
            if (field.IndexOf('\\') < 0)
            {
                return field;
            }

            var builder = new StringBuilder(field.Length);
            for (int i = 0; i < field.Length; i++)
            {
                if (field[i] == '\\' && i + 3 < field.Length &&
                    IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
                {
                    builder.Append((char)((field[i + 1] - '0') * 64 + (field[i + 2] - '0') * 8 + (field[i + 3] - '0')));
                    i += 3;
                }
                else
                {
                    builder.Append(field[i]);
                }
            }

            return builder.ToString();
        }

        private static bool IsOctal(char c) => c >= '0' && c <= '7';
    }
}
