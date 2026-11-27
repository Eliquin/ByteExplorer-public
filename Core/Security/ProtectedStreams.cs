// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Security.Cryptography;

namespace ByteExplorer.Core.Security
{
    /// <summary>
    /// Writes a protected file: the header, then whatever is written to it, encrypted in chunks (see
    /// <see cref="ProtectedFormat"/>). Write-only and forward-only (a ZIP archive can be created on it). A full chunk is
    /// sealed only once more data follows, so the last chunk (written on <see cref="Dispose(bool)"/>) is the one marked as
    /// last, and may be full or empty. Disposing without <see cref="Complete"/> leaves the file unfinished on purpose: it
    /// then fails to open, which is right for an interrupted write.
    /// </summary>
    public sealed class EncryptingStream : Stream
    {
        private readonly Stream _output;
        private readonly bool _leaveOpen;
        private readonly AesGcm _aes;
        private readonly byte[] _header;
        private readonly byte[] _plain;
        private readonly byte[] _cipher;
        private readonly byte[] _tag = new byte[ProtectedFormat.TagLength];
        private readonly byte[] _nonce = new byte[ProtectedFormat.NonceLength];
        private readonly byte[] _data = new byte[ProtectedFormat.AssociatedDataLength];
        private int _filled;
        private uint _index;
        private long _written;
        private bool _completed;
        private bool _disposed;

        public EncryptingStream(Stream output, ProtectedKey key, bool leaveOpen = false)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _leaveOpen = leaveOpen;
            _header = key.Header;
            int chunkSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(40));
            _plain = GC.AllocateArray<byte>(chunkSize, pinned: true);
            _cipher = new byte[chunkSize];
            _aes = key.NewCipher();
            _output.Write(_header, 0, _header.Length);
        }

        /// <summary>Plaintext bytes written so far.</summary>
        public long PlaintextLength => _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("The protected file is already complete.");
            }

            while (buffer.Length > 0)
            {
                if (_filled == _plain.Length)
                {
                    Seal(final: false); // full, and more is coming: not the last chunk
                }

                int take = Math.Min(buffer.Length, _plain.Length - _filled);
                buffer.Slice(0, take).CopyTo(_plain.AsSpan(_filled));
                _filled += take;
                _written += take;
                buffer = buffer.Slice(take);
            }
        }

        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(new[] { value }));

        /// <summary>Seals the last chunk and flushes. Call once all content is written.</summary>
        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return;
            }

            Seal(final: true);
            _completed = true;
            _output.Flush();
        }

        private void Seal(bool final)
        {
            if (_index == ProtectedFormat.VerifierIndex)
            {
                throw new IOException("The item is too large to protect.");
            }

            ProtectedFormat.Nonce(_header, _index, _nonce);
            ProtectedFormat.AssociatedData(_header, _index, final, _data);
            _aes.Encrypt(_nonce, _plain.AsSpan(0, _filled), _cipher.AsSpan(0, _filled), _tag, _data);
            _output.Write(_cipher, 0, _filled);
            _output.Write(_tag, 0, _tag.Length);
            CryptographicOperations.ZeroMemory(_plain.AsSpan(0, _filled));
            _filled = 0;
            _index++;
        }

        public override void Flush()
        {
            // Nothing to flush before the chunk is full: a partial chunk can't be sealed early (it would end the file).
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                CryptographicOperations.ZeroMemory(_plain);
                _aes.Dispose();
                if (!_leaveOpen)
                {
                    _output.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Reads a protected file's content: read-only and seekable, decrypting (and authenticating) one chunk at a time, the
    /// last one kept for the next read. The length comes from the file's length (no length is stored, so none can lie).
    /// A chunk that fails authentication throws <see cref="InvalidDataException"/> ("damaged"), never returns bytes.
    /// </summary>
    public sealed class DecryptingStream : Stream
    {
        private readonly Stream _input;
        private readonly bool _leaveOpen;
        private readonly AesGcm _aes;
        private readonly byte[] _header;
        private readonly int _chunkSize;
        private readonly long _chunkCount;
        private readonly long _length;
        private readonly byte[] _plain;
        private readonly byte[] _cipher;
        private readonly byte[] _tag = new byte[ProtectedFormat.TagLength];
        private readonly byte[] _nonce = new byte[ProtectedFormat.NonceLength];
        private readonly byte[] _data = new byte[ProtectedFormat.AssociatedDataLength];
        private long _cachedChunk = -1;
        private int _cachedLength;
        private long _position;
        private bool _disposed;

        /// <summary>Opens the content of the stream (seekable) with its key. Throws when the key isn't this file's.</summary>
        public DecryptingStream(Stream input, ProtectedKey key, bool leaveOpen = false)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            if (!input.CanSeek || !input.CanRead)
            {
                throw new ArgumentException("The protected file must be readable and seekable.", nameof(input));
            }

            _leaveOpen = leaveOpen;
            ProtectedHeader header = ProtectedFormat.ReadHeader(input);
            if (!key.Matches(header))
            {
                throw new WrongPasswordException();
            }

            _header = key.Header;
            _chunkSize = header.ChunkSize;
            long record = (long)_chunkSize + ProtectedFormat.TagLength;
            long payload = input.Length - ProtectedFormat.HeaderLength;
            long full = payload / record;
            long rest = payload % record;
            if (rest == 0)
            {
                if (full == 0)
                {
                    throw Damaged();
                }

                _chunkCount = full;
                _length = full * _chunkSize;
            }
            else
            {
                if (rest < ProtectedFormat.TagLength)
                {
                    throw Damaged();
                }

                _chunkCount = full + 1;
                _length = full * _chunkSize + (rest - ProtectedFormat.TagLength);
            }

            if (_chunkCount > ProtectedFormat.VerifierIndex)
            {
                throw Damaged();
            }

            _plain = GC.AllocateArray<byte>(_chunkSize, pinned: true);
            _cipher = new byte[_chunkSize];
            _aes = key.NewCipher();
        }

        /// <summary>Opens a protected file on disk.</summary>
        public static DecryptingStream Open(string path, ProtectedKey key)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            try
            {
                return new DecryptingStream(file, key);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                long chunk = _position / _chunkSize;
                int within = (int)(_position % _chunkSize);
                Load(chunk);
                int take = Math.Min(buffer.Length, _cachedLength - within);
                if (take <= 0)
                {
                    break;
                }

                _plain.AsSpan(within, take).CopyTo(buffer);
                buffer = buffer.Slice(take);
                _position += take;
                total += take;
            }

            return total;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        private void Load(long chunk)
        {
            if (chunk == _cachedChunk)
            {
                return;
            }

            bool final = chunk == _chunkCount - 1;
            int length = final ? (int)(_length - chunk * _chunkSize) : _chunkSize;
            _input.Position = ProtectedFormat.HeaderLength + chunk * ((long)_chunkSize + ProtectedFormat.TagLength);
            _input.ReadExactly(_cipher, 0, length);
            _input.ReadExactly(_tag, 0, _tag.Length);
            ProtectedFormat.Nonce(_header, (uint)chunk, _nonce);
            ProtectedFormat.AssociatedData(_header, (uint)chunk, final, _data);
            _cachedChunk = -1;
            try
            {
                _aes.Decrypt(_nonce, _cipher.AsSpan(0, length), _tag, _plain.AsSpan(0, length), _data);
            }
            catch (AuthenticationTagMismatchException)
            {
                CryptographicOperations.ZeroMemory(_plain);
                throw Damaged();
            }

            _cachedChunk = chunk;
            _cachedLength = length;
        }

        /// <summary>
        /// Decrypts every chunk once (authenticating the whole file) and returns its SHA-256, without the bytes going
        /// anywhere else. Progress is the plaintext bytes checked so far.
        /// </summary>
        public byte[] HashAll(Action<long> progress, Func<bool> keepGoing)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long chunk = 0; chunk < _chunkCount; chunk++)
            {
                if (keepGoing != null && !keepGoing())
                {
                    throw new OperationCanceledException();
                }

                Load(chunk);
                hash.AppendData(_plain, 0, _cachedLength);
                progress?.Invoke(chunk * _chunkSize + _cachedLength);
            }

            return hash.GetHashAndReset();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
            _position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static InvalidDataException Damaged() => new InvalidDataException("This password-protected item is damaged (part of it is missing or was changed).");

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                CryptographicOperations.ZeroMemory(_plain);
                _aes?.Dispose();
                if (!_leaveOpen)
                {
                    _input.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
