// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace ByteSearcher
{
    /// <summary>
    /// One immutable file of the content index: terms in byte order, each with the sorted ids of the documents holding
    /// it. Layout:
    /// <code>
    /// "BXCSEG01"
    /// per term:  varint length, UTF-8 bytes, varint document count, varint posting bytes, posting ids (delta varints)
    /// sparse:    varint count, then every 64th term: varint length, bytes, varint offset of that term
    /// trailer:   int64 sparse offset, int64 term count, int64 posting count, "BXCSEND1"
    /// </code>
    /// Only the sparse table (one term in 64) is held in memory: a lookup binary-searches it and reads one run of terms
    /// from disk with positional reads (thread-safe, no shared file position), so a segment of millions of terms costs a
    /// few hundred kilobytes of memory. A prefix lookup reads the whole range of terms starting with the prefix.
    /// </summary>
    internal sealed class ContentSegment : IDisposable
    {
        public const int SparseStep = 64;
        private static readonly byte[] Magic = "BXCSEG01"u8.ToArray();
        private static readonly byte[] EndMagic = "BXCSEND1"u8.ToArray();
        private const int TrailerLength = 32;

        private readonly SafeFileHandle _handle;
        private readonly byte[][] _sparseTerms;
        private readonly long[] _sparseOffsets;
        private readonly long _termsEnd;

        private ContentSegment(string path, SafeFileHandle handle, byte[][] sparseTerms, long[] sparseOffsets, long termsEnd, long termCount, long postingCount, long length)
        {
            Path = path;
            _handle = handle;
            _sparseTerms = sparseTerms;
            _sparseOffsets = sparseOffsets;
            _termsEnd = termsEnd;
            TermCount = termCount;
            PostingCount = postingCount;
            Length = length;
        }

        public string Path { get; }

        public string Name => System.IO.Path.GetFileName(Path);

        public long TermCount { get; }

        public long PostingCount { get; }

        /// <summary>File size in bytes.</summary>
        public long Length { get; }

        // ------------------------------------------------------------------ reading

        /// <summary>Opens a segment file, checking its header and trailer. Throws InvalidDataException when damaged.</summary>
        public static ContentSegment Open(string path)
        {
            SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
            try
            {
                long length = RandomAccess.GetLength(handle);
                if (length < Magic.Length + TrailerLength)
                {
                    throw new InvalidDataException("Content index segment too short.");
                }

                Span<byte> header = stackalloc byte[8];
                RandomAccess.Read(handle, header, 0);
                Span<byte> trailer = stackalloc byte[TrailerLength];
                RandomAccess.Read(handle, trailer, length - TrailerLength);
                if (!header.SequenceEqual(Magic) || !trailer.Slice(24).SequenceEqual(EndMagic))
                {
                    throw new InvalidDataException("Not a content index segment.");
                }

                long sparseOffset = BinaryPrimitives.ReadInt64LittleEndian(trailer);
                long termCount = BinaryPrimitives.ReadInt64LittleEndian(trailer.Slice(8));
                long postingCount = BinaryPrimitives.ReadInt64LittleEndian(trailer.Slice(16));
                long sparseLength = length - TrailerLength - sparseOffset;
                if (sparseOffset < Magic.Length || sparseLength < 1 || sparseLength > int.MaxValue / 2 || termCount < 0)
                {
                    throw new InvalidDataException("Content index segment trailer is damaged.");
                }

                byte[] sparse = new byte[sparseLength];
                RandomAccess.Read(handle, sparse, sparseOffset);
                int position = 0;
                long count = ReadVarint(sparse, ref position);
                if (count < 0 || count > sparseLength)
                {
                    throw new InvalidDataException("Content index segment sparse table is damaged.");
                }

                var terms = new byte[count][];
                var offsets = new long[count];
                for (long i = 0; i < count; i++)
                {
                    int termLength = (int)ReadVarint(sparse, ref position);
                    if (termLength < 0 || position + termLength > sparse.Length)
                    {
                        throw new InvalidDataException("Content index segment sparse table is damaged.");
                    }

                    terms[i] = sparse.AsSpan(position, termLength).ToArray();
                    position += termLength;
                    offsets[i] = ReadVarint(sparse, ref position);
                    if (offsets[i] < Magic.Length || offsets[i] >= sparseOffset)
                    {
                        throw new InvalidDataException("Content index segment sparse table is damaged.");
                    }
                }

                return new ContentSegment(path, handle, terms, offsets, sparseOffset, termCount, postingCount, length);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Adds to <paramref name="ids"/> the documents of the terms equal to <paramref name="term"/> (or, with
        /// <paramref name="prefix"/>, starting with it).
        /// </summary>
        public void Collect(byte[] term, bool prefix, List<int> ids)
        {
            if (_sparseTerms.Length == 0)
            {
                return;
            }

            // The last sampled term not after the searched one: the run to read starts there.
            int low = 0;
            int high = _sparseTerms.Length - 1;
            int start = -1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                if (_sparseTerms[middle].AsSpan().SequenceCompareTo(term) <= 0)
                {
                    start = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (start < 0)
            {
                start = 0;
            }

            var reader = new SegmentReader(_handle, _sparseOffsets[start], _termsEnd);
            while (reader.Next())
            {
                ReadOnlySpan<byte> current = reader.Term;
                int compare = current.SequenceCompareTo(term);
                bool matches = prefix ? current.StartsWith(term) : compare == 0;
                if (matches)
                {
                    reader.ReadPostings(ids);
                }
                else if (compare > 0)
                {
                    break; // past the term and every term that starts with it
                }
                else
                {
                    reader.SkipPostings();
                }
            }
        }

        /// <summary>Every term with its postings in order (merging segments).</summary>
        public SegmentReader ReadAll() => new SegmentReader(_handle, Magic.Length, _termsEnd);

        public void Dispose() => _handle.Dispose();

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Writes a segment from terms in byte order, each with its ids ascending; atomically (temporary file, flushed,
        /// then renamed). Terms with no ids are left out. Returns the number of terms written.
        /// </summary>
        public static long Write(string path, IEnumerable<(byte[] Term, IReadOnlyList<int> Ids)> terms)
        {
            string temporary = path + ".tmp";
            long termCount = 0;
            long postingCount = 0;
            var sparse = new MemoryStream();
            long sparseCount = 0;
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(Magic);
                byte[] postings = new byte[1024];
                byte[] scratch = new byte[10];
                foreach ((byte[] term, IReadOnlyList<int> ids) in terms)
                {
                    if (ids == null || ids.Count == 0 || term.Length == 0)
                    {
                        continue;
                    }

                    int length = 0;
                    int previous = -1;
                    int written = 0;
                    for (int i = 0; i < ids.Count; i++)
                    {
                        int id = ids[i];
                        if (id <= previous)
                        {
                            continue; // duplicates (and anything out of order) are dropped
                        }

                        if (postings.Length - length < 5)
                        {
                            Array.Resize(ref postings, postings.Length * 2);
                        }

                        length += WriteVarint(postings.AsSpan(length), (ulong)(id - previous - 1));
                        previous = id;
                        written++;
                    }

                    if ((termCount % SparseStep) == 0)
                    {
                        sparse.Write(scratch, 0, WriteVarint(scratch, (ulong)term.Length));
                        sparse.Write(term);
                        sparse.Write(scratch, 0, WriteVarint(scratch, (ulong)stream.Position));
                        sparseCount++;
                    }

                    stream.Write(scratch, 0, WriteVarint(scratch, (ulong)term.Length));
                    stream.Write(term);
                    stream.Write(scratch, 0, WriteVarint(scratch, (ulong)written));
                    stream.Write(scratch, 0, WriteVarint(scratch, (ulong)length));
                    stream.Write(postings, 0, length);
                    termCount++;
                    postingCount += written;
                }

                long sparseOffset = stream.Position;
                stream.Write(scratch, 0, WriteVarint(scratch, (ulong)sparseCount));
                sparse.Position = 0;
                sparse.CopyTo(stream);
                Span<byte> trailer = stackalloc byte[TrailerLength];
                BinaryPrimitives.WriteInt64LittleEndian(trailer, sparseOffset);
                BinaryPrimitives.WriteInt64LittleEndian(trailer.Slice(8), termCount);
                BinaryPrimitives.WriteInt64LittleEndian(trailer.Slice(16), postingCount);
                EndMagic.CopyTo(trailer.Slice(24));
                stream.Write(trailer);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return termCount;
        }

        // ------------------------------------------------------------------ varints

        internal static int WriteVarint(Span<byte> target, ulong value)
        {
            int i = 0;
            while (value >= 0x80)
            {
                target[i++] = (byte)(value | 0x80);
                value >>= 7;
            }

            target[i++] = (byte)value;
            return i;
        }

        internal static long ReadVarint(ReadOnlySpan<byte> source, ref int position)
        {
            ulong value = 0;
            int shift = 0;
            while (position < source.Length && shift < 64)
            {
                byte b = source[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return (long)value;
                }

                shift += 7;
            }

            throw new InvalidDataException("Damaged number in the content index.");
        }

        /// <summary>
        /// Reads terms and postings forward from an offset with a 64 KB buffer of positional reads (each reader has its own,
        /// so any number can read one segment at once).
        /// </summary>
        internal sealed class SegmentReader
        {
            private readonly SafeFileHandle _handle;
            private readonly long _end;
            private readonly byte[] _buffer = new byte[64 * 1024];
            private long _bufferOffset;
            private int _bufferLength;
            private int _position;
            private byte[] _term = new byte[64];
            private int _termLength;
            private int _postingCount;
            private int _postingBytes;

            public SegmentReader(SafeFileHandle handle, long start, long end)
            {
                _handle = handle;
                _end = end;
                _bufferOffset = start;
            }

            public ReadOnlySpan<byte> Term => _term.AsSpan(0, _termLength);

            public int PostingCount => _postingCount;

            /// <summary>Moves to the next term (after the current one's postings were read or skipped).</summary>
            public bool Next()
            {
                if (_bufferOffset + _position >= _end)
                {
                    return false;
                }

                _termLength = (int)ReadNumber();
                if (_termLength <= 0 || _termLength > 4096)
                {
                    throw new InvalidDataException("Damaged term in the content index.");
                }

                if (_term.Length < _termLength)
                {
                    _term = new byte[_termLength];
                }

                ReadBytes(_term.AsSpan(0, _termLength));
                _postingCount = (int)ReadNumber();
                _postingBytes = (int)ReadNumber();
                if (_postingCount < 0 || _postingBytes < 0)
                {
                    throw new InvalidDataException("Damaged postings in the content index.");
                }

                return true;
            }

            public void SkipPostings()
            {
                long target = _bufferOffset + _position + _postingBytes;
                if (target <= _bufferOffset + _bufferLength)
                {
                    _position += _postingBytes;
                }
                else
                {
                    _bufferOffset = target;
                    _bufferLength = 0;
                    _position = 0;
                }

                _postingBytes = 0;
            }

            public void ReadPostings(List<int> ids)
            {
                long previous = -1;
                for (int i = 0; i < _postingCount; i++)
                {
                    previous += ReadNumber() + 1;
                    ids.Add((int)previous);
                }

                _postingBytes = 0;
            }

            private long ReadNumber()
            {
                ulong value = 0;
                int shift = 0;
                while (shift < 64)
                {
                    byte b = ReadByte();
                    value |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80)
                    {
                        return (long)value;
                    }

                    shift += 7;
                }

                throw new InvalidDataException("Damaged number in the content index.");
            }

            private byte ReadByte()
            {
                if (_position >= _bufferLength)
                {
                    Fill();
                }

                return _buffer[_position++];
            }

            private void ReadBytes(Span<byte> target)
            {
                int done = 0;
                while (done < target.Length)
                {
                    if (_position >= _bufferLength)
                    {
                        Fill();
                    }

                    int take = Math.Min(target.Length - done, _bufferLength - _position);
                    _buffer.AsSpan(_position, take).CopyTo(target.Slice(done));
                    _position += take;
                    done += take;
                }
            }

            private void Fill()
            {
                _bufferOffset += _position;
                _position = 0;
                long remaining = _end - _bufferOffset;
                if (remaining <= 0)
                {
                    throw new InvalidDataException("Unexpected end of a content index segment.");
                }

                _bufferLength = RandomAccess.Read(_handle, _buffer.AsSpan(0, (int)Math.Min(_buffer.Length, remaining)), _bufferOffset);
                if (_bufferLength <= 0)
                {
                    throw new InvalidDataException("Unexpected end of a content index segment.");
                }
            }
        }
    }
}
