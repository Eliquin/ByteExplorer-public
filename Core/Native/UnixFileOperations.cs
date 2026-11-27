// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// The Linux/macOS side of <see cref="NativeFileOperations"/>: the same contract as the Win32 calls it stands in for
    /// (CopyFileEx with COPY_FILE_FAIL_IF_EXISTS and a progress callback, MoveFileEx without overwrite, DeleteFile,
    /// RemoveDirectory), built on managed I/O since none of those Win32 calls exist there.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static class UnixFileOperations
    {
        private const int CopyBufferSize = 1024 * 1024;

        // errno values. .NET puts the raw errno in IOException.HResult on Unix (Interop.GetExceptionForIoErrno).
        // Linux and macOS agree on the low ones and differ on the network ones, hence the pairs.
        private const int EINTR = 4;
        private const int EBUSY = 16;
        private const int EXDEV = 18;
        private const int EagainLinux = 11, EagainMac = 35;
        private const int EtimedoutLinux = 110, EtimedoutMac = 60;
        private const int EconnresetLinux = 104, EconnresetMac = 54;
        private const int EconnabortedLinux = 103, EconnabortedMac = 53;
        private const int EhostdownLinux = 112, EhostdownMac = 64;
        private const int EstaleLinux = 116, EstaleMac = 70;

        /// <summary>
        /// Copies one file without ever overwriting (the destination is opened with CreateNew, so a file that appears
        /// there after the caller picked the name makes the copy fail instead of being replaced), reporting progress
        /// per buffer and stopping on cancellation. A cancelled or failed copy deletes the partial destination it
        /// created. Keeps the source's modification time and permission bits, as CopyFileEx keeps the attributes and
        /// last-write time.
        /// </summary>
        internal static void CopyFile(string source, string destination, Action<long, long> onProgress, CancellationToken cancellationToken, bool flushToDisk = false)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            bool created = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                {
                    long total = input.Length;
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
                    {
                        created = true;
                        if (total > 0)
                        {
                            // Reserve the space up front: a full disk fails now rather than after copying most of it.
                            try
                            {
                                output.SetLength(total);
                                output.Position = 0;
                            }
                            catch (IOException)
                            {
                                // Not every file system supports preallocation (some FUSE mounts); copy anyway.
                            }
                        }

                        onProgress?.Invoke(0, total);
                        long copied = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, CopyBufferSize)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            copied += read;
                            onProgress?.Invoke(copied, total);
                        }

                        if (output.Length != copied)
                        {
                            output.SetLength(copied); // the source shrank while being copied
                        }

                        output.Flush(flushToDisk); // fsync when the source is about to be deleted
                    }
                }

                CopyMetadata(source, destination);
            }
            catch
            {
                if (created)
                {
                    TryDelete(destination);
                }

                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Renames a file or folder without overwriting. Folders: Directory.Move refuses an existing destination.
        /// Files: File.Move with overwrite false; .NET makes it atomic on Unix (link + unlink), so a file that appears at
        /// the destination is never replaced.
        /// </summary>
        internal static void Move(string source, string destination)
        {
            if (Directory.Exists(source) && !IsSymbolicLink(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination, overwrite: false);
            }
        }

        /// <summary>Deletes a file or a symbolic link (never its target). Throws if nothing is there, like DeleteFile.</summary>
        internal static void DeleteFile(string path)
        {
            if (!File.Exists(path) && !IsSymbolicLink(path))
            {
                throw new FileNotFoundException("The file does not exist.", path);
            }

            File.Delete(path);
        }

        /// <summary>Removes an empty folder, or a symbolic link to a folder without touching what it points to.</summary>
        internal static void RemoveEmptyDirectoryOrLink(string path)
        {
            if (IsSymbolicLink(path))
            {
                File.Delete(path);
                return;
            }

            Directory.Delete(path, recursive: false);
        }

        internal static bool IsCrossDevice(Exception ex) => ex is IOException && ex.HResult == EXDEV;

        /// <summary>
        /// Errors worth a retry: a busy file, an interrupted call, and the network-file-system hiccups (timeouts, resets,
        /// stale NFS handles), the Unix counterparts of the Win32 sharing/network codes retried on Windows.
        /// </summary>
        internal static bool IsTransient(Exception ex)
        {
            if (!(ex is IOException))
            {
                return false;
            }

            int errno = ex.HResult;
            switch (errno)
            {
                case EINTR:
                case EBUSY:
                    return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                return errno == EagainMac || errno == EtimedoutMac || errno == EconnresetMac || errno == EconnabortedMac
                    || errno == EhostdownMac || errno == EstaleMac;
            }

            return errno == EagainLinux || errno == EtimedoutLinux || errno == EconnresetLinux || errno == EconnabortedLinux
                || errno == EhostdownLinux || errno == EstaleLinux;
        }

        internal static bool IsSymbolicLink(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists || Directory.Exists(path)
                    ? (info.Attributes & FileAttributes.ReparsePoint) != 0
                    : info.LinkTarget != null; // a dangling link: Exists is false, but the link itself is there
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int Fsync(int fd);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
        private static extern int PosixFadvise(int fd, long offset, long length, int advice);

        /// <summary>
        /// Puts the file on the disk and drops it from the page cache (Linux: fsync, then POSIX_FADV_DONTNEED), so reading it
        /// next comes from the disk: a verification then checks what was written, not what is still in memory. Best effort.
        /// </summary>
        internal static void DropFromCache(string path)
        {
            try
            {
                int fd = Open(path, 0);
                if (fd >= 0)
                {
                    Fsync(fd);
                    if (OperatingSystem.IsLinux())
                    {
                        PosixFadvise(fd, 0, 0, 4); // POSIX_FADV_DONTNEED
                    }

                    Close(fd);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // Verified through the cache instead.
            }
        }

        /// <summary>fsync of a folder: a rename into it is on the disk once this returns. Best effort.</summary>
        internal static void SyncDirectory(string folder)
        {
            try
            {
                int fd = Open(folder, 0); // O_RDONLY: enough to fsync a directory on Linux and macOS
                if (fd >= 0)
                {
                    Fsync(fd);
                    Close(fd);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No libc symbols (unusual runtime): the rename is still in the file system's journal.
            }
        }

        private static void CopyMetadata(string source, string destination)
        {
            try
            {
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The data is copied; a file system that refuses times or modes (FAT on a USB stick) is not a failure.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Cleanup must never hide the original exception.
            }
        }
    }
}
