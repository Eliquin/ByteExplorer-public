// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>Per-entry flags of the index (one byte each).</summary>
    [Flags]
    internal enum EntryFlags : byte
    {
        None = 0,

        /// <summary>A folder (or a link to one).</summary>
        Directory = 1,

        /// <summary>A symbolic link, junction or mount point: indexed, never crawled into.</summary>
        Link = 2,

        /// <summary>Hidden itself (attribute, or a dot name on Linux and macOS) or inside a hidden folder.</summary>
        Hidden = 4,

        /// <summary>Removed by a live update: skipped by searches (with its whole subtree) until the next full build compacts it away.</summary>
        Deleted = 8,

        /// <summary>The name is already lower case: its original spelling is the folded one, so only that copy is stored.</summary>
        NameIsFolded = 16,

        /// <summary>
        /// Has the system attribute itself (Windows; not inherited like <see cref="Hidden"/>): the results show it in the
        /// system-item color. An index saved before this flag existed has none until its next rebuild.
        /// </summary>
        System = 32
    }

    /// <summary>
    /// One block of up to <see cref="Capacity"/> consecutive entries, stored column by column. Ids are global and dense:
    /// entry <c>id</c> lives in chunk <c>id &gt;&gt; Shift</c> at slot <c>id &amp; Mask</c>, so finding an entry is a shift, and
    /// no array ever grows past a few megabytes however large the index is (the old format kept whole files in 128 MB
    /// segments and every entry's full path).
    /// <para>
    /// Names are kept twice: <see cref="Folded"/>, lower case (invariant, per UTF-16 char) UTF-8 with a 0 byte after each
    /// name, is what searches scan with vectorized IndexOf; a query can never match across two names because no query
    /// contains a 0 byte. <see cref="Original"/> keeps the real spelling for display, and is empty for names that are
    /// already lower case. Full paths are not stored: an entry points to its parent folder, and a path is rebuilt from the
    /// chain when a result is shown.
    /// </para>
    /// <para>
    /// Immutable once published in an <see cref="IndexData"/>: live updates clone the chunks they touch.
    /// </para>
    /// </summary>
    internal sealed class IndexChunk
    {
        public const int Shift = 16;
        public const int Capacity = 1 << Shift;
        public const int Mask = Capacity - 1;

        public int Count;
        public int[] Parent;
        public byte[] Flags;
        public byte[] Kind;
        public byte[] Depth;
        public long[] Size;
        public long[] Ticks;

        public byte[] Folded;
        public int[] FoldedStart; // Count + 1 entries; name k is [FoldedStart[k], FoldedStart[k + 1] - 1), then a 0 byte
        public int FoldedLength;

        public byte[] Original;
        public int[] OriginalStart; // Count + 1 entries; empty for NameIsFolded entries
        public int OriginalLength;

        public static IndexChunk CreateEmpty(int nameBytesHint = 256 * 1024)
        {
            return new IndexChunk
            {
                Parent = new int[Capacity],
                Flags = new byte[Capacity],
                Kind = new byte[Capacity],
                Depth = new byte[Capacity],
                Size = new long[Capacity],
                Ticks = new long[Capacity],
                Folded = new byte[nameBytesHint],
                FoldedStart = new int[Capacity + 1],
                Original = new byte[nameBytesHint / 2],
                OriginalStart = new int[Capacity + 1]
            };
        }

        public bool IsFull => Count == Capacity;

        public ReadOnlySpan<byte> FoldedName(int slot) => new ReadOnlySpan<byte>(Folded, FoldedStart[slot], FoldedStart[slot + 1] - FoldedStart[slot] - 1);

        public ReadOnlySpan<byte> OriginalName(int slot) =>
            (Flags[slot] & (byte)EntryFlags.NameIsFolded) != 0
                ? FoldedName(slot)
                : new ReadOnlySpan<byte>(Original, OriginalStart[slot], OriginalStart[slot + 1] - OriginalStart[slot]);

        /// <summary>A copy whose per-entry columns can be changed (flags, size, date); names are shared (read-only).</summary>
        public IndexChunk CloneColumns()
        {
            var copy = (IndexChunk)MemberwiseClone();
            copy.Flags = (byte[])Flags.Clone();
            copy.Size = (long[])Size.Clone();
            copy.Ticks = (long[])Ticks.Clone();
            return copy;
        }

        /// <summary>A full copy at full capacity, for appending more entries to a partly filled chunk.</summary>
        public IndexChunk CloneForAppend()
        {
            var copy = new IndexChunk
            {
                Count = Count,
                Parent = Grow(Parent, Capacity),
                Flags = Grow(Flags, Capacity),
                Kind = Grow(Kind, Capacity),
                Depth = Grow(Depth, Capacity),
                Size = Grow(Size, Capacity),
                Ticks = Grow(Ticks, Capacity),
                Folded = Grow(Folded, Math.Max(FoldedLength * 2, 256 * 1024)),
                FoldedStart = Grow(FoldedStart, Capacity + 1),
                FoldedLength = FoldedLength,
                Original = Grow(Original, Math.Max(OriginalLength * 2, 128 * 1024)),
                OriginalStart = Grow(OriginalStart, Capacity + 1),
                OriginalLength = OriginalLength
            };
            return copy;
        }

        /// <summary>Shrinks the arrays of a partly filled chunk to what it holds (the last chunk of a build).</summary>
        public void Trim()
        {
            if (Count < Capacity)
            {
                Parent = Shrink(Parent, Count);
                Flags = Shrink(Flags, Count);
                Kind = Shrink(Kind, Count);
                Depth = Shrink(Depth, Count);
                Size = Shrink(Size, Count);
                Ticks = Shrink(Ticks, Count);
                FoldedStart = Shrink(FoldedStart, Count + 1);
                OriginalStart = Shrink(OriginalStart, Count + 1);
            }

            Folded = Shrink(Folded, FoldedLength);
            Original = Shrink(Original, OriginalLength);
        }

        private static T[] Grow<T>(T[] source, int length)
        {
            var result = new T[Math.Max(length, source.Length)];
            Array.Copy(source, result, source.Length);
            return result;
        }

        private static T[] Shrink<T>(T[] source, int length)
        {
            if (source.Length == length)
            {
                return source;
            }

            var result = new T[length];
            Array.Copy(source, result, Math.Min(length, source.Length));
            return result;
        }
    }

    /// <summary>
    /// One immutable, searchable state of the index: the chunks plus what the build recorded. Published by a single
    /// reference swap; searches hold the instance they started with, so a rebuild or a live update never changes data under
    /// a running search.
    /// </summary>
    internal sealed class IndexData
    {
        private int[] _childrenOrder; // ids sorted by parent, built on demand for live updates (see Children)
        private int[] _childrenStart;

        public IndexData(IndexChunk[] chunks, int count)
        {
            Chunks = chunks;
            Count = count;
            for (int id = 0; id < count; id++)
            {
                IndexChunk chunk = chunks[id >> IndexChunk.Shift];
                byte flags = chunk.Flags[id & IndexChunk.Mask];
                if ((flags & (byte)EntryFlags.Deleted) != 0)
                {
                    DeletedCount++;
                    if ((flags & (byte)EntryFlags.Directory) != 0)
                    {
                        HasDeletedDirectories = true;
                    }
                }
                else if ((flags & (byte)EntryFlags.Directory) != 0)
                {
                    Folders++;
                }
                else
                {
                    Files++;
                }
            }
        }

        public static IndexData Empty { get; } = new IndexData(Array.Empty<IndexChunk>(), 0);

        public IndexChunk[] Chunks { get; }

        /// <summary>Entries stored, live and deleted.</summary>
        public int Count { get; }

        public long Files { get; }

        public long Folders { get; }

        public int DeletedCount { get; }

        /// <summary>A live update removed a folder: entries below it must be checked for a deleted ancestor.</summary>
        public bool HasDeletedDirectories { get; }

        public DateTime BuildDateUtc { get; set; }

        public TimeSpan BuildDuration { get; set; }

        public bool IsPartial { get; set; }

        public string[] Roots { get; set; } = Array.Empty<string>();

        public long LiveCount => Files + Folders;

        public IndexChunk ChunkOf(int id) => Chunks[id >> IndexChunk.Shift];

        public EntryFlags FlagsOf(int id) => (EntryFlags)Chunks[id >> IndexChunk.Shift].Flags[id & IndexChunk.Mask];

        public int ParentOf(int id) => Chunks[id >> IndexChunk.Shift].Parent[id & IndexChunk.Mask];

        public ReadOnlySpan<byte> FoldedName(int id) => Chunks[id >> IndexChunk.Shift].FoldedName(id & IndexChunk.Mask);

        public string GetName(int id) => Encoding.UTF8.GetString(Chunks[id >> IndexChunk.Shift].OriginalName(id & IndexChunk.Mask));

        /// <summary>False when the entry or a folder above it was removed by a live update.</summary>
        public bool IsAlive(int id)
        {
            if ((FlagsOf(id) & EntryFlags.Deleted) != 0)
            {
                return false;
            }

            if (!HasDeletedDirectories)
            {
                return true;
            }

            for (int current = ParentOf(id); current >= 0; current = ParentOf(current))
            {
                if ((FlagsOf(current) & EntryFlags.Deleted) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The full path: the root's path, then each folder name down to the entry.</summary>
        public string GetFullPath(int id)
        {
            Span<int> chain = stackalloc int[256];
            int depth = 0;
            for (int current = id; current >= 0 && depth < chain.Length; current = ParentOf(current))
            {
                chain[depth++] = current;
            }

            var builder = new StringBuilder(128);
            for (int i = depth - 1; i >= 0; i--)
            {
                if (builder.Length > 0 && !PathUtil.IsSeparator(builder[builder.Length - 1]))
                {
                    builder.Append(PathUtil.Separator);
                }

                builder.Append(GetName(chain[i]));
            }

            return builder.ToString();
        }

        /// <summary>
        /// The children of a folder (live and deleted), from an index of ids by parent built on first use for this state
        /// (a counting sort, O(n); live updates ask for it once per update).
        /// </summary>
        public ReadOnlySpan<int> Children(int parentId)
        {
            EnsureChildrenIndex();
            if (parentId < 0 || parentId >= Count)
            {
                return ReadOnlySpan<int>.Empty;
            }

            return new ReadOnlySpan<int>(_childrenOrder, _childrenStart[parentId], _childrenStart[parentId + 1] - _childrenStart[parentId]);
        }

        /// <summary>The root entries (parent -1).</summary>
        public IEnumerable<int> RootIds()
        {
            for (int id = 0; id < Count; id++)
            {
                if (ParentOf(id) < 0)
                {
                    yield return id;
                }
            }
        }

        private void EnsureChildrenIndex()
        {
            if (_childrenOrder != null)
            {
                return;
            }

            lock (this)
            {
                if (_childrenOrder != null)
                {
                    return;
                }

                var start = new int[Count + 2];
                for (int id = 0; id < Count; id++)
                {
                    int parent = ParentOf(id);
                    if (parent >= 0)
                    {
                        start[parent + 2]++;
                    }
                }

                for (int i = 2; i < start.Length; i++)
                {
                    start[i] += start[i - 1];
                }

                var order = new int[Math.Max(0, start[start.Length - 1])];
                for (int id = 0; id < Count; id++)
                {
                    int parent = ParentOf(id);
                    if (parent >= 0)
                    {
                        order[start[parent + 1]++] = id;
                    }
                }

                _childrenStart = start;
                System.Threading.Volatile.Write(ref _childrenOrder, order);
            }
        }
    }
}
