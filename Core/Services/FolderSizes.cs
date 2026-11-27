// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;

namespace ByteExplorer.Core.Services
{
    /// <summary>What a folder holds, as far as it was counted.</summary>
    public readonly struct FolderSizeInfo
    {
        public FolderSizeInfo(long bytes, long files, long folders, int unreadable, bool isComplete)
        {
            Bytes = bytes;
            Files = files;
            Folders = folders;
            Unreadable = unreadable;
            IsComplete = isComplete;
        }

        public long Bytes { get; }
        public long Files { get; }

        /// <summary>Subfolders, at every depth.</summary>
        public long Folders { get; }

        /// <summary>Subfolders that couldn't be read (access denied, gone meanwhile): the size is at least this much.</summary>
        public int Unreadable { get; }

        /// <summary>False while the count runs (the numbers so far).</summary>
        public bool IsComplete { get; }
    }

    /// <summary>
    /// Folder sizes, Total Commander's way: a folder's size is counted when asked for (or, with the setting, for the
    /// folders of the open folder on local drives), then kept for the session so the Size column, sorting by size, the
    /// status bar and the details pane can show it.
    /// <para>
    /// Counting walks the tree with the same native enumeration the lists use (one call per folder, sizes included) and
    /// never follows links or junctions, as the copy engine doesn't: a link to a folder counts as nothing, and a link loop
    /// can't make it run forever. A folder it can't read is counted as unreadable and the rest continues.
    /// </para>
    /// <para>
    /// A kept size goes stale when something changes inside: file operations invalidate the folders they touched and
    /// every folder above them; a refresh (F5) recounts the open folder's folders. A change made by another program deep
    /// inside a folder isn't noticed until then.
    /// </para>
    /// </summary>
    public static class FolderSizes
    {
        private static readonly ConcurrentDictionary<string, FolderSizeInfo> Known = new ConcurrentDictionary<string, FolderSizeInfo>(PathUtil.Comparer);
        private static long s_version;

        /// <summary>Raised (on the thread that reported it, the UI thread in the app) when a folder's size changed or was forgotten.</summary>
        public static event Action<string> Changed;

        /// <summary>Changes whenever any size does: sorted views that sort folders by size are rebuilt.</summary>
        public static long Version => Interlocked.Read(ref s_version);

        public static bool TryGet(string folder, out FolderSizeInfo info)
        {
            info = default;
            return !string.IsNullOrEmpty(folder) && Known.TryGetValue(PathUtil.TrimTrailingSeparator(folder), out info);
        }

        /// <summary>The counted bytes of a folder, or -1 when it hasn't been counted (or the count is still running).</summary>
        public static long BytesOrUnknown(string folder) =>
            TryGet(folder, out FolderSizeInfo info) && info.IsComplete ? info.Bytes : -1;

        public static void Set(string folder, FolderSizeInfo info)
        {
            Known[PathUtil.TrimTrailingSeparator(folder)] = info;
            Interlocked.Increment(ref s_version);
        }

        public static void NotifyChanged(string folder) => Changed?.Invoke(folder);

        /// <summary>
        /// Something changed inside <paramref name="folder"/>: its size and every folder's above it are forgotten (they
        /// all contain the change). Its subfolders keep theirs.
        /// </summary>
        public static void Invalidate(string folder)
        {
            for (string path = PathUtil.TrimTrailingSeparator(folder); !string.IsNullOrEmpty(path); path = PathUtil.GetParent(path))
            {
                if (Known.TryRemove(path, out _))
                {
                    Interlocked.Increment(ref s_version);
                    Changed?.Invoke(path);
                }
            }
        }

        /// <summary>Forgets one folder's size (a count that was cancelled), not its ancestors'.</summary>
        public static void ForgetOnly(string folder)
        {
            if (Known.TryRemove(PathUtil.TrimTrailingSeparator(folder), out _))
            {
                Interlocked.Increment(ref s_version);
                Changed?.Invoke(folder);
            }
        }

        /// <summary>Forgets the sizes of the folders directly inside <paramref name="folder"/> (a refresh recounts them).</summary>
        public static void ForgetChildren(string folder)
        {
            foreach (string key in Known.Keys)
            {
                string parent = PathUtil.GetParent(key);
                if (parent != null && PathUtil.AreSame(parent, folder) && Known.TryRemove(key, out _))
                {
                    Interlocked.Increment(ref s_version);
                    Changed?.Invoke(key);
                }
            }
        }

        internal static void ClearForTests()
        {
            Known.Clear();
            Interlocked.Increment(ref s_version);
        }

        /// <summary>
        /// Counts a folder's files, subfolders and bytes. <paramref name="progress"/> gets the running totals now and then
        /// (about every 100 ms, on the counting thread). Throws OperationCanceledException when cancelled.
        /// </summary>
        public static FolderSizeInfo Calculate(string folder, CancellationToken cancellationToken, Action<FolderSizeInfo> progress = null)
        {
            long bytes = 0;
            long files = 0;
            long folders = 0;
            int unreadable = 0;
            long lastReport = Environment.TickCount64;
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                try
                {
                    foreach (DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(current, cancellationToken))
                    {
                        if (entry.IsDirectory)
                        {
                            string child = Path.Combine(current, entry.Name);
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && NativeFileOperations.IsLink(child))
                            {
                                continue; // a link or junction: what it points to is counted where it really is
                            }

                            folders++;
                            pending.Push(child);
                        }
                        else
                        {
                            files++;
                            bytes += Math.Max(0, entry.SizeBytes);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    if (ReferenceEquals(current, folder))
                    {
                        throw; // the folder itself can't be read: no size to show
                    }

                    unreadable++;
                }

                long now = Environment.TickCount64;
                if (progress != null && now - lastReport >= 100)
                {
                    lastReport = now;
                    progress(new FolderSizeInfo(bytes, files, folders, unreadable, isComplete: false));
                }
            }

            return new FolderSizeInfo(bytes, files, folders, unreadable, isComplete: true);
        }

        /// <summary>"1.2 GB", or "≥ 1.2 GB" when some subfolders couldn't be read; "1.2 GB…" while counting.</summary>
        public static string Format(FolderSizeInfo info)
        {
            string size = FormatBytes(info.Bytes);
            return !info.IsComplete ? size + "…" : info.Unreadable > 0 ? "≥ " + size : size;
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:N0} {1}", bytes, units[0])
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:N1} {1}", size, units[unit]);
        }
    }
}
