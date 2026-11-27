// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Security;
using ByteExplorer.Core.Services.Archives;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Browsing inside archives as if they were folders, read-only: ZIP, TAR (plain and compressed), single compressed
    /// files, and 7z, RAR, ISO and CAB through an installed archiver (<see cref="ArchiveFormats"/> says which; each format
    /// is an <see cref="ArchiveProvider"/>). A path that runs through an archive names a folder or file inside it:
    /// "/home/me/photos.zip/2024/beach.jpg" is the entry "2024/beach.jpg" of "/home/me/photos.zip", and the archive's own
    /// path is its root. The file list, breadcrumbs, Up, Back and the address bar work unchanged on such paths; what can't
    /// work on them (writing into the archive, watching it) checks <see cref="IsInsideArchive"/>.
    /// <para>
    /// <b>Listing</b>: the archive's entries are read once (a ZIP's central directory, a TAR stream from start to end, an
    /// archiver's listing) into a tree of folders (explicit folder entries and the ones implied by file paths), kept for
    /// the last few archives and rebuilt when the file changes (size or modification time). A 100,000-entry archive is
    /// read once, then each folder lists from memory.
    /// </para>
    /// <para>
    /// <b>Extracting</b> (to open a file, to copy out of the archive) writes to a private temporary folder; entry names
    /// that would land outside it ("../../etc/passwd", absolute paths: "zip slip") are refused. Temporary folders older
    /// than a day are removed at startup.
    /// </para>
    /// </summary>
    public static class ArchiveBrowser
    {
        private const int CachedArchives = 4;
        private const int MaxEntries = 1_000_000;
        private const int MaxDepth = 64;

        private static readonly object Gate = new object();
        private static readonly LinkedList<ArchiveTree> Cache = new LinkedList<ArchiveTree>();

        /// <summary>Where extracted copies go (one subfolder per extraction).</summary>
        public static string TempRoot => Path.Combine(Path.GetTempPath(), "ByteExplorer", "archives");

        /// <summary>True for the name of an archive this machine can browse (.zip, .tar, .tar.gz, and more with an archiver).</summary>
        public static bool IsArchiveFile(string path) => ArchiveFormats.IsBrowsable(path);

        /// <summary>True when the path is inside an archive (or is the archive, browsed as a folder).</summary>
        public static bool IsInsideArchive(string path) => TryResolve(path, out _, out _);

        /// <summary>
        /// Splits a path that runs through an archive into the archive and the entry path inside it ('/' separated, ""
        /// for the root). Only path segments with an archive extension are checked on disk, so ordinary paths cost no I/O.
        /// </summary>
        public static bool TryResolve(string path, out string archivePath, out string innerPath)
        {
            archivePath = null;
            innerPath = null;
            if (!ArchiveFormats.MayContainArchive(path))
            {
                return false;
            }

            var inner = new Stack<string>();
            string candidate = PathUtil.Normalize(path);
            for (int depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(candidate); depth++)
            {
                if ((IsArchiveFile(candidate) || ProtectedItems.IsUnlocked(candidate)) && File.Exists(candidate))
                {
                    archivePath = candidate;
                    innerPath = string.Join("/", inner);
                    return true;
                }

                string name = PathUtil.GetName(candidate);
                string parent = PathUtil.GetParent(candidate);
                if (string.IsNullOrEmpty(name) || parent == null || PathUtil.AreSame(parent, candidate))
                {
                    break;
                }

                inner.Push(name);
                candidate = parent;
            }

            return false;
        }

        /// <summary>Lists one folder of an archive. Never throws for a bad archive: returns an error index.</summary>
        public static DirectoryIndex List(string archivePath, string innerPath, string displayPath)
        {
            try
            {
                ArchiveTree tree = GetTree(archivePath, CancellationToken.None);
                string key = NormalizeInner(innerPath);
                if (!tree.Folders.TryGetValue(key, out List<DirectoryIndexEntry> entries))
                {
                    return tree.Files.ContainsKey(key)
                        ? DirectoryIndex.Error(displayPath, "This is a file in the archive, not a folder.")
                        : DirectoryIndex.Error(displayPath, "This folder isn't in the archive.");
                }

                return new DirectoryIndex(displayPath, entries.ToArray(), false, null);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Log.Logger.Warning(ex, "Couldn't read the archive {Archive}", archivePath);
                return DirectoryIndex.Error(displayPath, DescribeFailure(archivePath, ex));
            }
        }

        /// <summary>
        /// Extracts the given items (paths inside an archive: files or folders) into a new temporary folder and returns
        /// the extracted top-level paths, in the same order. Folders are extracted with everything in them.
        /// </summary>
        public static IReadOnlyList<string> ExtractToTemp(IReadOnlyList<string> paths, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string plainDestination = null;
            var extracted = new List<string>(paths.Count);
            var byArchive = new Dictionary<string, List<string>>(PathUtil.Comparer);
            foreach (string path in paths)
            {
                if (!TryResolve(path, out string archive, out string inner))
                {
                    throw new IOException($"\"{PathUtil.GetName(path)}\" isn't inside an archive.");
                }

                if (!byArchive.TryGetValue(archive, out List<string> list))
                {
                    byArchive[archive] = list = new List<string>();
                }

                list.Add(NormalizeInner(inner));
            }

            foreach (KeyValuePair<string, List<string>> pair in byArchive)
            {
                // A password-protected item's files go to its own folder, deleted when it is locked again.
                bool isProtected = ProtectedFormat.IsProtectedFile(pair.Key);
                string destination = isProtected ? ProtectedItems.NewTempFolder(pair.Key) : plainDestination ??= NewTempFolder();
                ArchiveTree tree = GetTree(pair.Key, cancellationToken);
                var files = new List<ArchiveExtraction>();
                var taken = new HashSet<string>(PathUtil.Comparer);
                foreach (string item in pair.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string itemName = item.Length == 0 ? ArchiveFormats.StemOf(pair.Key) : item.Substring(item.LastIndexOf('/') + 1);
                    string prefix = item.Length == 0 ? string.Empty : item + "/";
                    if (isProtected && item.Length == 0)
                    {
                        // The protected file's root holds the one item it was made from: give that, not a folder around it.
                        ArchiveEntryInfo first = tree.Entries.Count > 0 ? tree.Entries[0] : null;
                        string root = first == null ? itemName : NormalizeInner(first.RawName).Split('/')[0];
                        if (root.Length > 0 && !IsUnsafeName(root))
                        {
                            itemName = root;
                            prefix = root + "/";
                        }
                    }

                    bool any = false;
                    foreach (ArchiveEntryInfo entry in tree.Entries)
                    {
                        string name = NormalizeInner(entry.RawName);
                        if (name.Length == 0)
                        {
                            continue; // the archive's root ("./" in TAR files)
                        }

                        if (IsUnsafeName(entry.RawName))
                        {
                            Log.Logger.Warning("Skipped the archive entry {Entry}: its name points outside the archive's folders", entry.RawName);
                            continue;
                        }

                        string relative;
                        if (string.Equals(name, item, StringComparison.Ordinal) && !entry.IsFolder && item.Length > 0)
                        {
                            relative = itemName; // the selected file itself
                        }
                        else if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
                        {
                            relative = itemName + "/" + name.Substring(prefix.Length);
                        }
                        else
                        {
                            continue;
                        }

                        string target = SafeTarget(destination, relative);
                        any = true;
                        if (entry.IsFolder)
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        if (!taken.Add(target))
                        {
                            continue; // a name stored twice: the first one, as listed
                        }

                        if (entry.IsEncrypted)
                        {
                            throw new ArchivePasswordException();
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        files.Add(new ArchiveExtraction(entry, target));
                    }

                    string top = Path.Combine(destination, itemName);
                    if (!any)
                    {
                        // An implied folder with no entries of its own is still a folder.
                        Directory.CreateDirectory(top);
                    }

                    extracted.Add(top);
                }

                tree.Provider.Extract(pair.Key, files, progress, cancellationToken);
            }

            return extracted;
        }

        /// <summary>Removes extraction folders older than <paramref name="age"/> (files opened from archives last session).</summary>
        public static void CleanupTemp(TimeSpan age)
        {
            try
            {
                if (!Directory.Exists(TempRoot))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow - age;
                foreach (string folder in Directory.EnumerateDirectories(TempRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                        {
                            Directory.Delete(folder, recursive: true);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // In use (a file still open in another program): next time.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Archive temp cleanup skipped");
            }
        }

        private static string NewTempFolder()
        {
            string folder = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The failures that make an archive unreadable (damaged, locked, a missing or failing tool), not bugs.</summary>
        internal static bool IsReadFailure(Exception ex) =>
            ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is NotSupportedException
            || ex is FormatException || ex is System.Security.Cryptography.CryptographicException;

        /// <summary>The message shown in place of an archive's items when it can't be read.</summary>
        private static string DescribeFailure(string archivePath, Exception ex)
        {
            if (ex is UnauthorizedAccessException)
            {
                return "Access denied.";
            }

            if (ProtectedFormat.IsProtectedFile(archivePath))
            {
                return ex.Message.Length > 0 ? ex.Message : "This password-protected item couldn't be read.";
            }

            if (ex is ArchivePasswordException || ex is NotSupportedException)
            {
                return ex.Message;
            }

            ArchiveProvider provider = ArchiveFormats.GetProvider(archivePath);
            if (provider == null || provider is ZipArchiveProvider)
            {
                return "This ZIP file is damaged, or isn't a ZIP file.";
            }

            if (provider is ExternalArchiveProvider && ex.GetType() == typeof(IOException) && ex.Message.Length > 0)
            {
                return ex.Message; // the archiver's own reason
            }

            string article = "aeiou".IndexOf(char.ToLowerInvariant(provider.FormatName[0])) >= 0 ? "an" : "a";
            return $"This {provider.FormatName} is damaged, or isn't {article} {provider.FormatName}.";
        }

        /// <summary>Drops the cached listing of an archive (a protected item that was locked, or re-encrypted).</summary>
        public static void Forget(string archivePath)
        {
            lock (Gate)
            {
                for (LinkedListNode<ArchiveTree> node = Cache.First; node != null; node = node.Next)
                {
                    if (PathUtil.AreSame(node.Value.Path, archivePath))
                    {
                        Cache.Remove(node);
                        return;
                    }
                }
            }
        }

        /// <summary>The target path of an entry, refusing names that would leave the destination ("zip slip").</summary>
        internal static string SafeTarget(string destination, string relative)
        {
            string target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            string root = PathUtil.WithTrailingSeparator(Path.GetFullPath(destination));
            if (!target.StartsWith(root, PathUtil.Comparison))
            {
                throw new IOException($"The archive entry \"{relative}\" points outside the destination folder and was not extracted.");
            }

            return target;
        }

        /// <summary>
        /// Entry names that are never listed or extracted: a ".." segment, or an absolute path ("/etc/x", "C:\\x",
        /// "\\\\server\\x"). Archivers refuse them too; they exist only to write outside the destination.
        /// </summary>
        internal static bool IsUnsafeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return true;
            }

            string name = fullName.Replace('\\', '/');
            if (name.StartsWith("/", StringComparison.Ordinal) || (name.Length >= 2 && name[1] == ':'))
            {
                return true;
            }

            foreach (string segment in name.Split('/'))
            {
                if (segment == "..")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// "./dir\sub/" → "dir/sub": forward slashes, no leading or trailing ones, and no "." segments (TAR files made
        /// with "tar -C folder ." name every entry "./…").
        /// </summary>
        internal static string NormalizeInner(string inner)
        {
            string name = (inner ?? string.Empty).Replace('\\', '/').Trim('/');
            if (name != "." && !name.StartsWith("./", StringComparison.Ordinal) && name.IndexOf("/./", StringComparison.Ordinal) < 0
                && !name.EndsWith("/.", StringComparison.Ordinal))
            {
                return name;
            }

            var parts = new List<string>();
            foreach (string part in name.Split('/'))
            {
                if (part.Length > 0 && part != ".")
                {
                    parts.Add(part);
                }
            }

            return string.Join("/", parts);
        }

        private static ArchiveTree GetTree(string archivePath, CancellationToken cancellationToken)
        {
            var info = new FileInfo(archivePath);
            long length = info.Length;
            DateTime modified = info.LastWriteTimeUtc;

            lock (Gate)
            {
                for (LinkedListNode<ArchiveTree> node = Cache.First; node != null; node = node.Next)
                {
                    ArchiveTree cached = node.Value;
                    if (PathUtil.AreSame(cached.Path, archivePath))
                    {
                        if (cached.Length == length && cached.Modified == modified)
                        {
                            Cache.Remove(node);
                            Cache.AddFirst(node);
                            return cached;
                        }

                        Cache.Remove(node);
                        break;
                    }
                }
            }

            ArchiveTree tree = BuildTree(archivePath, length, modified, cancellationToken);
            lock (Gate)
            {
                Cache.AddFirst(tree);
                while (Cache.Count > CachedArchives)
                {
                    Cache.RemoveLast();
                }
            }

            return tree;
        }

        private static ArchiveTree BuildTree(string archivePath, long length, DateTime modified, CancellationToken cancellationToken)
        {
            ArchiveProvider provider = ArchiveFormats.GetProvider(archivePath)
                ?? throw new NotSupportedException(ArchiveFormats.MissingToolHint(archivePath) ?? "This kind of archive can't be browsed.");
            IReadOnlyList<ArchiveEntryInfo> entries = provider.ReadEntries(archivePath, MaxEntries, cancellationToken);
            var tree = new ArchiveTree(archivePath, length, modified, provider, entries);
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [string.Empty] = new HashSet<string>(StringComparer.Ordinal) };
            tree.Folders[string.Empty] = new List<DirectoryIndexEntry>();

            foreach (ArchiveEntryInfo entry in entries)
            {
                bool isFolder = entry.IsFolder;
                string name = NormalizeInner(entry.RawName);
                if (name.Length == 0 || IsUnsafeName(entry.RawName))
                {
                    continue; // empty or escaping names are not browsable (and never extracted)
                }

                string[] parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string parent = string.Empty;
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    string path = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                    if (!last || isFolder)
                    {
                        if (seen[parent].Add(parts[i]))
                        {
                            DateTime folderTime = last ? entry.LastWriteUtc : default;
                            tree.Folders[parent].Add(new DirectoryIndexEntry(parts[i], true, 0, folderTime, FileAttributes.Directory | FileAttributes.ReadOnly));
                        }

                        if (!tree.Folders.ContainsKey(path))
                        {
                            tree.Folders[path] = new List<DirectoryIndexEntry>();
                            seen[path] = new HashSet<string>(StringComparer.Ordinal);
                        }
                    }
                    else if (seen[parent].Add(parts[i]))
                    {
                        tree.Folders[parent].Add(new DirectoryIndexEntry(parts[i], false, entry.Length, entry.LastWriteUtc, FileAttributes.ReadOnly));
                        tree.Files[path] = entry.Length;
                    }

                    parent = path;
                }
            }

            return tree;
        }

        private sealed class ArchiveTree
        {
            public ArchiveTree(string path, long length, DateTime modified, ArchiveProvider provider, IReadOnlyList<ArchiveEntryInfo> entries)
            {
                Path = path;
                Length = length;
                Modified = modified;
                Provider = provider;
                Entries = entries;
            }

            public string Path { get; }
            public long Length { get; }
            public DateTime Modified { get; }

            /// <summary>The format that read the archive (and extracts from it).</summary>
            public ArchiveProvider Provider { get; }

            /// <summary>Every entry in archive order, kept so extracting needn't read a TAR stream or run a tool again to find them.</summary>
            public IReadOnlyList<ArchiveEntryInfo> Entries { get; }

            public Dictionary<string, List<DirectoryIndexEntry>> Folders { get; } = new Dictionary<string, List<DirectoryIndexEntry>>(StringComparer.Ordinal);
            public Dictionary<string, long> Files { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
