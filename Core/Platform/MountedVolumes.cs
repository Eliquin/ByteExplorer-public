// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;

namespace ByteExplorer.Core.Platform
{
    /// <summary>What a volume is, for its icon and for the search indexer's worker sizing.</summary>
    public enum VolumeKind
    {
        System,
        Fixed,
        Removable,
        Network,
        Optical
    }

    /// <summary>A mounted volume worth showing to a person.</summary>
    public sealed class MountedVolume
    {
        public MountedVolume(string rootPath, string label, VolumeKind kind, DriveInfo driveInfo)
        {
            RootPath = rootPath;
            Label = label;
            Kind = kind;
            DriveInfo = driveInfo;
        }

        public string RootPath { get; }
        public string Label { get; }
        public VolumeKind Kind { get; }
        public DriveInfo DriveInfo { get; }
    }

    /// <summary>
    /// The volumes a file explorer should list on Linux and macOS. <see cref="DriveInfo.GetDrives"/> there returns every
    /// mount in the system: /proc, /sys, cgroups, tmpfs, snap squashfs images, /System/Volumes/* on macOS — dozens of
    /// entries nobody browses. This keeps the root file system ("/") plus the volumes a desktop shows in its own file
    /// manager: anything mounted under the desktop's removable-media folders (/media, /run/media, /mnt on Linux;
    /// /Volumes on macOS) and network file systems wherever they are mounted. On Windows every drive letter is kept, as
    /// before.
    /// </summary>
    public static class MountedVolumes
    {
        private static readonly HashSet<string> NetworkFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nfs", "nfs4", "cifs", "smb", "smbfs", "smb2", "smb3", "afpfs", "webdav", "fuse.sshfs", "sshfs", "9p", "fuse.rclone"
        };

        private static readonly HashSet<string> OpticalFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "iso9660", "udf", "cd9660"
        };

        /// <summary>Tests only (the README screenshots): mount points to leave out, such as a build machine's own mounts.</summary>
        internal static Func<string, bool> HideForTests { get; set; }

        /// <summary>Every volume to show, the root file system first on Unix.</summary>
        public static IReadOnlyList<MountedVolume> GetDisplayVolumes()
        {
            var result = new List<MountedVolume>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return result;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (DriveInfo drive in drives)
                {
                    result.Add(new MountedVolume(drive.Name, null, KindOf(drive.DriveType), drive));
                }

                return result;
            }

            var seen = new HashSet<string>(PathUtil.Comparer);
            foreach (DriveInfo drive in drives)
            {
                string mountPoint;
                string format;
                try
                {
                    mountPoint = drive.Name;
                    format = drive.DriveFormat ?? string.Empty;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(mountPoint) || !seen.Add(mountPoint))
                {
                    continue;
                }

                if (HideForTests?.Invoke(PathUtil.TrimTrailingSeparator(mountPoint)) == true)
                {
                    continue;
                }

                if (mountPoint == "/")
                {
                    result.Insert(0, new MountedVolume("/", OperatingSystem.IsMacOS() ? "Macintosh HD" : "File System", VolumeKind.System, drive));
                    continue;
                }

                bool isNetwork = drive.DriveType == DriveType.Network || NetworkFormats.Contains(format);
                bool inUserMediaFolder = IsUserMediaMount(mountPoint);
                if (!isNetwork && !inUserMediaFolder)
                {
                    continue;
                }

                if (OperatingSystem.IsMacOS() && IsMacBootVolumeAlias(mountPoint))
                {
                    continue;
                }

                VolumeKind kind = isNetwork ? VolumeKind.Network
                    : OpticalFormats.Contains(format) || drive.DriveType == DriveType.CDRom ? VolumeKind.Optical
                    : VolumeKind.Removable;
                result.Add(new MountedVolume(PathUtil.TrimTrailingSeparator(mountPoint), PathUtil.GetName(mountPoint), kind, drive));
            }

            if (result.Count == 0 || result[0].RootPath != "/")
            {
                result.Insert(0, new MountedVolume("/", OperatingSystem.IsMacOS() ? "Macintosh HD" : "File System", VolumeKind.System, null));
            }

            return result;
        }

        /// <summary>The data volumes (everything but the Unix root file system), for the search indexer.</summary>
        public static IReadOnlyList<MountedVolume> GetDataVolumes()
        {
            var result = new List<MountedVolume>();
            foreach (MountedVolume volume in GetDisplayVolumes())
            {
                if (volume.Kind != VolumeKind.System)
                {
                    result.Add(volume);
                }
            }

            return result;
        }

        /// <summary>True if the path lies on a network volume (Unix: a network file system type).</summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            MountedVolume best = null;
            foreach (MountedVolume volume in GetDisplayVolumes())
            {
                if (PathUtil.IsSameOrInside(path, volume.RootPath) && (best == null || volume.RootPath.Length > best.RootPath.Length))
                {
                    best = volume;
                }
            }

            return best?.Kind == VolumeKind.Network;
        }

        private static bool IsUserMediaMount(string mountPoint)
        {
            if (OperatingSystem.IsMacOS())
            {
                return mountPoint.StartsWith("/Volumes/", StringComparison.Ordinal);
            }

            return mountPoint.StartsWith("/media/", StringComparison.Ordinal)
                || mountPoint.StartsWith("/run/media/", StringComparison.Ordinal)
                || mountPoint.StartsWith("/mnt/", StringComparison.Ordinal);
        }

        /// <summary>/Volumes/Macintosh HD is a link back to "/": listing it would show the boot disk twice.</summary>
        private static bool IsMacBootVolumeAlias(string mountPoint)
        {
            try
            {
                var info = new DirectoryInfo(mountPoint);
                return info.LinkTarget == "/";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static VolumeKind KindOf(DriveType type)
        {
            switch (type)
            {
                case DriveType.Removable: return VolumeKind.Removable;
                case DriveType.Network: return VolumeKind.Network;
                case DriveType.CDRom: return VolumeKind.Optical;
                default: return VolumeKind.Fixed;
            }
        }
    }
}
