// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>What a Details column needs read from each file, beyond what the folder listing gives.</summary>
    [Flags]
    public enum ItemDetailsNeeds
    {
        None = 0,

        /// <summary>The owning account (file security on Windows, the owner's id on Linux and macOS).</summary>
        Owner = 1,

        /// <summary>A picture's header and EXIF: dimensions, date taken, camera.</summary>
        Image = 2,

        /// <summary>A song's or video's tags and headers: length, video size, artist, album.</summary>
        Media = 4
    }

    /// <summary>The values read for one file (null or 0 where unknown or not applicable).</summary>
    public sealed class ItemDetails
    {
        internal ItemDetailsNeeds Loaded;

        public string Owner { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public DateTime? DateTaken { get; internal set; }
        public string Camera { get; internal set; }
        public TimeSpan? Length { get; internal set; }
        public string Artist { get; internal set; }
        public string Album { get; internal set; }

        public string DimensionsText => Width > 0 && Height > 0 ? $"{Width:N0} × {Height:N0}" : string.Empty;

        public string DateTakenText => DateTaken.HasValue ? DateTaken.Value.ToString("g", CultureInfo.CurrentCulture) : string.Empty;

        public string LengthText => Length.HasValue ? FileMetadataReader.FormatDuration(Length.Value) : string.Empty;
    }

    /// <summary>A view waiting for an item's details; told when they arrive (held weakly).</summary>
    public interface IItemDetailsObserver
    {
        void OnItemDetailsReady();
    }

    /// <summary>
    /// The Details view's per-file columns (Owner, Dimensions, Date taken, Camera model, Length, Artist, Album): read in
    /// the background for the rows on screen only, what the shown columns need only (a picture's header, not its pixels),
    /// and kept for the session by path and modification time (an edited file is read again).
    /// <para>
    /// Requests are served newest first (while scrolling, the newest are the rows on screen), on two workers for file
    /// headers and owners and one for media (LibVLC's parse is heavier); only the rows that asked redraw when a value
    /// arrives. A row asks on every draw and never keeps the details, so the cache can drop old entries (bounded LRU).
    /// </para>
    /// </summary>
    public static class ItemDetailsCache
    {
        private const int MaxEntries = 50_000;
        private const int FileWorkers = 2;

        private static readonly object Gate = new object();
        private static readonly Dictionary<(string Path, long Ticks), LinkedListNode<(string Path, long Ticks, ItemDetails Details)>> Entries =
            new Dictionary<(string, long), LinkedListNode<(string, long, ItemDetails)>>();
        private static readonly LinkedList<(string Path, long Ticks, ItemDetails Details)> Lru = new LinkedList<(string, long, ItemDetails)>();
        private static readonly Dictionary<(string Path, long Ticks, ItemDetailsNeeds Need), List<WeakReference<IItemDetailsObserver>>> Waiters =
            new Dictionary<(string, long, ItemDetailsNeeds), List<WeakReference<IItemDetailsObserver>>>();
        private static readonly Stack<(string Path, long Ticks, ItemDetailsNeeds Need)> FileQueue = new Stack<(string, long, ItemDetailsNeeds)>();
        private static readonly Stack<(string Path, long Ticks, ItemDetailsNeeds Need)> MediaQueue = new Stack<(string, long, ItemDetailsNeeds)>();
        private static readonly SemaphoreSlim FileSignal = new SemaphoreSlim(0);
        private static readonly SemaphoreSlim MediaSignal = new SemaphoreSlim(0);
        private static readonly Dictionary<string, string> OwnerNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private static int _started;

        /// <summary>Values read so far (tests and diagnostics).</summary>
        public static int ReadCount;

        /// <summary>The details already read for a file (nothing is asked for); null when none are. UI thread.</summary>
        public static ItemDetails Peek(string path, DateTime modifiedUtc)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (Gate)
            {
                return Entries.TryGetValue((path, modifiedUtc.Ticks), out var node) ? node.Value.Details : null;
            }
        }

        /// <summary>
        /// The details known for a file, asking for what <paramref name="needs"/> lacks (the observer is told when it
        /// arrives); null when nothing is known yet. Only what applies is asked: picture facts for pictures, media facts for
        /// songs and videos. UI thread.
        /// </summary>
        public static ItemDetails TryGet(string path, DateTime modifiedUtc, bool isDirectory, ItemDetailsNeeds needs, IItemDetailsObserver observer)
        {
            if (string.IsNullOrEmpty(path) || needs == ItemDetailsNeeds.None || Core.Services.ArchiveBrowser.IsInsideArchive(Core.PathUtil.GetParent(path) ?? string.Empty)
                || Core.Platform.Phones.PhonePaths.IsPhonePath(path))
            {
                return null;
            }

            needs = Applicable(path, isDirectory, needs);
            long ticks = modifiedUtc.Ticks;
            lock (Gate)
            {
                ItemDetails details = null;
                if (Entries.TryGetValue((path, ticks), out var node))
                {
                    Lru.Remove(node);
                    Lru.AddFirst(node);
                    details = node.Value.Details;
                }

                ItemDetailsNeeds missing = needs & ~(details?.Loaded ?? ItemDetailsNeeds.None);
                foreach (ItemDetailsNeeds need in new[] { ItemDetailsNeeds.Owner, ItemDetailsNeeds.Image, ItemDetailsNeeds.Media })
                {
                    if ((missing & need) == 0)
                    {
                        continue;
                    }

                    var key = (path, ticks, need);
                    bool queued = Waiters.TryGetValue(key, out List<WeakReference<IItemDetailsObserver>> waiting);
                    if (!queued)
                    {
                        Waiters[key] = waiting = new List<WeakReference<IItemDetailsObserver>>(1);
                        if (need == ItemDetailsNeeds.Media)
                        {
                            MediaQueue.Push(key);
                            MediaSignal.Release();
                        }
                        else
                        {
                            FileQueue.Push(key);
                            FileSignal.Release();
                        }
                    }

                    if (observer != null && !waiting.Exists(w => w.TryGetTarget(out IItemDetailsObserver o) && ReferenceEquals(o, observer)))
                    {
                        waiting.Add(new WeakReference<IItemDetailsObserver>(observer));
                    }
                }

                if (missing != ItemDetailsNeeds.None)
                {
                    StartWorkers();
                }

                return details;
            }
        }

        /// <summary>The needs that apply to this item: picture facts only for pictures, media facts only for songs and videos.</summary>
        private static ItemDetailsNeeds Applicable(string path, bool isDirectory, ItemDetailsNeeds needs)
        {
            if (isDirectory)
            {
                return needs & ItemDetailsNeeds.Owner;
            }

            if (!FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Images))
            {
                needs &= ~ItemDetailsNeeds.Image;
            }

            if (!FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Videos) && !FileCategoryClassifier.Matches(path, false, FileCategoryFilter.Music))
            {
                needs &= ~ItemDetailsNeeds.Media;
            }

            return needs;
        }

        private static void StartWorkers()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            for (int i = 0; i < FileWorkers; i++)
            {
                new Thread(FileWork) { IsBackground = true, Name = "ByteExplorer.Details." + i, Priority = ThreadPriority.BelowNormal }.Start();
            }

            Task.Run(MediaWorkAsync);
        }

        private static void FileWork()
        {
            while (true)
            {
                FileSignal.Wait();
                (string Path, long Ticks, ItemDetailsNeeds Need) request;
                lock (Gate)
                {
                    if (FileQueue.Count == 0)
                    {
                        continue;
                    }

                    request = FileQueue.Pop();
                }

                var details = new ItemDetails();
                try
                {
                    if (request.Need == ItemDetailsNeeds.Owner)
                    {
                        details.Owner = ReadOwner(request.Path);
                    }
                    else
                    {
                        (int width, int height, DateTime? taken, string camera) = FileMetadataReader.ReadImageFacts(request.Path);
                        details.Width = width;
                        details.Height = height;
                        details.DateTaken = taken;
                        details.Camera = camera;
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "Details unavailable for {Path}", request.Path);
                }

                Post(request, details);
            }
        }

        private static async Task MediaWorkAsync()
        {
            while (true)
            {
                await MediaSignal.WaitAsync().ConfigureAwait(false);
                (string Path, long Ticks, ItemDetailsNeeds Need) request;
                lock (Gate)
                {
                    if (MediaQueue.Count == 0)
                    {
                        continue;
                    }

                    request = MediaQueue.Pop();
                }

                var details = new ItemDetails();
                try
                {
                    (TimeSpan? length, int width, int height, string artist, string album) = await FileMetadataReader.ReadMediaFactsAsync(request.Path, CancellationToken.None).ConfigureAwait(false);
                    details.Length = length;
                    details.Width = width;
                    details.Height = height;
                    details.Artist = artist;
                    details.Album = album;
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "Media details unavailable for {Path}", request.Path);
                }

                Post(request, details);
            }
        }

        private static void Post((string Path, long Ticks, ItemDetailsNeeds Need) request, ItemDetails read)
        {
            Interlocked.Increment(ref ReadCount);
            Dispatcher.UIThread.Post(() => Arrived(request, read), DispatcherPriority.Background);
        }

        private static void Arrived((string Path, long Ticks, ItemDetailsNeeds Need) request, ItemDetails read)
        {
            List<WeakReference<IItemDetailsObserver>> waiting;
            lock (Gate)
            {
                if (!Entries.TryGetValue((request.Path, request.Ticks), out var node))
                {
                    node = Lru.AddFirst((request.Path, request.Ticks, new ItemDetails()));
                    Entries[(request.Path, request.Ticks)] = node;
                    while (Lru.Count > MaxEntries)
                    {
                        var last = Lru.Last.Value;
                        Lru.RemoveLast();
                        Entries.Remove((last.Path, last.Ticks));
                    }
                }

                ItemDetails details = node.Value.Details;
                details.Loaded |= request.Need;
                switch (request.Need)
                {
                    case ItemDetailsNeeds.Owner:
                        details.Owner = read.Owner;
                        break;
                    case ItemDetailsNeeds.Image:
                        details.Width = read.Width;
                        details.Height = read.Height;
                        details.DateTaken = read.DateTaken;
                        details.Camera = read.Camera;
                        break;
                    case ItemDetailsNeeds.Media:
                        details.Length = read.Length;
                        if (read.Width > 0)
                        {
                            details.Width = read.Width;
                            details.Height = read.Height;
                        }

                        details.Artist = read.Artist;
                        details.Album = read.Album;
                        break;
                }

                Waiters.Remove(request, out waiting);
            }

            if (waiting == null)
            {
                return;
            }

            foreach (WeakReference<IItemDetailsObserver> reference in waiting)
            {
                if (reference.TryGetTarget(out IItemDetailsObserver observer))
                {
                    try
                    {
                        observer.OnItemDetailsReady();
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Warning(ex, "A row failed to redraw with its details");
                    }
                }
            }
        }

        /// <summary>The owning account: "DOMAIN\user" on Windows (names looked up once per account), the user name elsewhere.</summary>
        private static string ReadOwner(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindowsOwner(path);
            }

            byte[] buffer = FileSystemFacts.CreateStatBuffer();
            return FileSystemFacts.TryGetUnixStat(path, buffer, out UnixStat stat) ? FileSystemFacts.UserName(stat.Uid) : null;
        }

        [SupportedOSPlatform("windows")]
        private static string ReadWindowsOwner(string path)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier sid)
            {
                return null;
            }

            string key = sid.Value;
            lock (OwnerNames)
            {
                if (OwnerNames.TryGetValue(key, out string known))
                {
                    return known;
                }
            }

            string name;
            try
            {
                name = sid.Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is SystemException)
            {
                name = key; // an account of another computer or a deleted one: its id, as Explorer shows it
            }

            lock (OwnerNames)
            {
                OwnerNames[key] = name;
            }

            return name;
        }
    }
}
