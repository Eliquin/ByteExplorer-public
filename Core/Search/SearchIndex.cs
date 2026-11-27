// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>
    /// The file and folder index behind global search: crawls the drives (local, removable, network) once, keeps every
    /// name in memory in a compact searchable form, answers searches in milliseconds, and stays current between full
    /// builds through live updates of the folders that change.
    /// <list type="bullet">
    /// <item><b>Memory</b>: chunks of 65,536 entries stored column by column (<see cref="IndexChunk"/>): parent id, flags,
    /// kind, depth, size, date, and the name twice (lower case for searching, original for display, the second skipped
    /// when it is the same). About 40 bytes per entry plus the names; paths are rebuilt from parent links when shown. The
    /// previous format stored each entry's full path and name in three on-disk files loaded whole: several times larger.</item>
    /// <item><b>Search</b> (<see cref="Search"/>): vectorized substring scan of the lower-case names, every chunk on its
    /// own core, then the query's other conditions on each candidate, a relevance score, and an order over all matches
    /// (not, as before, the first few hundred found in whatever order the threads reached them). See
    /// <see cref="SearchQuery"/> for the syntax.</item>
    /// <item><b>Build</b> (<see cref="BuildIndexAsync(Action{string}, CancellationToken, bool, IEnumerable{string}, IEnumerable{string})"/>):
    /// a pool of workers shares one stack of folders to list (FindFirstFileEx on Windows, readdir on Linux and macOS), each
    /// appending whole folders to the builder, so even one huge root is crawled in parallel. The finished index is saved
    /// atomically (<see cref="IndexStorage"/>) and swapped in with one reference write; the previous index serves searches
    /// until then, and stays when a build is cancelled or fails.</item>
    /// <item><b>Live updates</b> (<see cref="ApplyChanges"/>): a changed folder is listed again and its entries patched
    /// (new ones appended, removed ones marked deleted, changed sizes and dates updated, new subfolders crawled),
    /// copy-on-write per chunk, then swapped in. Changes reported during a build are replayed on the new index.</item>
    /// </list>
    /// Thread-safe: searches run concurrently with each other, with a build and with live updates.
    /// </summary>
    public sealed partial class SearchIndex : IDisposable
    {
        public static readonly IReadOnlyCollection<string> DefaultExcludedDirectoryNames = new[]
        {
            "$Recycle.Bin", "System Volume Information", "Windows.old", "$WinREAgent", "$SysReset", "Config.Msi"
        };

        /// <summary>Files of the previous index format, deleted once an index of this format is saved.</summary>
        private static readonly string[] LegacyFiles = { "records.bin", "names.bin", "paths.bin", "buckets.idx", "stats.txt" };

        private readonly string _indexDirectory;
        private readonly object _updateGate = new object();
        private readonly SemaphoreSlim _buildGate = new SemaphoreSlim(1, 1);
        private IndexData _data;
        private CancellationTokenSource _pauseRequested;
        private HashSet<string> _changedDuringBuild;
        private volatile bool _dirty;
        private volatile bool _needsRebuild;
        private bool _disposed;

        public SearchIndex(string indexDirectory)
        {
            if (string.IsNullOrWhiteSpace(indexDirectory))
            {
                throw new ArgumentNullException(nameof(indexDirectory));
            }

            _indexDirectory = indexDirectory;
            Directory.CreateDirectory(_indexDirectory);
        }

        /// <summary>
        /// Raised (on a background thread) when a new index state is in place: a build finished, an index was loaded, or a
        /// live update changed entries. A results view re-runs its search on it.
        /// </summary>
        public event EventHandler IndexChanged;

        /// <summary>Names (files or folders, case-insensitive) never indexed or crawled into.</summary>
        public HashSet<string> ExcludedDirectoryNames { get; } = new HashSet<string>(DefaultExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders never indexed (with everything inside them).</summary>
        public List<string> ExcludedPathPrefixes { get; } = new List<string>();

        /// <summary>Overrides the crawl's worker count for network roots (see ResolveWorkerCount).</summary>
        public int? EnumerationWorkerCount { get; set; }

        private string IndexPath => Path.Combine(_indexDirectory, IndexStorage.FileName);

        private IndexData Current => Volatile.Read(ref _data);

        public bool IsIndexLoaded => Current != null;

        /// <summary>The loaded index came from a build that was paused before it finished.</summary>
        public bool IsPartialIndex => Current?.IsPartial ?? false;

        public long TotalItems => Current?.LiveCount ?? 0;

        public long TotalFiles => Current?.Files ?? 0;

        public long TotalFolders => Current?.Folders ?? 0;

        public DateTime LastBuildDate => Current?.BuildDateUtc ?? DateTime.MinValue;

        public TimeSpan LastBuildTime => Current?.BuildDuration ?? TimeSpan.Zero;

        /// <summary>The roots the loaded index covers.</summary>
        public IReadOnlyList<string> IndexedRoots => Current?.Roots ?? Array.Empty<string>();

        public long TotalIndexSize
        {
            get
            {
                try
                {
                    return File.Exists(IndexPath) ? new FileInfo(IndexPath).Length : 0;
                }
                catch (IOException)
                {
                    return 0;
                }
            }
        }

        /// <summary>Live updates changed the index since it was last saved.</summary>
        public bool HasUnsavedChanges => _dirty;

        /// <summary>
        /// A full build would serve better than more live updates: a quarter of the entries are deleted ones, or a new
        /// folder held more than a live update crawls.
        /// </summary>
        public bool NeedsRebuild => _needsRebuild;

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Whether an index of this format is on disk (header check only; loading validates everything).</summary>
        public bool IndexExistsAndValid()
        {
            try
            {
                if (!File.Exists(IndexPath))
                {
                    return false;
                }

                using var stream = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> header = stackalloc byte[12];
                if (stream.Read(header) != header.Length)
                {
                    return false;
                }

                return header.Slice(0, 8).SequenceEqual("BXIDX-02"u8) && BitConverter.ToInt32(header.Slice(8)) == 2;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Loads the index from disk (a no-op when one is loaded). Throws when it is missing or damaged.</summary>
        public void LoadIndex()
        {
            if (Current != null)
            {
                return;
            }

            IndexData data;
            try
            {
                data = IndexStorage.Load(IndexPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is OutOfMemoryException)
            {
                throw new InvalidOperationException("The search index could not be loaded: " + ex.Message, ex);
            }

            lock (_updateGate)
            {
                if (_data == null)
                {
                    Volatile.Write(ref _data, data);
                }
            }

            RaiseIndexChanged();
        }

        /// <summary>Drops the loaded index (searches already running keep theirs).</summary>
        public void ClearCache()
        {
            lock (_updateGate)
            {
                Volatile.Write(ref _data, null);
            }
        }

        /// <summary>Stops a running build gracefully: what was found is kept, saved and loaded as a partial index.</summary>
        public void RequestPause()
        {
            try
            {
                _pauseRequested?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Writes the loaded index to disk if live updates changed it (atomic; safe to call any time).</summary>
        public void SaveIfChanged()
        {
            IndexData data = Current;
            if (data == null || !_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                IndexStorage.Save(data, IndexPath);
            }
            catch
            {
                _dirty = true;
                throw;
            }
        }

        // ------------------------------------------------------------------ search

        /// <summary>
        /// Searches the loaded index (see <see cref="SearchQuery"/> for the syntax). An empty query with no filters finds
        /// nothing. Throws OperationCanceledException when cancelled, InvalidOperationException when no index is loaded.
        /// </summary>
        public SearchResults Search(string query, SearchOptions options = null, CancellationToken cancellationToken = default)
        {
            IndexData data = Current ?? throw new InvalidOperationException("No search index is loaded.");
            Interlocked.Increment(ref _activeSearches);
            try
            {
                SearchQuery parsed = SearchQuery.Parse(query);
                options ??= new SearchOptions();
                ContentHits content = FindContent(data, Content, parsed, options, cancellationToken);
                return SearchEngine.Run(data, parsed, options, cancellationToken, content);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSearches);
            }
        }

        /// <summary>
        /// Compatibility wrapper: the best <paramref name="maxResults"/> matches by relevance as plain entries. Returns
        /// nothing (rather than throwing) when cancelled or when no index is available.
        /// </summary>
        public SearchResult SearchAll(string query, CancellationToken cancellationToken = default, int maxResults = 20000)
        {
            var outcome = new SearchResult { Items = new List<FileIndexEntry>() };
            if (string.IsNullOrWhiteSpace(query))
            {
                return outcome;
            }

            try
            {
                if (Current == null && IndexExistsAndValid())
                {
                    LoadIndex();
                }

                if (Current == null)
                {
                    return outcome;
                }

                SearchResults results = Search(query, new SearchOptions(), cancellationToken);
                int take = Math.Min(results.Count, Math.Max(0, maxResults));
                outcome.Items.Capacity = take;
                for (int i = 0; i < take; i++)
                {
                    SearchHit hit = results.GetHit(i);
                    outcome.Items.Add(new FileIndexEntry
                    {
                        Name = hit.Name,
                        FullPath = hit.FullPath,
                        Modified = hit.ModifiedUtc,
                        Size = hit.Size,
                        IsDirectory = hit.IsDirectory
                    });
                }

                outcome.Truncated = results.TotalMatches > take;
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return outcome;
        }

        /// <summary>
        /// The id of the entry at <paramref name="path"/> in <paramref name="data"/>, or -1: the longest indexed root that
        /// contains the path, then one child per path segment (names compared as the platform compares paths).
        /// </summary>
        internal static int FindEntry(IndexData data, string path)
        {
            if (data == null || string.IsNullOrEmpty(path))
            {
                return -1;
            }

            string target = PathUtil.TrimTrailingSeparator(path);
            int best = -1;
            int bestLength = -1;
            foreach (int rootId in data.RootIds())
            {
                if ((data.FlagsOf(rootId) & EntryFlags.Deleted) != 0)
                {
                    continue;
                }

                string root = PathUtil.TrimTrailingSeparator(data.GetName(rootId));
                if (root.Length > bestLength &&
                    (string.Equals(root, target, PathUtil.Comparison) || target.StartsWith(PathUtil.WithTrailingSeparator(root), PathUtil.Comparison)))
                {
                    best = rootId;
                    bestLength = root.Length;
                }
            }

            if (best < 0)
            {
                return -1;
            }

            int current = best;
            int position = PathUtil.WithTrailingSeparator(PathUtil.TrimTrailingSeparator(data.GetName(best))).Length;
            while (position < target.Length)
            {
                int end = position;
                while (end < target.Length && !PathUtil.IsSeparator(target[end]))
                {
                    end++;
                }

                ReadOnlySpan<char> segment = target.AsSpan(position, end - position);
                int next = -1;
                foreach (int child in data.Children(current))
                {
                    if ((data.FlagsOf(child) & EntryFlags.Deleted) == 0 && segment.Equals(data.GetName(child), PathUtil.Comparison))
                    {
                        next = child;
                        break;
                    }
                }

                if (next < 0)
                {
                    return -1;
                }

                current = next;
                position = end + 1;
            }

            return current;
        }

        // ------------------------------------------------------------------ helpers

        private void Publish(IndexData data)
        {
            Volatile.Write(ref _data, data);
            _needsRebuild = data.Count > 10_000 && data.DeletedCount > data.Count / 4;
        }

        private void RaiseIndexChanged()
        {
            try
            {
                IndexChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Debug(ex, "An IndexChanged handler failed");
            }
        }

        private void DeleteLegacyFiles()
        {
            foreach (string name in LegacyFiles)
            {
                try
                {
                    File.Delete(Path.Combine(_indexDirectory, name));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Left for the next save.
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RequestPause();
            lock (_updateGate)
            {
                Volatile.Write(ref _data, null);
            }
        }
    }

    /// <summary>Result of <see cref="SearchIndex.SearchAll"/>: the best matches and whether there were more.</summary>
    public sealed class SearchResult
    {
        public List<FileIndexEntry> Items { get; set; }

        public bool Truncated { get; set; }
    }

    /// <summary>Kind of a root, for icons and grouping.</summary>
    public enum RootKind
    {
        Local,
        Removable,
        Network
    }

    /// <summary>A drive or folder that can be indexed.</summary>
    public sealed class RootInfo
    {
        public string Root { get; set; }

        public string DisplayLabel { get; set; }

        public RootKind Kind { get; set; }
    }

    /// <summary>A single indexed file or folder.</summary>
    public sealed class FileIndexEntry
    {
        public string Name { get; set; }

        public string FullPath { get; set; }

        public DateTime Modified { get; set; }

        public long Size { get; set; }

        public bool IsDirectory { get; set; }
    }

    /// <summary>How far a build has come.</summary>
    public readonly record struct IndexBuildProgress(long Items, long Folders, string CurrentFolder, TimeSpan Elapsed, long UnreadableFolders);
}
