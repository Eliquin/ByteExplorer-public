// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// Native single-file copy/move — CopyFileEx for real byte-level
    /// progress and long-path support that Microsoft.VisualBasic.FileIO
    /// doesn't expose; MoveFileEx for same-volume moves, which are an
    /// atomic filesystem rename with no "half-moved" state possible,
    /// so they need none of the temp-file/verify/commit machinery a
    /// cross-volume move does (see FileOperationService).
    ///
    /// Deliberately scoped to single files only — directory-tree
    /// operations stay on the existing, proven Microsoft.VisualBasic.
    /// FileIO path. A full recursive native directory engine is
    /// meaningfully more work (streaming enumeration, bounded memory
    /// for millions of entries) and isn't worth the added risk here
    /// when the existing path already works.
    /// </summary>
    internal static class NativeFileOperations
    {
        /// <summary>
        /// Blocking, native copy with real progress reporting. The
        /// progress callback is a plain local closure, not a GCHandle-
        /// pinned delegate with a userdata pointer — safe because
        /// CopyFileEx is synchronous: the CLR keeps any argument to an
        /// in-progress call alive for that call's duration, so there's
        /// no window where the GC could collect the delegate while
        /// native code might still invoke it. That's only true because
        /// this call blocks; it would NOT be safe for a callback
        /// registered for later, asynchronous invocation.
        ///
        /// Cancellation is tracked explicitly via a local flag set
        /// inside the callback, rather than inferred from the resulting
        /// Win32 error code afterward — more robust than trusting a
        /// specific error code (ERROR_REQUEST_ABORTED) to always mean
        /// "we cancelled this," since that's inference, not fact.
        /// </summary>
        /// <summary>Files at least this large are copied unbuffered.</summary>
        internal const long UnbufferedCopyThreshold = 256L * 1024 * 1024;

        /// <param name="flushToDisk">
        /// Wait until the copy is on the disk, not just in the system's write cache, before returning: required before the
        /// only other copy (a moved file's source) is deleted, or a power cut can lose both.
        /// </param>
        internal static void CopyFile(string source, string destination, Action<long, long> onProgress, CancellationToken cancellationToken, bool largeFile = false, bool flushToDisk = false)
        {
            if (!OperatingSystem.IsWindows())
            {
                UnixFileOperations.CopyFile(source, destination, onProgress, cancellationToken, flushToDisk);
                return;
            }

            string extendedSource = ExtendedPath.Prefix(source);
            string extendedDestination = ExtendedPath.Prefix(destination);
            bool cancelledByToken = false;

            NativeFileOperationMethods.CopyProgressResult ProgressCallback(
                long totalFileSize,
                long totalBytesTransferred,
                long streamSize,
                long streamBytesTransferred,
                uint streamNumber,
                uint callbackReason,
                IntPtr sourceFile,
                IntPtr destinationFile,
                IntPtr data)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelledByToken = true;
                    return NativeFileOperationMethods.CopyProgressResult.Cancel;
                }

                onProgress?.Invoke(totalBytesTransferred, totalFileSize);
                return NativeFileOperationMethods.CopyProgressResult.Continue;
            }

            // FAIL_IF_EXISTS: the caller picked a free name, but a file can appear there before the copy starts; never
            // overwrite it. Unbuffered I/O for large files: faster, and it doesn't push the whole file through the
            // system cache (evicting everything else), as Explorer and robocopy /J do.
            uint flags = NativeFileOperationMethods.COPY_FILE_FAIL_IF_EXISTS;
            if (largeFile)
            {
                flags |= NativeFileOperationMethods.COPY_FILE_NO_BUFFERING;
            }

            bool succeeded = NativeFileOperationMethods.CopyFileEx(
                extendedSource,
                extendedDestination,
                ProgressCallback,
                IntPtr.Zero,
                IntPtr.Zero,
                flags);

            if (!succeeded)
            {
                int errorCode = Marshal.GetLastWin32Error();

                if (cancelledByToken)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new Win32Exception(errorCode);
            }

            if (flushToDisk)
            {
                // CopyFileEx leaves the data in the cache; FlushFileBuffers (Flush(true)) writes it out. Opened for write
                // only to be allowed to flush: nothing is written.
                using var stream = new FileStream(extendedDestination, FileMode.Open, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
                stream.Flush(flushToDisk: true);
            }
        }

        /// <summary>
        /// Makes a rename into <paramref name="folder"/> durable (Linux, macOS: fsync of the folder, so the new name survives a
        /// power cut; NTFS journals renames itself). Best effort: a file system that can't sync a folder is not an error.
        /// </summary>
        internal static void DropFromCache(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                UnixFileOperations.DropFromCache(path);
            }
        }

        internal static void SyncDirectory(string folder)
        {
            if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(folder))
            {
                UnixFileOperations.SyncDirectory(folder);
            }
        }

        /// <summary>
        /// Same-volume move — an atomic rename at the filesystem level,
        /// not a copy. Deliberately does NOT pass MOVEFILE_COPY_ALLOWED:
        /// if source and destination somehow aren't on the same volume
        /// after all, this should fail loudly rather than silently fall
        /// back to a slow copy the caller didn't ask for.
        /// </summary>
        internal static void MoveFile(string source, string destination, bool writeThrough)
        {
            if (!OperatingSystem.IsWindows())
            {
                UnixFileOperations.Move(source, destination);
                return;
            }

            string extendedSource = ExtendedPath.Prefix(source);
            string extendedDestination = ExtendedPath.Prefix(destination);

            uint flags = writeThrough ? NativeFileOperationMethods.MOVEFILE_WRITE_THROUGH : 0;

            bool succeeded = NativeFileOperationMethods.MoveFileEx(extendedSource, extendedDestination, flags);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        internal static int GetWin32ErrorCode(Exception ex)
        {
            if (ex is Win32Exception win32)
            {
                return win32.NativeErrorCode;
            }

            // .NET I/O exceptions (IOException, UnauthorizedAccessException
            // — what Microsoft.VisualBasic.FileIO.FileSystem throws for
            // directory operations, as opposed to the Win32Exception our
            // own native calls above throw) typically encode the
            // underlying Win32 error code in HResult's low 16 bits, the
            // standard HRESULT_FROM_WIN32 convention. Without this
            // fallback, retry would only ever work for the native single-
            // file path and silently never retry a transient failure
            // inside a FileSystem.CopyDirectory/MoveDirectory call.
            return ex.HResult & 0xFFFF;
        }

        /// <summary>
        /// Transient conditions worth retrying (a momentary AV-scan
        /// lock, a brief network hiccup) vs. permanent ones that
        /// retrying can't fix (invalid path, access genuinely denied,
        /// disk genuinely full) and shouldn't be retried — retrying a
        /// permanent error just delays an inevitable failure.
        /// </summary>
        /// <summary>
        /// True if the move failed because source and destination are on different volumes (Windows
        /// ERROR_NOT_SAME_DEVICE, Unix EXDEV): the caller falls back to copy + delete.
        /// </summary>
        internal static bool IsCrossDeviceError(Exception ex)
        {
            if (OperatingSystem.IsWindows())
            {
                return GetWin32ErrorCode(ex) == NativeFileOperationMethods.ERROR_NOT_SAME_DEVICE;
            }

            return UnixFileOperations.IsCrossDevice(ex);
        }

        /// <summary>The destination is full (Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL, Unix ENOSPC / EDQUOT).</summary>
        internal static bool IsDiskFull(Exception ex)
        {
            if (OperatingSystem.IsWindows())
            {
                int code = GetWin32ErrorCode(ex);
                return code == 112 || code == 39;
            }

            return ex is IOException && (ex.HResult == 28 || ex.HResult == (OperatingSystem.IsMacOS() ? 69 : 122));
        }

        internal static bool IsTransientError(Exception ex)
        {
            if (!OperatingSystem.IsWindows())
            {
                return UnixFileOperations.IsTransient(ex);
            }

            int code = GetWin32ErrorCode(ex);

            switch (code)
            {
                case NativeFileOperationMethods.ERROR_SHARING_VIOLATION:
                case NativeFileOperationMethods.ERROR_LOCK_VIOLATION:
                case NativeFileOperationMethods.ERROR_NETWORK_BUSY:
                case NativeFileOperationMethods.ERROR_BAD_NETPATH:
                case NativeFileOperationMethods.ERROR_NETNAME_DELETED:
                case NativeFileOperationMethods.ERROR_SEM_TIMEOUT:
                case NativeFileOperationMethods.ERROR_REQUEST_ABORTED:
                case NativeFileOperationMethods.ERROR_CONNECTION_ABORTED:
                case NativeFileOperationMethods.ERROR_CONNECTION_RESET:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Permanent delete (no Recycle Bin) — used internally to remove
        /// a source file after a cross-volume move has been copied,
        /// verified, and committed. This is deliberately NOT the
        /// user-facing delete: from the user's perspective nothing was
        /// deleted, the content just relocated, so a Recycle Bin entry
        /// here would just be a confusing orphan. The actual user-facing
        /// Delete command stays on Microsoft.VisualBasic.FileIO.FileSystem
        /// specifically for its Recycle Bin support — see
        /// FileOperationService.DeleteAsync.
        ///
        /// Clears the read-only attribute first if set — a real,
        /// confirmed bug this fixes: DeleteFileW fails with
        /// ERROR_ACCESS_DENIED on a read-only file regardless of
        /// privilege level (admin rights don't bypass this), and unlike
        /// Explorer's own Shell-based delete, which handles this
        /// transparently, a raw DeleteFileW call has no such built-in
        /// behavior. This also explains why retry alone couldn't fix
        /// it: the attribute doesn't change between attempts, so it was
        /// never a transient condition to begin with.
        /// </summary>
        internal static void DeleteFilePermanently(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                UnixFileOperations.DeleteFile(path);
                return;
            }

            string extendedPath = ExtendedPath.Prefix(path);

            ClearReadOnlyAttributeIfSet(extendedPath);

            if (!NativeFileOperationMethods.DeleteFile(extendedPath))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static void ClearReadOnlyAttributeIfSet(string extendedPath)
        {
            uint attributes = NativeFileOperationMethods.GetFileAttributes(extendedPath);
            if (attributes == NativeFileOperationMethods.INVALID_FILE_ATTRIBUTES)
            {
                // Couldn't read attributes — let the delete call itself
                // surface whatever the real problem is, rather than
                // silently swallowing this and proceeding blind.
                return;
            }

            if ((attributes & NativeFileOperationMethods.FILE_ATTRIBUTE_READONLY) != 0)
            {
                NativeFileOperationMethods.SetFileAttributes(extendedPath, attributes & ~NativeFileOperationMethods.FILE_ATTRIBUTE_READONLY);
            }
        }

        /// <summary>
        /// Best-effort cleanup of a temp/partial file after a failure —
        /// must never itself throw, since it runs inside an exception
        /// handler and a cleanup failure must never mask the original
        /// error that triggered the cleanup.
        /// </summary>
        /// <summary>
        /// Deletes a directory tree permanently. Directory reparse points (junctions, symlinks) are removed as links,
        /// never followed: following one would delete the contents of its target, which lies outside this tree.
        /// Read-only files are deleted too, as the shell does.
        /// </summary>
        internal static void DeleteDirectoryTreePermanently(string path, CancellationToken cancellationToken)
        {
            foreach (Models.DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(path, cancellationToken))
            {
                string child = System.IO.Path.Combine(path, entry.Name);
                if (!entry.IsDirectory)
                {
                    DeleteFilePermanently(child);
                }
                else if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    RemoveDirectoryOrThrow(child);
                }
                else
                {
                    DeleteDirectoryTreePermanently(child, cancellationToken);
                }
            }

            RemoveDirectoryOrThrow(path);
        }

        private static void RemoveDirectoryOrThrow(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                UnixFileOperations.RemoveEmptyDirectoryOrLink(path);
                return;
            }

            string extendedPath = ExtendedPath.Prefix(path);
            ClearReadOnlyAttributeIfSet(extendedPath);
            if (!NativeFileOperationMethods.RemoveDirectory(extendedPath))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        /// <summary>
        /// Sends the given items to the Recycle Bin in one shell call (one call per item made recycling a large
        /// selection take seconds). Returns false if the shell reported any failure or abort, in which case the
        /// caller finds out per item what is left. Paths must be shorter than MAX_PATH: SHFileOperation does not
        /// accept longer ones.
        /// </summary>
        internal static bool TryRecycle(System.Collections.Generic.IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                return Platform.Trash.TryMoveToTrash(paths);
            }

            // Double-null-terminated list.
            var builder = new System.Text.StringBuilder();
            foreach (string path in paths)
            {
                builder.Append(path).Append('\0');
            }

            builder.Append('\0');
            string from = builder.ToString();
            ushort flags = (ushort)(NativeFileOperationMethods.FOF_ALLOWUNDO | NativeFileOperationMethods.FOF_NOCONFIRMATION |
                                    NativeFileOperationMethods.FOF_SILENT | NativeFileOperationMethods.FOF_NOERRORUI |
                                    NativeFileOperationMethods.FOF_WANTNUKEWARNING);
            int result;
            bool aborted;
            if (IntPtr.Size == 8)
            {
                var op = new NativeFileOperationMethods.SHFILEOPSTRUCT64 { wFunc = NativeFileOperationMethods.FO_DELETE, pFrom = from, fFlags = flags };
                result = NativeFileOperationMethods.SHFileOperation64(ref op);
                aborted = op.fAnyOperationsAborted;
            }
            else
            {
                var op = new NativeFileOperationMethods.SHFILEOPSTRUCT32 { wFunc = NativeFileOperationMethods.FO_DELETE, pFrom = from, fFlags = flags };
                result = NativeFileOperationMethods.SHFileOperation32(ref op);
                aborted = op.fAnyOperationsAborted;
            }

            return result == 0 && !aborted;
        }

        /// <summary>
        /// A symbolic link or junction: the item has a link target (dangling links included). Other reparse points, such as
        /// OneDrive placeholders, are ordinary files and folders.
        /// </summary>
        internal static bool IsLink(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LinkTarget != null)
                {
                    return true;
                }

                return Directory.Exists(path) && new DirectoryInfo(path).LinkTarget != null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Recreates the link at <paramref name="destination"/> with the same target text (a relative target stays relative).
        /// Windows needs Developer Mode or administrator rights to create symbolic links: without them this fails with a
        /// message saying so, and the link is reported, never replaced by a copy of what it points to.
        /// </summary>
        internal static void CopyLink(string source, string destination)
        {
            // Windows marks a folder link as a directory even when its target is gone; on Unix a link has no kind of its own.
            bool isDirectory = OperatingSystem.IsWindows()
                ? (new FileInfo(source).Attributes & FileAttributes.Directory) != 0
                : Directory.Exists(source);
            string target = (isDirectory ? new DirectoryInfo(source).LinkTarget : new FileInfo(source).LinkTarget)
                            ?? throw new IOException($"\"{Path.GetFileName(source)}\" is no longer a link.");
            try
            {
                if (isDirectory)
                {
                    Directory.CreateSymbolicLink(destination, target);
                }
                else
                {
                    File.CreateSymbolicLink(destination, target);
                }
            }
            catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows())
            {
                throw new IOException($"\"{Path.GetFileName(source)}\" is a link, and Windows allows creating links only with Developer Mode or as administrator. It was not copied.", ex);
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && GetWin32ErrorCode(ex) == NativeFileOperationMethods.ERROR_PRIVILEGE_NOT_HELD)
            {
                throw new IOException($"\"{Path.GetFileName(source)}\" is a link, and Windows allows creating links only with Developer Mode or as administrator. It was not copied.", ex);
            }
        }

        /// <summary>Removes a link itself (a folder link with RemoveDirectory, a file link with DeleteFile), never its target.</summary>
        internal static void DeleteLink(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path); // unlink: the link, never what it points to
                return;
            }

            if (Directory.Exists(path))
            {
                RemoveDirectoryOrThrow(path);
            }
            else
            {
                DeleteFilePermanently(path);
            }
        }

        internal static void TryDeleteTemporaryFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    DeleteFilePermanently(path);
                }
            }
            catch (Exception)
            {
                // Cleanup must never hide the original operation exception.
            }
        }
    }
}
