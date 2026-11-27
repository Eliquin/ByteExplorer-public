// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteSearcher
{
    public sealed partial class SearchIndex
    {
        private const int MaxDirectoryDepth = 50;
        private const int MinNetworkWorkers = 4;
        private const int MaxWorkers = 64;

        /// <summary>One folder waiting to be listed: its path (with a trailing separator), id, depth and hidden state.</summary>
        private readonly record struct FolderWork(string Path, int Id, byte Depth, bool Hidden);

        /// <summary>Receives one directory entry of a listing (the name is only valid during the call).</summary>
        private delegate void EntryVisitor(ReadOnlySpan<char> name, bool isDirectory, bool isLink, bool isHidden, bool isSystem, long size, long ticks);

        /// <summary>
        /// Builds a new index of <paramref name="restrictToRoots"/> (or every available root, see
        /// <see cref="GetAvailableRoots"/>) plus <paramref name="extraRoots"/>, saves it and (with
        /// <paramref name="loadIntoMemoryAfter"/>) puts it in place. <paramref name="progress"/> gets a line now and then.
        /// Cancelling throws OperationCanceledException and changes nothing: the previous index stays on disk and loaded.
        /// <see cref="RequestPause"/> instead stops early and keeps what was found, as a partial index.
        /// </summary>
        public async Task BuildIndexAsync(Action<string> progress = null, CancellationToken cancellationToken = default, bool loadIntoMemoryAfter = true, IEnumerable<string> extraRoots = null, IEnumerable<string> restrictToRoots = null)
        {
            IProgress<IndexBuildProgress> structured = progress == null
                ? null
                : new InlineProgress(p => progress($"Indexing: {p.Items:N0} items in {p.Folders:N0} folders"));
            IndexBuildSummary summary = await BuildAsync(structured, cancellationToken, loadIntoMemoryAfter, extraRoots, restrictToRoots).ConfigureAwait(false);
            progress?.Invoke(summary.WasPaused
                ? $"Indexing paused: {summary.Items:N0} items saved (partial index)"
                : $"Index built: {summary.Items:N0} items in {summary.Duration.TotalSeconds:F1}s");
        }

        /// <summary>The build, with structured progress (about four times a second) and a summary.</summary>
        public async Task<IndexBuildSummary> BuildAsync(IProgress<IndexBuildProgress> progress, CancellationToken cancellationToken = default, bool loadIntoMemoryAfter = true, IEnumerable<string> extraRoots = null, IEnumerable<string> restrictToRoots = null)
        {
            await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var pause = new CancellationTokenSource();
            _pauseRequested = pause;
            lock (_updateGate)
            {
                _changedDuringBuild = new HashSet<string>(PathUtil.Comparer);
            }

            try
            {
                List<string> roots = ResolveRoots(extraRoots, restrictToRoots);
                var stopwatch = Stopwatch.StartNew();
                var crawl = new CrawlState(this, roots, progress, cancellationToken, pause.Token, stopwatch);
                IndexData data = await Task.Run(crawl.Run, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                bool paused = pause.IsCancellationRequested;
                data.BuildDateUtc = DateTime.UtcNow;
                data.BuildDuration = stopwatch.Elapsed;
                data.IsPartial = paused;
                data.Roots = roots.ToArray();

                await Task.Run(() => IndexStorage.Save(data, IndexPath), cancellationToken).ConfigureAwait(false);
                DeleteLegacyFiles();

                HashSet<string> replay;
                lock (_updateGate)
                {
                    if (loadIntoMemoryAfter)
                    {
                        Publish(data);
                        _dirty = false;
                    }

                    replay = _changedDuringBuild;
                    _changedDuringBuild = null;
                }

                if (loadIntoMemoryAfter)
                {
                    RaiseIndexChanged();
                    if (replay != null && replay.Count > 0)
                    {
                        ApplyChanges(replay, cancellationToken); // folders that changed while the crawl was elsewhere
                    }
                }

                return new IndexBuildSummary(data.LiveCount, data.Files, data.Folders, stopwatch.Elapsed, paused, crawl.UnreadableFolders, roots);
            }
            finally
            {
                lock (_updateGate)
                {
                    _changedDuringBuild = null;
                }

                _pauseRequested = null;
                pause.Dispose();
                _buildGate.Release();
            }
        }

        private List<string> ResolveRoots(IEnumerable<string> extraRoots, IEnumerable<string> restrictToRoots)
        {
            var roots = restrictToRoots != null
                ? restrictToRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => PathUtil.TrimTrailingSeparator(r.Trim())).ToList()
                : GetAvailableRoots().Select(r => r.Root).ToList();
            foreach (string extra in extraRoots ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    roots.Add(PathUtil.TrimTrailingSeparator(extra.Trim()));
                }
            }

            // No duplicates and no root inside another: a path must belong to exactly one root.
            var result = new List<string>();
            foreach (string root in roots.Distinct(PathUtil.Comparer).OrderBy(r => r.Length))
            {
                if (!result.Any(existing => PathUtil.IsSameOrInside(root, existing)))
                {
                    result.Add(root);
                }
            }

            return result;
        }

        /// <summary>
        /// Local fixed, removable and network drives on Windows; on Linux and macOS the home folder plus every mounted data
        /// volume (not "/": a whole-system crawl would index /proc, /sys and the package manager's files, none of which a
        /// person looks for in a file explorer).
        /// </summary>
        public List<RootInfo> GetAvailableRoots() => OperatingSystem.IsWindows() ? GetWindowsDriveRoots() : GetUnixRoots();

        private static List<RootInfo> GetWindowsDriveRoots()
        {
            var result = new List<RootInfo>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    RootKind kind;
                    switch (drive.DriveType)
                    {
                        case DriveType.Fixed: kind = RootKind.Local; break;
                        case DriveType.Removable: kind = RootKind.Removable; break;
                        case DriveType.Network: kind = RootKind.Network; break;
                        default: continue;
                    }

                    string root = drive.Name.TrimEnd('\\');
                    string label;
                    try
                    {
                        label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? DefaultLabelFor(kind) : drive.VolumeLabel;
                    }
                    catch (Exception)
                    {
                        label = DefaultLabelFor(kind);
                    }

                    result.Add(new RootInfo { Root = root, DisplayLabel = $"{label} ({root})", Kind = kind });
                }
                catch (Exception)
                {
                    // A drive that is not answering (a mapped share whose VPN is down) is skipped.
                }
            }

            return result;
        }

        private static List<RootInfo> GetUnixRoots()
        {
            var result = new List<RootInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            {
                result.Add(new RootInfo { Root = PathUtil.TrimTrailingSeparator(home), DisplayLabel = $"Home ({home})", Kind = RootKind.Local });
            }

            foreach (var volume in ByteExplorer.Core.Platform.MountedVolumes.GetDataVolumes())
            {
                if (result.Any(r => PathUtil.IsSameOrInside(volume.RootPath, r.Root) || PathUtil.IsInside(r.Root, volume.RootPath)))
                {
                    continue;
                }

                RootKind kind = volume.Kind == ByteExplorer.Core.Platform.VolumeKind.Network ? RootKind.Network
                    : volume.Kind == ByteExplorer.Core.Platform.VolumeKind.Removable ? RootKind.Removable
                    : RootKind.Local;
                result.Add(new RootInfo { Root = volume.RootPath, DisplayLabel = $"{volume.Label} ({volume.RootPath})", Kind = kind });
            }

            return result;
        }

        private static string DefaultLabelFor(RootKind kind) => kind switch
        {
            RootKind.Removable => "Removable Disk",
            RootKind.Network => "Network Drive",
            _ => "Local Disk"
        };

        /// <summary>
        /// Local disks: about one worker per core (up to 8); listing a local folder is a fast system call, and more threads
        /// only contend. Network roots: more, since each listing waits on a round trip, but capped so a modest server or
        /// VPN is not flooded (<see cref="EnumerationWorkerCount"/> overrides it).
        /// </summary>
        private int ResolveWorkerCount(IReadOnlyList<string> roots)
        {
            bool anyNetwork = roots.Any(IsNetworkPath);
            if (!anyNetwork)
            {
                return Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
            }

            if (EnumerationWorkerCount.HasValue)
            {
                return Math.Clamp(EnumerationWorkerCount.Value, 1, MaxWorkers);
            }

            return Math.Max(MinNetworkWorkers, Math.Min(32, Environment.ProcessorCount * 2));
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                return ByteExplorer.Core.Platform.MountedVolumes.IsNetworkPath(path);
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                return new DriveInfo(path).DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ exclusions

        private string[] _excludedPrefixes = Array.Empty<string>();
        private HashSet<string> _excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _excludedNameLookup;

        /// <summary>Snapshots the exclusions for a crawl or a live update (the lists may be edited between them).</summary>
        private void PrepareExclusions()
        {
            _excludedNames = new HashSet<string>(ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
            _excludedNameLookup = _excludedNames.GetAlternateLookup<ReadOnlySpan<char>>();
            // Files opened from password-protected items are decrypted copies: never indexed, whatever the settings say.
            _excludedPrefixes = ExcludedPathPrefixes
                .Append(ByteExplorer.Core.Security.ProtectedItems.TempRoot)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => PathUtil.TrimTrailingSeparator(p.Trim()))
                .ToArray();
        }

        /// <summary>The path is an excluded folder or inside one (a sibling sharing the text, "Builds2" for "Builds", is not).</summary>
        private bool IsExcludedPath(ReadOnlySpan<char> fullPath)
        {
            foreach (string prefix in _excludedPrefixes)
            {
                if (fullPath.Length >= prefix.Length && fullPath.Slice(0, prefix.Length).Equals(prefix, PathUtil.Comparison) &&
                    (fullPath.Length == prefix.Length || PathUtil.IsSeparator(fullPath[prefix.Length]) || PathUtil.IsSeparator(prefix[^1])))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ listing one folder

        /// <summary>
        /// Lists one folder, calling <paramref name="visitor"/> per entry (no "." or ".."). False when the folder cannot be
        /// read. Windows uses FindFirstFileExW with the basic info level and large fetch (no 8.3 names, fewer round trips on
        /// network shares); other systems FileSystemEnumerable (readdir + lstat).
        /// </summary>
        private static bool ListFolder(string folder, EntryVisitor visitor, CancellationToken cancellationToken, CancellationToken pause)
        {
            return OperatingSystem.IsWindows()
                ? ListFolderWin32(folder, visitor, cancellationToken, pause)
                : ListFolderManaged(folder, visitor, cancellationToken, pause);
        }

        private static readonly EnumerationOptions ManagedListOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            BufferSize = 64 * 1024
        };

        private readonly record struct ManagedEntry(string Name, bool IsDirectory, bool IsLink, long Size, long Ticks);

        private static bool ListFolderManaged(string folder, EntryVisitor visitor, CancellationToken cancellationToken, CancellationToken pause)
        {
            IEnumerator<ManagedEntry> entries;
            try
            {
                entries = new System.IO.Enumeration.FileSystemEnumerable<ManagedEntry>(folder, (ref System.IO.Enumeration.FileSystemEntry e) =>
                {
                    bool isLink = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                    long size = 0;
                    long ticks = 0;
                    try
                    {
                        if (!e.IsDirectory)
                        {
                            size = e.Length;
                        }

                        ticks = e.LastWriteTimeUtc.UtcTicks;
                    }
                    catch (Exception)
                    {
                        // A dangling link or an entry removed meanwhile: no size or date.
                    }

                    return new ManagedEntry(e.FileName.ToString(), e.IsDirectory, isLink, size, ticks);
                }, ManagedListOptions).GetEnumerator();
            }
            catch (Exception)
            {
                return false;
            }

            using (entries)
            {
                while (!cancellationToken.IsCancellationRequested && !pause.IsCancellationRequested)
                {
                    try
                    {
                        if (!entries.MoveNext())
                        {
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        break; // the folder vanished or became unreadable mid-listing
                    }

                    ManagedEntry entry = entries.Current;
                    visitor(entry.Name, entry.IsDirectory, entry.IsLink, entry.Name.Length > 1 && entry.Name[0] == '.', false, entry.Size, entry.Ticks);
                }
            }

            return true;
        }

        private static bool ListFolderWin32(string folder, EntryVisitor visitor, CancellationToken cancellationToken, CancellationToken pause)
        {
            IntPtr handle = FindFirstFileExW(folder + "*", FindExInfoBasic, out WIN32_FIND_DATAW data, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
            if (handle == InvalidHandleValue)
            {
                return false;
            }

            try
            {
                do
                {
                    if (cancellationToken.IsCancellationRequested || pause.IsCancellationRequested)
                    {
                        break;
                    }

                    string name = data.cFileName;
                    if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                    {
                        continue;
                    }

                    bool isDirectory = (data.dwFileAttributes & FileAttributeDirectory) != 0;
                    bool isReparse = (data.dwFileAttributes & FileAttributeReparsePoint) != 0;

                    // Only symbolic links, junctions and mount points can make a cycle; other reparse points (cloud
                    // placeholders) are crawled: listing them does not download anything.
                    bool isLink = isReparse && (data.dwReserved0 == IoReparseTagSymlink || data.dwReserved0 == IoReparseTagMountPoint);
                    bool isHidden = (data.dwFileAttributes & (FileAttributeHidden | FileAttributeSystem)) != 0;
                    bool isSystem = (data.dwFileAttributes & FileAttributeSystem) != 0;
                    long size = isDirectory ? 0 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    long fileTime = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime;
                    long ticks = fileTime > 0 && fileTime < DateTime.MaxValue.Ticks - 504911232000000000L ? fileTime + 504911232000000000L : 0;
                    visitor(name, isDirectory, isLink, isHidden, isSystem, size, ticks);
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }

            return true;
        }

        // ------------------------------------------------------------------ the crawl

        /// <summary>
        /// One build's crawl: a shared stack of folders, a pool of workers (each with its own batch), counters for progress.
        /// Idle workers wait on a semaphore (no spinning, no Sleep(1) that waits 15 ms on Windows' timer).
        /// </summary>
        private sealed class CrawlState
        {
            private readonly SearchIndex _owner;
            private readonly List<string> _roots;
            private readonly IProgress<IndexBuildProgress> _progress;
            private readonly CancellationToken _cancellation;
            private readonly CancellationToken _pause;
            private readonly Stopwatch _stopwatch;
            private readonly IndexBuilder _builder = new IndexBuilder();
            private readonly ConcurrentStack<FolderWork> _pending = new ConcurrentStack<FolderWork>();
            private readonly SemaphoreSlim _workAvailable = new SemaphoreSlim(0);
            private long _outstanding;
            private long _items;
            private long _folders;
            private long _unreadable;
            private string _currentFolder;

            public CrawlState(SearchIndex owner, List<string> roots, IProgress<IndexBuildProgress> progress, CancellationToken cancellation, CancellationToken pause, Stopwatch stopwatch)
            {
                _owner = owner;
                _roots = roots;
                _progress = progress;
                _cancellation = cancellation;
                _pause = pause;
                _stopwatch = stopwatch;
            }

            public long UnreadableFolders => Interlocked.Read(ref _unreadable);

            public IndexData Run()
            {
                _owner.PrepareExclusions();
                var rootBatch = new EntryBatch();
                var rootPaths = new List<string>();
                foreach (string root in _roots)
                {
                    try
                    {
                        if (!Directory.Exists(root))
                        {
                            continue;
                        }

                        long ticks = Directory.GetLastWriteTimeUtc(root).Ticks;
                        rootBatch.Add(root, -1, EntryFlags.Directory, 0, 0, ticks);
                        rootPaths.Add(root);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref _unreadable);
                    }
                }

                if (rootBatch.Count > 0)
                {
                    int first = _builder.Append(rootBatch);
                    for (int i = 0; i < rootPaths.Count; i++)
                    {
                        Push(new FolderWork(PathUtil.WithTrailingSeparator(rootPaths[i]), first + i, 0, false));
                    }
                }

                int workerCount = _owner.ResolveWorkerCount(_roots);
                var workers = new Thread[workerCount];
                Exception failure = null;
                for (int i = 0; i < workerCount; i++)
                {
                    workers[i] = new Thread(() =>
                    {
                        try
                        {
                            Work();
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "ByteExplorer.Indexer",
                        Priority = ThreadPriority.BelowNormal
                    };
                    workers[i].Start();
                }

                // Progress from here, a few times a second, while the workers run.
                while (!workers.All(w => w.Join(250)))
                {
                    Report();
                }

                Report();
                _cancellation.ThrowIfCancellationRequested();
                if (failure != null)
                {
                    throw new IOException("Indexing failed: " + failure.Message, failure);
                }

                return _builder.Finish();
            }

            private void Report() =>
                _progress?.Report(new IndexBuildProgress(Interlocked.Read(ref _items), Interlocked.Read(ref _folders), Volatile.Read(ref _currentFolder), _stopwatch.Elapsed, Interlocked.Read(ref _unreadable)));

            private void Push(FolderWork work)
            {
                Interlocked.Increment(ref _outstanding);
                _pending.Push(work);
                _workAvailable.Release();
            }

            private void Work()
            {
                var batch = new EntryBatch();
                var subfolders = new List<(int Slot, string Path, bool Hidden)>();
                while (!_cancellation.IsCancellationRequested && !_pause.IsCancellationRequested)
                {
                    if (!_pending.TryPop(out FolderWork work))
                    {
                        if (Interlocked.Read(ref _outstanding) <= 0)
                        {
                            return;
                        }

                        _workAvailable.Wait(50, _cancellation);
                        continue;
                    }

                    try
                    {
                        Volatile.Write(ref _currentFolder, work.Path);
                        if (!ListInto(work, batch, subfolders))
                        {
                            Interlocked.Increment(ref _unreadable);
                        }

                        Flush(work, batch, subfolders);
                        Interlocked.Increment(ref _folders);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _outstanding);
                    }
                }
            }

            private bool ListInto(FolderWork work, EntryBatch batch, List<(int Slot, string Path, bool Hidden)> subfolders)
            {
                SearchIndex owner = _owner;
                bool hasPrefixes = owner._excludedPrefixes.Length > 0;
                byte childDepth = (byte)Math.Min(work.Depth + 1, 255);
                return ListFolder(work.Path, (ReadOnlySpan<char> name, bool isDirectory, bool isLink, bool isHidden, bool isSystem, long size, long ticks) =>
                {
                    if (owner._excludedNames.Count > 0 && owner._excludedNameLookup.Contains(name))
                    {
                        return;
                    }

                    string fullPath = null;
                    if (hasPrefixes && owner.IsExcludedPath(fullPath = string.Concat(work.Path, name)))
                    {
                        return;
                    }

                    bool hidden = work.Hidden || isHidden;
                    EntryFlags flags = (isDirectory ? EntryFlags.Directory : EntryFlags.None) | (isLink ? EntryFlags.Link : EntryFlags.None) |
                                       (hidden ? EntryFlags.Hidden : EntryFlags.None) | (isSystem ? EntryFlags.System : EntryFlags.None);
                    int slot = batch.Add(name, work.Id, flags, childDepth, isDirectory ? 0 : size, ticks);
                    if (isDirectory && !isLink && childDepth < MaxDirectoryDepth)
                    {
                        subfolders.Add((slot, PathUtil.WithTrailingSeparator(fullPath ?? string.Concat(work.Path, name)), hidden));
                    }

                    if (batch.IsFull)
                    {
                        Flush(work, batch, subfolders);
                    }
                }, _cancellation, _pause);
            }

            /// <summary>Appends the batch and queues its subfolders with their new ids.</summary>
            private void Flush(FolderWork work, EntryBatch batch, List<(int Slot, string Path, bool Hidden)> subfolders)
            {
                if (batch.Count == 0)
                {
                    return;
                }

                int first = _builder.Append(batch);
                Interlocked.Add(ref _items, batch.Count);
                byte childDepth = (byte)Math.Min(work.Depth + 1, 255);
                foreach ((int slot, string path, bool hidden) in subfolders)
                {
                    Push(new FolderWork(path, first + slot, childDepth, hidden));
                }

                batch.Clear();
                subfolders.Clear();
            }
        }

        private sealed class InlineProgress : IProgress<IndexBuildProgress>
        {
            private readonly Action<IndexBuildProgress> _report;

            public InlineProgress(Action<IndexBuildProgress> report) => _report = report;

            public void Report(IndexBuildProgress value) => _report(value);
        }

        // ------------------------------------------------------------------ Win32

        private const uint FileAttributeHidden = 0x2;
        private const uint FileAttributeSystem = 0x4;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint IoReparseTagSymlink = 0xA000000C;
        private const uint IoReparseTagMountPoint = 0xA0000003;
        private const int FindExInfoBasic = 1;
        private const int FindExSearchNameMatch = 0;
        private const uint FindFirstExLargeFetch = 0x2;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0; // the reparse tag, for a reparse point
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, uint dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);
    }

    /// <summary>What a build produced.</summary>
    public readonly record struct IndexBuildSummary(long Items, long Files, long Folders, TimeSpan Duration, bool WasPaused, long UnreadableFolders, IReadOnlyList<string> Roots);
}
