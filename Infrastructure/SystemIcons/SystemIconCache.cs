// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ByteExplorer.Core;
using Serilog;

namespace ByteExplorer.Infrastructure.SystemIcons
{
    /// <summary>Where the icons come from (the Windows shell; a fake in tests).</summary>
    public interface ISystemIconSource
    {
        bool IsAvailable { get; }

        /// <summary>
        /// The item's slot in the system image list, or -1. <paramref name="byPath"/>: ask about the item itself (its own
        /// icon); otherwise only its name or kind counts (no disk access).
        /// </summary>
        int IndexOf(string path, bool isDirectory, bool byPath);

        /// <summary>The icon at the slot, about <paramref name="size"/> pixels (16, 32, 48 or 256 are native), or null.</summary>
        IconPixels Render(int index, int size);
    }

    /// <summary>An icon's pixels: top-down BGRA, straight (not premultiplied) alpha.</summary>
    public sealed class IconPixels
    {
        public IconPixels(int width, int height, byte[] bgra)
        {
            Width = width;
            Height = height;
            Bgra = bgra;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Bgra { get; }

        /// <summary>The larger side of the box around the visible pixels (a 48 px icon in a 256 px square reports about 48).</summary>
        public int ContentExtent
        {
            get
            {
                int minX = Width, minY = Height, maxX = -1, maxY = -1;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        if (Bgra[(y * Width + x) * 4 + 3] > 8)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }

                return maxX < 0 ? 0 : Math.Max(maxX - minX + 1, maxY - minY + 1);
            }
        }
    }

    /// <summary>A view drawing an item that waits for its icon; told when it arrives (held weakly).</summary>
    public interface ISystemIconObserver
    {
        void OnSystemIconReady();
    }

    /// <summary>
    /// The icons Windows Explorer shows (Settings › Folder options › "Show Windows icons"), for the file views.
    /// <para>
    /// <b>Fast path</b>: one dictionary lookup per drawn item, on the UI thread. Items are grouped the way the shell
    /// itself groups them: by extension for ordinary files (every .docx shares one icon), and by path only for what has
    /// an icon of its own: programs, shortcuts, icon files, Internet shortcuts, drives. Folders have no Windows icon here:
    /// they keep ByteExplorer's folder glyph and its color (the user's choice). The key leads to the system image list slot,
    /// and slot + size to a bitmap: two thousand .jpg files cost one bitmap per size.
    /// </para>
    /// <para>
    /// <b>Slow path</b>: an item seen for the first time draws the app's own glyph once while one background STA thread
    /// asks the shell (newest request first, as while scrolling the newest are the ones on screen) and posts the bitmap
    /// back; only the views that asked for that key are told to redraw. Bitmaps are made at 16, 32, 48, 64, 96, 128 or
    /// 256 pixels, the next bucket at or above what is drawn (in physical pixels), so zooming reuses them. Memory is
    /// bounded (least recently used out).
    /// </para>
    /// <para>Off Windows there is no source and every call returns null: the views draw the app's glyphs.</para>
    /// </summary>
    public static class SystemIconCache
    {
        private const long MaxCacheBytes = 48L * 1024 * 1024;
        private const int MaxPathKeys = 20_000;
        private static readonly int[] Buckets = { 16, 32, 48, 64, 96, 128, 256 };

        /// <summary>Extensions whose files carry their own icon (asked about by path).</summary>
        private static readonly HashSet<string> OwnIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".ico", ".cur", ".ani", ".url", ".msc", ".scr", ".cpl", ".appref-ms", ".website", ".icl", ".pif"
        };

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<(int Index, int Size), LinkedListNode<Entry>> Bitmaps = new Dictionary<(int, int), LinkedListNode<Entry>>();
        private static readonly LinkedList<Entry> Lru = new LinkedList<Entry>();
        private static readonly Dictionary<(string Key, int Size), List<WeakReference<ISystemIconObserver>>> Waiters =
            new Dictionary<(string, int), List<WeakReference<ISystemIconObserver>>>();
        private static readonly HashSet<(string Key, int Size)> Queued = new HashSet<(string, int)>();
        private static readonly Stack<Request> Queue = new Stack<Request>();
        private static readonly SemaphoreSlim Pending = new SemaphoreSlim(0);
        private static long _cacheBytes;
        private static int _pathKeys;
        private static Thread _worker;
        private static ISystemIconSource _source;
        private static bool _sourceChosen;

        private static Func<bool> s_enabled;

        /// <summary>The setting (Folder options › "Show Windows icons"), read live; on until attached.</summary>
        public static bool Enabled => s_enabled?.Invoke() ?? true;

        /// <summary>Where the setting is read from (the settings service's FileList.UseShellIcons).</summary>
        public static void AttachSetting(Func<bool> enabled) => s_enabled = enabled;

        /// <summary>True when the views should draw the shell's icons now: the setting is on and this system has them.</summary>
        public static bool IsActive => Enabled && IsSupported;

        /// <summary>True when the icons can be shown here (Windows, or a test's source).</summary>
        public static bool IsSupported => Source?.IsAvailable == true;

        /// <summary>Requests answered by the source so far (tests and diagnostics).</summary>
        public static int LookupCount;

        /// <summary>Replaces the source (tests); null goes back to the system's own.</summary>
        internal static void UseSourceForTests(ISystemIconSource source)
        {
            lock (Gate)
            {
                _source = source;
                _sourceChosen = source != null;
                Indexes.Clear();
                Bitmaps.Clear();
                Lru.Clear();
                Waiters.Clear();
                Queued.Clear();
                Queue.Clear();
                _cacheBytes = 0;
                _pathKeys = 0;
                LookupCount = 0;
            }
        }

        private static ISystemIconSource Source
        {
            get
            {
                if (!_sourceChosen)
                {
                    _sourceChosen = true;
                    if (OperatingSystem.IsWindows())
                    {
                        _source = new WindowsShellIconSource();
                    }
                }

                return _source;
            }
        }

        /// <summary>The bucket a drawn size (physical pixels) uses.</summary>
        public static int BucketFor(double pixels)
        {
            foreach (int bucket in Buckets)
            {
                if (pixels <= bucket)
                {
                    return bucket;
                }
            }

            return Buckets[^1];
        }

        /// <summary>
        /// How items share icons: "path:…" for an item with an icon of its own (and drives), ".ext" (or "file:" without one)
        /// for ordinary files, null for folders (they keep the app's own folder icon). Items inside archives, on phones or in protected items are never asked
        /// about by path (the shell can't see them).
        /// </summary>
        public static string KeyFor(string path, string name, bool isDirectory, bool isSystem)
        {
            if (isDirectory)
            {
                // Folders keep ByteExplorer's own folder icon (and the folder color setting) even with Windows icons on:
                // the Windows icons are for files, and for drives, which aren't folders to look at. Null: no Windows icon.
                bool drive = PathUtil.GetParent(path ?? string.Empty) == null && !string.IsNullOrEmpty(path);
                return drive && !IsVirtualPlace(path) ? "path:" + path : null;
            }

            string extension = Path.GetExtension(name ?? path);
            if (OwnIconExtensions.Contains(extension) && !IsVirtualPlace(path))
            {
                return "path:" + path;
            }

            return string.IsNullOrEmpty(extension) ? "file:" : extension.ToLowerInvariant();
        }

        /// <summary>
        /// Inside an archive, on a phone or in a protected item: the shell can't see the item, so only its type counts.
        /// Asked only for the few items that would be looked up by path (it can touch the disk for paths through ".zip").
        /// </summary>
        private static bool IsVirtualPlace(string path)
        {
            string parent = PathUtil.GetParent(path ?? string.Empty) ?? string.Empty;
            return Core.Platform.Phones.PhonePaths.IsPhonePath(path) || Core.Security.ProtectedFormat.IsProtectedPath(parent)
                || Core.Services.ArchiveBrowser.IsInsideArchive(parent);
        }

        /// <summary>
        /// The icon to draw for the item at about <paramref name="pixels"/> physical pixels, or null (not enabled, not
        /// supported, or not ready yet: the observer is told when it is). UI thread.
        /// </summary>
        public static Bitmap TryGet(string path, string name, bool isDirectory, bool isSystem, double pixels, ISystemIconObserver observer) =>
            TryGet(KeyFor(path, name, isDirectory, isSystem), path, isDirectory, pixels, observer);

        /// <summary>
        /// The same with the item's key worked out before (<see cref="KeyFor"/>; the file views keep it on the item, so a
        /// redraw costs no string work).
        /// </summary>
        public static Bitmap TryGet(string key, string path, bool isDirectory, double pixels, ISystemIconObserver observer)
        {
            if (pixels < 1 || key == null || !IsActive)
            {
                return null;
            }

            int size = BucketFor(pixels);
            lock (Gate)
            {
                if (Indexes.TryGetValue(key, out int index))
                {
                    if (index < 0)
                    {
                        return null; // the shell has no icon for it: the glyph stays
                    }

                    if (Bitmaps.TryGetValue((index, size), out LinkedListNode<Entry> node))
                    {
                        Lru.Remove(node);
                        Lru.AddFirst(node);
                        return node.Value.Bitmap;
                    }
                }

                Wait(key, size, observer);
                if (Queued.Add((key, size)))
                {
                    Queue.Push(new Request(key, size, path, isDirectory, key.StartsWith("path:", StringComparison.Ordinal)));
                    EnsureWorker();
                    Pending.Release();
                }
            }

            return null;
        }

        private static void Wait(string key, int size, ISystemIconObserver observer)
        {
            if (observer == null)
            {
                return;
            }

            if (!Waiters.TryGetValue((key, size), out List<WeakReference<ISystemIconObserver>> list))
            {
                Waiters[(key, size)] = list = new List<WeakReference<ISystemIconObserver>>(2);
            }

            foreach (WeakReference<ISystemIconObserver> existing in list)
            {
                if (existing.TryGetTarget(out ISystemIconObserver o) && ReferenceEquals(o, observer))
                {
                    return;
                }
            }

            list.Add(new WeakReference<ISystemIconObserver>(observer));
        }

        private static void EnsureWorker()
        {
            if (_worker != null)
            {
                return;
            }

            _worker = new Thread(Work) { IsBackground = true, Name = "ByteExplorer.SystemIcons", Priority = ThreadPriority.BelowNormal };
            if (OperatingSystem.IsWindows())
            {
                _worker.SetApartmentState(ApartmentState.STA); // the shell's image lists and icon handlers expect STA
            }

            _worker.Start();
        }

        private static void Work()
        {
            while (true)
            {
                Pending.Wait();
                Request request;
                ISystemIconSource source;
                int index;
                bool known;
                lock (Gate)
                {
                    if (Queue.Count == 0)
                    {
                        continue;
                    }

                    request = Queue.Pop();
                    source = _source;
                    known = Indexes.TryGetValue(request.Key, out index);
                }

                Bitmap bitmap = null;
                try
                {
                    if (!known)
                    {
                        index = source?.IndexOf(request.Path, request.IsDirectory, request.ByPath) ?? -1;
                        Interlocked.Increment(ref LookupCount);
                    }

                    if (index >= 0)
                    {
                        bool have;
                        lock (Gate)
                        {
                            have = Bitmaps.ContainsKey((index, request.Size));
                        }

                        if (!have)
                        {
                            bitmap = ToBitmap(source?.Render(index, request.Size), request.Size);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "No system icon for {Path}", request.Path);
                    index = -1;
                }

                int found = index;
                Dispatcher.UIThread.Post(() => Arrived(request, found, bitmap), DispatcherPriority.Background);
            }
        }

        private static void Arrived(Request request, int index, Bitmap bitmap)
        {
            List<WeakReference<ISystemIconObserver>> waiting;
            lock (Gate)
            {
                Queued.Remove((request.Key, request.Size));
                if (!Indexes.ContainsKey(request.Key))
                {
                    if (request.ByPath && ++_pathKeys > MaxPathKeys)
                    {
                        ForgetPathKeys(); // a long session through many program folders: start that part over
                    }

                    Indexes[request.Key] = index;
                }

                if (bitmap != null && !Bitmaps.ContainsKey((index, request.Size)))
                {
                    var entry = new Entry(index, request.Size, bitmap, (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
                    Bitmaps[(index, request.Size)] = Lru.AddFirst(entry);
                    _cacheBytes += entry.Bytes;
                    while (_cacheBytes > MaxCacheBytes && Lru.Count > 1)
                    {
                        Entry old = Lru.Last.Value;
                        Lru.RemoveLast();
                        Bitmaps.Remove((old.Index, old.Size));
                        _cacheBytes -= old.Bytes;
                        old.Bitmap.Dispose(); // the views ask on every draw, never keep one; recorded frames hold their own reference
                    }
                }
                else
                {
                    bitmap?.Dispose();
                }

                Waiters.Remove((request.Key, request.Size), out waiting);
            }

            if (waiting == null)
            {
                return;
            }

            foreach (WeakReference<ISystemIconObserver> reference in waiting)
            {
                if (reference.TryGetTarget(out ISystemIconObserver observer))
                {
                    try
                    {
                        observer.OnSystemIconReady();
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Warning(ex, "A view failed to redraw with its icon");
                    }
                }
            }
        }

        private static void ForgetPathKeys()
        {
            var keep = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> pair in Indexes)
            {
                if (!pair.Key.StartsWith("path:", StringComparison.Ordinal))
                {
                    keep.Add(pair);
                }
            }

            Indexes.Clear();
            foreach (KeyValuePair<string, int> pair in keep)
            {
                Indexes[pair.Key] = pair.Value;
            }

            _pathKeys = 0;
        }

        /// <summary>The pixels as a bitmap of the bucket's size (scaled with high quality when the shell's size differs).</summary>
        private static Bitmap ToBitmap(IconPixels pixels, int size)
        {
            if (pixels == null || pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length < pixels.Width * pixels.Height * 4)
            {
                return null;
            }

            GCHandle handle = GCHandle.Alloc(pixels.Bgra, GCHandleType.Pinned);
            try
            {
                var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                    new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), pixels.Width * 4);
                if (pixels.Width == size && pixels.Height == size)
                {
                    return bitmap;
                }

                using (bitmap)
                {
                    return bitmap.CreateScaledBitmap(new PixelSize(size, size), BitmapInterpolationMode.HighQuality);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private sealed record Request(string Key, int Size, string Path, bool IsDirectory, bool ByPath);

        private sealed record Entry(int Index, int Size, Bitmap Bitmap, long Bytes);
    }
}
