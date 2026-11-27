// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>A term of a content query: folded text, and whether longer terms starting with it match too.</summary>
    internal readonly record struct ContentTerm(string Text, bool Prefix);

    /// <summary>
    /// The on-disk inverted index of file contents: for every term, the documents (files) that contain it. Built and
    /// updated by <see cref="ContentIndexer"/> on one background thread; searched from any thread.
    /// <list type="bullet">
    /// <item><b>Documents</b>: each indexed file has an id, its size and modification time when it was read (so a sync
    /// only re-reads files that changed) and its path. The table is in memory (about 100 bytes per file) and saved as
    /// <c>docs.bxc</c>, which also lists the live segments: it is the commit record.</item>
    /// <item><b>Segments</b> (<see cref="ContentSegment"/>): new documents' terms gather in a memory buffer (bounded:
    /// it is written out at a few million postings) and are written as a new immutable segment at each commit. Searches
    /// look at every segment; when there are more than <see cref="MaxSegments"/>, the smallest are merged into one,
    /// dropping removed documents, so the count and the dead weight stay bounded. Ids are never reused, so a changed
    /// file is a new id and its old postings are ignored (the document table no longer has that id) until a merge drops
    /// them.</item>
    /// <item><b>Crash safety</b>: segments and the document table are written to temporary files, flushed and renamed;
    /// the document table is written last, and segment files it does not list are deleted at load. A damaged index is
    /// dropped and rebuilt.</item>
    /// <item><b>Search</b> (<see cref="Search"/>): per term, the union of its postings in each segment (a prefix term
    /// reads the range of terms starting with it), then the intersection over the terms, then the paths.</item>
    /// </list>
    /// </summary>
    internal sealed class ContentIndex : IDisposable
    {
        public const string DocumentsFileName = "docs.bxc";
        private const int MaxSegments = 8;
        private const long BufferPostingLimit = 3_000_000;
        private const int BufferTermLimit = 400_000;
        private static readonly byte[] Magic = "BXCDOC01"u8.ToArray();
        private static readonly byte[] EndMagic = "BXCDEND1"u8.ToArray();
        private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(2);

        private readonly string _directory;
        private readonly object _writeGate = new object();
        private readonly Dictionary<string, DocumentEntry> _documents = new Dictionary<string, DocumentEntry>(PathUtil.Comparer);
        private readonly Dictionary<string, List<int>> _buffer = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly List<(int Id, string Path)> _added = new List<(int, string)>();
        private readonly List<int> _removed = new List<int>();
        private readonly List<(ContentSegment Segment, DateTime RetiredUtc)> _retired = new List<(ContentSegment, DateTime)>();
        private Snapshot _snapshot = Snapshot.Empty;
        private long _bufferPostings;
        private int _nextDocumentId;
        private int _nextSegment;
        private bool _disposed;
        private FileStream _lock;

        public ContentIndex(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public string Directory => _directory;

        /// <summary>Files in the index (committed).</summary>
        public int DocumentCount => Volatile.Read(ref _snapshot).DocumentCount;

        /// <summary>Files read since the last commit.</summary>
        public int PendingCount
        {
            get { lock (_writeGate) return _added.Count + _removed.Count; }
        }

        public int SegmentCount => Volatile.Read(ref _snapshot).Segments.Length;

        /// <summary>Bytes on disk (segments and the document table).</summary>
        public long SizeOnDisk
        {
            get
            {
                long size = Volatile.Read(ref _snapshot).Segments.Sum(s => s.Length);
                try
                {
                    string documents = Path.Combine(_directory, DocumentsFileName);
                    return size + (File.Exists(documents) ? new FileInfo(documents).Length : 0);
                }
                catch (IOException)
                {
                    return size;
                }
            }
        }

        /// <summary>
        /// This instance holds the folder's lock (<see cref="TryLock"/>) and may write. Another ByteExplorer process (or
        /// window with its own search service) that finds the folder locked opens the index read-only: it searches what
        /// was committed when it loaded, and leaves the files alone.
        /// </summary>
        public bool IsOwner => _lock != null;

        /// <summary>
        /// Takes the folder's lock: one writer at a time, across processes (an exclusive open of a "lock" file, released
        /// when this instance is disposed or the process ends). False when another instance holds it.
        /// </summary>
        public bool TryLock()
        {
            lock (_writeGate)
            {
                if (_lock != null)
                {
                    return true;
                }

                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    _lock = new FileStream(Path.Combine(_directory, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <summary>The memory buffer is large enough to be written out.</summary>
        public bool ShouldCommit
        {
            get { lock (_writeGate) return _bufferPostings >= BufferPostingLimit || _buffer.Count >= BufferTermLimit; }
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>
        /// Loads the saved index; a missing one starts empty, a damaged one is deleted and starts empty. Returns false when
        /// the saved index was damaged.
        /// </summary>
        public bool Load()
        {
            lock (_writeGate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, DocumentsFileName);
                if (!File.Exists(path))
                {
                    if (IsOwner)
                    {
                        DeleteStrayFiles(Array.Empty<string>());
                    }

                    return true;
                }

                var segments = new List<ContentSegment>();
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new BinaryReader(stream, Encoding.UTF8);
                    if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != 1)
                    {
                        throw new InvalidDataException("Not a content index.");
                    }

                    int nextId = reader.ReadInt32();
                    int nextSegment = reader.ReadInt32();
                    int segmentCount = reader.ReadInt32();
                    if (nextId < 0 || nextSegment < 0 || segmentCount < 0 || segmentCount > 1000)
                    {
                        throw new InvalidDataException("Damaged content index.");
                    }

                    var names = new List<string>();
                    for (int i = 0; i < segmentCount; i++)
                    {
                        string name = reader.ReadString();
                        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
                        {
                            throw new InvalidDataException("Damaged content index.");
                        }

                        names.Add(name);
                        segments.Add(ContentSegment.Open(Path.Combine(_directory, name)));
                    }

                    int count = reader.ReadInt32();
                    if (count < 0 || count > nextId)
                    {
                        throw new InvalidDataException("Damaged content index.");
                    }

                    var paths = new string[nextId];
                    var documents = new Dictionary<string, DocumentEntry>(count, PathUtil.Comparer);
                    for (int i = 0; i < count; i++)
                    {
                        int id = reader.ReadInt32();
                        long size = reader.ReadInt64();
                        long ticks = reader.ReadInt64();
                        string documentPath = reader.ReadString();
                        if (id < 0 || id >= nextId || paths[id] != null)
                        {
                            throw new InvalidDataException("Damaged content index.");
                        }

                        paths[id] = documentPath;
                        documents[documentPath] = new DocumentEntry(id, size, ticks);
                    }

                    if (!reader.ReadBytes(8).AsSpan().SequenceEqual(EndMagic))
                    {
                        throw new InvalidDataException("Damaged content index.");
                    }

                    ClearState();
                    foreach (KeyValuePair<string, DocumentEntry> pair in documents)
                    {
                        _documents[pair.Key] = pair.Value;
                    }

                    _nextDocumentId = nextId;
                    _nextSegment = nextSegment;
                    Volatile.Write(ref _snapshot, new Snapshot(segments.ToArray(), paths, documents.Count));
                    if (IsOwner)
                    {
                        DeleteStrayFiles(names);
                    }

                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is EndOfStreamException ||
                                           ex is OutOfMemoryException || ex is ArgumentException)
                {
                    Serilog.Log.Logger.Warning(ex, "The content index could not be loaded; it is rebuilt");
                    foreach (ContentSegment segment in segments)
                    {
                        segment.Dispose();
                    }

                    ClearState();
                    Volatile.Write(ref _snapshot, Snapshot.Empty);
                    if (IsOwner)
                    {
                        DeleteStrayFiles(Array.Empty<string>());
                        TryDelete(path);
                    }

                    return false;
                }
            }
        }

        /// <summary>Deletes the whole index (the setting was turned off).</summary>
        public void Clear()
        {
            lock (_writeGate)
            {
                if (!IsOwner)
                {
                    return; // another instance's index
                }

                Snapshot old = Volatile.Read(ref _snapshot);
                Volatile.Write(ref _snapshot, Snapshot.Empty);
                ClearState();
                foreach (ContentSegment segment in old.Segments)
                {
                    segment.Dispose();
                }

                TryDelete(Path.Combine(_directory, DocumentsFileName));
                DeleteStrayFiles(Array.Empty<string>());
            }
        }

        public void Dispose()
        {
            lock (_writeGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (ContentSegment segment in Volatile.Read(ref _snapshot).Segments)
                {
                    segment.Dispose();
                }

                foreach ((ContentSegment segment, _) in _retired)
                {
                    segment.Dispose();
                    TryDelete(segment.Path);
                }

                _retired.Clear();
                Volatile.Write(ref _snapshot, Snapshot.Empty);
                _lock?.Dispose();
                _lock = null;
            }
        }

        // ------------------------------------------------------------------ updating (the indexer's thread)

        /// <summary>Whether the file is indexed as it is now (same size and time): no need to read it again.</summary>
        public bool IsCurrent(string path, long size, long ticks)
        {
            lock (_writeGate)
            {
                return _documents.TryGetValue(path, out DocumentEntry entry) && entry.Size == size && entry.Ticks == ticks;
            }
        }

        /// <summary>The indexed paths (a copy).</summary>
        public List<string> DocumentPaths()
        {
            lock (_writeGate)
            {
                return _documents.Keys.ToList();
            }
        }

        /// <summary>
        /// Adds (or replaces) a file with its distinct terms; searchable after the next <see cref="Commit"/>. A file whose
        /// text could not be read is added with no terms, so it is not read again until it changes.
        /// </summary>
        public void AddDocument(string path, long size, long ticks, IEnumerable<string> terms)
        {
            lock (_writeGate)
            {
                if (_documents.TryGetValue(path, out DocumentEntry old))
                {
                    _removed.Add(old.Id);
                }

                int id = _nextDocumentId++;
                _documents[path] = new DocumentEntry(id, size, ticks);
                _added.Add((id, path));
                foreach (string term in terms ?? Enumerable.Empty<string>())
                {
                    if (!_buffer.TryGetValue(term, out List<int> ids))
                    {
                        ids = new List<int>(2);
                        _buffer[term] = ids;
                    }

                    ids.Add(id);
                    _bufferPostings++;
                }
            }
        }

        /// <summary>Removes a file (deleted, moved out of the indexed places, or no longer wanted).</summary>
        public void RemoveDocument(string path)
        {
            lock (_writeGate)
            {
                if (_documents.Remove(path, out DocumentEntry old))
                {
                    _removed.Add(old.Id);
                }
            }
        }

        /// <summary>
        /// Makes the changes since the last commit searchable and durable: the buffer becomes a segment, the document
        /// table is saved, and segments are merged when there are too many. Disk errors propagate (nothing is lost: the
        /// pending changes stay and the next commit tries again).
        /// </summary>
        public void Commit(CancellationToken cancellationToken = default)
        {
            lock (_writeGate)
            {
                if (_disposed || !IsOwner || (_added.Count == 0 && _removed.Count == 0 && _buffer.Count == 0))
                {
                    DisposeRetired(force: false);
                    return;
                }

                System.IO.Directory.CreateDirectory(_directory);
                Snapshot old = Volatile.Read(ref _snapshot);
                var segments = new List<ContentSegment>(old.Segments);
                ContentSegment written = null;
                if (_buffer.Count > 0)
                {
                    string name = $"seg-{_nextSegment++:D6}.bxs";
                    string path = Path.Combine(_directory, name);
                    var terms = _buffer
                        .Select(pair => (Term: Encoding.UTF8.GetBytes(pair.Key), Ids: (IReadOnlyList<int>)pair.Value))
                        .ToList();
                    terms.Sort((a, b) => a.Term.AsSpan().SequenceCompareTo(b.Term));
                    ContentSegment.Write(path, terms);
                    written = ContentSegment.Open(path);
                    segments.Add(written);
                }

                string[] paths = BuildPaths(old.PathsById);
                try
                {
                    WriteDocuments(segments);
                }
                catch
                {
                    if (written != null)
                    {
                        written.Dispose();
                        TryDelete(written.Path);
                        _nextSegment--;
                    }

                    throw;
                }

                _buffer.Clear();
                _bufferPostings = 0;
                _added.Clear();
                _removed.Clear();
                Volatile.Write(ref _snapshot, new Snapshot(segments.ToArray(), paths, _documents.Count));
                DisposeRetired(force: false);
            }

            MergeIfNeeded(cancellationToken);
        }

        /// <summary>Merges the smallest segments into one when there are more than <see cref="MaxSegments"/>.</summary>
        private void MergeIfNeeded(CancellationToken cancellationToken)
        {
            Snapshot current = Volatile.Read(ref _snapshot);
            if (current.Segments.Length <= MaxSegments)
            {
                return;
            }

            ContentSegment[] chosen = current.Segments.OrderBy(s => s.Length).Take(current.Segments.Length - MaxSegments / 2 + 1).ToArray();
            string[] paths = current.PathsById;
            string name;
            lock (_writeGate)
            {
                name = $"seg-{_nextSegment++:D6}.bxs";
            }

            string path = Path.Combine(_directory, name);
            ContentSegment.Write(path, MergeTerms(chosen, paths, cancellationToken));
            ContentSegment merged = ContentSegment.Open(path);
            lock (_writeGate)
            {
                if (_disposed)
                {
                    merged.Dispose();
                    return;
                }

                Snapshot latest = Volatile.Read(ref _snapshot);
                var segments = latest.Segments.Where(s => !chosen.Contains(s)).ToList();
                segments.Add(merged);
                WriteDocuments(segments);
                Volatile.Write(ref _snapshot, new Snapshot(segments.ToArray(), latest.PathsById, latest.DocumentCount));
                DateTime now = DateTime.UtcNow;
                foreach (ContentSegment segment in chosen)
                {
                    _retired.Add((segment, now)); // searches running now may still read it
                }
            }
        }

        /// <summary>The terms of several segments in order, each with the union of its live postings.</summary>
        private static IEnumerable<(byte[] Term, IReadOnlyList<int> Ids)> MergeTerms(ContentSegment[] segments, string[] paths, CancellationToken cancellationToken)
        {
            var readers = segments.Select(s => s.ReadAll()).ToArray();
            var active = new bool[readers.Length];
            for (int i = 0; i < readers.Length; i++)
            {
                active[i] = readers[i].Next();
            }

            var ids = new List<int>();
            long steps = 0;
            while (true)
            {
                int first = -1;
                for (int i = 0; i < readers.Length; i++)
                {
                    if (active[i] && (first < 0 || readers[i].Term.SequenceCompareTo(readers[first].Term) < 0))
                    {
                        first = i;
                    }
                }

                if (first < 0)
                {
                    yield break;
                }

                if ((++steps & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                byte[] term = readers[first].Term.ToArray();
                ids.Clear();
                for (int i = 0; i < readers.Length; i++)
                {
                    if (active[i] && readers[i].Term.SequenceEqual(term))
                    {
                        readers[i].ReadPostings(ids);
                        active[i] = readers[i].Next();
                    }
                }

                ids.RemoveAll(id => id >= paths.Length || paths[id] == null);
                if (ids.Count > 0)
                {
                    ids.Sort();
                    yield return (term, ids);
                }
            }
        }

        private string[] BuildPaths(string[] previous)
        {
            var paths = new string[Math.Max(_nextDocumentId, previous.Length)];
            Array.Copy(previous, paths, previous.Length);
            foreach (int id in _removed)
            {
                if (id < paths.Length)
                {
                    paths[id] = null;
                }
            }

            foreach ((int id, string path) in _added)
            {
                // A file added and replaced again before this commit: only its newest id is in the table.
                if (_documents.TryGetValue(path, out DocumentEntry entry) && entry.Id == id)
                {
                    paths[id] = path;
                }
            }

            return paths;
        }

        private void WriteDocuments(List<ContentSegment> segments)
        {
            string path = Path.Combine(_directory, DocumentsFileName);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(1);
                writer.Write(_nextDocumentId);
                writer.Write(_nextSegment);
                writer.Write(segments.Count);
                foreach (ContentSegment segment in segments)
                {
                    writer.Write(segment.Name);
                }

                writer.Write(_documents.Count);
                foreach (KeyValuePair<string, DocumentEntry> pair in _documents)
                {
                    writer.Write(pair.Value.Id);
                    writer.Write(pair.Value.Size);
                    writer.Write(pair.Value.Ticks);
                    writer.Write(pair.Key);
                }

                writer.Write(EndMagic);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }

        private void DisposeRetired(bool force)
        {
            DateTime now = DateTime.UtcNow;
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if (force || now - _retired[i].RetiredUtc > RetireDelay)
                {
                    _retired[i].Segment.Dispose();
                    TryDelete(_retired[i].Segment.Path);
                    _retired.RemoveAt(i);
                }
            }
        }

        private void ClearState()
        {
            _documents.Clear();
            _buffer.Clear();
            _added.Clear();
            _removed.Clear();
            _bufferPostings = 0;
            _nextDocumentId = 0;
            _nextSegment = 0;
        }

        /// <summary>Segment and temporary files the document table does not list (left by a crash or a failed write).</summary>
        private void DeleteStrayFiles(IReadOnlyCollection<string> keep)
        {
            try
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(_directory))
                {
                    string name = Path.GetFileName(file);
                    bool segment = name.StartsWith("seg-", StringComparison.Ordinal) && (name.EndsWith(".bxs", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal));
                    if ((segment && !keep.Contains(name)) || name == DocumentsFileName + ".tmp")
                    {
                        TryDelete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        // ------------------------------------------------------------------ searching (any thread)

        /// <summary>
        /// The paths of the files containing every term (a prefix term: some term starting with it). Empty when there are
        /// no terms. Postings of removed or replaced files are skipped.
        /// </summary>
        public List<string> Search(IReadOnlyList<ContentTerm> terms, CancellationToken cancellationToken = default)
        {
            var paths = new List<string>();
            if (terms == null || terms.Count == 0)
            {
                return paths;
            }

            Snapshot snapshot = Volatile.Read(ref _snapshot);
            if (snapshot.Segments.Length == 0)
            {
                return paths;
            }

            int[] result = null;
            // Exact terms first: they are usually the most selective, so the intersection shrinks fastest.
            foreach (ContentTerm term in terms.OrderBy(t => t.Prefix ? 1 : 0).ThenByDescending(t => t.Text.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int[] ids = Postings(snapshot, term);
                result = result == null ? ids : Intersect(result, ids);
                if (result.Length == 0)
                {
                    return paths;
                }
            }

            foreach (int id in result)
            {
                if (id < snapshot.PathsById.Length && snapshot.PathsById[id] is string path)
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private static int[] Postings(Snapshot snapshot, ContentTerm term)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(term.Text);
            var ids = new List<int>();
            foreach (ContentSegment segment in snapshot.Segments)
            {
                try
                {
                    segment.Collect(bytes, term.Prefix, ids);
                }
                catch (ObjectDisposedException)
                {
                    // A segment retired and closed during a very long search: its documents are in the merged one.
                }
            }

            ids.Sort();
            int count = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (i == 0 || ids[i] != ids[i - 1])
                {
                    ids[count++] = ids[i];
                }
            }

            return ids.GetRange(0, count).ToArray();
        }

        private static int[] Intersect(int[] a, int[] b)
        {
            var result = new List<int>(Math.Min(a.Length, b.Length));
            int i = 0;
            int j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j])
                {
                    result.Add(a[i]);
                    i++;
                    j++;
                }
                else if (a[i] < b[j])
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return result.ToArray();
        }

        private readonly record struct DocumentEntry(int Id, long Size, long Ticks);

        /// <summary>What searches read: the live segments and the paths by id; replaced as a whole at each commit.</summary>
        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot(Array.Empty<ContentSegment>(), Array.Empty<string>(), 0);

            public Snapshot(ContentSegment[] segments, string[] pathsById, int documentCount)
            {
                Segments = segments;
                PathsById = pathsById;
                DocumentCount = documentCount;
            }

            public ContentSegment[] Segments { get; }

            public string[] PathsById { get; }

            public int DocumentCount { get; }
        }
    }
}
