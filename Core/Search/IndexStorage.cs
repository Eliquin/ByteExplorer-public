// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ByteSearcher
{
    /// <summary>
    /// The index on disk: one file, <c>index.bxi</c>.
    /// <para>
    /// Layout (little endian): magic "BXIDX-02", version, an endianness marker, entry count, build date and duration,
    /// partial flag, the roots, the chunk count; then per chunk its counts and columns (parents, flags, kinds, depths,
    /// sizes, dates, folded name offsets and bytes, original name offsets and bytes), each written straight from its array;
    /// then the magic "BXIDXEND". Loading reads each column straight into its array and checks every offset and parent
    /// link, so a damaged file is refused (and the index rebuilt) instead of crashing a search later.
    /// </para>
    /// <para>
    /// Saving writes <c>index.bxi.tmp</c>, flushes it to the disk and renames it over the old file, which is atomic: a
    /// crash, a full disk or a cancelled build at any moment leaves the previous index intact. (The old format wrote four
    /// files in place while crawling, so an interrupted build left files that looked valid but did not match each other.)
    /// </para>
    /// </summary>
    internal static class IndexStorage
    {
        public const string FileName = "index.bxi";
        private const int Version = 2;
        private const int EndianMarker = 0x01020304;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BXIDX-02");
        private static readonly byte[] EndMagic = Encoding.ASCII.GetBytes("BXIDXEND");

        public static void Save(IndexData data, string path)
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(EndianMarker);
                writer.Write(data.Count);
                writer.Write(data.BuildDateUtc.Ticks);
                writer.Write(data.BuildDuration.Ticks);
                writer.Write(data.IsPartial ? 1 : 0);
                writer.Write(data.Roots.Length);
                foreach (string root in data.Roots)
                {
                    writer.Write(root ?? string.Empty);
                }

                writer.Write(data.Chunks.Length);
                writer.Flush();
                foreach (IndexChunk chunk in data.Chunks)
                {
                    int count = chunk.Count;
                    writer.Write(count);
                    writer.Write(chunk.FoldedLength);
                    writer.Write(chunk.OriginalLength);
                    writer.Flush();
                    Write(stream, chunk.Parent, count);
                    stream.Write(chunk.Flags, 0, count);
                    stream.Write(chunk.Kind, 0, count);
                    stream.Write(chunk.Depth, 0, count);
                    Write(stream, chunk.Size, count);
                    Write(stream, chunk.Ticks, count);
                    Write(stream, chunk.FoldedStart, count + 1);
                    stream.Write(chunk.Folded, 0, chunk.FoldedLength);
                    Write(stream, chunk.OriginalStart, count + 1);
                    stream.Write(chunk.Original, 0, chunk.OriginalLength);
                }

                stream.Write(EndMagic);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>Reads an index; throws InvalidDataException (or an IOException) when the file is not a complete, consistent index.</summary>
        public static IndexData Load(string path)
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new InvalidDataException("The index format is little-endian.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            Span<byte> magic = stackalloc byte[8];
            stream.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic) || reader.ReadInt32() != Version || reader.ReadInt32() != EndianMarker)
            {
                throw new InvalidDataException("Not an index of this version.");
            }

            int count = reader.ReadInt32();
            var buildDate = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var duration = new TimeSpan(reader.ReadInt64());
            bool partial = reader.ReadInt32() != 0;
            int rootCount = reader.ReadInt32();
            if (count < 0 || rootCount < 0 || rootCount > 10_000)
            {
                throw new InvalidDataException("Corrupt index header.");
            }

            var roots = new string[rootCount];
            for (int i = 0; i < rootCount; i++)
            {
                roots[i] = reader.ReadString();
            }

            int chunkCount = reader.ReadInt32();
            if (chunkCount != (count + IndexChunk.Capacity - 1) / IndexChunk.Capacity)
            {
                throw new InvalidDataException("Corrupt index: chunk count does not match the entry count.");
            }

            var chunks = new IndexChunk[chunkCount];
            int idBase = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                int chunkEntries = reader.ReadInt32();
                int foldedLength = reader.ReadInt32();
                int originalLength = reader.ReadInt32();
                bool last = c == chunkCount - 1;
                if (chunkEntries <= 0 || chunkEntries > IndexChunk.Capacity || (!last && chunkEntries != IndexChunk.Capacity) ||
                    foldedLength < 0 || originalLength < 0)
                {
                    throw new InvalidDataException("Corrupt index chunk header.");
                }

                var chunk = new IndexChunk
                {
                    Count = chunkEntries,
                    Parent = new int[chunkEntries],
                    Flags = new byte[chunkEntries],
                    Kind = new byte[chunkEntries],
                    Depth = new byte[chunkEntries],
                    Size = new long[chunkEntries],
                    Ticks = new long[chunkEntries],
                    FoldedStart = new int[chunkEntries + 1],
                    Folded = new byte[foldedLength],
                    FoldedLength = foldedLength,
                    OriginalStart = new int[chunkEntries + 1],
                    Original = new byte[originalLength],
                    OriginalLength = originalLength
                };
                Read(stream, chunk.Parent.AsSpan());
                stream.ReadExactly(chunk.Flags);
                stream.ReadExactly(chunk.Kind);
                stream.ReadExactly(chunk.Depth);
                Read(stream, chunk.Size.AsSpan());
                Read(stream, chunk.Ticks.AsSpan());
                Read(stream, chunk.FoldedStart.AsSpan());
                stream.ReadExactly(chunk.Folded);
                Read(stream, chunk.OriginalStart.AsSpan());
                stream.ReadExactly(chunk.Original);
                Validate(chunk, idBase);
                chunks[c] = chunk;
                idBase += chunkEntries;
            }

            stream.ReadExactly(magic);
            if (idBase != count || !magic.SequenceEqual(EndMagic))
            {
                throw new InvalidDataException("The index file is incomplete.");
            }

            return new IndexData(chunks, count)
            {
                BuildDateUtc = buildDate,
                BuildDuration = duration,
                IsPartial = partial,
                Roots = roots
            };
        }

        /// <summary>Every offset inside its buffer and increasing, every name ending with its 0 byte, every parent an earlier entry.</summary>
        private static void Validate(IndexChunk chunk, int idBase)
        {
            if (chunk.FoldedStart[0] != 0 || chunk.FoldedStart[chunk.Count] != chunk.FoldedLength ||
                chunk.OriginalStart[0] != 0 || chunk.OriginalStart[chunk.Count] != chunk.OriginalLength)
            {
                throw new InvalidDataException("Corrupt index: name offsets do not cover the name data.");
            }

            for (int k = 0; k < chunk.Count; k++)
            {
                int start = chunk.FoldedStart[k];
                int end = chunk.FoldedStart[k + 1];
                if (end <= start || chunk.Folded[end - 1] != 0 || chunk.OriginalStart[k + 1] < chunk.OriginalStart[k])
                {
                    throw new InvalidDataException("Corrupt index: bad name offsets.");
                }

                int parent = chunk.Parent[k];
                if (parent < -1 || parent >= idBase + k)
                {
                    throw new InvalidDataException("Corrupt index: bad parent link.");
                }
            }
        }

        /// <summary>
        /// Writes the values' raw bytes. Takes the array segment (not a span) so the element type is inferred under C# 13
        /// too: before C# 14's span conversions, a Span&lt;T&gt; argument does not infer T for a ReadOnlySpan&lt;T&gt; parameter.
        /// </summary>
        private static void Write<T>(Stream stream, T[] values, int count) where T : unmanaged =>
            stream.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(values, 0, count)));

        private static void Read<T>(Stream stream, Span<T> values) where T : unmanaged =>
            stream.ReadExactly(MemoryMarshal.AsBytes(values));
    }
}
