// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ByteExplorer.Core;

namespace ByteSearcher
{
    public sealed partial class SearchIndex
    {
        /// <summary>A live update crawls at most this many entries under new folders; past it a full build is asked for.</summary>
        private const int MaxLiveCrawlEntries = 200_000;

        /// <summary>
        /// Brings the given folders up to date: each is listed again and compared with what the index holds. New entries are
        /// added (new folders crawled, up to <see cref="MaxLiveCrawlEntries"/>), vanished ones marked deleted (with
        /// everything under them), changed sizes and dates updated. A folder the index does not have is handled through its
        /// nearest indexed parent; a folder outside the indexed roots is ignored. Returns how many entries changed. The new
        /// state is swapped in at once; <see cref="SaveIfChanged"/> persists it. Thread-safe; called from a background
        /// thread (it lists folders).
        /// </summary>
        public int ApplyChanges(IEnumerable<string> folders, CancellationToken cancellationToken = default)
        {
            if (folders == null)
            {
                return 0;
            }

            var targets = folders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => PathUtil.TrimTrailingSeparator(f.Trim()))
                .Distinct(PathUtil.Comparer)
                .OrderBy(f => f.Length) // parents first: a folder created inside another listed one is found as new there
                .ToList();
            if (targets.Count == 0)
            {
                return 0;
            }

            int changed;
            lock (_updateGate)
            {
                _changedDuringBuild?.UnionWith(targets);
                IndexData data = _data;
                if (data == null)
                {
                    return 0;
                }

                PrepareExclusions();
                var update = new LiveUpdate(this, data, cancellationToken);
                foreach (string folder in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    update.Refresh(folder);
                }

                if (update.ChangedEntries == 0)
                {
                    return 0;
                }

                IndexData next = update.Finish();
                Publish(next);
                _dirty = true;
                if (update.CrawlLimitReached)
                {
                    _needsRebuild = true;
                }

                changed = update.ChangedEntries;
            }

            RaiseIndexChanged();
            return changed;
        }

        /// <summary>
        /// One live update: a builder continuing the index (appended entries), the chunks copied so far (changed flags,
        /// sizes, dates), and the folders already refreshed.
        /// </summary>
        private sealed class LiveUpdate
        {
            private readonly SearchIndex _owner;
            private readonly IndexData _data;
            private readonly CancellationToken _cancellation;
            private readonly IndexBuilder _builder;
            private readonly HashSet<int> _copiedChunks = new HashSet<int>();
            private readonly HashSet<int> _refreshed = new HashSet<int>();
            private readonly EntryBatch _batch = new EntryBatch();
            private int _crawled;

            public LiveUpdate(SearchIndex owner, IndexData data, CancellationToken cancellation)
            {
                _owner = owner;
                _data = data;
                _cancellation = cancellation;
                _builder = IndexBuilder.ContinueFrom(data);
                if (data.Chunks.Length > 0 && !data.Chunks[^1].IsFull)
                {
                    _copiedChunks.Add(data.Chunks.Length - 1); // ContinueFrom copied the partly filled last chunk
                }
            }

            public int ChangedEntries { get; private set; }

            public bool CrawlLimitReached { get; private set; }

            public void Refresh(string folder)
            {
                int id = FindEntry(_data, folder);
                if (id < 0)
                {
                    // Not indexed (new, or under a new folder): refresh the nearest indexed folder above it, which finds it.
                    for (string parent = PathUtil.GetParent(folder); parent != null; parent = PathUtil.GetParent(parent))
                    {
                        int parentId = FindEntry(_data, parent);
                        if (parentId >= 0)
                        {
                            RefreshFolder(parentId, parent);
                            return;
                        }
                    }

                    return; // outside every indexed root
                }

                RefreshFolder(id, folder);
            }

            private void RefreshFolder(int id, string folder)
            {
                if (!_refreshed.Add(id) || (_data.FlagsOf(id) & EntryFlags.Directory) == 0)
                {
                    return;
                }

                if (!Directory.Exists(folder))
                {
                    if (_data.ParentOf(id) >= 0)
                    {
                        MarkDeleted(id); // gone: its whole subtree goes with it
                    }

                    return;
                }

                // What the index holds now, by name.
                var existing = new Dictionary<string, int>(PathUtil.Comparer);
                foreach (int child in _data.Children(id))
                {
                    if ((_data.FlagsOf(child) & EntryFlags.Deleted) == 0)
                    {
                        existing[_data.GetName(child)] = child;
                    }
                }

                bool parentHidden = (_data.FlagsOf(id) & EntryFlags.Hidden) != 0;
                byte childDepth = (byte)Math.Min(_data.ChunkOf(id).Depth[id & IndexChunk.Mask] + 1, 255);
                string prefix = PathUtil.WithTrailingSeparator(folder);
                var added = new List<(string Name, bool IsDirectory, bool IsLink, bool Hidden, bool System, long Size, long Ticks)>();
                bool listed = ListFolder(prefix, (ReadOnlySpan<char> name, bool isDirectory, bool isLink, bool isHidden, bool isSystem, long size, long ticks) =>
                {
                    if (_owner._excludedNames.Count > 0 && _owner._excludedNameLookup.Contains(name))
                    {
                        return;
                    }

                    string text = name.ToString();
                    if (_owner._excludedPrefixes.Length > 0 && _owner.IsExcludedPath(prefix + text))
                    {
                        return;
                    }

                    if (existing.Remove(text, out int child))
                    {
                        bool wasDirectory = (_data.FlagsOf(child) & EntryFlags.Directory) != 0;
                        if (wasDirectory != isDirectory)
                        {
                            MarkDeleted(child); // a folder replaced by a file of the same name, or the reverse
                            added.Add((text, isDirectory, isLink, parentHidden || isHidden, isSystem, size, ticks));
                        }
                        else
                        {
                            Update(child, isDirectory ? 0 : size, ticks);
                        }
                    }
                    else
                    {
                        added.Add((text, isDirectory, isLink, parentHidden || isHidden, isSystem, size, ticks));
                    }
                }, _cancellation, CancellationToken.None);

                if (!listed)
                {
                    return; // unreadable now: keep what the index has rather than dropping it
                }

                foreach (int gone in existing.Values)
                {
                    MarkDeleted(gone);
                }

                try
                {
                    Update(id, 0, Directory.GetLastWriteTimeUtc(folder).Ticks);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }

                if (added.Count == 0)
                {
                    return;
                }

                var newFolders = new List<FolderWork>();
                AppendEntries(id, prefix, childDepth, added, newFolders);
                CrawlNewFolders(newFolders);
            }

            /// <summary>Appends entries under <paramref name="parentId"/>; collects the new folders to crawl.</summary>
            private void AppendEntries(int parentId, string prefix, byte depth, List<(string Name, bool IsDirectory, bool IsLink, bool Hidden, bool System, long Size, long Ticks)> entries, List<FolderWork> newFolders)
            {
                var folderSlots = new List<(int Slot, string Path, bool Hidden)>();
                foreach (var entry in entries)
                {
                    EntryFlags flags = (entry.IsDirectory ? EntryFlags.Directory : EntryFlags.None) | (entry.IsLink ? EntryFlags.Link : EntryFlags.None) |
                                       (entry.Hidden ? EntryFlags.Hidden : EntryFlags.None) | (entry.System ? EntryFlags.System : EntryFlags.None);
                    int slot = _batch.Add(entry.Name, parentId, flags, depth, entry.IsDirectory ? 0 : entry.Size, entry.Ticks);
                    if (entry.IsDirectory && !entry.IsLink && depth < MaxDirectoryDepth)
                    {
                        folderSlots.Add((slot, PathUtil.WithTrailingSeparator(prefix + entry.Name), entry.Hidden));
                    }

                    if (_batch.IsFull)
                    {
                        FlushBatch(folderSlots, depth, newFolders);
                    }
                }

                FlushBatch(folderSlots, depth, newFolders);
            }

            private void FlushBatch(List<(int Slot, string Path, bool Hidden)> folderSlots, byte depth, List<FolderWork> newFolders)
            {
                if (_batch.Count == 0)
                {
                    return;
                }

                int first = _builder.Append(_batch);
                ChangedEntries += _batch.Count;
                _crawled += _batch.Count;
                foreach ((int slot, string path, bool hidden) in folderSlots)
                {
                    newFolders.Add(new FolderWork(path, first + slot, depth, hidden));
                }

                _batch.Clear();
                folderSlots.Clear();
            }

            /// <summary>Crawls folders that appeared (a copied or extracted tree), depth first, within the live-update budget.</summary>
            private void CrawlNewFolders(List<FolderWork> folders)
            {
                var stack = new Stack<FolderWork>(folders);
                while (stack.Count > 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (_crawled > MaxLiveCrawlEntries)
                    {
                        CrawlLimitReached = true;
                        return;
                    }

                    FolderWork work = stack.Pop();
                    byte depth = (byte)Math.Min(work.Depth + 1, 255);
                    var entries = new List<(string Name, bool IsDirectory, bool IsLink, bool Hidden, bool System, long Size, long Ticks)>();
                    ListFolder(work.Path, (ReadOnlySpan<char> name, bool isDirectory, bool isLink, bool isHidden, bool isSystem, long size, long ticks) =>
                    {
                        if (_owner._excludedNames.Count > 0 && _owner._excludedNameLookup.Contains(name))
                        {
                            return;
                        }

                        string text = name.ToString();
                        if (_owner._excludedPrefixes.Length == 0 || !_owner.IsExcludedPath(work.Path + text))
                        {
                            entries.Add((text, isDirectory, isLink, work.Hidden || isHidden, isSystem, size, ticks));
                        }
                    }, _cancellation, CancellationToken.None);

                    var found = new List<FolderWork>();
                    AppendEntries(work.Id, work.Path, depth, entries, found);
                    foreach (FolderWork child in found)
                    {
                        stack.Push(child);
                    }
                }
            }

            private void MarkDeleted(int id)
            {
                IndexChunk chunk = Writable(id);
                int slot = id & IndexChunk.Mask;
                if ((chunk.Flags[slot] & (byte)EntryFlags.Deleted) == 0)
                {
                    chunk.Flags[slot] |= (byte)EntryFlags.Deleted;
                    ChangedEntries++;
                }
            }

            private void Update(int id, long size, long ticks)
            {
                IndexChunk current = _data.ChunkOf(id);
                int slot = id & IndexChunk.Mask;
                if (current.Size[slot] == size && current.Ticks[slot] == ticks)
                {
                    return;
                }

                IndexChunk chunk = Writable(id);
                chunk.Size[slot] = size;
                chunk.Ticks[slot] = ticks;
                ChangedEntries++;
            }

            /// <summary>The chunk holding <paramref name="id"/>, copied once per update before its first change.</summary>
            private IndexChunk Writable(int id)
            {
                int index = id >> IndexChunk.Shift;
                if (_copiedChunks.Add(index))
                {
                    _builder.ReplaceChunk(index, _builder.ChunkAt(index).CloneColumns());
                }

                return _builder.ChunkAt(index);
            }

            public IndexData Finish()
            {
                IndexData next = _builder.Finish();
                next.BuildDateUtc = _data.BuildDateUtc;
                next.BuildDuration = _data.BuildDuration;
                next.IsPartial = _data.IsPartial;
                next.Roots = _data.Roots;
                return next;
            }
        }
    }
}
