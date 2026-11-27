// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace ByteExplorer.Core.Services
{
    public enum VerificationMode
    {
        None = 0,

        /// <summary>Cheap — compares destination length to source
        /// length. No extra I/O beyond what's already needed to stat
        /// both files.</summary>
        Size = 1,

        /// <summary>Reads both files fully to compare content hashes.
        /// A real, meaningful cost — doubles the read I/O for the
        /// destination on top of the write that just happened — but
        /// catches silent corruption that a size match wouldn't.</summary>
        Sha256 = 2
    }

    /// <summary>
    /// Post-copy verification, run after CopyFile/before deleting the
    /// source on a cross-volume move — confirms the bytes that arrived
    /// actually match what was sent, rather than just trusting that
    /// "CopyFileEx returned success" means the destination is correct.
    /// Complementary to FileBackupService's pre-operation backup, not a
    /// replacement for it: this verifies the copy; the backup protects
    /// against the operation being interrupted at all.
    /// </summary>
    public static class FileVerification
    {
        private const int StreamBufferSize = 1024 * 1024; // 1 MiB, bounded regardless of file size

        /// <summary>
        /// Throws with a clear message on mismatch — verification
        /// failing during a Move means the source must NOT be deleted,
        /// so the caller needs a hard stop here, not a bool to
        /// potentially ignore.
        /// </summary>
        public static void Verify(VerificationMode mode, string sourcePath, string destinationPath, long expectedLength, CancellationToken cancellationToken)
        {
            switch (mode)
            {
                case VerificationMode.None:
                    return;

                case VerificationMode.Size:
                    VerifySize(destinationPath, expectedLength);
                    return;

                case VerificationMode.Sha256:
                    VerifySha256(sourcePath, destinationPath, cancellationToken);
                    return;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        private static void VerifySize(string destinationPath, long expectedLength)
        {
            long actualLength = new FileInfo(destinationPath).Length;
            if (actualLength != expectedLength)
            {
                throw new IOException($"Verification failed: destination is {actualLength:N0} bytes, expected {expectedLength:N0}.");
            }
        }

        /// <summary>
        /// Both files hashed at once (the source from wherever it is, the copy from the disk itself): a mismatch means the
        /// copy on the disk is not the source, whatever the cache holds.
        /// </summary>
        private static void VerifySha256(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            System.Threading.Tasks.Task<byte[]> source = System.Threading.Tasks.Task.Run(() => HashCached(sourcePath, cancellationToken), cancellationToken);
            byte[] destination = HashFromDisk(destinationPath, cancellationToken);
            byte[] expected = source.GetAwaiter().GetResult();
            if (!expected.AsSpan().SequenceEqual(destination))
            {
                throw new IOException("Verification failed: the copy on the disk differs from the original (SHA-256). The copy was discarded.");
            }
        }

        /// <summary>SHA-256 of a file, as hex (the Properties window and tests use it).</summary>
        public static string ComputeSha256(string path, CancellationToken cancellationToken) =>
            Convert.ToHexString(HashCached(path, cancellationToken));

        private static byte[] HashCached(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(StreamBufferSize);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, StreamBufferSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, read);
                }

                return sha.GetHashAndReset();
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// The copy's hash read from the disk, past the system cache: Windows reads it unbuffered (FILE_FLAG_NO_BUFFERING,
        /// sector-aligned buffer); Linux first puts it on the disk and drops its cached pages. Falls back to a cached read
        /// where the file system refuses (some network shares).
        /// </summary>
        private static byte[] HashFromDisk(string path, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return HashUnbuffered(path, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    return HashCached(path, cancellationToken);
                }
            }

            Native.NativeFileOperations.DropFromCache(path);
            return HashCached(path, cancellationToken);
        }

        private static unsafe byte[] HashUnbuffered(string path, CancellationToken cancellationToken)
        {
            const FileOptions NoBuffering = (FileOptions)0x20000000;
            const int Alignment = 4096; // a multiple of every sector size in use (512, 4K)
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle =
                File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoBuffering | FileOptions.SequentialScan);
            void* memory = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(StreamBufferSize, Alignment);
            try
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = RandomAccess.Read(handle, new Span<byte>(memory, StreamBufferSize), offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    sha.AppendData(new ReadOnlySpan<byte>(memory, read));
                    offset += read;
                    if (read < StreamBufferSize)
                    {
                        break; // the end of the file (unbuffered reads return whole sectors, the last one short)
                    }
                }

                return sha.GetHashAndReset();
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.AlignedFree(memory);
            }
        }
    }
}
