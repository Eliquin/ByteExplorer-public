// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Bounded, thread-safe LRU cache of DirectoryIndex results, with a
    /// per-call TTL and capacity so DirectoryIndexService can always
    /// pass in the live settings.json values rather than baking them in
    /// at construction time — DirectoryIndexService is constructed
    /// before settings finish loading from disk, so a fixed-at-startup
    /// value would silently never reflect a hand-edited settings.json.
    /// A DirectoryIndex is immutable once built, so it's always safe to
    /// hand the same instance out to multiple callers/navigations
    /// without copying.
    ///
    /// Deliberately internal to DirectoryIndexService — nothing else
    /// should reach into this directly; it's plumbing, not a subsystem
    /// in its own right.
    /// </summary>
    internal sealed class DirectoryIndexCache
    {
        private sealed class Entry
        {
            public DirectoryIndex Index;
            public DateTime CachedAtUtc;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _map =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>>(PathUtil.Comparer);
        private readonly LinkedList<KeyValuePair<string, Entry>> _lruOrder = new LinkedList<KeyValuePair<string, Entry>>();

        public bool TryGet(string key, int ttlMs, out DirectoryIndex index)
        {
            lock (_gate)
            {
                PurgeExpiredLocked(ttlMs);
                if (_map.TryGetValue(key, out LinkedListNode<KeyValuePair<string, Entry>> node))
                {
                    bool expired = (DateTime.UtcNow - node.Value.Value.CachedAtUtc).TotalMilliseconds > Math.Max(0, ttlMs);
                    if (expired)
                    {
                        _lruOrder.Remove(node);
                        _map.Remove(key);
                        index = null;
                        return false;
                    }

                    // Touch: move to the back (most-recently-used end).
                    _lruOrder.Remove(node);
                    LinkedListNode<KeyValuePair<string, Entry>> refreshed = _lruOrder.AddLast(node.Value);
                    _map[key] = refreshed;

                    index = node.Value.Value.Index;
                    return true;
                }
            }

            index = null;
            return false;
        }

        /// <summary>
        /// Stores a listing, dropping the least recently used beyond <paramref name="capacity"/> and every listing older
        /// than <paramref name="ttlMs"/>. Expired listings used to stay until looked up again or pushed out, so the last
        /// eight folders opened (megabytes each for a big folder) stayed in memory for the session although none of them
        /// could be served any more.
        /// </summary>
        public void Set(string key, DirectoryIndex index, int capacity, int ttlMs)
        {
            int effectiveCapacity = Math.Max(1, capacity);

            lock (_gate)
            {
                PurgeExpiredLocked(ttlMs);
                if (_map.TryGetValue(key, out LinkedListNode<KeyValuePair<string, Entry>> existing))
                {
                    _lruOrder.Remove(existing);
                    _map.Remove(key);
                }

                var entry = new Entry { Index = index, CachedAtUtc = DateTime.UtcNow };
                LinkedListNode<KeyValuePair<string, Entry>> node = _lruOrder.AddLast(new KeyValuePair<string, Entry>(key, entry));
                _map[key] = node;

                while (_map.Count > effectiveCapacity)
                {
                    LinkedListNode<KeyValuePair<string, Entry>> oldest = _lruOrder.First;
                    _lruOrder.RemoveFirst();
                    _map.Remove(oldest.Value.Key);
                }
            }
        }

        /// <summary>
        /// Explicit eviction — needed because a file operation can
        /// change a folder's contents without that folder ever being
        /// reloaded through the normal TryGet/Set path (a Move's
        /// source folder, specifically: the destination gets reloaded
        /// after the move, but the source doesn't, so its cached index
        /// would otherwise silently go stale until the TTL expires on
        /// its own).
        /// </summary>
        /// <summary>Drops the listings older than the time to live (a scan of at most a handful of entries).</summary>
        private void PurgeExpiredLocked(int ttlMs)
        {
            DateTime cutoff = DateTime.UtcNow.AddMilliseconds(-Math.Max(0, ttlMs));
            LinkedListNode<KeyValuePair<string, Entry>> node = _lruOrder.First;
            while (node != null)
            {
                LinkedListNode<KeyValuePair<string, Entry>> next = node.Next;
                if (node.Value.Value.CachedAtUtc < cutoff)
                {
                    _lruOrder.Remove(node);
                    _map.Remove(node.Value.Key);
                }

                node = next;
            }
        }

        public void Remove(string key)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out LinkedListNode<KeyValuePair<string, Entry>> node))
                {
                    _lruOrder.Remove(node);
                    _map.Remove(key);
                }
            }
        }
    }
}
