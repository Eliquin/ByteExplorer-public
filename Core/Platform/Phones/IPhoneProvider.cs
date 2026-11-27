// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Platform.Phones
{
    /// <summary>One storage of a phone ("Internal shared storage", "SD card"): a folder at the top of the phone.</summary>
    public sealed class PhoneStorage
    {
        public PhoneStorage(string name, string path, long totalBytes, long freeBytes)
        {
            Name = name;
            Path = path;
            TotalBytes = totalBytes;
            FreeBytes = freeBytes;
        }

        public string Name { get; }
        public string Path { get; }

        /// <summary>Bytes; -1 when the phone doesn't say.</summary>
        public long TotalBytes { get; }

        /// <summary>Bytes; -1 when the phone doesn't say.</summary>
        public long FreeBytes { get; }
    }

    /// <summary>A connected phone (or other MTP device: a camera, a music player), shown in This PC.</summary>
    public sealed class PhoneDevice
    {
        public PhoneDevice(string name, string rootPath, string description, IReadOnlyList<PhoneStorage> storages, bool needsMount = false, string mountAddress = null)
        {
            Name = name;
            RootPath = rootPath;
            Description = description;
            Storages = storages ?? Array.Empty<PhoneStorage>();
            NeedsMount = needsMount;
            MountAddress = mountAddress;
        }

        public string Name { get; }

        /// <summary>Opening it shows the phone's storages: "phone://Galaxy S21" on Windows, the GVfs folder on Linux.</summary>
        public string RootPath { get; }

        /// <summary>"Samsung · Galaxy S21", or null.</summary>
        public string Description { get; }

        public IReadOnlyList<PhoneStorage> Storages { get; }

        /// <summary>Linux: the desktop sees the phone but hasn't mounted it yet; opening it mounts <see cref="MountAddress"/> first.</summary>
        public bool NeedsMount { get; }

        /// <summary>Linux: the GIO address to mount ("mtp://SAMSUNG_Android_R58M…/").</summary>
        public string MountAddress { get; }

        public long TotalBytes => Sum(s => s.TotalBytes);
        public long FreeBytes => Sum(s => s.FreeBytes);

        private long Sum(Func<PhoneStorage, long> value)
        {
            long total = 0;
            foreach (PhoneStorage storage in Storages)
            {
                long v = value(storage);
                if (v < 0)
                {
                    return -1;
                }

                total += v;
            }

            return Storages.Count == 0 ? -1 : total;
        }
    }

    /// <summary>What a phone says about one item; null from <see cref="IPhoneProvider.GetInfo"/> when there is none.</summary>
    public sealed class PhoneItemInfo
    {
        public PhoneItemInfo(string path, bool isDirectory, long size, DateTime modifiedUtc)
        {
            Path = path;
            IsDirectory = isDirectory;
            Size = size;
            ModifiedUtc = modifiedUtc;
        }

        public string Path { get; }
        public bool IsDirectory { get; }

        /// <summary>Bytes; 0 for a folder.</summary>
        public long Size { get; }

        /// <summary>default when the phone doesn't say.</summary>
        public DateTime ModifiedUtc { get; }
    }

    /// <summary>
    /// A file being written to a phone. MTP creates a file in one transfer of a size given up front, so nothing exists
    /// on the phone until <see cref="Commit"/>; disposing without committing abandons it (on Linux the partial file is
    /// deleted).
    /// </summary>
    public abstract class PhoneWriteStream : Stream
    {
        /// <summary>Finishes the file: after this it exists on the phone under its name. Returns its path.</summary>
        public abstract string Commit();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Reading and writing a phone's files. Every call may block for seconds (a phone answers slowly, or not at all while
    /// locked), so callers run them off the UI thread. Failures are IOException (UnauthorizedAccessException when the
    /// phone refuses), with a message for the person.
    /// </summary>
    public interface IPhoneProvider
    {
        /// <summary>The connected phones. Never throws: a phone that can't be asked is left out.</summary>
        IReadOnlyList<PhoneDevice> GetDevices();

        /// <summary>One folder's items (or a phone root's storages), as a folder listing; an error listing when it can't.</summary>
        DirectoryIndex List(string folderPath, CancellationToken cancellationToken);

        /// <summary>The item at <paramref name="path"/>, or null when there is none.</summary>
        PhoneItemInfo GetInfo(string path);

        Stream OpenRead(string path);

        /// <summary>Starts a new file <paramref name="name"/> in <paramref name="folderPath"/> of exactly <paramref name="size"/> bytes.</summary>
        PhoneWriteStream CreateFile(string folderPath, string name, long size, DateTime modifiedUtc);

        /// <summary>Creates a folder and returns its path.</summary>
        string CreateFolder(string parentPath, string name);

        /// <summary>Deletes a file, or a folder with everything in it. Phones have no recycle bin: this is permanent.</summary>
        void Delete(string path);

        /// <summary>Renames in place and returns the new path.</summary>
        string Rename(string path, string newName);
    }
}
