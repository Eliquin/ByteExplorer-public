// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using Serilog;

namespace ByteExplorer.Core.Security
{
    /// <summary>
    /// Password protection of files and folders: an item is encrypted into one <c>.bxlock</c> file beside it (see
    /// <see cref="ProtectedFormat"/>), and only then, once the encrypted copy has been read back and every file in it
    /// matched, is the original deleted. The <c>.bxlock</c> file can be browsed like a ZIP file once unlocked, turned back
    /// into the original (Remove protection), or given another password.
    /// <para>
    /// <b>Deleting the original</b> is permanent, not to the Recycle Bin (where the unencrypted copy would stay readable).
    /// The data isn't overwritten first: on SSDs and copy-on-write file systems an overwrite doesn't reach the old blocks
    /// anyway, so traces of the original may remain on the drive until it reuses that space; the dialogs say so.
    /// </para>
    /// <para>
    /// <b>Unlocked items</b> keep their key in memory (zeroed when locked) until "Lock" or until ByteExplorer closes; files
    /// opened from them are extracted into a private temporary folder that is deleted on lock, at exit, and at the next
    /// start after a crash.
    /// </para>
    /// <para>
    /// All operations here run on the calling thread (a worker: they are slow), report progress, honour the pause gate
    /// and the cancellation token, and leave nothing half-done behind: an interrupted write deletes its partial file and
    /// keeps the original.
    /// </para>
    /// </summary>
    public static class ProtectedItems
    {
        public const int MinPasswordLength = 8;

        private const int CopyBuffer = 1 << 20;
        private const string PartialSuffix = ".partial";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Unlocked> Session = new Dictionary<string, Unlocked>(PathUtil.Comparer);

        /// <summary>Raised (on any thread) when an item is locked again, with its path; panes inside it leave it.</summary>
        public static event EventHandler<string> Locked;

        /// <summary>Where files opened from unlocked items are extracted (wiped on lock, at exit and at start).</summary>
        public static string TempRoot => Path.Combine(Path.GetTempPath(), "ByteExplorer", "unlocked");

        /// <summary>Formats that are already compressed: stored, not compressed again (faster, and no bigger).</summary>
        private static readonly HashSet<string> Compressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic",
            ".heif", ".avif", ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm",
            ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".msi", ".iso", ProtectedFormat.Extension
        };

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// Why the item can't be protected, or null when it can: it must be a local file or folder (not inside an archive or
        /// on a phone, not a drive's root), not a link, and not already protected.
        /// </summary>
        public static string CannotProtectReason(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                return "This item can't be protected.";
            }

            if (ArchiveBrowser.IsInsideArchive(path))
            {
                return "Items inside an archive can't be protected. Copy them out first.";
            }

            if (ProtectedFormat.IsProtectedFile(path))
            {
                return "This item is already protected.";
            }

            string parent = PathUtil.GetParent(path);
            if (string.IsNullOrEmpty(parent) || PathUtil.AreSame(parent, path))
            {
                return "A whole drive can't be protected. Protect the folders in it instead.";
            }

            try
            {
                var info = new FileInfo(path);
                FileAttributes attributes = info.Attributes;
                if ((int)attributes == -1 || (!info.Exists && !Directory.Exists(path)))
                {
                    return "The item no longer exists.";
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return "Links and shortcuts to folders can't be protected. Protect the item they point to.";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return ex.Message;
            }

            return ProtectedFormat.IsSupported ? null : "This system has no AES-GCM encryption, which password protection needs.";
        }

        /// <summary>Why a new password isn't acceptable, or null.</summary>
        public static string PasswordProblem(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "Type a password.";
            }

            if (password.Length < MinPasswordLength)
            {
                return $"Use at least {MinPasswordLength} characters.";
            }

            return string.IsNullOrWhiteSpace(password) ? "A password can't be only spaces." : null;
        }

        /// <summary>A rough strength label for a new password: "Weak", "Fair", "Good" or "Strong".</summary>
        public static string Strength(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return string.Empty;
            }

            int classes = (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsUpper) ? 1 : 0)
                + (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
            int score = password.Length >= 16 ? 3 : password.Length >= 12 ? 2 : password.Length >= MinPasswordLength ? 1 : 0;
            score += classes >= 3 ? 1 : 0;
            score += password.Distinct().Count() < 5 ? -1 : 0;
            return score switch
            {
                <= 0 => "Weak",
                1 => "Fair",
                2 => "Good",
                _ => "Strong"
            };
        }

        // ------------------------------------------------------------------ session

        /// <summary>True when the protected file is unlocked in this session (its key is in memory).</summary>
        public static bool IsUnlocked(string path)
        {
            if (!ProtectedFormat.IsProtectedFile(path))
            {
                return false;
            }

            lock (Gate)
            {
                return Session.ContainsKey(PathUtil.Normalize(path));
            }
        }

        /// <summary>The unlocked items' paths.</summary>
        public static IReadOnlyList<string> UnlockedPaths
        {
            get
            {
                lock (Gate)
                {
                    return Session.Keys.ToArray();
                }
            }
        }

        /// <summary>
        /// Checks the password of a protected file and returns its key (slow: call off the UI thread). Throws
        /// <see cref="WrongPasswordException"/>, or <see cref="InvalidDataException"/> when the file isn't ours.
        /// </summary>
        public static ProtectedKey Unlock(string path, string password)
        {
            ProtectedHeader header = ProtectedFormat.ReadHeader(path);
            return ProtectedKey.Unlock(header, password);
        }

        /// <summary>
        /// Keeps the file unlocked (the session gets its own copy of the key): until <see cref="Lock"/> or exit, or, with
        /// <paramref name="lockWhenLeft"/>, until no pane shows anything inside it (<see cref="LockLeft"/>).
        /// </summary>
        public static void Remember(string path, ProtectedKey key, bool lockWhenLeft)
        {
            Remember(path, key);
            lock (Gate)
            {
                if (Session.TryGetValue(PathUtil.Normalize(path), out Unlocked entry))
                {
                    entry.LockWhenLeft = lockWhenLeft;
                }
            }
        }

        /// <summary>
        /// Locks the items unlocked "until I leave" that none of the given places (the panes' folders) is in any more.
        /// </summary>
        public static void LockLeft(IReadOnlyCollection<string> placesShown)
        {
            List<string> left = null;
            lock (Gate)
            {
                foreach (KeyValuePair<string, Unlocked> pair in Session)
                {
                    if (pair.Value.LockWhenLeft && !placesShown.Any(p => !string.IsNullOrEmpty(p) && PathUtil.IsSameOrInside(PathUtil.Normalize(p), pair.Key)))
                    {
                        (left ??= new List<string>()).Add(pair.Key);
                    }
                }
            }

            if (left != null)
            {
                foreach (string path in left)
                {
                    Lock(path);
                }
            }
        }

        /// <summary>True when some unlocked item locks itself once left (so panes report where they are).</summary>
        public static bool AnyLockWhenLeft
        {
            get
            {
                lock (Gate)
                {
                    return Session.Values.Any(u => u.LockWhenLeft);
                }
            }
        }

        /// <summary>Keeps the file unlocked for this session (the session gets its own copy of the key).</summary>
        public static void Remember(string path, ProtectedKey key)
        {
            string normalized = PathUtil.Normalize(path);
            ProtectedKey copy = key.Clone();
            lock (Gate)
            {
                if (Session.TryGetValue(normalized, out Unlocked old))
                {
                    old.Key.Dispose();
                    old.Key = copy;
                }
                else
                {
                    Session[normalized] = new Unlocked(copy);
                }
            }
        }

        /// <summary>
        /// A copy of the session's key for an unlocked file (the caller disposes it), or null when it isn't unlocked or the
        /// file has changed since (another password was set elsewhere).
        /// </summary>
        public static ProtectedKey KeyFor(string path)
        {
            ProtectedKey key;
            lock (Gate)
            {
                if (!Session.TryGetValue(PathUtil.Normalize(path), out Unlocked entry))
                {
                    return null;
                }

                key = entry.Key.Clone();
            }

            try
            {
                if (key.Matches(ProtectedFormat.ReadHeader(path)))
                {
                    return key;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Log.Logger.Debug(ex, "Couldn't read {Path} to check its key", path);
            }

            key.Dispose();
            return null;
        }

        /// <summary>
        /// Locks an unlocked item: forgets its key (zeroed), drops its cached listing, deletes the files extracted from it
        /// (those still open in another program are deleted at exit or next start), and tells the panes inside it.
        /// </summary>
        public static void Lock(string path)
        {
            string normalized = PathUtil.Normalize(path);
            Unlocked entry;
            lock (Gate)
            {
                if (!Session.Remove(normalized, out entry))
                {
                    return;
                }
            }

            entry.Key.Dispose();
            ArchiveBrowser.Forget(normalized);
            foreach (string folder in entry.TempFolders)
            {
                TryDeleteFolder(folder);
            }

            try
            {
                Locked?.Invoke(null, normalized);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "A lock listener failed");
            }
        }

        /// <summary>Locks everything (at exit) and wipes the temporary folder.</summary>
        public static void LockAll()
        {
            foreach (string path in UnlockedPaths)
            {
                Lock(path);
            }

            CleanupTemp();
        }

        /// <summary>Deletes every folder of extracted files (at start: a crash may have left some; at exit).</summary>
        public static void CleanupTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot))
                {
                    return;
                }

                foreach (string folder in Directory.EnumerateDirectories(TempRoot))
                {
                    bool inUse;
                    lock (Gate)
                    {
                        inUse = Session.Values.Any(u => u.TempFolders.Contains(folder, PathUtil.Comparer));
                    }

                    if (!inUse)
                    {
                        TryDeleteFolder(folder);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Unlocked temp cleanup skipped");
            }
        }

        /// <summary>A new folder for files extracted from an unlocked item, deleted when it is locked.</summary>
        internal static string NewTempFolder(string protectedPath)
        {
            string folder = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            lock (Gate)
            {
                if (Session.TryGetValue(PathUtil.Normalize(protectedPath), out Unlocked entry))
                {
                    entry.TempFolders.Add(folder);
                }
            }

            return folder;
        }

        /// <summary>Opens an unlocked file's content as a ZIP archive (for browsing and extracting). Throws when locked.</summary>
        internal static ZipArchive OpenArchive(string path)
        {
            ProtectedKey key = KeyFor(path) ?? throw new IOException("This item is locked. Open it again with its password.");
            try
            {
                return new ZipArchive(DecryptingStream.Open(path, key), ZipArchiveMode.Read, leaveOpen: false);
            }
            finally
            {
                key.Dispose(); // the stream made its own cipher from it
            }
        }

        // ------------------------------------------------------------------ protect

        /// <summary>
        /// Encrypts a file or folder into "name.bxlock" beside it, reads the result back and compares every file with
        /// what was written, then deletes the original permanently. Returns the new file's path and the originals that
        /// couldn't be deleted (in use, access denied), which the caller reports.
        /// </summary>
        public static ProtectResult Protect(string sourcePath, ProtectedKey key, Action<FileOperationProgress> progress, PauseGate pause, CancellationToken token)
        {
            string reason = CannotProtectReason(sourcePath);
            if (reason != null)
            {
                throw new IOException(reason);
            }

            string source = PathUtil.TrimTrailingSeparator(Path.GetFullPath(sourcePath));
            bool isFolder = Directory.Exists(source);
            if (isFolder != key.IsFolder)
            {
                throw new ArgumentException("The key was made for another kind of item.", nameof(key));
            }

            string name = PathUtil.GetName(source);
            string parent = PathUtil.GetParent(source);
            List<Item> items = Gather(source, name, isFolder, token);
            long totalBytes = items.Sum(i => i.Length);
            int fileCount = items.Count(i => !i.IsFolder);
            var tracker = new Tracker(progress, totalBytes * 2, fileCount * 2);

            string target = FreeName(parent, name + ProtectedFormat.Extension);
            string partial = target + PartialSuffix;
            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                Write(partial, key, items, hashes, tracker, pause, token);
                Verify(partial, key, items, hashes, tracker, pause, token);
                token.ThrowIfCancellationRequested();
                File.Move(partial, target);
            }
            catch
            {
                TryDeleteFile(partial);
                throw;
            }

            // The encrypted copy is complete and checked: only now does the original go.
            List<string> notDeleted = DeleteOriginal(source, isFolder);
            Log.Logger.Information("Protected {Source} as {Target} ({Files} files, {Bytes} bytes)", source, target, fileCount, totalBytes);
            return new ProtectResult(target, notDeleted);
        }

        private static void Write(string partial, ProtectedKey key, List<Item> items, Dictionary<string, byte[]> hashes, Tracker tracker, PauseGate pause, CancellationToken token)
        {
            byte[] buffer = GC.AllocateArray<byte>(CopyBuffer, pinned: true);
            try
            {
                using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
                using (var encrypted = new EncryptingStream(file, key, leaveOpen: true))
                {
                    using (var zip = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (Item item in items)
                        {
                            token.ThrowIfCancellationRequested();
                            pause?.WaitWhilePaused(token);
                            if (item.IsFolder)
                            {
                                ZipArchiveEntry folder = zip.CreateEntry(item.EntryName + "/", CompressionLevel.NoCompression);
                                folder.LastWriteTime = ZipTime(item.Modified);
                                continue;
                            }

                            tracker.Item(item.Path);
                            CompressionLevel level = Compressed.Contains(Path.GetExtension(item.Path)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                            ZipArchiveEntry entry = zip.CreateEntry(item.EntryName, level);
                            entry.LastWriteTime = ZipTime(item.Modified);
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            using (Stream input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                            using (Stream output = entry.Open())
                            {
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    pause?.WaitWhilePaused(token);
                                    hash.AppendData(buffer, 0, read);
                                    output.Write(buffer, 0, read);
                                    tracker.Bytes(read);
                                }
                            }

                            hashes[item.EntryName] = hash.GetHashAndReset();
                            tracker.ItemDone();
                        }
                    }

                    encrypted.Complete();
                }

                file.Flush(flushToDisk: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        /// <summary>Reads the new file back with its key: every file must be there, with the same bytes as were read from the original.</summary>
        private static void Verify(string partial, ProtectedKey key, List<Item> items, Dictionary<string, byte[]> hashes, Tracker tracker, PauseGate pause, CancellationToken token)
        {
            tracker.Verifying();
            byte[] buffer = GC.AllocateArray<byte>(CopyBuffer, pinned: true);
            try
            {
                using var zip = new ZipArchive(DecryptingStream.Open(partial, key), ZipArchiveMode.Read, leaveOpen: false);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    pause?.WaitWhilePaused(token);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!hashes.TryGetValue(entry.FullName, out byte[] expected) || !seen.Add(entry.FullName))
                    {
                        throw new IOException("The encrypted copy didn't match the original (unexpected item). The original was kept.");
                    }

                    tracker.Item(entry.FullName);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (Stream input = entry.Open())
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            pause?.WaitWhilePaused(token);
                            hash.AppendData(buffer, 0, read);
                            tracker.Bytes(read);
                        }
                    }

                    if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                    {
                        throw new IOException($"The encrypted copy of \"{entry.Name}\" didn't match the original. The original was kept.");
                    }

                    tracker.ItemDone();
                }

                if (seen.Count != hashes.Count)
                {
                    throw new IOException("The encrypted copy is missing items. The original was kept.");
                }
            }
            catch (InvalidDataException ex)
            {
                throw new IOException("The encrypted copy couldn't be read back. The original was kept. " + ex.Message, ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        /// <summary>
        /// The files and folders of the item, folders before what is in them. Links inside a folder are refused (the
        /// original is deleted afterwards, and a link can't be stored and given back as one).
        /// </summary>
        private static List<Item> Gather(string source, string name, bool isFolder, CancellationToken token)
        {
            var items = new List<Item>();
            if (!isFolder)
            {
                var file = new FileInfo(source);
                items.Add(new Item(source, name, false, file.Length, file.LastWriteTime));
                return items;
            }

            var pending = new Stack<(DirectoryInfo Folder, string Entry)>();
            var root = new DirectoryInfo(source);
            items.Add(new Item(source, name, true, 0, root.LastWriteTime));
            pending.Push((root, name));
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                (DirectoryInfo folder, string entry) = pending.Pop();
                foreach (FileSystemInfo child in folder.EnumerateFileSystemInfos("*", options))
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"\"{child.FullName}\" is a link. Folders with links in them can't be protected.");
                    }

                    string childEntry = entry + "/" + child.Name;
                    if (child is DirectoryInfo sub)
                    {
                        items.Add(new Item(sub.FullName, childEntry, true, 0, sub.LastWriteTime));
                        pending.Push((sub, childEntry));
                    }
                    else if (child is FileInfo file)
                    {
                        items.Add(new Item(file.FullName, childEntry, false, file.Length, file.LastWriteTime));
                    }
                }
            }

            return items;
        }

        private static List<string> DeleteOriginal(string source, bool isFolder)
        {
            var failed = new List<string>();
            if (!isFolder)
            {
                if (!TryDeleteFile(source))
                {
                    failed.Add(source);
                }

                return failed;
            }

            DeleteTree(new DirectoryInfo(source), failed);
            return failed;
        }

        private static void DeleteTree(DirectoryInfo folder, List<string> failed)
        {
            try
            {
                foreach (FileSystemInfo child in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    if (child is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        DeleteTree(sub, failed);
                    }
                    else if (!TryDeleteFile(child.FullName))
                    {
                        failed.Add(child.FullName);
                    }
                }

                folder.Attributes = FileAttributes.Normal;
                folder.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Couldn't delete {Folder} after protecting it", folder.FullName);
                if (failed.Count == 0 || !PathUtil.IsInside(failed[^1], folder.FullName))
                {
                    failed.Add(folder.FullName);
                }
            }
        }

        // ------------------------------------------------------------------ remove protection

        /// <summary>
        /// Decrypts a protected file back into the item it was, beside it (under a free name when the original name is
        /// taken), then deletes the protected file. Everything is first extracted into a hidden folder next to it, so an
        /// interrupted or damaged file leaves nothing half-restored. Returns the restored item's path.
        /// </summary>
        public static string RemoveProtection(string path, ProtectedKey key, Action<FileOperationProgress> progress, PauseGate pause, CancellationToken token)
        {
            string protectedPath = Path.GetFullPath(path);
            string parent = PathUtil.GetParent(protectedPath);
            string staging = Path.Combine(parent, ".bxlock-restore-" + Guid.NewGuid().ToString("N")[..8]);
            string restored;
            try
            {
                Directory.CreateDirectory(staging);
                TryHide(staging);
                using (var zip = new ZipArchive(DecryptingStream.Open(protectedPath, key), ZipArchiveMode.Read, leaveOpen: false))
                {
                    long total = zip.Entries.Sum(e => e.Length);
                    var tracker = new Tracker(progress, total, zip.Entries.Count(e => !e.FullName.EndsWith("/", StringComparison.Ordinal)));
                    ExtractAll(zip.Entries, staging, tracker, pause, token, Array.Empty<string>());
                }

                string[] tops = Directory.GetFileSystemEntries(staging);
                if (tops.Length != 1)
                {
                    throw new InvalidDataException("This password-protected item is damaged (it doesn't hold one item).");
                }

                token.ThrowIfCancellationRequested();
                string top = tops[0];
                restored = FreeName(parent, PathUtil.GetName(top));
                if (Directory.Exists(top))
                {
                    Directory.Move(top, restored);
                }
                else
                {
                    File.Move(top, restored);
                }
            }
            finally
            {
                TryDeleteFolder(staging);
            }

            Lock(protectedPath);
            if (!TryDeleteFile(protectedPath))
            {
                Log.Logger.Warning("Restored {Restored} but couldn't delete {Path}", restored, protectedPath);
            }

            Log.Logger.Information("Removed the protection of {Path} into {Restored}", protectedPath, restored);
            return restored;
        }

        /// <summary>
        /// Extracts the entries (all, or those under the given inner paths) into a folder, refusing names that would leave
        /// it. Each file's bytes are authenticated as they are decrypted; a damaged part throws.
        /// </summary>
        internal static void ExtractAll(IEnumerable<ZipArchiveEntry> entries, string destination, Tracker tracker, PauseGate pause, CancellationToken token, IReadOnlyList<string> only)
        {
            byte[] buffer = new byte[CopyBuffer];
            foreach (ZipArchiveEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                pause?.WaitWhilePaused(token);
                if (ArchiveBrowser.IsUnsafeName(entry.FullName))
                {
                    throw new InvalidDataException("This password-protected item is damaged (it names a place outside its folder).");
                }

                string target = ArchiveBrowser.SafeTarget(destination, entry.FullName.TrimEnd('/'));
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                tracker?.Item(entry.FullName);
                using (Stream input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        pause?.WaitWhilePaused(token);
                        output.Write(buffer, 0, read);
                        tracker?.Bytes(read);
                    }
                }

                TrySetTime(target, entry.LastWriteTime);
                tracker?.ItemDone();
            }

            // Folders' times last (writing into a folder changes its time).
            foreach (ZipArchiveEntry entry in entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) && !ArchiveBrowser.IsUnsafeName(entry.FullName))
                {
                    try
                    {
                        Directory.SetLastWriteTime(ArchiveBrowser.SafeTarget(destination, entry.FullName.TrimEnd('/')), entry.LastWriteTime.LocalDateTime);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                    }
                }
            }
        }

        // ------------------------------------------------------------------ change password

        /// <summary>
        /// Re-encrypts a protected file with a new key (a new password and salt): into a partial file beside it, which is
        /// read back and compared before it replaces the old one. The old password stops working only once the new file
        /// is in place.
        /// </summary>
        public static void ChangePassword(string path, ProtectedKey oldKey, ProtectedKey newKey, Action<FileOperationProgress> progress, PauseGate pause, CancellationToken token)
        {
            string protectedPath = Path.GetFullPath(path);
            string partial = protectedPath + PartialSuffix;
            byte[] buffer = GC.AllocateArray<byte>(CopyBuffer, pinned: true);
            try
            {
                byte[] written;
                using (DecryptingStream source = DecryptingStream.Open(protectedPath, oldKey))
                {
                    var tracker = new Tracker(progress, source.Length * 2, 2);
                    tracker.Item(PathUtil.GetName(protectedPath));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        using (var target = new EncryptingStream(file, newKey, leaveOpen: true))
                        {
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                pause?.WaitWhilePaused(token);
                                hash.AppendData(buffer, 0, read);
                                target.Write(buffer, 0, read);
                                tracker.Bytes(read);
                            }

                            target.Complete();
                        }

                        file.Flush(flushToDisk: true);
                    }

                    written = hash.GetHashAndReset();
                    tracker.ItemDone();
                    tracker.Verifying();
                    using DecryptingStream check = DecryptingStream.Open(partial, newKey);
                    long before = tracker.Completed;
                    byte[] readBack = check.HashAll(done => tracker.SetBytes(before + done), () =>
                    {
                        pause?.WaitWhilePaused(token);
                        return !token.IsCancellationRequested;
                    });
                    if (!CryptographicOperations.FixedTimeEquals(written, readBack))
                    {
                        throw new IOException("The re-encrypted copy didn't match. The old password still works.");
                    }
                }

                token.ThrowIfCancellationRequested();
                File.Move(partial, protectedPath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(partial);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }

            ArchiveBrowser.Forget(protectedPath);
            bool wasUnlocked;
            lock (Gate)
            {
                wasUnlocked = Session.ContainsKey(PathUtil.Normalize(protectedPath));
            }

            if (wasUnlocked)
            {
                Remember(protectedPath, newKey);
            }

            Log.Logger.Information("Changed the password of {Path}", protectedPath);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>"name", else "name (2)", "name (3)"… keeping a final extension (".bxlock", ".docx") at the end.</summary>
        internal static string FreeName(string folder, string name)
        {
            string candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + PartialSuffix))
            {
                return candidate;
            }

            string extension = Path.GetExtension(name);
            string stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
            if (extension.Length == 0 || extension.Length == name.Length)
            {
                extension = string.Empty;
                stem = name;
            }

            for (int i = 2; i < 10_000; i++)
            {
                candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + PartialSuffix))
                {
                    return candidate;
                }
            }

            throw new IOException("There's no free name for the item in this folder.");
        }

        private static DateTimeOffset ZipTime(DateTime local)
        {
            // ZIP times run from 1980 to 2107.
            if (local.Year < 1980)
            {
                return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(1980, 1, 1)));
            }

            if (local.Year > 2107)
            {
                return new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2107, 12, 31)));
            }

            return new DateTimeOffset(local);
        }

        private static void TrySetTime(string path, DateTimeOffset time)
        {
            try
            {
                File.SetLastWriteTime(path, time.LocalDateTime);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }
        }

        private static void TryHide(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        internal static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Couldn't delete {Path}", path);
                return false;
            }
        }

        private static void TryDeleteFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Couldn't delete {Folder} yet", folder);
            }
        }

        private sealed class Unlocked
        {
            public Unlocked(ProtectedKey key) => Key = key;

            public ProtectedKey Key { get; set; }

            public List<string> TempFolders { get; } = new List<string>();

            public bool LockWhenLeft { get; set; }
        }

        private sealed record Item(string Path, string EntryName, bool IsFolder, long Length, DateTime Modified);

        /// <summary>Progress in the queue's terms: bytes and items, throttled by the job itself.</summary>
        internal sealed class Tracker
        {
            private readonly Action<FileOperationProgress> _sink;
            private readonly long _totalBytes;
            private readonly int _totalItems;
            private string _prefix = string.Empty;
            private int _items;
            private string _current;

            public Tracker(Action<FileOperationProgress> sink, long totalBytes, int totalItems)
            {
                _sink = sink;
                _totalBytes = totalBytes;
                _totalItems = totalItems;
            }

            public long Completed { get; private set; }

            /// <summary>From here on, items are named as being checked (reading the result back).</summary>
            public void Verifying()
            {
                _prefix = "Checking ";
                Report();
            }

            public void Item(string name)
            {
                _current = name;
                Report();
            }

            public void ItemDone()
            {
                _items++;
                Report();
            }

            public void Bytes(long count)
            {
                Completed += count;
                Report();
            }

            public void SetBytes(long completed)
            {
                Completed = completed;
                Report();
            }

            private void Report() =>
                _sink?.Invoke(new FileOperationProgress(FileOperationPhase.Working, _totalBytes, Math.Min(Completed, _totalBytes), _totalItems, Math.Min(_items, _totalItems), _current == null ? null : _prefix + _current));
        }
    }

    /// <summary>The outcome of protecting an item: the new file, and the parts of the original that couldn't be deleted.</summary>
    public sealed class ProtectResult
    {
        public ProtectResult(string protectedPath, IReadOnlyList<string> notDeleted)
        {
            ProtectedPath = protectedPath;
            NotDeleted = notDeleted;
        }

        public string ProtectedPath { get; }
        public IReadOnlyList<string> NotDeleted { get; }
    }
}
