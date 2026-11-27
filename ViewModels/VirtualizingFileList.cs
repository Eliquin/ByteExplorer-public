// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The engine behind "a million items, only ~500 resident at once."
    ///
    /// Backed by a DirectoryIndex (cheap struct per entry, covers the
    /// whole folder — this is what makes Count and random-access
    /// indexing instant, so the scrollbar is correctly sized and a
    /// scrollbar drag to the middle of a million-item folder jumps
    /// there immediately rather than needing to load everything before
    /// it). On top of that, this class implements IList directly so
    /// WPF's virtualizing panel can index into it — every this[index]
    /// call either returns an already-hydrated FileListItemViewModel or
    /// builds one on the spot (cheap: see FileListItemViewModel) and
    /// adds it to a bounded LRU cache, evicting the least-recently-
    /// touched entry once the cache exceeds the configured window.
    ///
    /// Count is fixed for the lifetime of an instance — a fresh
    /// DirectoryIndex is built and a new VirtualizingFileList
    /// constructed for every folder navigation, rather than mutating
    /// one in place. That keeps this class simple and keeps WPF's
    /// virtualization behavior predictable.
    ///
    /// Thread safety: hydration state (_hydrated, the LRU list/index)
    /// is protected by _gate. This didn't matter when the indexer was
    /// the only thing touching it — WPF always calls ItemsSource
    /// indexers on the UI thread. It matters now that RequestPrefetch
    /// hydrates rows on a background thread ahead of where the user is
    /// scrolling: without the lock, a background prefetch and a UI-
    /// thread indexer access could race on the same dictionaries. The
    /// lock is held only for fast in-memory dictionary/list operations
    /// (constructing a FileListItemViewModel is pure string formatting,
    /// no I/O), so contention cost is negligible.
    /// </summary>
    public sealed class VirtualizingFileList : IList, INotifyCollectionChanged
    {
        private readonly DirectoryIndex _index;
        private readonly string _parentPath;
        private readonly int _residentWindow;
        private readonly int _prefetchBufferSize;
        private readonly ILogger _logger;
        private static int s_lastId;

        /// <summary>Identifies this list to the items it creates (see FileListItemViewModel.OwnerListId).</summary>
        private readonly int _id = Interlocked.Increment(ref s_lastId);
        private readonly object _gate = new object();
        private readonly Dictionary<int, FileListItemViewModel> _hydrated = new Dictionary<int, FileListItemViewModel>();
        private readonly LinkedList<int> _lruOrder = new LinkedList<int>();
        private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new Dictionary<int, LinkedListNode<int>>();
        private CancellationTokenSource _prefetchCts;
        private Dictionary<string, int> _indexByName;

        /// <summary>
        /// Computed once here rather than left for the status bar to
        /// figure out — DirectoryIndex.Entries already holds the full,
        /// lightweight entry list for the whole folder (not just the
        /// hydrated window), so this single O(n) pass over already-
        /// in-memory structs is cheap even for a folder with millions
        /// of entries, and every consumer gets the same cached numbers
        /// instead of each re-scanning independently.
        /// </summary>
        public int FolderCount { get; }
        public int FileCount { get; }

        public VirtualizingFileList(DirectoryIndex index, int residentWindow, int prefetchBufferSize = 0, ILogger logger = null)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _parentPath = index.ParentPath;
            _residentWindow = Math.Max(50, residentWindow);
            _prefetchBufferSize = Math.Max(0, prefetchBufferSize);
            _logger = (logger ?? Log.Logger).ForContext<VirtualizingFileList>();

            int folderCount = 0;
            int fileCount = 0;
            foreach (DirectoryIndexEntry entry in _index.Entries)
            {
                if (entry.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                }
            }

            FolderCount = folderCount;
            FileCount = fileCount;
        }

        public int Count => _index.Count;

        /// <summary>The name of the entry at an index, read from the folder index without creating its item (type-to-select).</summary>
        public string NameAt(int index) => (uint)index < (uint)_index.Count ? _index.Entries[index].Name : null;

        public object this[int index]
        {
            get => HydrateIfNeeded(index);
            set => throw new NotSupportedException("VirtualizingFileList is read-only.");
        }

        /// <summary>
        /// Called from the view layer when the visible scroll range
        /// changes. Hydrates rows just ahead of the visible range, in
        /// the direction of travel, on a background thread — so by the
        /// time WPF's virtualizing panel actually asks for them, they're
        /// already built and this[index] resolves as a fast cache hit
        /// instead of doing formatting work synchronously on the UI
        /// thread mid-scroll.
        ///
        /// Any previous, not-yet-finished prefetch is cancelled first:
        /// only the most recent scroll position's prefetch is worth
        /// finishing, and letting stale ones keep running would waste
        /// CPU on rows the user has already scrolled past.
        /// </summary>
        public void RequestPrefetch(int firstVisibleIndex, int lastVisibleIndex, bool scrollingForward)
        {
            if (_prefetchBufferSize <= 0 || Count == 0)
            {
                return;
            }

            _prefetchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _prefetchCts = cts;

            int start;
            int end;
            if (scrollingForward)
            {
                start = Math.Max(0, lastVisibleIndex + 1);
                end = Math.Min(Count - 1, lastVisibleIndex + _prefetchBufferSize);
            }
            else
            {
                start = Math.Max(0, firstVisibleIndex - _prefetchBufferSize);
                end = Math.Max(0, firstVisibleIndex - 1);
            }

            if (start > end)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    for (int i = start; i <= end; i++)
                    {
                        if (cts.Token.IsCancellationRequested)
                        {
                            return;
                        }

                        HydrateIfNeeded(i);
                    }
                }
                catch (Exception ex)
                {
                    // Prefetch is a pure performance optimization — a
                    // failure here must never surface to the user or
                    // affect the visible list. Log and move on.
                    _logger.Debug(ex, "Prefetch failed for range [{Start},{End}]", start, end);
                }
            }, cts.Token);
        }

        private FileListItemViewModel HydrateIfNeeded(int index)
        {
            if (index < 0 || index >= _index.Count)
            {
                return null;
            }

            lock (_gate)
            {
                if (_hydrated.TryGetValue(index, out FileListItemViewModel existing))
                {
                    TouchLocked(index);
                    return existing;
                }

                var viewModel = new FileListItemViewModel(_index.Entries[index], _parentPath)
                {
                    OwnerListId = _id,
                    OwnerIndex = index,
                };
                _hydrated[index] = viewModel;
                TouchLocked(index);
                EvictIfOverCapacityLocked();
                return viewModel;
            }
        }

        /// <summary>Must only be called while holding _gate.</summary>
        /// <summary>Marks an item as just used: its LRU node moves to the end (no allocation; the indexer runs per frame).</summary>
        private void TouchLocked(int index)
        {
            if (_lruNodes.TryGetValue(index, out LinkedListNode<int> node))
            {
                if (!ReferenceEquals(node, _lruOrder.Last))
                {
                    _lruOrder.Remove(node);
                    _lruOrder.AddLast(node);
                }

                return;
            }

            _lruNodes[index] = _lruOrder.AddLast(index);
        }

        private void EvictIfOverCapacityLocked()
        {
            while (_hydrated.Count > _residentWindow && _lruOrder.Count > 0)
            {
                int oldest = _lruOrder.First.Value;
                _lruOrder.RemoveFirst();
                _lruNodes.Remove(oldest);
                _hydrated.Remove(oldest);
            }
        }

        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public int Add(object value) => throw new NotSupportedException("VirtualizingFileList is read-only.");
        public void Clear() => throw new NotSupportedException("VirtualizingFileList is read-only.");
        public void Insert(int index, object value) => throw new NotSupportedException("VirtualizingFileList is read-only.");
        public void Remove(object value) => throw new NotSupportedException("VirtualizingFileList is read-only.");
        public void RemoveAt(int index) => throw new NotSupportedException("VirtualizingFileList is read-only.");

        public bool Contains(object value) => IndexOf(value) >= 0;

        /// <summary>
        /// WPF calls this for selection, keyboard focus and ScrollIntoView. Items this list created know their
        /// index; an equal item from elsewhere (an evicted instance, the same file in another view) is found by
        /// name, through a lookup built on first use.
        /// </summary>
        public int IndexOf(object value)
        {
            if (!(value is FileListItemViewModel item))
            {
                return -1;
            }

            if (item.OwnerListId == _id)
            {
                return item.OwnerIndex;
            }

            if (!string.Equals(PathUtil.TrimTrailingSeparator(Path.GetDirectoryName(item.FullPath)), PathUtil.TrimTrailingSeparator(_parentPath), PathUtil.Comparison))
            {
                return -1;
            }

            return IndexOfName(item.Name);
        }

        /// <summary>The index of the entry with this name (OS case rules), or -1; the lookup is built on first use.</summary>
        public int IndexOfName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            Dictionary<string, int> byName = Volatile.Read(ref _indexByName);
            if (byName == null)
            {
                byName = new Dictionary<string, int>(_index.Count, PathUtil.Comparer);
                IReadOnlyList<DirectoryIndexEntry> entries = _index.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    byName[entries[i].Name] = i;
                }

                Volatile.Write(ref _indexByName, byName);
            }

            return byName.TryGetValue(name, out int found) ? found : -1;
        }

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        // Count never changes after construction (see class remarks), so
        // this event is never actually raised today. It's implemented
        // for interface completeness and so a future progressive-reveal
        // feature (streaming items in while a huge folder is still being
        // indexed) can add Add/Reset notifications without changing this
        // class's public surface.
        public event NotifyCollectionChangedEventHandler CollectionChanged { add { } remove { } }
    }
}
