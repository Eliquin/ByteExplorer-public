// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ByteExplorer.Core.Security
{
    /// <summary>
    /// The layout of a password-protected item (a <c>.bxlock</c> file): a plain header, a block that proves the password,
    /// then the item's content, encrypted in chunks.
    /// <code>
    ///  0  "BXLOCK1\0"                 magic, 8 bytes
    ///  8  version (uint16 LE)          1
    /// 10  key derivation (byte)        1 = PBKDF2-HMAC-SHA256
    /// 11  flags (byte)                 bit 0: the protected item is a folder
    /// 12  iterations (int32 LE)        PBKDF2 rounds
    /// 16  salt                         16 random bytes
    /// 32  nonce prefix                 8 random bytes
    /// 40  chunk size (int32 LE)        plaintext bytes per chunk (the last one may be shorter, or empty)
    /// 44  reserved                     4 zero bytes
    /// 48  verifier                     16 known bytes, AES-256-GCM encrypted, + 16-byte tag
    /// 80  chunks                       each AES-256-GCM: ciphertext + 16-byte tag
    /// </code>
    /// <para>
    /// The key is PBKDF2-HMAC-SHA256 of the password (UTF-8) with the file's own salt, so the same password gives every
    /// file a different key. Each chunk's nonce is the file's random prefix followed by the chunk's number (big-endian),
    /// and its additional data is the header (bytes 0–47), the chunk number and a "last chunk" flag. So a changed header,
    /// a changed, swapped or removed chunk, or a file cut short (its last chunk was not written as the last) fails
    /// authentication instead of decrypting to something else. The verifier uses the chunk number 0xFFFFFFFF, which no
    /// chunk can have, and lets a wrong password be told apart before any content is read.
    /// </para>
    /// <para>
    /// The plaintext is a ZIP archive holding the item (a file, or a folder with everything in it), so a protected folder
    /// can be browsed without decrypting all of it: the decrypting stream is seekable, chunk by chunk.
    /// </para>
    /// </summary>
    public static class ProtectedFormat
    {
        public const string Extension = ".bxlock";

        public const int HeaderLength = 80;
        public const int AuthenticatedHeaderLength = 48;
        public const int TagLength = 16;
        public const int SaltLength = 16;
        public const int NoncePrefixLength = 8;
        public const ushort Version = 1;
        public const byte KdfPbkdf2Sha256 = 1;
        public const byte FlagFolder = 1;

        /// <summary>PBKDF2 rounds for new files (OWASP's 2023 figure for PBKDF2-HMAC-SHA256).</summary>
        public const int DefaultIterations = 600_000;

        /// <summary>Files claiming fewer or more rounds are refused (too weak to be ours; or a crafted file that would hang the check).</summary>
        public const int MinIterations = 100_000;
        public const int MaxIterations = 10_000_000;

        public const int DefaultChunkSize = 1 << 20;
        public const int MinChunkSize = 4 << 10;
        public const int MaxChunkSize = 16 << 20;

        /// <summary>The verifier's chunk number: no content chunk has it (a file would need 4 billion chunks).</summary>
        internal const uint VerifierIndex = uint.MaxValue;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BXLOCK1\0");
        internal static readonly byte[] VerifierPlaintext = Encoding.ASCII.GetBytes("ByteExplorer-OK\0");

        /// <summary>True for a file name ending in .bxlock.</summary>
        public static bool IsProtectedFile(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for a protected file itself or a path inside one ("…/Photos.bxlock/Photos/2024"): such places are never
        /// written to the history kept on disk (frequent folders, recent files, saved tabs), so the names inside don't
        /// leak out of the encryption. A string test, no disk access.
        /// </summary>
        public static bool IsProtectedPath(string path) => FirstProtectedSegmentEnd(path) > 0;

        /// <summary>The folder holding the protected file a path is in (or the path itself when it isn't in one).</summary>
        public static string OutsideProtected(string path)
        {
            int end = FirstProtectedSegmentEnd(path);
            if (end <= 0)
            {
                return path;
            }

            string file = path.Substring(0, end);
            return PathUtil.GetParent(file) ?? path;
        }

        private static int FirstProtectedSegmentEnd(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }

            int start = 0;
            while (true)
            {
                int index = path.IndexOf(Extension, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return -1;
                }

                int end = index + Extension.Length;
                if (end == path.Length || PathUtil.IsSeparator(path[end]))
                {
                    return end;
                }

                start = end;
            }
        }

        /// <summary>Whether AES-GCM works on this system (it needs the operating system's cryptography; every supported Windows has it).</summary>
        public static bool IsSupported => AesGcm.IsSupported;

        /// <summary>Reads and checks the header (not the password). Throws <see cref="InvalidDataException"/> for anything not ours.</summary>
        public static ProtectedHeader ReadHeader(Stream stream)
        {
            byte[] raw = new byte[HeaderLength];
            stream.Position = 0;
            stream.ReadExactly(raw);
            return ParseHeader(raw);
        }

        /// <summary>Reads the header of a file on disk.</summary>
        public static ProtectedHeader ReadHeader(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            if (stream.Length < HeaderLength)
            {
                throw new InvalidDataException("This isn't a password-protected item, or it is damaged.");
            }

            return ReadHeader(stream);
        }

        internal static ProtectedHeader ParseHeader(byte[] raw)
        {
            if (raw.Length != HeaderLength || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidDataException("This isn't a password-protected item, or it is damaged.");
            }

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8));
            if (version != Version)
            {
                throw new InvalidDataException("This item was protected by a newer version of ByteExplorer.");
            }

            byte kdf = raw[10];
            int iterations = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12));
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(40));
            if (kdf != KdfPbkdf2Sha256 || iterations < MinIterations || iterations > MaxIterations
                || chunkSize < MinChunkSize || chunkSize > MaxChunkSize)
            {
                throw new InvalidDataException("This password-protected item is damaged.");
            }

            return new ProtectedHeader(raw, iterations, chunkSize, (raw[11] & FlagFolder) != 0);
        }

        /// <summary>A new header: fresh salt and nonce prefix, the verifier still empty (filled by <see cref="ProtectedKey"/>).</summary>
        internal static byte[] NewHeader(bool isFolder, int iterations, int chunkSize)
        {
            byte[] raw = new byte[HeaderLength];
            Magic.CopyTo(raw, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(8), Version);
            raw[10] = KdfPbkdf2Sha256;
            raw[11] = isFolder ? FlagFolder : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(12), iterations);
            RandomNumberGenerator.Fill(raw.AsSpan(16, SaltLength + NoncePrefixLength));
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(40), chunkSize);
            return raw;
        }

        internal static void Nonce(ReadOnlySpan<byte> header, uint index, Span<byte> nonce)
        {
            header.Slice(32, NoncePrefixLength).CopyTo(nonce);
            BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixLength), index);
        }

        /// <summary>The additional data of a chunk: header, chunk number, last-chunk flag.</summary>
        internal static void AssociatedData(ReadOnlySpan<byte> header, uint index, bool final, Span<byte> data)
        {
            header.Slice(0, AuthenticatedHeaderLength).CopyTo(data);
            BinaryPrimitives.WriteUInt32BigEndian(data.Slice(AuthenticatedHeaderLength), index);
            data[AuthenticatedHeaderLength + 4] = final ? (byte)1 : (byte)0;
        }

        internal const int AssociatedDataLength = AuthenticatedHeaderLength + 5;
        internal const int NonceLength = 12;
    }

    /// <summary>The plain header of a protected file.</summary>
    public sealed class ProtectedHeader
    {
        internal ProtectedHeader(byte[] raw, int iterations, int chunkSize, bool isFolder)
        {
            Raw = raw;
            Iterations = iterations;
            ChunkSize = chunkSize;
            IsFolder = isFolder;
        }

        internal byte[] Raw { get; }
        public int Iterations { get; }
        public int ChunkSize { get; }

        /// <summary>The protected item was a folder (it is a file otherwise).</summary>
        public bool IsFolder { get; }

        internal ReadOnlySpan<byte> Salt => Raw.AsSpan(16, ProtectedFormat.SaltLength);
    }

    /// <summary>The password is wrong (or the header was changed, which looks the same).</summary>
    public sealed class WrongPasswordException : CryptographicException
    {
        public WrongPasswordException()
            : base("The password is wrong.")
        {
        }
    }

    /// <summary>
    /// The key of one protected file, derived from its password and salt, with the header it belongs to. The key bytes
    /// live in a pinned array (never moved or copied by the garbage collector) and are zeroed on <see cref="Dispose"/>.
    /// Thread-safe for reading: each stream makes its own AES-GCM instance from it.
    /// </summary>
    public sealed class ProtectedKey : IDisposable
    {
        private readonly byte[] _key;
        private bool _disposed;

        private ProtectedKey(byte[] key, byte[] header)
        {
            _key = key;
            Header = header;
        }

        /// <summary>The 80-byte header (with the verifier) this key belongs to.</summary>
        internal byte[] Header { get; }

        public bool IsFolder => (Header[11] & ProtectedFormat.FlagFolder) != 0;

        /// <summary>
        /// Derives the key of an existing file and checks it against the verifier. Slow on purpose (hundreds of
        /// milliseconds): call it off the UI thread. Throws <see cref="WrongPasswordException"/>.
        /// </summary>
        public static ProtectedKey Unlock(ProtectedHeader header, string password)
        {
            byte[] key = Derive(password, header.Salt, header.Iterations);
            byte[] raw = header.Raw;
            Span<byte> nonce = stackalloc byte[ProtectedFormat.NonceLength];
            Span<byte> data = stackalloc byte[ProtectedFormat.AssociatedDataLength];
            Span<byte> plain = stackalloc byte[ProtectedFormat.VerifierPlaintext.Length];
            ProtectedFormat.Nonce(raw, ProtectedFormat.VerifierIndex, nonce);
            ProtectedFormat.AssociatedData(raw, ProtectedFormat.VerifierIndex, true, data);
            try
            {
                using var aes = new AesGcm(key, ProtectedFormat.TagLength);
                aes.Decrypt(nonce, raw.AsSpan(48, 16), raw.AsSpan(64, ProtectedFormat.TagLength), plain, data);
                if (!plain.SequenceEqual(ProtectedFormat.VerifierPlaintext))
                {
                    throw new WrongPasswordException();
                }
            }
            catch (AuthenticationTagMismatchException)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new WrongPasswordException();
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }

            return new ProtectedKey(key, (byte[])raw.Clone());
        }

        /// <summary>A key for a new file: a fresh salt and header, the verifier written. Slow like <see cref="Unlock"/>.</summary>
        public static ProtectedKey Create(string password, bool isFolder, int iterations = ProtectedFormat.DefaultIterations, int chunkSize = ProtectedFormat.DefaultChunkSize)
        {
            byte[] header = ProtectedFormat.NewHeader(isFolder, iterations, chunkSize);
            byte[] key = Derive(password, header.AsSpan(16, ProtectedFormat.SaltLength), iterations);
            Span<byte> nonce = stackalloc byte[ProtectedFormat.NonceLength];
            Span<byte> data = stackalloc byte[ProtectedFormat.AssociatedDataLength];
            ProtectedFormat.Nonce(header, ProtectedFormat.VerifierIndex, nonce);
            ProtectedFormat.AssociatedData(header, ProtectedFormat.VerifierIndex, true, data);
            using (var aes = new AesGcm(key, ProtectedFormat.TagLength))
            {
                aes.Encrypt(nonce, ProtectedFormat.VerifierPlaintext, header.AsSpan(48, 16), header.AsSpan(64, ProtectedFormat.TagLength), data);
            }

            return new ProtectedKey(key, header);
        }

        private static byte[] Derive(string password, ReadOnlySpan<byte> salt, int iterations)
        {
            ArgumentException.ThrowIfNullOrEmpty(password);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
            byte[] key = GC.AllocateArray<byte>(32, pinned: true);
            try
            {
                Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, key, iterations, HashAlgorithmName.SHA256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            return key;
        }

        internal AesGcm NewCipher()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new AesGcm(_key, ProtectedFormat.TagLength);
        }

        /// <summary>The same key for the same file, owned separately (the session cache and an operation each dispose their own).</summary>
        public ProtectedKey Clone()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] copy = GC.AllocateArray<byte>(32, pinned: true);
            _key.CopyTo(copy, 0);
            return new ProtectedKey(copy, (byte[])Header.Clone());
        }

        /// <summary>True when this key belongs to the file whose header this is (same salt and verifier).</summary>
        public bool Matches(ProtectedHeader header) => header.Raw.AsSpan().SequenceEqual(Header);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                CryptographicOperations.ZeroMemory(_key);
            }
        }
    }
}
