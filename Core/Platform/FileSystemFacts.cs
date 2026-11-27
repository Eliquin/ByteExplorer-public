// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ByteExplorer.Core.Platform
{
    /// <summary>What <see cref="FileSystemFacts.TryGetUnixStat"/> read about one path (Linux statx).</summary>
    public readonly struct UnixStat
    {
        public UnixStat(uint uid, uint gid, long allocatedBytes, uint hardLinks, ulong inode)
        {
            Uid = uid;
            Gid = gid;
            AllocatedBytes = allocatedBytes;
            HardLinks = hardLinks;
            Inode = inode;
        }

        public uint Uid { get; }
        public uint Gid { get; }

        /// <summary>The blocks the file system allocated for the data (st_blocks × 512): its real size on disk.</summary>
        public long AllocatedBytes { get; }

        public uint HardLinks { get; }
        public ulong Inode { get; }
    }

    /// <summary>One access rule of a Windows ACL, ready to show.</summary>
    public sealed class AccessRuleInfo
    {
        public AccessRuleInfo(string identity, bool allow, string rights, bool inherited)
        {
            Identity = identity;
            Allow = allow;
            Rights = rights;
            Inherited = inherited;
        }

        public string Identity { get; }
        public bool Allow { get; }
        public string Rights { get; }
        public bool Inherited { get; }
    }

    /// <summary>The owner and the access rules of a Windows file or folder.</summary>
    public sealed class WindowsSecurityInfo
    {
        public WindowsSecurityInfo(string owner, IReadOnlyList<AccessRuleInfo> rules)
        {
            Owner = owner;
            Rules = rules;
        }

        public string Owner { get; }
        public IReadOnlyList<AccessRuleInfo> Rules { get; }
    }

    /// <summary>
    /// The file system facts the Properties window shows that .NET does not expose directly, each read the cheapest way
    /// the OS offers and each optional: a method returns null (or false) when the fact is not available here, and never
    /// throws for a missing file, a broken link or a denied access.
    /// <list type="bullet">
    /// <item>Size on disk. Windows: the compressed/sparse-aware size (GetCompressedFileSizeW) rounded up to the volume's
    /// cluster (GetDiskFreeSpaceW), as Explorer computes it (except that Explorer shows 0 for tiny files stored inside
    /// the MFT record, which no public API reports). Linux: the allocated blocks from statx. macOS: not available (no
    /// statx; the stat struct's layout differs by architecture), so the row is left out rather than guessed.</item>
    /// <item>Owner and group on Linux: the uid/gid from statx, named through getpwuid/getgrgid (so LDAP/NSS users
    /// resolve too); only the first field of passwd/group (the name pointer) is read, which has the same offset on every
    /// libc and architecture.</item>
    /// <item>Windows: the type name and default application from the shell's associations (AssocQueryStringW), and the
    /// owner and ACL from System.Security.AccessControl (in the .NET runtime, no package).</item>
    /// </list>
    /// </summary>
    public static class FileSystemFacts
    {
        private const int StatxBufferSize = 256;
        private const int AtFdCwd = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint StatxNlink = 0x4, StatxUid = 0x8, StatxGid = 0x10, StatxIno = 0x100, StatxBlocks = 0x400;

        private static readonly object PasswdLock = new object();
        private static readonly Dictionary<uint, string> UserNames = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> GroupNames = new Dictionary<uint, string>();
        private static volatile bool s_statxMissing;

        /// <summary>A buffer for <see cref="TryGetUnixStat"/>, reusable across calls on one thread.</summary>
        public static byte[] CreateStatBuffer() => new byte[StatxBufferSize];

        /// <summary>Linux only: statx of the path itself (links not followed). False elsewhere or when it fails.</summary>
        public static bool TryGetUnixStat(string path, byte[] buffer, out UnixStat stat)
        {
            stat = default;
            if (!OperatingSystem.IsLinux() || s_statxMissing || string.IsNullOrEmpty(path) || buffer == null || buffer.Length < StatxBufferSize)
            {
                return false;
            }

            try
            {
                if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxNlink | StatxUid | StatxGid | StatxIno | StatxBlocks, buffer) != 0)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                s_statxMissing = true; // an old libc: no point asking again
                return false;
            }

            // struct statx has the same layout on every Linux architecture (include/uapi/linux/stat.h).
            uint mask = BitConverter.ToUInt32(buffer, 0);
            uint nlink = BitConverter.ToUInt32(buffer, 16);
            uint uid = BitConverter.ToUInt32(buffer, 20);
            uint gid = BitConverter.ToUInt32(buffer, 24);
            ulong inode = BitConverter.ToUInt64(buffer, 32);
            ulong blocks = BitConverter.ToUInt64(buffer, 48);
            if ((mask & (StatxUid | StatxGid | StatxBlocks)) != (StatxUid | StatxGid | StatxBlocks))
            {
                return false;
            }

            stat = new UnixStat(uid, gid, (long)Math.Min(blocks * 512, long.MaxValue), (mask & StatxNlink) != 0 ? nlink : 0, (mask & StatxIno) != 0 ? inode : 0);
            return true;
        }

        /// <summary>The user's name for a uid ("alice"), or the number when it has no name. Unix only.</summary>
        public static string UserName(uint uid) => LookUp(UserNames, uid, true);

        /// <summary>The group's name for a gid, or the number when it has no name. Unix only.</summary>
        public static string GroupName(uint gid) => LookUp(GroupNames, gid, false);

        private static string LookUp(Dictionary<uint, string> cache, uint id, bool user)
        {
            // getpwuid/getgrgid return a static buffer: one caller at a time, and the answers are cached.
            lock (PasswdLock)
            {
                if (cache.TryGetValue(id, out string cached))
                {
                    return cached;
                }

                string name = null;
                try
                {
                    IntPtr entry = user ? GetPwUid(id) : GetGrGid(id);
                    if (entry != IntPtr.Zero)
                    {
                        IntPtr namePointer = Marshal.ReadIntPtr(entry); // pw_name / gr_name: the first field
                        name = namePointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(namePointer);
                    }
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
                {
                }

                name = string.IsNullOrEmpty(name) ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
                cache[id] = name;
                return name;
            }
        }

        /// <summary>The volume's allocation unit for this path on Windows; 0 when unknown or not on Windows.</summary>
        public static long ClusterSize(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                {
                    return 0;
                }

                return GetDiskFreeSpace(PathUtil.WithTrailingSeparator(root), out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
                    ? (long)sectorsPerCluster * bytesPerSector
                    : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>Rounds a size up to whole clusters (a cluster of 0 means unknown: the size is returned).</summary>
        public static long RoundUpToCluster(long bytes, long cluster) =>
            cluster <= 0 || bytes <= 0 ? Math.Max(bytes, 0) : ((bytes + cluster - 1) / cluster) * cluster;

        /// <summary>
        /// The size one file takes on disk; null when it cannot be determined here (see the class remarks).
        /// <paramref name="cluster"/> is <see cref="ClusterSize"/> for the file's volume (Windows).
        /// </summary>
        public static long? FileSizeOnDisk(string path, long length, long cluster, byte[] statBuffer)
        {
            if (OperatingSystem.IsWindows())
            {
                if (cluster <= 0)
                {
                    return null;
                }

                long actual = length;
                try
                {
                    uint low = GetCompressedFileSize(path, out uint high);
                    if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0)
                    {
                        actual = ((long)high << 32) | low;
                    }
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
                {
                }

                return RoundUpToCluster(actual, cluster);
            }

            return TryGetUnixStat(path, statBuffer, out UnixStat stat) ? stat.AllocatedBytes : null;
        }

        /// <summary>True when this platform can say how much space files take on disk.</summary>
        public static bool CanMeasureSizeOnDisk => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && !s_statxMissing);

        /// <summary>Windows: "Text Document" for a .txt file; null elsewhere or when the shell does not know the type.</summary>
        public static string FriendlyTypeName(string path) => QueryAssociation(path, AssocStrFriendlyDocName);

        /// <summary>
        /// The application that opens the file by default: Windows from the shell's associations, Linux from
        /// mimeapps.list (see <see cref="OpenWithApplications"/>); null when unknown or on macOS.
        /// </summary>
        public static string DefaultApplicationName(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return QueryAssociation(path, AssocStrFriendlyAppName);
            }

            foreach (OpenWithApplication application in OpenWithApplications.For(path))
            {
                if (application.IsDefault)
                {
                    return application.Name;
                }
            }

            return null;
        }

        private const int AssocStrFriendlyDocName = 3;
        private const int AssocStrFriendlyAppName = 4;
        private const int AssocFNoTruncate = 0x20;
        private const int AssocFInitIgnoreUnknown = 0x400;

        private static string QueryAssociation(string path, int what)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension) || extension == ".")
            {
                return null;
            }

            try
            {
                uint length = 0;
                AssocQueryString(AssocFNoTruncate | AssocFInitIgnoreUnknown, what, extension, null, null, ref length);
                if (length == 0 || length > 4096)
                {
                    return null;
                }

                var buffer = new StringBuilder((int)length);
                int result = AssocQueryString(AssocFNoTruncate | AssocFInitIgnoreUnknown, what, extension, null, buffer, ref length);
                string text = result == 0 ? buffer.ToString().Trim() : null;
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Windows: the owner and the access rules; null when they cannot be read (or not on Windows).</summary>
        public static WindowsSecurityInfo ReadWindowsSecurity(string path, bool isDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return ReadAcl(path, isDirectory);
            }
            catch (SystemException ex)
            {
                // Access denied, a missing file, a file system without ACLs (FAT), an unmappable identity...
                Serilog.Log.Logger.Debug(ex, "Could not read the security of {Path}", path);
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static WindowsSecurityInfo ReadAcl(string path, bool isDirectory)
        {
            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(sections)
                : new FileInfo(path).GetAccessControl(sections);

            string owner = null;
            try
            {
                owner = NameOf(security.GetOwner(typeof(SecurityIdentifier)));
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
            {
            }

            // Explorer lists each group or user once per Allow/Deny, with the rights of all its entries together (an ACL
            // often holds one entry for the folder and another for its children).
            var merged = new List<(string Identity, bool Allow, FileSystemRights Rights, bool Inherited)>();
            foreach (AuthorizationRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule is not FileSystemAccessRule access)
                {
                    continue;
                }

                string identity = NameOf(access.IdentityReference);
                bool allow = access.AccessControlType == AccessControlType.Allow;
                int index = merged.FindIndex(r => r.Allow == allow && string.Equals(r.Identity, identity, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    merged.Add((identity, allow, access.FileSystemRights, access.IsInherited));
                }
                else
                {
                    var existing = merged[index];
                    merged[index] = (identity, allow, existing.Rights | access.FileSystemRights, existing.Inherited && access.IsInherited);
                }
            }

            var rules = new List<AccessRuleInfo>(merged.Count);
            foreach (var rule in merged)
            {
                rules.Add(new AccessRuleInfo(rule.Identity, rule.Allow, DescribeRights(rule.Rights, isDirectory), rule.Inherited));
            }

            return new WindowsSecurityInfo(owner, rules);
        }

        [SupportedOSPlatform("windows")]
        private static string NameOf(IdentityReference identity)
        {
            if (identity == null)
            {
                return null;
            }

            try
            {
                return identity.Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
            {
                return identity.Value; // an orphaned SID: show it as Explorer does
            }
        }

        /// <summary>The permission names of Explorer's Security tab for a set of rights.</summary>
        [SupportedOSPlatform("windows")]
        public static string DescribeRights(FileSystemRights rights, bool isDirectory)
        {
            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                return "Full control";
            }

            var parts = new List<string>();
            if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
            {
                parts.Add("Modify");
            }
            else
            {
                if ((rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                {
                    parts.Add(isDirectory ? "Read & execute, List folder contents" : "Read & execute");
                }
                else if ((rights & FileSystemRights.Read) == FileSystemRights.Read)
                {
                    parts.Add("Read");
                }

                if ((rights & FileSystemRights.Write) == FileSystemRights.Write)
                {
                    parts.Add("Write");
                }
            }

            return parts.Count == 0 ? "Special permissions" : string.Join(", ", parts);
        }

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

        [DllImport("libc", EntryPoint = "getpwuid")]
        private static extern IntPtr GetPwUid(uint uid);

        [DllImport("libc", EntryPoint = "getgrgid")]
        private static extern IntPtr GetGrGid(uint gid);

        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

        [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

        [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder output, ref uint outputLength);
    }
}
