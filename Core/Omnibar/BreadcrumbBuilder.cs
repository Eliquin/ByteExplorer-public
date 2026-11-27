// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;

namespace ByteExplorer.Core.Omnibar
{
    public enum CrumbKind
    {
        /// <summary>The file system root ("/", "Macintosh HD") or a Windows drive root.</summary>
        Root,

        /// <summary>The user's home folder, shown as "Home" instead of its path.</summary>
        Home,

        /// <summary>A mounted volume (USB stick, second disk), shown by its label.</summary>
        Volume,

        /// <summary>A network share (\\server\share, a gvfs or network mount).</summary>
        Network,

        /// <summary>A ZIP file browsed as a folder.</summary>
        Archive,
        /// <summary>A phone (its root).</summary>
        Phone,

        Folder,

        /// <summary>A place that is not a folder: the Home dashboard, This PC.</summary>
        Place,

        /// <summary>A lens (left panel › Lens): "Lens › Photos" while it is shown.</summary>
        Lens
    }

    /// <summary>One breadcrumb segment.</summary>
    public sealed class Crumb
    {
        public Crumb(string name, string path, CrumbKind kind)
        {
            Name = name;
            Path = path;
            Kind = kind;
        }

        public string Name { get; }

        /// <summary>The folder it opens; null for <see cref="CrumbKind.Place"/>.</summary>
        public string Path { get; }

        public CrumbKind Kind { get; }
    }

    /// <summary>A mounted volume the breadcrumb can name: its root folder, label, and whether it is on the network.</summary>
    public readonly struct CrumbVolume
    {
        public CrumbVolume(string root, string label, bool isNetwork)
        {
            Root = root;
            Label = label;
            IsNetwork = isNetwork;
        }

        public string Root { get; }
        public string Label { get; }
        public bool IsNetwork { get; }
    }

    /// <summary>
    /// Turns a path into breadcrumb segments the way people read locations, not the way the file system spells them:
    /// <list type="bullet">
    /// <item>Inside the home folder, the first segment is "Home", not "/home/me" or "C:\Users\me" (as GNOME Files and
    /// Finder do).</item>
    /// <item>Inside a mounted volume (a USB stick under /media, /Volumes or a second Windows drive), the first segment is
    /// the volume's label; the deepest mount wins (a volume mounted inside the home folder is named as the volume).</item>
    /// <item>A UNC share is one segment, "photos on nas"; a ZIP file is marked as an archive.</item>
    /// <item>Otherwise the root reads "File System" (Linux), "Macintosh HD" (macOS) or "C:" (Windows).</item>
    /// </list>
    /// Pure: volumes come from the caller, so this never touches the disk.
    /// </summary>
    public static class BreadcrumbBuilder
    {
        public static IReadOnlyList<Crumb> Build(string path, string homeFolder, IReadOnlyList<CrumbVolume> volumes)
        {
            var crumbs = new List<Crumb>();
            if (string.IsNullOrEmpty(path))
            {
                return crumbs;
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                // A Windows phone: the phone, then its storage and folders ("Galaxy S21 › Internal shared storage › DCIM").
                string[] segments = Platform.Phones.PhonePaths.Segments(path);
                for (int i = 0; i < segments.Length; i++)
                {
                    crumbs.Add(new Crumb(segments[i], Platform.Phones.PhonePaths.Build(segments[0], segments[1..(i + 1)]),
                        i == 0 ? CrumbKind.Phone : CrumbKind.Folder));
                }

                return crumbs;
            }

            if (TryBuildMountedPhone(path, crumbs))
            {
                return crumbs;
            }

            string normalized;
            string root;
            try
            {
                normalized = PathUtil.Normalize(path);
                root = System.IO.Path.GetPathRoot(normalized);
            }
            catch (ArgumentException)
            {
                return crumbs;
            }

            if (string.IsNullOrEmpty(root))
            {
                return crumbs;
            }

            // The anchor: the deepest of a mounted volume and the home folder that contains the path.
            string anchorPath = null;
            Crumb anchor = null;
            if (volumes != null)
            {
                foreach (CrumbVolume volume in volumes)
                {
                    if (string.IsNullOrEmpty(volume.Root) || IsFileSystemRoot(volume.Root) || !PathUtil.IsSameOrInside(normalized, volume.Root))
                    {
                        continue;
                    }

                    if (anchorPath == null || volume.Root.Length > anchorPath.Length)
                    {
                        anchorPath = PathUtil.TrimTrailingSeparator(volume.Root);
                        string label = string.IsNullOrWhiteSpace(volume.Label) ? PathUtil.GetName(anchorPath) : volume.Label;
                        anchor = new Crumb(label, anchorPath, volume.IsNetwork ? CrumbKind.Network : CrumbKind.Volume);
                    }
                }
            }

            if (!string.IsNullOrEmpty(homeFolder) && !IsFileSystemRoot(homeFolder) && PathUtil.IsSameOrInside(normalized, homeFolder) &&
                (anchorPath == null || homeFolder.Length >= anchorPath.Length))
            {
                anchorPath = PathUtil.TrimTrailingSeparator(homeFolder);
                anchor = new Crumb("Home", anchorPath, CrumbKind.Home);
            }

            if (anchor == null)
            {
                if (OperatingSystem.IsWindows() && root.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    anchorPath = PathUtil.TrimTrailingSeparator(root);
                    string[] parts = anchorPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    string name = parts.Length >= 2 ? $"{parts[1]} on {parts[0]}" : anchorPath;
                    anchor = new Crumb(name, anchorPath, CrumbKind.Network);
                }
                else
                {
                    anchorPath = root;
                    string name = OperatingSystem.IsWindows()
                        ? PathUtil.TrimTrailingSeparator(root).TrimEnd('\\')
                        : OperatingSystem.IsMacOS() ? "Macintosh HD" : "File System";
                    anchor = new Crumb(name, root, CrumbKind.Root);
                }
            }

            crumbs.Add(anchor);

            string rest = normalized.Length > anchorPath.Length ? normalized.Substring(anchorPath.Length) : string.Empty;
            string accumulated = anchorPath;
            foreach (string part in rest.Split(PathUtil.Separator, StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = System.IO.Path.Combine(accumulated, part);
                CrumbKind kind = ByteExplorer.Core.Services.Archives.ArchiveFormats.IsKnownArchiveName(part) || part.EndsWith(ByteExplorer.Core.Security.ProtectedFormat.Extension, StringComparison.OrdinalIgnoreCase)
                    ? CrumbKind.Archive
                    : CrumbKind.Folder;
                crumbs.Add(new Crumb(part, accumulated, kind));
            }

            return crumbs;
        }

        /// <summary>
        /// A Linux phone mounted by GVfs: the phone (named from its GVfs folder) instead of "File System › run › user ›
        /// 1000 › gvfs › mtp:host=…".
        /// </summary>
        private static bool TryBuildMountedPhone(string path, List<Crumb> crumbs)
        {
            if (!OperatingSystem.IsLinux())
            {
                return false;
            }

            string normalized = PathUtil.Normalize(path);
            int gvfs = normalized.IndexOf("/gvfs/", StringComparison.Ordinal);
            if (gvfs < 0)
            {
                return false;
            }

            int start = gvfs + "/gvfs/".Length;
            int end = normalized.IndexOf('/', start);
            string entry = end < 0 ? normalized.Substring(start) : normalized.Substring(start, end - start);
            if (!Platform.Phones.MountedPhoneProvider.TryParseGvfsFolder(entry, out _, out string host))
            {
                return false;
            }

            string accumulated = end < 0 ? normalized : normalized.Substring(0, end);
            crumbs.Add(new Crumb(Platform.Phones.MountedPhoneProvider.NameFromHost(host), accumulated, CrumbKind.Phone));
            if (end >= 0)
            {
                foreach (string part in normalized.Substring(end).Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    accumulated += "/" + part;
                    crumbs.Add(new Crumb(part, accumulated, CrumbKind.Folder));
                }
            }

            return true;
        }

        private static bool IsFileSystemRoot(string path)
        {
            string trimmed = PathUtil.TrimTrailingSeparator(path);
            return trimmed.Length == 0 || trimmed == "/" || (OperatingSystem.IsWindows() && trimmed.Length <= 3 && trimmed.Length >= 2 && trimmed[1] == ':');
        }
    }
}
