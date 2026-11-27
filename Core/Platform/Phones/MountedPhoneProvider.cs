// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ByteExplorer.Core.Models;
using Serilog;

namespace ByteExplorer.Core.Platform.Phones
{
    /// <summary>
    /// Linux: phones as the desktop mounts them. GNOME, Cinnamon, MATE and Xfce (GVfs) put a phone in File transfer
    /// mode at /run/user/&lt;uid&gt;/gvfs/mtp:host=…, and a phone or camera in photo mode (PTP) at …/gphoto2:host=…; the
    /// FUSE tools jmtpfs, simple-mtpfs and go-mtpfs mount one wherever asked. These are real folders, so listing and
    /// reading are ordinary file access; only writing differs: MTP can't rename a half-written file into place or flush
    /// it, so a new file is written under its name directly and removed if it doesn't finish.
    /// <para>
    /// A phone the desktop sees but hasn't mounted yet (GVfs mounts on first use) is listed too, from "gio mount -li";
    /// opening it mounts it (<see cref="MountAsync"/>).
    /// </para>
    /// </summary>
    public sealed class MountedPhoneProvider : IPhoneProvider
    {
        private static readonly HashSet<string> FuseMtpTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "fuse.jmtpfs", "fuse.simple-mtpfs", "fuse.go-mtpfs", "fuse.aft-mtp-mount", "fuse.android-file-transfer"
        };

        private static readonly TimeSpan GioTimeout = TimeSpan.FromSeconds(5);

        /// <summary>The GVfs FUSE folder of this session ($XDG_RUNTIME_DIR/gvfs).</summary>
        public static string GvfsRoot
        {
            get
            {
                string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                return string.IsNullOrEmpty(runtime) ? null : Path.Combine(runtime, "gvfs");
            }
        }

        /// <summary>Cheap: is the path on a mounted phone (GVfs MTP/PTP folder or an MTP FUSE mount)? Linux only.</summary>
        public static bool IsMountedPhonePath(string path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path) || path[0] != '/')
            {
                return false;
            }

            if (path.Contains("/gvfs/mtp:", StringComparison.Ordinal) || path.Contains("/gvfs/gphoto2:", StringComparison.Ordinal))
            {
                return true;
            }

            string type = MountPoints.FileSystemTypeLinux(path);
            return type != null && FuseMtpTypes.Contains(type);
        }

        /// <summary>The phone's own folder (".../gvfs/mtp:host=…" or the FUSE mount point) of a path on it, or null.</summary>
        public static string DeviceRootOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (string marker in new[] { "/gvfs/mtp:", "/gvfs/gphoto2:" })
            {
                int at = path.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    int end = path.IndexOf('/', at + "/gvfs/".Length);
                    return end < 0 ? path : path.Substring(0, end);
                }
            }

            (string mount, string type) = MountPoints.FileSystemType(path, MountPoints.CurrentTableLinux());
            return type != null && FuseMtpTypes.Contains(type) ? mount : null;
        }

        public IReadOnlyList<PhoneDevice> GetDevices()
        {
            var devices = new List<PhoneDevice>();
            if (!OperatingSystem.IsLinux())
            {
                return devices;
            }

            try
            {
                IReadOnlyList<GioVolume> volumes = ReadGioVolumes();
                var shown = new HashSet<string>(StringComparer.Ordinal);

                string gvfs = GvfsRoot;
                if (gvfs != null && Directory.Exists(gvfs))
                {
                    foreach (string folder in Directory.EnumerateDirectories(gvfs))
                    {
                        string entry = Path.GetFileName(folder);
                        if (!TryParseGvfsFolder(entry, out string scheme, out string host))
                        {
                            continue;
                        }

                        GioVolume volume = volumes.FirstOrDefault(v => v.Scheme == scheme && v.Host == host);
                        string name = volume?.Name ?? NameFromHost(host);
                        devices.Add(new PhoneDevice(name, folder, scheme == "gphoto2" ? "Camera (photos)" : "Phone (MTP)", ListStorages(folder)));
                        shown.Add(scheme + ":" + host);
                    }
                }

                foreach (string mount in MountPoints.MountPointsOfTypesLinux(FuseMtpTypes))
                {
                    devices.Add(new PhoneDevice(Path.GetFileName(mount), mount, "Phone (MTP)", ListStorages(mount)));
                }

                // Seen by the desktop, not mounted yet: opening it mounts it.
                foreach (GioVolume volume in volumes)
                {
                    if (!volume.IsMounted && !shown.Contains(volume.Scheme + ":" + volume.Host))
                    {
                        devices.Add(new PhoneDevice(volume.Name, null, volume.Scheme == "gphoto2" ? "Camera (photos)" : "Phone (MTP)",
                            Array.Empty<PhoneStorage>(), needsMount: true, mountAddress: volume.Address));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Listing mounted phones failed");
            }

            return devices;
        }

        /// <summary>Mounts a phone the desktop sees (gio mount) and returns its folder. Throws IOException with the reason.</summary>
        public static async System.Threading.Tasks.Task<string> MountAsync(PhoneDevice device, CancellationToken cancellationToken)
        {
            if (device?.MountAddress == null)
            {
                throw new IOException("This phone can't be opened.");
            }

            (int exit, string output) = await NetworkLocations.RunAsync("gio", new[] { "mount", device.MountAddress }, cancellationToken).ConfigureAwait(false);
            if (exit == -1)
            {
                throw new IOException("Opening phones needs GIO (the gvfs package, part of GNOME, Cinnamon, MATE and Xfce desktops).");
            }

            (int infoExit, string info) = await NetworkLocations.RunAsync("gio", new[] { "info", device.MountAddress }, cancellationToken).ConfigureAwait(false);
            string local = infoExit == 0 ? NetworkLocations.ParseLocalPath(info) : null;
            if (!string.IsNullOrEmpty(local) && Directory.Exists(local))
            {
                return local;
            }

            string reason = (output ?? string.Empty).Trim();
            throw new IOException("Couldn't open the phone. Unlock it and check that USB is set to \"File transfer\", then try again." +
                                  (reason.Length > 0 ? "\n\n" + reason : string.Empty));
        }

        // ------------------------------------------------------------------ files (ordinary file access on the mount)

        public DirectoryIndex List(string folderPath, CancellationToken cancellationToken)
        {
            try
            {
                var entries = new List<DirectoryIndexEntry>();
                foreach (FileSystemInfo info in new DirectoryInfo(folderPath).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new DirectoryIndexEntry(info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc, info.Attributes));
                }

                return new DirectoryIndex(folderPath, entries, false, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return DirectoryIndex.Error(folderPath, PhoneErrors.ListingMessage(ex));
            }
        }

        public PhoneItemInfo GetInfo(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return new PhoneItemInfo(path, true, 0, Directory.GetLastWriteTimeUtc(path));
                }

                var file = new FileInfo(path);
                return file.Exists ? new PhoneItemInfo(path, false, file.Length, file.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public Stream OpenRead(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

        public PhoneWriteStream CreateFile(string folderPath, string name, long size, DateTime modifiedUtc)
        {
            string path = Path.Combine(folderPath, PhonePaths.SanitizeName(name));
            return new MountedWriteStream(path, modifiedUtc);
        }

        public string CreateFolder(string parentPath, string name)
        {
            string path = Path.Combine(parentPath, PhonePaths.SanitizeName(name));
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException($"\"{name}\" already exists on the phone.");
            }

            Directory.CreateDirectory(path);
            return path;
        }

        public void Delete(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }

        public string Rename(string path, string newName)
        {
            string destination = Path.Combine(Path.GetDirectoryName(path) ?? path, PhonePaths.SanitizeName(newName));
            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
            }
            else
            {
                File.Move(path, destination, overwrite: false);
            }

            return destination;
        }

        private static IReadOnlyList<PhoneStorage> ListStorages(string root)
        {
            try
            {
                // The top folders of an MTP mount are the phone's storages; listing them asks the phone once.
                return Directory.EnumerateDirectories(root).Select(d => new PhoneStorage(Path.GetFileName(d), d, -1, -1)).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<PhoneStorage>(); // locked phone: storages appear once it is unlocked
            }
        }

        // ------------------------------------------------------------------ GVfs names

        /// <summary>"mtp:host=SAMSUNG_Android_R58M" → ("mtp", "SAMSUNG_Android_R58M"); GVfs escapes some characters as %xx.</summary>
        internal static bool TryParseGvfsFolder(string entry, out string scheme, out string host)
        {
            scheme = null;
            host = null;
            foreach (string candidate in new[] { "mtp", "gphoto2" })
            {
                string prefix = candidate + ":host=";
                if (entry != null && entry.StartsWith(prefix, StringComparison.Ordinal))
                {
                    scheme = candidate;
                    host = Uri.UnescapeDataString(entry.Substring(prefix.Length));
                    return host.Length > 0;
                }
            }

            return false;
        }

        /// <summary>"SAMSUNG_SAMSUNG_Android_R58M12345" → "SAMSUNG Android" when GIO gives no name.</summary>
        internal static string NameFromHost(string host)
        {
            string[] words = host.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (string word in words)
            {
                // Drop repeats ("SAMSUNG SAMSUNG") and the serial number (a long run of letters and digits with digits in it).
                bool serial = word.Length >= 8 && word.Any(char.IsDigit) && word.All(char.IsLetterOrDigit);
                if (!serial && !kept.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    kept.Add(word);
                }
            }

            return kept.Count == 0 ? "Phone" : string.Join(" ", kept);
        }

        internal sealed class GioVolume
        {
            public string Name { get; init; }
            public string Address { get; init; }
            public string Scheme { get; init; }
            public string Host { get; init; }
            public bool IsMounted { get; set; }
        }

        private static IReadOnlyList<GioVolume> ReadGioVolumes()
        {
            try
            {
                using var timeout = new CancellationTokenSource(GioTimeout);
                (int exit, string output) = NetworkLocations.RunAsync("gio", new[] { "mount", "-li" }, timeout.Token).GetAwaiter().GetResult();
                return exit == 0 ? ParseGioMountList(output) : Array.Empty<GioVolume>();
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                return Array.Empty<GioVolume>();
            }
        }

        /// <summary>
        /// The MTP and PTP volumes in "gio mount -li" output: each "Volume(n): Name" block with an
        /// "activation_root=mtp://HOST/" line, marked mounted when a "Mount(n): Name -> mtp://HOST/" line names it.
        /// </summary>
        internal static IReadOnlyList<GioVolume> ParseGioMountList(string output)
        {
            var volumes = new List<GioVolume>();
            var mounted = new HashSet<string>(StringComparer.Ordinal);
            string pendingName = null;
            foreach (string raw in (output ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Volume(", StringComparison.Ordinal))
                {
                    int colon = line.IndexOf("):", StringComparison.Ordinal);
                    pendingName = colon > 0 ? line.Substring(colon + 2).Trim() : null;
                }
                else if (line.StartsWith("Drive(", StringComparison.Ordinal) || line.StartsWith("Mount(", StringComparison.Ordinal))
                {
                    if (line.StartsWith("Mount(", StringComparison.Ordinal))
                    {
                        int arrow = line.LastIndexOf("->", StringComparison.Ordinal);
                        if (arrow > 0 && TryParseAddress(line.Substring(arrow + 2).Trim(), out string scheme, out string host))
                        {
                            mounted.Add(scheme + ":" + host);
                        }
                    }

                    pendingName = null;
                }
                else if (pendingName != null && line.StartsWith("activation_root=", StringComparison.Ordinal))
                {
                    string address = line.Substring("activation_root=".Length).Trim();
                    if (TryParseAddress(address, out string scheme, out string host))
                    {
                        volumes.Add(new GioVolume { Name = pendingName, Address = address, Scheme = scheme, Host = host });
                    }
                }
            }

            foreach (GioVolume volume in volumes)
            {
                volume.IsMounted = mounted.Contains(volume.Scheme + ":" + volume.Host);
            }

            return volumes;
        }

        private static bool TryParseAddress(string address, out string scheme, out string host)
        {
            scheme = null;
            host = null;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || (uri.Scheme != "mtp" && uri.Scheme != "gphoto2"))
            {
                return false;
            }

            scheme = uri.Scheme;
            // The host part as written ("SAMSUNG_Android_R58M"); Uri lowercases Host, so take it from the text.
            string rest = address.Substring(scheme.Length + 3);
            int slash = rest.IndexOf('/');
            host = Uri.UnescapeDataString(slash >= 0 ? rest.Substring(0, slash) : rest);
            return host.Length > 0;
        }

        /// <summary>A file written under its name on the mount; deleted again unless <see cref="Commit"/> was reached.</summary>
        private sealed class MountedWriteStream : PhoneWriteStream
        {
            private readonly string _path;
            private readonly DateTime _modifiedUtc;
            private readonly FileStream _stream;
            private bool _committed;
            private bool _disposed;

            public MountedWriteStream(string path, DateTime modifiedUtc)
            {
                _path = path;
                _modifiedUtc = modifiedUtc;
                // CreateNew: never over an existing file (the engine has asked about clashes already).
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            }

            public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

            public override void Flush() => _stream.Flush();

            public override string Commit()
            {
                _stream.Flush();
                _stream.Dispose(); // GVfs uploads to the phone on close: a failure surfaces here
                _committed = true;
                if (_modifiedUtc != default)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(_path, _modifiedUtc);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                    {
                        // Most phones keep their own time for a new file: not a failure.
                    }
                }

                return _path;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_disposed)
                {
                    _disposed = true;
                    if (!_committed)
                    {
                        try
                        {
                            _stream.Dispose();
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                        }

                        try
                        {
                            File.Delete(_path); // never leave a partial file on the phone
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            Log.Logger.Warning(ex, "Couldn't remove the partial file {Path} from the phone", _path);
                        }
                    }
                }

                base.Dispose(disposing);
            }
        }
    }
}
