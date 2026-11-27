// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using ByteExplorer.Core;
using Serilog;

namespace ByteExplorer.Infrastructure.Search
{
    /// <summary>
    /// Watches the indexed roots for changes and reports the folders whose contents changed (a created, deleted or renamed
    /// item's folder; a renamed or deleted folder itself), so the index can update just those (SearchIndex.ApplyChanges).
    /// One recursive FileSystemWatcher per root: on Windows (ReadDirectoryChangesW) and macOS (FSEvents) watching a whole
    /// tree is one kernel object. On Linux it costs an inotify watch per folder, so <see cref="CanWatch"/> allows it only
    /// when the index's folders fit well within the system limit.
    /// <para>
    /// A watcher whose buffer overflows (a burst of thousands of changes) reports <see cref="Overflowed"/>: the index may
    /// then have missed changes, and the owner schedules a full build. Events inside <see cref="IgnoredFolder"/> (the
    /// index's own folder, whose saves would otherwise feed back as changes) are dropped. Handlers run on thread-pool
    /// threads.
    /// </para>
    /// </summary>
    public sealed class IndexChangeWatcher : IDisposable
    {
        private static readonly ILogger Logger = Log.Logger.ForContext<IndexChangeWatcher>();
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Action<IReadOnlyList<string>> _foldersChanged;
        private readonly Action _overflowed;

        public IndexChangeWatcher(IEnumerable<string> roots, string ignoredFolder, Action<IReadOnlyList<string>> foldersChanged, Action overflowed)
        {
            IgnoredFolder = string.IsNullOrEmpty(ignoredFolder) ? null : PathUtil.TrimTrailingSeparator(ignoredFolder);
            _foldersChanged = foldersChanged ?? throw new ArgumentNullException(nameof(foldersChanged));
            _overflowed = overflowed;
            foreach (string root in roots ?? Array.Empty<string>())
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024
                    };
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Changed += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException)
                {
                    Logger.Information(ex, "Changes under {Root} are not watched; the index updates there on the next build", root);
                }
            }
        }

        /// <summary>A folder whose changes are ignored (the index's own).</summary>
        public string IgnoredFolder { get; }

        public int WatchedRoots => _watchers.Count;

        /// <summary>
        /// Whether watching is affordable for an index of <paramref name="folders"/> folders: always on Windows and macOS;
        /// on Linux only when the folders take at most half of fs.inotify.max_user_watches (one watch per folder).
        /// </summary>
        public static bool CanWatch(long folders)
        {
            if (!OperatingSystem.IsLinux())
            {
                return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
            }

            try
            {
                string text = File.ReadAllText("/proc/sys/fs/inotify/max_user_watches").Trim();
                return long.TryParse(text, out long limit) && folders <= limit / 2;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
            {
                return; // a folder's own timestamp moved: its changed children are reported on their own
            }

            Report(e.FullPath, e.ChangeType == WatcherChangeTypes.Deleted);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Report(e.OldFullPath, true);
            Report(e.FullPath, false);
        }

        private void Report(string path, bool removed)
        {
            if (string.IsNullOrEmpty(path) || IsIgnored(path))
            {
                return;
            }

            string parent = PathUtil.GetParent(path);
            var folders = new List<string>(2);
            if (parent != null)
            {
                folders.Add(parent);
            }

            if (!removed && Directory.Exists(path))
            {
                folders.Add(path); // a new or renamed folder: its contents too
            }

            if (folders.Count > 0)
            {
                try
                {
                    _foldersChanged(folders);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Index change handler failed");
                }
            }
        }

        private bool IsIgnored(string path) => IgnoredFolder != null && PathUtil.IsSameOrInside(path, IgnoredFolder);

        private void OnError(object sender, ErrorEventArgs e)
        {
            Logger.Information(e.GetException(), "A change watcher lost events (buffer overflow or the drive went away)");
            try
            {
                _overflowed?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Index overflow handler failed");
            }
        }

        public void Dispose()
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnChanged;
                    watcher.Deleted -= OnChanged;
                    watcher.Changed -= OnChanged;
                    watcher.Renamed -= OnRenamed;
                    watcher.Error -= OnError;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Disposing a change watcher failed");
                }
            }

            _watchers.Clear();
        }
    }
}
