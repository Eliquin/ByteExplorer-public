// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Image thumbnails for the file views: decoded in the background, at the size they are shown, and kept in a memory
    /// cache bounded by pixel memory.
    /// <para>
    /// Requests are served newest first (a stack), because while scrolling the newest requests are the items now on
    /// screen and the oldest ones have usually scrolled away; a request whose item no view shows any more is dropped
    /// before decoding (<see cref="Release"/>). A few workers (half the cores, at most 4) decode with
    /// <see cref="ImageLoader"/>: SkiaSharp, scaled during decode for JPEG, EXIF orientation applied, the file opened
    /// share-everything so previews never lock a file.
    /// </para>
    /// <para>
    /// Keys include the file's last write time, so an edited image gets a new thumbnail; sizes are rounded up to 64, 128,
    /// or 256 pixels so zooming reuses thumbnails instead of decoding again at every step. The cache lives on the UI
    /// thread; workers only decode and post results back (the wanted counts have a small lock so workers can skip stale
    /// requests). Evicted bitmaps are disposed: a view
    /// asks the cache for its bitmap on every draw (it never keeps one), and frames already recorded hold their own
    /// reference to the pixels, so disposal never pulls an image out from under the renderer.
    /// </para>
    /// </summary>
    public static class ThumbnailCache
    {
        // Decoded pixels in memory (native, outside the GC heap). 64 MB holds about 250 thumbnails at the largest size or
        // 1,000 at the large one, several screens' worth; it was 160 MB, all of which a single photo folder could fill.
        private const long MaxCacheBytes = 64L * 1024 * 1024;
        private static readonly int WorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".ico", ".wbmp"
        };

        private static readonly Dictionary<Key, LinkedListNode<Entry>> Cache = new Dictionary<Key, LinkedListNode<Entry>>();
        private static readonly LinkedList<Entry> Lru = new LinkedList<Entry>();
        // Read by the workers to skip stale requests, so guarded by its own lock (held for a dictionary operation only).
        private static readonly Dictionary<Key, int> Wanted = new Dictionary<Key, int>();
        private static readonly object WantedLock = new object();
        private const int MaxFailedEntries = 10_000;
        private static readonly HashSet<Key> Failed = new HashSet<Key>();
        private static readonly object QueueLock = new object();
        private static readonly Stack<Key> Queue = new Stack<Key>();
        private static readonly HashSet<Key> Queued = new HashSet<Key>();
        private static readonly SemaphoreSlim Pending = new SemaphoreSlim(0);
        private static long _cacheBytes;
        private static int _workersStarted;

        /// <summary>
        /// The views waiting for each thumbnail, held weakly (UI thread only). Only they are told when it arrives. This
        /// replaced a static event every visible view subscribed to: each decoded thumbnail called every view (on
        /// screen and in the container pools), every subscription copied the whole handler list, and a view never
        /// detached from its visual tree (a closed window does not detach) kept its entire window alive.
        /// </summary>
        private static readonly Dictionary<Key, List<WeakReference<IThumbnailObserver>>> Waiters = new Dictionary<Key, List<WeakReference<IThumbnailObserver>>>();

        private static long _decodedCount;

        /// <summary>How many thumbnails have been decoded and cached since startup (diagnostics and tests).</summary>
        public static long DecodedCount => Interlocked.Read(ref _decodedCount);

        public readonly struct Key : IEquatable<Key>
        {
            public Key(string path, long lastWriteTicks, int size)
            {
                Path = path;
                LastWriteTicks = lastWriteTicks;
                Size = size;
            }

            public string Path { get; }
            public long LastWriteTicks { get; }
            public int Size { get; }

            public bool Equals(Key other) => Size == other.Size && LastWriteTicks == other.LastWriteTicks && string.Equals(Path, other.Path, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Path, LastWriteTicks, Size);
        }

        private sealed class Entry
        {
            public Key Key;
            public Bitmap Bitmap;
            public long Bytes;
        }

        /// <summary>
        /// Whether the file is an image this cache can thumbnail (by extension). Not on a phone: each thumbnail would
        /// download the whole photo over USB, and a camera folder holds thousands.
        /// </summary>
        public static bool IsSupported(string path)
        {
            if (Core.Platform.Phones.Phones.IsPhonePath(path))
            {
                return false;
            }

            string extension = System.IO.Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);
        }

        /// <summary>The cache key for a file shown at a size (rounded up to the next bucket).</summary>
        public static Key KeyFor(string path, DateTime lastWriteUtc, double displaySize)
        {
            int size = displaySize <= 64 ? 64 : displaySize <= 128 ? 128 : 256;
            return new Key(path, lastWriteUtc.Ticks, size);
        }

        /// <summary>The thumbnail if decoded already; null otherwise. UI thread.</summary>
        public static Bitmap TryGet(Key key)
        {
            if (Cache.TryGetValue(key, out LinkedListNode<Entry> node))
            {
                if (!ReferenceEquals(node, Lru.Last))
                {
                    Lru.Remove(node);
                    Lru.AddLast(node);
                }

                return node.Value.Bitmap;
            }

            return null;
        }

        /// <summary>
        /// Registers that a view shows this file and wants its thumbnail; queues the decode if needed. Each call must be
        /// paired with <see cref="Release"/> when the view shows something else. UI thread.
        /// </summary>
        public static void Acquire(Key key, IThumbnailObserver observer = null)
        {
            if (observer != null && !Cache.ContainsKey(key))
            {
                if (!Waiters.TryGetValue(key, out List<WeakReference<IThumbnailObserver>> list))
                {
                    list = new List<WeakReference<IThumbnailObserver>>(1);
                    Waiters[key] = list;
                }

                list.Add(new WeakReference<IThumbnailObserver>(observer));
            }

            int count;
            lock (WantedLock)
            {
                Wanted.TryGetValue(key, out count);
                Wanted[key] = count + 1;
            }

            if (count > 0 || Cache.ContainsKey(key) || Failed.Contains(key))
            {
                return;
            }

            lock (QueueLock)
            {
                if (Queued.Add(key))
                {
                    Queue.Push(key);
                    Pending.Release();
                }
            }

            EnsureWorkers();
        }

        /// <summary>The view no longer shows the file; a decode not started yet is skipped. UI thread.</summary>
        public static void Release(Key key, IThumbnailObserver observer = null)
        {
            if (observer != null && Waiters.TryGetValue(key, out List<WeakReference<IThumbnailObserver>> list))
            {
                list.RemoveAll(reference => !reference.TryGetTarget(out IThumbnailObserver target) || ReferenceEquals(target, observer));
                if (list.Count == 0)
                {
                    Waiters.Remove(key);
                }
            }

            lock (WantedLock)
            {
                if (!Wanted.TryGetValue(key, out int count))
                {
                    return;
                }

                if (count <= 1)
                {
                    Wanted.Remove(key);
                }
                else
                {
                    Wanted[key] = count - 1;
                }
            }
        }

        private static void EnsureWorkers()
        {
            if (Interlocked.CompareExchange(ref _workersStarted, 1, 0) != 0)
            {
                return;
            }

            for (int i = 0; i < WorkerCount; i++)
            {
                var thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "ByteExplorer.Thumbnails",
                    Priority = ThreadPriority.BelowNormal
                };
                thread.Start();
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Pending.Wait();
                Key key;
                lock (QueueLock)
                {
                    if (Queue.Count == 0)
                    {
                        continue;
                    }

                    key = Queue.Pop();
                    Queued.Remove(key);
                }

                // Skip work nobody needs any more: the item scrolled away before its turn came.
                bool wanted;
                lock (WantedLock)
                {
                    wanted = Wanted.ContainsKey(key);
                }

                if (!wanted)
                {
                    continue;
                }

                Bitmap bitmap = null;
                try
                {
                    bitmap = ImageLoader.Load(key.Path, key.Size);
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "Thumbnail decode failed for {Path}", key.Path);
                }

                UiThread.Post(() => Complete(key, bitmap), DispatcherPriority.Background);
            }
        }

        private static void Complete(Key key, Bitmap bitmap)
        {
            if (bitmap == null)
            {
                // Not decodable (corrupt, unsupported variant, unreadable): remember it so it is not retried while visible.
                if (Failed.Count >= MaxFailedEntries)
                {
                    Failed.Clear();
                }

                Failed.Add(key);
                Waiters.Remove(key);
                return;
            }

            if (Cache.ContainsKey(key))
            {
                bitmap.Dispose();
                return;
            }

            long bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            Cache[key] = Lru.AddLast(new Entry { Key = key, Bitmap = bitmap, Bytes = bytes });
            _cacheBytes += bytes;
            while (_cacheBytes > MaxCacheBytes && Lru.First != null && !Lru.First.Value.Key.Equals(key))
            {
                Entry oldest = Lru.First.Value;
                Lru.RemoveFirst();
                Cache.Remove(oldest.Key);
                _cacheBytes -= oldest.Bytes;
                oldest.Bitmap.Dispose();
            }

            Interlocked.Increment(ref _decodedCount);
            if (Waiters.Remove(key, out List<WeakReference<IThumbnailObserver>> waiting))
            {
                foreach (WeakReference<IThumbnailObserver> reference in waiting)
                {
                    if (reference.TryGetTarget(out IThumbnailObserver observer))
                    {
                        observer.OnThumbnailReady(key);
                    }
                }
            }
        }
    }

    /// <summary>A view waiting for a thumbnail (see <see cref="ThumbnailCache.Acquire"/>); called on the UI thread.</summary>
    public interface IThumbnailObserver
    {
        void OnThumbnailReady(ThumbnailCache.Key key);
    }
}
