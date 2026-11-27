// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform.Phones;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// An in-memory phone behaving like an MTP device: a tree of folders and files under "phone://Name/Storage", files
    /// created in one transfer of a declared size that exist only once committed, permanent deletes, case-insensitive
    /// names. Failures can be injected to prove the copy engine's phone rules (nothing half-written, nothing lost).
    /// </summary>
    internal sealed class FakePhoneProvider : IPhoneProvider
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        public FakePhoneProvider(string name = "Pixel 7", string storage = "Internal shared storage")
        {
            Name = name;
            StorageName = storage;
            Root = PhonePaths.Build(name);
            Storage = PhonePaths.Build(name, storage);
            _nodes[Storage] = new Node(true, null, DateTime.UtcNow);
        }

        public string Name { get; }
        public string StorageName { get; }
        public string Root { get; }
        public string Storage { get; }

        /// <summary>An upload throws after this many bytes (once), simulating an unplugged cable.</summary>
        public long? FailUploadAfterBytes { get; set; }

        /// <summary>Renames throw (some phones refuse).</summary>
        public bool RefuseRenames { get; set; }

        /// <summary>The next N reads throw an IOException (then work), to exercise Retry.</summary>
        public int FailNextOpens { get; set; }

        public int UploadsStarted { get; private set; }

        private sealed class Node
        {
            public Node(bool isDirectory, byte[] data, DateTime modifiedUtc)
            {
                IsDirectory = isDirectory;
                Data = data;
                ModifiedUtc = modifiedUtc;
            }

            public bool IsDirectory { get; }
            public byte[] Data { get; set; }
            public DateTime ModifiedUtc { get; set; }
        }

        // ------------------------------------------------------------------ test helpers

        public string AddFolder(string relative)
        {
            string path = Combine(relative);
            lock (_gate)
            {
                EnsureParents(path);
                _nodes[path] = new Node(true, null, DateTime.UtcNow);
            }

            return path;
        }

        /// <summary>Folders on the way to <paramref name="path"/>, as a real phone has them.</summary>
        private void EnsureParents(string path)
        {
            for (string parent = PhonePaths.GetParent(path); parent != null && !PathEquals(parent, Root); parent = PhonePaths.GetParent(parent))
            {
                if (!_nodes.ContainsKey(parent))
                {
                    _nodes[parent] = new Node(true, null, DateTime.UtcNow);
                }
            }
        }

        /// <summary>Each written buffer takes this long (a slow USB connection).</summary>
        public int WriteDelayMs { get; set; }

        public string AddFile(string relative, string text, DateTime? modifiedUtc = null)
        {
            string path = Combine(relative);
            lock (_gate)
            {
                EnsureParents(path);
                _nodes[path] = new Node(false, System.Text.Encoding.UTF8.GetBytes(text), modifiedUtc ?? DateTime.UtcNow.AddDays(-1));
            }

            return path;
        }

        public bool Exists(string path)
        {
            lock (_gate)
            {
                return _nodes.ContainsKey(PhonePaths.Normalize(path));
            }
        }

        public string ReadText(string path)
        {
            lock (_gate)
            {
                return System.Text.Encoding.UTF8.GetString(_nodes[PhonePaths.Normalize(path)].Data);
            }
        }

        public IReadOnlyList<string> AllPaths()
        {
            lock (_gate)
            {
                return _nodes.Keys.ToList();
            }
        }

        private string Combine(string relative) => PhonePaths.Combine(Storage, relative.Replace('\\', '/'));

        // ------------------------------------------------------------------ IPhoneProvider

        public IReadOnlyList<PhoneDevice> GetDevices() =>
            new[] { new PhoneDevice(Name, Root, "Google · Pixel 7", new[] { new PhoneStorage(StorageName, Storage, 128L << 30, 64L << 30) }) };

        public DirectoryIndex List(string folderPath, CancellationToken cancellationToken)
        {
            string folder = PhonePaths.Normalize(folderPath);
            lock (_gate)
            {
                if (PathEquals(folder, Root))
                {
                    return new DirectoryIndex(folder, new[] { new DirectoryIndexEntry(StorageName, true, 0, default, FileAttributes.Directory) }, false, null);
                }

                if (!_nodes.TryGetValue(folder, out Node node) || !node.IsDirectory)
                {
                    return DirectoryIndex.Error(folder, "This folder isn't on the phone any more.");
                }

                var entries = new List<DirectoryIndexEntry>();
                foreach (KeyValuePair<string, Node> pair in _nodes)
                {
                    if (PathEquals(PhonePaths.GetParent(pair.Key), folder))
                    {
                        entries.Add(new DirectoryIndexEntry(PhonePaths.GetName(pair.Key), pair.Value.IsDirectory, pair.Value.Data?.LongLength ?? 0,
                            pair.Value.ModifiedUtc, pair.Value.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal));
                    }
                }

                return new DirectoryIndex(folder, entries, false, null);
            }
        }

        public PhoneItemInfo GetInfo(string path)
        {
            string normalized = PhonePaths.Normalize(path);
            lock (_gate)
            {
                if (PathEquals(normalized, Root))
                {
                    return new PhoneItemInfo(normalized, true, 0, default);
                }

                return _nodes.TryGetValue(normalized, out Node node)
                    ? new PhoneItemInfo(normalized, node.IsDirectory, node.Data?.LongLength ?? 0, node.ModifiedUtc)
                    : null;
            }
        }

        public Stream OpenRead(string path)
        {
            if (FailNextOpens > 0)
            {
                FailNextOpens--;
                throw new IOException("The phone is busy.");
            }

            lock (_gate)
            {
                return _nodes.TryGetValue(PhonePaths.Normalize(path), out Node node) && !node.IsDirectory
                    ? new MemoryStream(node.Data, writable: false)
                    : throw new FileNotFoundException("Not on the phone.", path);
            }
        }

        public PhoneWriteStream CreateFile(string folderPath, string name, long size, DateTime modifiedUtc)
        {
            string folder = PhonePaths.Normalize(folderPath);
            lock (_gate)
            {
                if (!_nodes.TryGetValue(folder, out Node parent) || !parent.IsDirectory)
                {
                    throw new DirectoryNotFoundException("No such folder on the phone.");
                }

                if (_nodes.ContainsKey(PhonePaths.Combine(folder, name)))
                {
                    throw new IOException("An item with that name exists.");
                }
            }

            UploadsStarted++;
            return new FakeWrite(this, PhonePaths.Combine(folder, name), size);
        }

        public string CreateFolder(string parentPath, string name)
        {
            string path = PhonePaths.Combine(parentPath, name);
            lock (_gate)
            {
                if (_nodes.ContainsKey(path))
                {
                    throw new IOException("Exists.");
                }

                _nodes[path] = new Node(true, null, DateTime.UtcNow);
            }

            return path;
        }

        public void Delete(string path)
        {
            string normalized = PhonePaths.Normalize(path);
            lock (_gate)
            {
                if (!_nodes.ContainsKey(normalized))
                {
                    throw new FileNotFoundException("Not on the phone.", path);
                }

                foreach (string key in _nodes.Keys.Where(k => PathEquals(k, normalized) || PhonePaths.IsInside(k, normalized)).ToList())
                {
                    _nodes.Remove(key);
                }
            }
        }

        public string Rename(string path, string newName)
        {
            if (RefuseRenames)
            {
                throw new IOException("The phone doesn't allow renaming this item.");
            }

            string from = PhonePaths.Normalize(path);
            string to = PhonePaths.Combine(PhonePaths.GetParent(from), newName);
            lock (_gate)
            {
                foreach (string key in _nodes.Keys.Where(k => PathEquals(k, from) || PhonePaths.IsInside(k, from)).ToList())
                {
                    Node node = _nodes[key];
                    _nodes.Remove(key);
                    _nodes[to + key.Substring(from.Length)] = node;
                }
            }

            return to;
        }

        private static bool PathEquals(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private sealed class FakeWrite : PhoneWriteStream
        {
            private readonly FakePhoneProvider _phone;
            private readonly string _path;
            private readonly long _size;
            private readonly MemoryStream _buffer = new MemoryStream();

            public FakeWrite(FakePhoneProvider phone, string path, long size)
            {
                _phone = phone;
                _path = path;
                _size = size;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_phone.FailUploadAfterBytes is long limit && _buffer.Length + count > limit)
                {
                    _phone.FailUploadAfterBytes = null;
                    throw new IOException("The phone was disconnected.");
                }

                if (_phone.WriteDelayMs > 0)
                {
                    Thread.Sleep(_phone.WriteDelayMs);
                }

                _buffer.Write(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override string Commit()
            {
                if (_buffer.Length != _size)
                {
                    throw new IOException("Size mismatch.");
                }

                lock (_phone._gate)
                {
                    _phone._nodes[_path] = new Node(false, _buffer.ToArray(), DateTime.UtcNow);
                }

                return _path;
            }
        }
    }
}
