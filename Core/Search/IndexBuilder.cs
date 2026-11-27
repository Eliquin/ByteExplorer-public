// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace ByteSearcher
{
    /// <summary>
    /// Entries prepared by one crawl worker (one folder, or up to <see cref="MaxCount"/> of a big one) before they are
    /// appended: names are encoded and folded here, on the worker's thread, so the builder's lock only copies bytes.
    /// Reused for every folder the worker lists.
    /// </summary>
    internal sealed class EntryBatch
    {
        public const int MaxCount = 4096;

        public int Count;
        public readonly int[] Parent = new int[MaxCount];
        public readonly byte[] Flags = new byte[MaxCount];
        public readonly byte[] Kind = new byte[MaxCount];
        public readonly byte[] Depth = new byte[MaxCount];
        public readonly long[] Size = new long[MaxCount];
        public readonly long[] Ticks = new long[MaxCount];
        public readonly int[] FoldedLength = new int[MaxCount];
        public readonly int[] OriginalLength = new int[MaxCount];
        public byte[] FoldedBytes = new byte[64 * 1024];
        public byte[] OriginalBytes = new byte[64 * 1024];
        public int FoldedUsed;
        public int OriginalUsed;

        private char[] _foldScratch = new char[256];

        public bool IsFull => Count == MaxCount;

        public void Clear()
        {
            Count = 0;
            FoldedUsed = 0;
            OriginalUsed = 0;
        }

        /// <summary>Adds an entry; returns its slot in the batch.</summary>
        public int Add(ReadOnlySpan<char> name, int parent, EntryFlags flags, byte depth, long size, long ticks)
        {
            int slot = Count++;
            Parent[slot] = parent;
            Depth[slot] = depth;
            Size[slot] = size;
            Ticks[slot] = ticks;
            Kind[slot] = (flags & EntryFlags.Directory) != 0 ? (byte)0 : (byte)ByteExplorer.Core.FileKinds.Of(name);

            // Fold: invariant lower case per UTF-16 char (so folded and original strings have the same char positions,
            // which is how matches are highlighted), ASCII without a table lookup.
            if (_foldScratch.Length < name.Length)
            {
                _foldScratch = new char[Math.Max(name.Length, _foldScratch.Length * 2)];
            }

            Span<char> folded = _foldScratch.AsSpan(0, name.Length);
            bool same = true;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                char lower = c < 128 ? (c >= 'A' && c <= 'Z' ? (char)(c + 32) : c) : char.ToLowerInvariant(c);
                folded[i] = lower;
                same &= lower == c;
            }

            int foldedMax = Encoding.UTF8.GetMaxByteCount(name.Length) + 1;
            EnsureCapacity(ref FoldedBytes, FoldedUsed + foldedMax);
            int foldedBytes = Encoding.UTF8.GetBytes(folded, FoldedBytes.AsSpan(FoldedUsed));
            FoldedLength[slot] = foldedBytes;
            FoldedUsed += foldedBytes;

            if (same)
            {
                flags |= EntryFlags.NameIsFolded;
                OriginalLength[slot] = 0;
            }
            else
            {
                EnsureCapacity(ref OriginalBytes, OriginalUsed + foldedMax);
                int originalBytes = Encoding.UTF8.GetBytes(name, OriginalBytes.AsSpan(OriginalUsed));
                OriginalLength[slot] = originalBytes;
                OriginalUsed += originalBytes;
            }

            Flags[slot] = (byte)flags;
            return slot;
        }

        private static void EnsureCapacity(ref byte[] buffer, int needed)
        {
            if (buffer.Length < needed)
            {
                Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
            }
        }
    }

    /// <summary>
    /// Collects entries into chunks. Thread-safe: crawl workers append whole batches under one short lock, and get back
    /// the id of the batch's first entry (ids are consecutive), which is how a folder's children learn their parent id.
    /// A parent therefore always has a smaller id than its children. <see cref="ContinueFrom"/> starts from an existing
    /// index for live updates: earlier chunks are shared, the last (partly filled) one is copied.
    /// </summary>
    internal sealed class IndexBuilder
    {
        private readonly object _gate = new object();
        private readonly List<IndexChunk> _chunks = new List<IndexChunk>();
        private int _count;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public static IndexBuilder ContinueFrom(IndexData data)
        {
            var builder = new IndexBuilder();
            builder._chunks.AddRange(data.Chunks);
            builder._count = data.Count;
            if (builder._chunks.Count > 0)
            {
                IndexChunk last = builder._chunks[builder._chunks.Count - 1];
                if (!last.IsFull)
                {
                    builder._chunks[builder._chunks.Count - 1] = last.CloneForAppend();
                }
            }

            return builder;
        }

        /// <summary>The chunk at <paramref name="index"/> as the builder holds it (the copy, for the last chunk of a continued index).</summary>
        public IndexChunk ChunkAt(int index)
        {
            lock (_gate)
            {
                return _chunks[index];
            }
        }

        /// <summary>Replaces a chunk (a live update's copy with changed flags, sizes or dates).</summary>
        public void ReplaceChunk(int index, IndexChunk chunk)
        {
            lock (_gate)
            {
                _chunks[index] = chunk;
            }
        }

        /// <summary>Appends the batch; returns the id of its first entry. The batch can be reused right after.</summary>
        public int Append(EntryBatch batch)
        {
            lock (_gate)
            {
                int firstId = _count;
                int foldedOffset = 0;
                int originalOffset = 0;
                for (int i = 0; i < batch.Count; i++)
                {
                    IndexChunk chunk = CurrentChunk();
                    int slot = chunk.Count;
                    chunk.Parent[slot] = batch.Parent[i];
                    chunk.Flags[slot] = batch.Flags[i];
                    chunk.Kind[slot] = batch.Kind[i];
                    chunk.Depth[slot] = batch.Depth[i];
                    chunk.Size[slot] = batch.Size[i];
                    chunk.Ticks[slot] = batch.Ticks[i];

                    int foldedLength = batch.FoldedLength[i];
                    Reserve(ref chunk.Folded, chunk.FoldedLength + foldedLength + 1);
                    chunk.FoldedStart[slot] = chunk.FoldedLength;
                    Buffer.BlockCopy(batch.FoldedBytes, foldedOffset, chunk.Folded, chunk.FoldedLength, foldedLength);
                    chunk.FoldedLength += foldedLength;
                    chunk.Folded[chunk.FoldedLength++] = 0;
                    chunk.FoldedStart[slot + 1] = chunk.FoldedLength;
                    foldedOffset += foldedLength;

                    int originalLength = batch.OriginalLength[i];
                    chunk.OriginalStart[slot] = chunk.OriginalLength;
                    if (originalLength > 0)
                    {
                        Reserve(ref chunk.Original, chunk.OriginalLength + originalLength);
                        Buffer.BlockCopy(batch.OriginalBytes, originalOffset, chunk.Original, chunk.OriginalLength, originalLength);
                        chunk.OriginalLength += originalLength;
                        originalOffset += originalLength;
                    }

                    chunk.OriginalStart[slot + 1] = chunk.OriginalLength;
                    chunk.Count++;
                    _count++;
                }

                return firstId;
            }
        }

        /// <summary>The finished index; the builder must not be used after.</summary>
        public IndexData Finish()
        {
            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    IndexChunk last = _chunks[_chunks.Count - 1];
                    last.Trim();
                }

                return new IndexData(_chunks.ToArray(), _count);
            }
        }

        private IndexChunk CurrentChunk()
        {
            if (_chunks.Count == 0 || _chunks[_chunks.Count - 1].IsFull)
            {
                if (_chunks.Count > 0)
                {
                    _chunks[_chunks.Count - 1].Trim(); // full: only the name buffers have slack
                }

                _chunks.Add(IndexChunk.CreateEmpty());
            }

            return _chunks[_chunks.Count - 1];
        }

        private static void Reserve(ref byte[] buffer, int needed)
        {
            if (buffer.Length < needed)
            {
                Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
            }
        }
    }
}
