// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>A match: an entry id and its relevance score.</summary>
    internal readonly struct Hit
    {
        public Hit(int id, int score)
        {
            Id = id;
            Score = score;
        }

        public int Id { get; }

        public int Score { get; }
    }

    /// <summary>
    /// A <see cref="SearchQuery"/> in the form the scan uses: UTF-8 bytes of the folded words, the folders of in: resolved
    /// to entry ids, and the "anchor": the longest literal text the query requires, which the scan looks for with
    /// vectorized IndexOf over a chunk's names (a 0 byte separates names, so a match is always inside one name).
    /// </summary>
    internal sealed class CompiledQuery
    {
        public byte[] Anchor;
        public byte[][] Required = Array.Empty<byte[]>();
        public byte[][] Excluded = Array.Empty<byte[]>();
        public byte[][] Patterns = Array.Empty<byte[]>();
        public byte[][] ExcludedPatterns = Array.Empty<byte[]>();
        public byte[][][] FolderSegments = Array.Empty<byte[][]>();
        public byte[][] InFolderNames = Array.Empty<byte[]>();
        public int[] InFolderIds = Array.Empty<int>();
        public int[] ExcludedFolderIds = Array.Empty<int>();
        public bool InFoldersRequested;
        public byte[][] Extensions = Array.Empty<byte[]>();
        public int KindMask;
        public bool? FoldersOnly;
        public bool IncludeHidden;
        public long MinSize = long.MinValue;
        public long MaxSize = long.MaxValue;
        public long MinTicks = long.MinValue;
        public long MaxTicks = long.MaxValue;
        public byte[] PrimaryTerm;
        public bool NeedsAncestorWalk;
        public bool CheckDeletedAncestors;
        public long NowTicks;

        /// <summary>True when nothing can match (an in: folder that is not in the index).</summary>
        public bool MatchesNothing;

        /// <summary>The same conditions without the words (content matches stand in for them).</summary>
        public CompiledQuery WithoutTerms()
        {
            var copy = (CompiledQuery)MemberwiseClone();
            copy.Required = Array.Empty<byte[]>();
            copy.Anchor = null;
            copy.PrimaryTerm = null;
            return copy;
        }
    }

    /// <summary>
    /// What the content index found for a search, as entry ids of the name index (sorted): the files matching the
    /// <c>content:</c> filter (null when there is none), and the files whose contents hold the search words (null unless
    /// "search file contents" is on and there are words).
    /// </summary>
    internal sealed class ContentHits
    {
        public int[] FilterIds;
        public int[] AlternativeIds;

        /// <summary>The words to show a snippet for.</summary>
        public string[] Words = Array.Empty<string>();
    }

    /// <summary>
    /// Runs searches over an <see cref="IndexData"/>: the scan (parallel over chunks), the checks, the relevance score and
    /// the ordering.
    /// </summary>
    internal static class SearchEngine
    {
        public static CompiledQuery Compile(IndexData data, SearchQuery query, SearchOptions options)
        {
            var compiled = new CompiledQuery
            {
                Required = query.Terms.Select(Utf8).ToArray(),
                Excluded = query.ExcludedTerms.Select(Utf8).ToArray(),
                Patterns = query.Patterns.Select(Utf8).ToArray(),
                ExcludedPatterns = query.ExcludedPatterns.Select(Utf8).ToArray(),
                FolderSegments = query.FolderSegments.Select(segments => segments.Select(Utf8).ToArray()).ToArray(),
                InFolderNames = query.InFolderNames.Select(Utf8).ToArray(),
                Extensions = query.Extensions.Select(Utf8).ToArray(),
                FoldersOnly = options.FoldersOnly ?? query.FoldersOnly,
                IncludeHidden = options.IncludeHidden,
                MinSize = query.MinSize,
                MaxSize = query.MaxSize,
                MinTicks = query.MinTicks,
                MaxTicks = query.MaxTicks,
                NowTicks = DateTime.UtcNow.Ticks,
                CheckDeletedAncestors = data.HasDeletedDirectories
            };

            foreach (FileKind kind in query.Kinds)
            {
                compiled.KindMask |= 1 << (int)kind;
            }

            if (options.Kind is FileKind optionKind && optionKind != FileKind.None)
            {
                // The results view's quick filter narrows the typed one: both apply.
                compiled.KindMask = compiled.KindMask == 0 ? 1 << (int)optionKind : compiled.KindMask & (1 << (int)optionKind);
                if (compiled.KindMask == 0)
                {
                    compiled.MatchesNothing = true;
                }
            }

            var folders = new List<string>(query.InFolders);
            if (!string.IsNullOrEmpty(options.ScopeFolder))
            {
                folders.Add(options.ScopeFolder);
            }

            if (folders.Count > 0)
            {
                compiled.InFoldersRequested = true;
                var ids = new List<int>();
                foreach (string folder in folders)
                {
                    int id = SearchIndex.FindEntry(data, folder);
                    if (id >= 0)
                    {
                        ids.Add(id);
                    }
                }

                compiled.InFolderIds = ids.ToArray();
                if (ids.Count == 0)
                {
                    compiled.MatchesNothing = true;
                }
            }

            if (options.ExcludedFolders != null && options.ExcludedFolders.Count > 0)
            {
                var excluded = new List<int>();
                foreach (string folder in options.ExcludedFolders)
                {
                    int id = string.IsNullOrEmpty(folder) ? -1 : SearchIndex.FindEntry(data, folder);
                    if (id >= 0)
                    {
                        excluded.Add(id);
                    }
                }

                compiled.ExcludedFolderIds = excluded.ToArray();
            }

            // The anchor: the longest literal the name must contain (a word, or the longest fixed part of a pattern).
            IEnumerable<byte[]> literals = compiled.Required.Concat(compiled.Patterns.SelectMany(LiteralParts));
            compiled.Anchor = literals.OrderByDescending(bytes => bytes.Length).FirstOrDefault(bytes => bytes.Length > 0);
            compiled.PrimaryTerm = compiled.Required.FirstOrDefault() ?? compiled.Anchor;
            compiled.NeedsAncestorWalk = compiled.FolderSegments.Length > 0 || compiled.InFolderNames.Length > 0 || compiled.InFolderIds.Length > 0
                || compiled.ExcludedFolderIds.Length > 0;
            return compiled;
        }

        /// <summary>
        /// The matches of <paramref name="query"/>, ordered by <see cref="SearchOptions.Sort"/>. Each chunk is scanned on its
        /// own thread; a chunk's matches come out in id order, and chunks are concatenated in order, so the result never
        /// depends on thread timing.
        /// </summary>
        public static SearchResults Run(IndexData data, SearchQuery query, SearchOptions options, CancellationToken cancellationToken, ContentHits content = null)
        {
            var stopwatch = Stopwatch.StartNew();
            options ??= new SearchOptions();
            CompiledQuery compiled = Compile(data, query, options);
            if (query.IsEmpty && options.Kind == null && options.FoldersOnly == null && string.IsNullOrEmpty(options.ScopeFolder) || compiled.MatchesNothing || data.Count == 0)
            {
                return new SearchResults(data, Array.Empty<Hit>(), 0, query, options, stopwatch.Elapsed);
            }

            if (content != null)
            {
                return RunWithContent(data, query, options, compiled, content, stopwatch, cancellationToken);
            }

            if (query.HasContentFilter)
            {
                return new SearchResults(data, Array.Empty<Hit>(), 0, query, options, stopwatch.Elapsed); // no content index asked
            }

            int chunkCount = data.Chunks.Length;
            var perChunk = new List<Hit>[chunkCount];
            long total = 0;
            long kept = 0;
            int max = Math.Max(1, options.MaxResults);
            Parallel.For(0, chunkCount, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
            {
                List<Hit> hits = ScanChunk(data, data.Chunks[c], c << IndexChunk.Shift, compiled, cancellationToken);
                if (hits == null || hits.Count == 0)
                {
                    return;
                }

                Interlocked.Add(ref total, hits.Count);
                long before = Interlocked.Add(ref kept, hits.Count) - hits.Count;
                if (before >= max)
                {
                    return; // counted, not kept
                }

                if (before + hits.Count > max)
                {
                    hits.RemoveRange((int)(max - before), hits.Count - (int)(max - before));
                }

                perChunk[c] = hits;
            });

            int count = 0;
            foreach (List<Hit> hits in perChunk)
            {
                count += hits?.Count ?? 0;
            }

            var all = new Hit[count];
            int offset = 0;
            foreach (List<Hit> hits in perChunk)
            {
                if (hits != null)
                {
                    hits.CopyTo(all, offset);
                    offset += hits.Count;
                }
            }

            Order(data, all, options.Sort, options.Descending, cancellationToken);
            return new SearchResults(data, all, total, query, options, stopwatch.Elapsed);
        }

        /// <summary>Content matches score this much lower, so under relevance every name match comes first.</summary>
        internal const int ContentOnlyPenalty = 20_000;

        /// <summary>
        /// A search that involves file contents. With a <c>content:</c> filter only its files are candidates (each checked
        /// against the rest of the query, words included); otherwise the usual scan. Then, when contents stand in for the
        /// words, files whose contents hold every word but whose name does not are added, checked against everything but
        /// the words, and ranked after the name matches.
        /// </summary>
        private static SearchResults RunWithContent(IndexData data, SearchQuery query, SearchOptions options, CompiledQuery compiled, ContentHits content,
            Stopwatch stopwatch, CancellationToken cancellationToken)
        {
            int max = Math.Max(1, options.MaxResults);
            var hits = new List<Hit>();
            long total = 0;
            if (content.FilterIds != null)
            {
                foreach (int id in content.FilterIds)
                {
                    if (TryAcceptId(data, id, compiled, out int score))
                    {
                        total++;
                        if (hits.Count < max)
                        {
                            hits.Add(new Hit(id, score));
                        }
                    }
                }
            }
            else
            {
                SearchResults byName = Run(data, query, new SearchOptions
                {
                    Sort = SearchSort.Relevance,
                    IncludeHidden = options.IncludeHidden,
                    ScopeFolder = options.ScopeFolder,
                    Kind = options.Kind,
                    FoldersOnly = options.FoldersOnly,
                    MaxResults = max,
                    ExcludedFolders = options.ExcludedFolders
                }, cancellationToken);
                hits.AddRange(byName.RawHits);
                total = byName.TotalMatches;
            }

            HashSet<int> contentOnly = null;
            if (content.AlternativeIds != null && compiled.Required.Length > 0)
            {
                CompiledQuery withoutTerms = compiled.WithoutTerms();
                HashSet<int> filter = content.FilterIds != null ? new HashSet<int>(content.FilterIds) : null;
                contentOnly = new HashSet<int>();
                int checks = 0;
                foreach (int id in content.AlternativeIds)
                {
                    if ((++checks & 0x3FF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if ((filter != null && !filter.Contains(id)) || TryAcceptId(data, id, compiled, out _))
                    {
                        continue; // not a candidate, or a name match already
                    }

                    if (TryAcceptId(data, id, withoutTerms, out int score))
                    {
                        total++;
                        if (hits.Count < max)
                        {
                            hits.Add(new Hit(id, score - ContentOnlyPenalty));
                            contentOnly.Add(id);
                        }
                    }
                }
            }

            Hit[] all = hits.ToArray();
            Order(data, all, options.Sort, options.Descending, cancellationToken);
            return new SearchResults(data, all, total, query, options, stopwatch.Elapsed,
                new ContentMarks(content.FilterIds != null, contentOnly, content.Words));
        }

        /// <summary>The query's conditions on one entry found by id (not by the scan, so every word is checked).</summary>
        private static bool TryAcceptId(IndexData data, int id, CompiledQuery query, out int score)
        {
            score = 0;
            if (id < 0 || id >= data.Count)
            {
                return false;
            }

            return Accept(data, data.ChunkOf(id), id & IndexChunk.Mask, id & ~IndexChunk.Mask, query, out score, anchorVerified: false);
        }

        private static List<Hit> ScanChunk(IndexData data, IndexChunk chunk, int idBase, CompiledQuery query, CancellationToken cancellationToken)
        {
            List<Hit> hits = null;
            byte[] anchor = query.Anchor;
            if (anchor == null)
            {
                for (int slot = 0; slot < chunk.Count; slot++)
                {
                    if ((slot & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (Accept(data, chunk, slot, idBase, query, out int score))
                    {
                        (hits ??= new List<Hit>()).Add(new Hit(idBase + slot, score));
                    }
                }

                return hits;
            }

            ReadOnlySpan<byte> names = new ReadOnlySpan<byte>(chunk.Folded, 0, chunk.FoldedLength);
            int[] starts = chunk.FoldedStart;
            int position = 0;
            int slotHint = 0;
            int checks = 0;
            while (position < names.Length)
            {
                int found = names.Slice(position).IndexOf(anchor);
                if (found < 0)
                {
                    break;
                }

                int absolute = position + found;
                int slot = FindSlot(starts, chunk.Count, absolute, slotHint);
                slotHint = slot;
                if (Accept(data, chunk, slot, idBase, query, out int score))
                {
                    (hits ??= new List<Hit>()).Add(new Hit(idBase + slot, score));
                }

                position = starts[slot + 1]; // the next name
                if ((++checks & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return hits;
        }

        /// <summary>The slot whose name contains byte <paramref name="position"/> (binary search from the last one found).</summary>
        private static int FindSlot(int[] starts, int count, int position, int low)
        {
            int high = count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) >> 1;
                if (starts[middle] <= position)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }

        /// <summary>Every condition of the query, cheapest first; the score of an accepted entry.</summary>
        private static bool Accept(IndexData data, IndexChunk chunk, int slot, int idBase, CompiledQuery query, out int score, bool anchorVerified = true)
        {
            score = 0;
            byte flags = chunk.Flags[slot];
            if ((flags & (byte)EntryFlags.Deleted) != 0)
            {
                return false;
            }

            if (!query.IncludeHidden && (flags & (byte)EntryFlags.Hidden) != 0)
            {
                return false;
            }

            bool isDirectory = (flags & (byte)EntryFlags.Directory) != 0;
            if (query.FoldersOnly is bool foldersOnly && foldersOnly != isDirectory)
            {
                return false;
            }

            if (query.KindMask != 0 && (isDirectory || (query.KindMask & (1 << chunk.Kind[slot])) == 0))
            {
                return false;
            }

            if (query.MinSize != long.MinValue || query.MaxSize != long.MaxValue)
            {
                long size = chunk.Size[slot];
                if (isDirectory || size < query.MinSize || size > query.MaxSize)
                {
                    return false;
                }
            }

            long ticks = chunk.Ticks[slot];
            if (ticks < query.MinTicks || ticks > query.MaxTicks)
            {
                return false;
            }

            ReadOnlySpan<byte> name = chunk.FoldedName(slot);
            if (query.Extensions.Length > 0)
            {
                bool any = false;
                foreach (byte[] extension in query.Extensions)
                {
                    if (!isDirectory && name.EndsWith(extension))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any)
                {
                    return false;
                }
            }

            foreach (byte[] term in query.Required)
            {
                if ((!anchorVerified || !ReferenceEquals(term, query.Anchor)) && name.IndexOf(term) < 0)
                {
                    return false;
                }
            }

            foreach (byte[] term in query.Excluded)
            {
                if (name.IndexOf(term) >= 0)
                {
                    return false;
                }
            }

            foreach (byte[] pattern in query.Patterns)
            {
                if (!Glob.IsMatch(name, pattern))
                {
                    return false;
                }
            }

            foreach (byte[] pattern in query.ExcludedPatterns)
            {
                if (Glob.IsMatch(name, pattern))
                {
                    return false;
                }
            }

            int id = idBase + slot;
            if (query.NeedsAncestorWalk && !AncestorsMatch(data, chunk.Parent[slot], query))
            {
                return false;
            }

            if (query.CheckDeletedAncestors && !data.IsAlive(id))
            {
                return false;
            }

            score = Score(name, isDirectory, chunk.Depth[slot], ticks, (flags & (byte)EntryFlags.Hidden) != 0, query);
            return true;
        }

        /// <summary>The folder words of path terms in order going down, the in: folder names, and the in: folders.</summary>
        private static bool AncestorsMatch(IndexData data, int parent, CompiledQuery query)
        {
            if (query.ExcludedFolderIds.Length > 0)
            {
                for (int current = parent; current >= 0; current = data.ParentOf(current))
                {
                    if (Array.IndexOf(query.ExcludedFolderIds, current) >= 0)
                    {
                        return false;
                    }
                }
            }

            if (query.InFolderIds.Length > 0)
            {
                bool inside = false;
                for (int current = parent; current >= 0 && !inside; current = data.ParentOf(current))
                {
                    inside = Array.IndexOf(query.InFolderIds, current) >= 0;
                }

                if (!inside)
                {
                    return false;
                }
            }

            foreach (byte[] folderName in query.InFolderNames)
            {
                bool found = false;
                for (int current = parent; current >= 0 && !found; current = data.ParentOf(current))
                {
                    found = data.FoldedName(current).IndexOf(folderName) >= 0;
                }

                if (!found)
                {
                    return false;
                }
            }

            foreach (byte[][] segments in query.FolderSegments)
            {
                // Innermost segment first, each in a folder above the previous one ("a/b/x": b above x, a above b).
                int segment = segments.Length - 1;
                for (int current = parent; current >= 0 && segment >= 0; current = data.ParentOf(current))
                {
                    if (data.FoldedName(current).IndexOf(segments[segment]) >= 0)
                    {
                        segment--;
                    }
                }

                if (segment >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Relevance: an exact name first, then the name without its extension, a name starting with the word, the word
        /// starting a word of the name; shorter names, shallower places, folders, recent changes rank higher, hidden items
        /// lower.
        /// </summary>
        private static int Score(ReadOnlySpan<byte> name, bool isDirectory, byte depth, long ticks, bool hidden, CompiledQuery query)
        {
            int score = 0;
            byte[] primary = query.PrimaryTerm;
            if (primary != null && primary.Length > 0)
            {
                int at = name.IndexOf(primary);
                int dot = name.LastIndexOf((byte)'.');
                if (name.SequenceEqual(primary))
                {
                    score += 1000;
                }
                else if (dot > 0 && name.Slice(0, dot).SequenceEqual(primary))
                {
                    score += 900;
                }
                else if (at == 0)
                {
                    score += 600;
                }
                else if (at > 0 && IsWordBoundary(name[at - 1]))
                {
                    score += 300;
                }

                foreach (byte[] term in query.Required)
                {
                    if (!ReferenceEquals(term, primary))
                    {
                        int other = name.IndexOf(term);
                        if (other == 0 || (other > 0 && IsWordBoundary(name[other - 1])))
                        {
                            score += 60;
                        }
                    }
                }
            }

            score -= Math.Min(name.Length, 120);
            score -= Math.Min((int)depth, 30) * 4;
            if (isDirectory)
            {
                score += 25;
            }

            long age = query.NowTicks - ticks;
            if (age >= 0)
            {
                if (age < TimeSpan.TicksPerDay * 7)
                {
                    score += 60;
                }
                else if (age < TimeSpan.TicksPerDay * 30)
                {
                    score += 30;
                }
                else if (age < TimeSpan.TicksPerDay * 365)
                {
                    score += 10;
                }
            }

            if (hidden)
            {
                score -= 250;
            }

            return score;
        }

        private static bool IsWordBoundary(byte previous) =>
            previous is (byte)' ' or (byte)'.' or (byte)'_' or (byte)'-' or (byte)'(' or (byte)'[' or (byte)'{' or (byte)',' or (byte)'+' or (byte)'@' or (byte)'#';

        // ------------------------------------------------------------------ ordering

        /// <summary>
        /// Orders the matches. Relevance, name, size and date first sort a 64-bit key per match with the runtime's
        /// primitive sort (fast and cache-friendly), then put each run of equal keys in order with the full comparison;
        /// the keys are built so that their order never contradicts it (see <see cref="NameKey"/>), so the result is
        /// exactly the comparer's order. Sorting 200,000 matches by comparer alone took a few hundred milliseconds,
        /// most of it comparing names of equal scores. Type and folder order use the comparer directly.
        /// </summary>
        public static void Order(IndexData data, Hit[] hits, SearchSort sort, bool descending, CancellationToken cancellationToken)
        {
            if (hits.Length < 2)
            {
                return;
            }

            IComparer<Hit> comparer = sort switch
            {
                SearchSort.Name => new NameComparer(data, descending),
                SearchSort.Size => new SizeComparer(data, descending),
                SearchSort.Modified => new ModifiedComparer(data, descending),
                SearchSort.Type => new TypeComparer(data, descending),
                SearchSort.Folder => new FolderComparer(data, hits, descending, cancellationToken),
                _ => new RelevanceComparer(data, descending)
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (sort == SearchSort.Type || sort == SearchSort.Folder)
            {
                DirectoryEntrySorter.SortRange(hits, 0, hits.Length, comparer);
                return;
            }

            var keys = new ulong[hits.Length];
            for (int i = 0; i < hits.Length; i++)
            {
                keys[i] = KeyOf(data, hits[i], sort, descending);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Array.Sort(keys, hits);

            // Runs of equal keys, put in order by the comparer; many or large runs on several cores.
            var runs = new List<(int Start, int Length)>();
            long tied = 0;
            for (int start = 0; start < hits.Length;)
            {
                int end = start + 1;
                while (end < hits.Length && keys[end] == keys[start])
                {
                    end++;
                }

                if (end - start > 1)
                {
                    runs.Add((start, end - start));
                    tied += end - start;
                }

                start = end;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (tied < 20_000)
            {
                foreach ((int start, int length) in runs)
                {
                    Array.Sort(hits, start, length, comparer);
                }

                return;
            }

            Parallel.ForEach(runs, new ParallelOptions { CancellationToken = cancellationToken }, run =>
            {
                if (run.Length >= DirectoryEntrySorter.ParallelThreshold)
                {
                    DirectoryEntrySorter.SortRange(hits, run.Start, run.Length, comparer);
                }
                else
                {
                    Array.Sort(hits, run.Start, run.Length, comparer);
                }
            });
        }

        /// <summary>A key whose ascending order agrees with the comparer of <paramref name="sort"/> (equal keys: compare in full).</summary>
        private static ulong KeyOf(IndexData data, Hit hit, SearchSort sort, bool descending)
        {
            IndexChunk chunk = data.ChunkOf(hit.Id);
            int slot = hit.Id & IndexChunk.Mask;
            switch (sort)
            {
                case SearchSort.Name:
                {
                    ulong key = NameKey(chunk.FoldedName(slot), 8);
                    return descending ? ~key : key;
                }

                case SearchSort.Size:
                {
                    if ((chunk.Flags[slot] & (byte)EntryFlags.Directory) != 0)
                    {
                        return 0; // folders first both ways, as the comparer does
                    }

                    ulong size = (ulong)Math.Max(0, chunk.Size[slot]);
                    return 1 + (descending ? (ulong)long.MaxValue - size : size);
                }

                case SearchSort.Modified:
                {
                    ulong ticks = (ulong)Math.Max(0, chunk.Ticks[slot]);
                    return descending ? (ulong)long.MaxValue - ticks : ticks;
                }

                default:
                {
                    // Best score first (descending: worst first) in the top 24 bits (scores stay far inside that range),
                    // then the first five bytes of the name.
                    long score = Math.Clamp(hit.Score, -(1 << 22), (1 << 22) - 1);
                    ulong rank = (ulong)(descending ? score + (1 << 22) : (1 << 22) - 1 - score);
                    return (rank << 40) | (NameKey(chunk.FoldedName(slot), 5) >> 24);
                }
            }
        }

        /// <summary>
        /// The first <paramref name="bytes"/> bytes of a folded name as a big-endian number, consistent with
        /// <see cref="NaturalCompare"/>: from the first digit on, the key holds '0' bytes (digit runs compare by value, so
        /// the key must not order them; '0' still orders a digit correctly against any other byte, since all digits are
        /// 0x30-0x39), and a shorter name is padded with 0 bytes (a name before its own extensions).
        /// </summary>
        private static ulong NameKey(ReadOnlySpan<byte> name, int bytes)
        {
            ulong key = 0;
            bool digits = false;
            for (int i = 0; i < 8; i++)
            {
                byte b;
                if (i >= bytes)
                {
                    b = 0;
                }
                else if (digits)
                {
                    b = (byte)'0';
                }
                else if (i < name.Length)
                {
                    b = name[i];
                    if ((uint)(b - '0') <= 9)
                    {
                        digits = true;
                        b = (byte)'0';
                    }
                }
                else
                {
                    b = 0;
                }

                key = (key << 8) | b;
            }

            return key;
        }

        /// <summary>Natural order of folded UTF-8 names: digit runs by value ("file2" before "file10"), then bytes.</summary>
        public static int NaturalCompare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
        {
            int prefix = x.CommonPrefixLength(y);
            while (prefix > 0 && (uint)(x[prefix - 1] - '0') <= 9)
            {
                prefix--;
            }

            int i = prefix;
            int j = prefix;
            while (i < x.Length && j < y.Length)
            {
                byte a = x[i];
                byte b = y[j];
                if ((uint)(a - '0') <= 9 && (uint)(b - '0') <= 9)
                {
                    while (i < x.Length && x[i] == '0') i++;
                    while (j < y.Length && y[j] == '0') j++;
                    int aStart = i;
                    int bStart = j;
                    while (i < x.Length && (uint)(x[i] - '0') <= 9) i++;
                    while (j < y.Length && (uint)(y[j] - '0') <= 9) j++;
                    int lengths = (i - aStart).CompareTo(j - bStart);
                    if (lengths != 0)
                    {
                        return lengths;
                    }

                    int digits = x.Slice(aStart, i - aStart).SequenceCompareTo(y.Slice(bStart, j - bStart));
                    if (digits != 0)
                    {
                        return Math.Sign(digits);
                    }

                    continue;
                }

                if (a != b)
                {
                    return a < b ? -1 : 1;
                }

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        private abstract class HitComparer : IComparer<Hit>
        {
            protected HitComparer(IndexData data, bool descending)
            {
                Data = data;
                Sign = descending ? -1 : 1;
            }

            protected IndexData Data { get; }

            protected int Sign { get; }

            public int Compare(Hit a, Hit b)
            {
                int result = CompareKeys(a, b);
                if (result == 0)
                {
                    result = NaturalCompare(Data.FoldedName(a.Id), Data.FoldedName(b.Id));
                }

                return result != 0 ? result : a.Id.CompareTo(b.Id);
            }

            protected abstract int CompareKeys(Hit a, Hit b);
        }

        private sealed class RelevanceComparer : HitComparer
        {
            public RelevanceComparer(IndexData data, bool descending) : base(data, descending)
            {
            }

            protected override int CompareKeys(Hit a, Hit b) => Sign * b.Score.CompareTo(a.Score); // best first
        }

        private sealed class NameComparer : HitComparer
        {
            public NameComparer(IndexData data, bool descending) : base(data, descending)
            {
            }

            protected override int CompareKeys(Hit a, Hit b) => Sign * NaturalCompare(Data.FoldedName(a.Id), Data.FoldedName(b.Id));
        }

        private sealed class SizeComparer : HitComparer
        {
            public SizeComparer(IndexData data, bool descending) : base(data, descending)
            {
            }

            protected override int CompareKeys(Hit a, Hit b)
            {
                IndexChunk ca = Data.ChunkOf(a.Id);
                IndexChunk cb = Data.ChunkOf(b.Id);
                bool aDirectory = (ca.Flags[a.Id & IndexChunk.Mask] & (byte)EntryFlags.Directory) != 0;
                bool bDirectory = (cb.Flags[b.Id & IndexChunk.Mask] & (byte)EntryFlags.Directory) != 0;
                if (aDirectory != bDirectory)
                {
                    return aDirectory ? -1 : 1; // folders (no size) first in both directions, as in the file list
                }

                return Sign * ca.Size[a.Id & IndexChunk.Mask].CompareTo(cb.Size[b.Id & IndexChunk.Mask]);
            }
        }

        private sealed class ModifiedComparer : HitComparer
        {
            public ModifiedComparer(IndexData data, bool descending) : base(data, descending)
            {
            }

            protected override int CompareKeys(Hit a, Hit b) =>
                Sign * Data.ChunkOf(a.Id).Ticks[a.Id & IndexChunk.Mask].CompareTo(Data.ChunkOf(b.Id).Ticks[b.Id & IndexChunk.Mask]);
        }

        private sealed class TypeComparer : HitComparer
        {
            public TypeComparer(IndexData data, bool descending) : base(data, descending)
            {
            }

            protected override int CompareKeys(Hit a, Hit b)
            {
                bool aDirectory = (Data.FlagsOf(a.Id) & EntryFlags.Directory) != 0;
                bool bDirectory = (Data.FlagsOf(b.Id) & EntryFlags.Directory) != 0;
                if (aDirectory != bDirectory)
                {
                    return aDirectory ? -1 : 1;
                }

                return Sign * Extension(Data.FoldedName(a.Id), aDirectory).SequenceCompareTo(Extension(Data.FoldedName(b.Id), bDirectory));
            }

            private static ReadOnlySpan<byte> Extension(ReadOnlySpan<byte> name, bool directory)
            {
                int dot = name.LastIndexOf((byte)'.');
                return directory || dot <= 0 ? ReadOnlySpan<byte>.Empty : name.Slice(dot);
            }
        }

        /// <summary>By the folder's path (each distinct folder's path built once), then by name.</summary>
        private sealed class FolderComparer : HitComparer
        {
            private readonly Dictionary<int, string> _paths;

            public FolderComparer(IndexData data, Hit[] hits, bool descending, CancellationToken cancellationToken) : base(data, descending)
            {
                _paths = new Dictionary<int, string>();
                for (int i = 0; i < hits.Length; i++)
                {
                    if ((i & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    int parent = data.ParentOf(hits[i].Id);
                    if (!_paths.ContainsKey(parent))
                    {
                        _paths[parent] = parent < 0 ? string.Empty : SearchQuery.Fold(data.GetFullPath(parent));
                    }
                }
            }

            protected override int CompareKeys(Hit a, Hit b) =>
                Sign * ByteExplorer.Core.Native.NaturalStringComparer.CompareManaged(_paths[Data.ParentOf(a.Id)], _paths[Data.ParentOf(b.Id)]);
        }

        // ------------------------------------------------------------------ helpers

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? string.Empty);

        private static IEnumerable<byte[]> LiteralParts(byte[] pattern)
        {
            int start = 0;
            for (int i = 0; i <= pattern.Length; i++)
            {
                if (i == pattern.Length || pattern[i] == (byte)'*' || pattern[i] == (byte)'?')
                {
                    if (i > start)
                    {
                        yield return pattern.AsSpan(start, i - start).ToArray();
                    }

                    start = i + 1;
                }
            }
        }
    }

    /// <summary>Whole-name patterns over folded UTF-8: <c>*</c> any run, <c>?</c> one character (a whole UTF-8 sequence).</summary>
    internal static class Glob
    {
        public static bool IsMatch(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)
        {
            int t = 0;
            int p = 0;
            int starP = -1;
            int starT = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == (byte)'?')
                {
                    t += SequenceLength(text[t]);
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == (byte)'*')
                {
                    starP = p++;
                    starT = t;
                }
                else if (p < pattern.Length && pattern[p] == text[t])
                {
                    t++;
                    p++;
                }
                else if (starP >= 0)
                {
                    p = starP + 1;
                    starT += SequenceLength(text[starT]);
                    t = starT;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == (byte)'*')
            {
                p++;
            }

            return p == pattern.Length && t == text.Length;
        }

        private static int SequenceLength(byte lead) => lead < 0x80 ? 1 : lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
    }

    /// <summary>One matched item, as a results view shows it.</summary>
    public sealed class SearchHit
    {
        internal SearchHit(int id, string name, string fullPath, bool isDirectory, bool isHidden, long size, DateTime modifiedUtc, FileKind kind, IReadOnlyList<TextRange> highlights,
            bool isSystem = false, bool isContentMatch = false)
        {
            IsSystem = isSystem;
            IsContentMatch = isContentMatch;
            Id = id;
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            IsHidden = isHidden;
            Size = size;
            ModifiedUtc = modifiedUtc;
            Kind = kind;
            NameHighlights = highlights;
        }

        /// <summary>The entry's id in the index state the results came from.</summary>
        public int Id { get; }

        public string Name { get; }

        public string FullPath { get; }

        /// <summary>The folder holding the item.</summary>
        public string FolderPath => PathUtil.GetParent(FullPath) ?? string.Empty;

        public bool IsDirectory { get; }

        public bool IsHidden { get; }

        /// <summary>Has the system attribute (Windows).</summary>
        public bool IsSystem { get; }

        public long Size { get; }

        public DateTime ModifiedUtc { get; }

        public FileKind Kind { get; }

        /// <summary>The parts of <see cref="Name"/> matching the search words (char ranges, sorted, not overlapping).</summary>
        public IReadOnlyList<TextRange> NameHighlights { get; }

        /// <summary>Found by its contents (the results view shows a "Contains: …" snippet).</summary>
        public bool IsContentMatch { get; }
    }

    /// <summary>Which results were found by their contents, and the words to show a snippet for.</summary>
    internal sealed class ContentMarks
    {
        public ContentMarks(bool all, HashSet<int> ids, string[] words)
        {
            All = all;
            Ids = ids;
            Words = words ?? Array.Empty<string>();
        }

        /// <summary>Every result (a content: filter).</summary>
        public bool All { get; }

        /// <summary>The results found by contents only (when not <see cref="All"/>).</summary>
        public HashSet<int> Ids { get; }

        public string[] Words { get; }

        public bool Contains(int id) => All || (Ids != null && Ids.Contains(id));
    }

    /// <summary>A range of characters in a string.</summary>
    public readonly record struct TextRange(int Start, int Length);

    /// <summary>
    /// The outcome of a search: every kept match in order (ids and scores only; an item's name and path are built when it
    /// is shown, see <see cref="GetHit"/>), how many matched in all, and how long it took. Holds the index state it was
    /// computed on, so it stays valid while the index changes; re-ordering it (<see cref="Sorted"/>) does not search again.
    /// </summary>
    public sealed class SearchResults
    {
        private readonly Hit[] _hits;
        private readonly string[] _highlightTerms;

        /// <summary>Ready-made hits (results that do not come from the index, such as a tag's items); null for index results.</summary>
        private readonly SearchHit[] _external;

        private SearchResults(SearchHit[] hits, SearchQuery query, SearchOptions options, TimeSpan elapsed)
        {
            Data = IndexData.Empty;
            _hits = Array.Empty<Hit>();
            _external = hits;
            TotalMatches = hits.Length;
            Query = query;
            Options = options;
            Elapsed = elapsed;
            _highlightTerms = Array.Empty<string>();
        }

        /// <summary>
        /// Results made of items found another way (the items of a tag), shown and sorted by the same results view; the
        /// hits keep their own highlights.
        /// </summary>
        public static SearchResults FromHits(IReadOnlyList<SearchHit> hits, string queryText, SearchOptions options, TimeSpan elapsed) =>
            new SearchResults((hits ?? Array.Empty<SearchHit>()).ToArray(), SearchQuery.Parse(queryText ?? string.Empty), options ?? new SearchOptions(), elapsed);

        internal SearchResults(IndexData data, Hit[] hits, long totalMatches, SearchQuery query, SearchOptions options, TimeSpan elapsed, ContentMarks content = null)
        {
            Content = content;
            Data = data;
            _hits = hits;
            TotalMatches = totalMatches;
            Query = query;
            Options = options;
            Elapsed = elapsed;
            _highlightTerms = query.HighlightTerms.Where(term => term.Length > 0).Distinct().ToArray();
        }

        public static SearchResults Empty { get; } = new SearchResults(IndexData.Empty, Array.Empty<Hit>(), 0, SearchQuery.Parse(string.Empty), new SearchOptions(), TimeSpan.Zero);

        internal IndexData Data { get; }

        /// <summary>The kept matches in order (ids and scores).</summary>
        internal Hit[] RawHits => _hits;

        /// <summary>Which results were found by contents; null when contents played no part.</summary>
        internal ContentMarks Content { get; }

        /// <summary>The words a content result's snippet looks for (empty when contents played no part).</summary>
        public IReadOnlyList<string> ContentWords => Content?.Words ?? Array.Empty<string>();

        public SearchQuery Query { get; }

        public SearchOptions Options { get; }

        /// <summary>Matches kept (at most <see cref="SearchOptions.MaxResults"/>).</summary>
        public int Count => _external?.Length ?? _hits.Length;

        /// <summary>Every match, kept or not.</summary>
        public long TotalMatches { get; }

        public bool IsTruncated => TotalMatches > Count;

        public TimeSpan Elapsed { get; }

        public IReadOnlyList<string> HighlightTerms => _highlightTerms;

        /// <summary>The item at <paramref name="index"/> in the current order, built now (name, path, highlights).</summary>
        /// <summary>The modification time of a result, without building its path (the lens groups and time river).</summary>
        public DateTime ModifiedUtcAt(int index)
        {
            if (_external != null)
            {
                return _external[index].ModifiedUtc;
            }

            int id = _hits[index].Id;
            long ticks = Data.ChunkOf(id).Ticks[id & IndexChunk.Mask];
            return new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
        }

        public SearchHit GetHit(int index)
        {
            if (_external != null)
            {
                return _external[index];
            }

            Hit hit = _hits[index];
            IndexChunk chunk = Data.ChunkOf(hit.Id);
            int slot = hit.Id & IndexChunk.Mask;
            byte flags = chunk.Flags[slot];
            string name = Data.GetName(hit.Id);
            return new SearchHit(
                hit.Id,
                name,
                Data.GetFullPath(hit.Id),
                (flags & (byte)EntryFlags.Directory) != 0,
                (flags & (byte)EntryFlags.Hidden) != 0,
                chunk.Size[slot],
                new DateTime(Math.Clamp(chunk.Ticks[slot], DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc),
                (FileKind)chunk.Kind[slot],
                Highlights(name),
                (flags & (byte)EntryFlags.System) != 0,
                Content != null && Content.Contains(hit.Id));
        }

        /// <summary>The same matches in another order (runs the sort on the calling thread).</summary>
        public SearchResults Sorted(SearchSort sort, bool descending, CancellationToken cancellationToken = default)
        {
            if (_external != null)
            {
                return SortedExternal(sort, descending);
            }

            var hits = (Hit[])_hits.Clone();
            SearchEngine.Order(Data, hits, sort, descending, cancellationToken);
            var options = new SearchOptions
            {
                Sort = sort,
                Descending = descending,
                IncludeHidden = Options.IncludeHidden,
                ScopeFolder = Options.ScopeFolder,
                Kind = Options.Kind,
                FoldersOnly = Options.FoldersOnly,
                MaxResults = Options.MaxResults,
                ExcludedFolders = Options.ExcludedFolders,
                SearchContents = Options.SearchContents
            };
            return new SearchResults(Data, hits, TotalMatches, Query, options, Elapsed, Content);
        }

        private SearchResults SortedExternal(SearchSort sort, bool descending)
        {
            var hits = (SearchHit[])_external.Clone();
            Comparison<SearchHit> compare = sort switch
            {
                SearchSort.Name => (a, b) => ByteExplorer.Core.Native.NaturalStringComparer.Instance.Compare(a.Name, b.Name),
                SearchSort.Folder => (a, b) => string.Compare(a.FolderPath, b.FolderPath, StringComparison.CurrentCultureIgnoreCase),
                SearchSort.Size => (a, b) => a.Size.CompareTo(b.Size),
                SearchSort.Modified => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
                SearchSort.Type => (a, b) => string.Compare(System.IO.Path.GetExtension(a.Name), System.IO.Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
                _ => null
            };
            if (compare != null)
            {
                // Folders first, as in the index results; a stable order for equal keys (by name).
                Comparison<SearchHit> full = (a, b) =>
                {
                    if (a.IsDirectory != b.IsDirectory)
                    {
                        return a.IsDirectory ? -1 : 1;
                    }

                    int c = compare(a, b);
                    if (descending)
                    {
                        c = -c;
                    }

                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                };
                hits = hits.OrderBy(h => h, Comparer<SearchHit>.Create(full)).ToArray();
            }

            var options = new SearchOptions
            {
                Sort = sort,
                Descending = descending,
                IncludeHidden = Options.IncludeHidden,
                ScopeFolder = Options.ScopeFolder,
                Kind = Options.Kind,
                FoldersOnly = Options.FoldersOnly,
                MaxResults = Options.MaxResults,
                ExcludedFolders = Options.ExcludedFolders,
                SearchContents = Options.SearchContents
            };
            return new SearchResults(hits, Query, options, Elapsed);
        }

        /// <summary>
        /// Where the search words occur in the name. Folding keeps one char per char, so positions in the folded name are
        /// positions in the real one.
        /// </summary>
        private IReadOnlyList<TextRange> Highlights(string name)
        {
            if (_highlightTerms.Length == 0 || string.IsNullOrEmpty(name))
            {
                return Array.Empty<TextRange>();
            }

            string folded = SearchQuery.Fold(name);
            var ranges = new List<TextRange>();
            foreach (string term in _highlightTerms)
            {
                for (int at = folded.IndexOf(term, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(term, at + term.Length, StringComparison.Ordinal))
                {
                    ranges.Add(new TextRange(at, term.Length));
                }
            }

            if (ranges.Count < 2)
            {
                return ranges;
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<TextRange> { ranges[0] };
            for (int i = 1; i < ranges.Count; i++)
            {
                TextRange last = merged[^1];
                TextRange next = ranges[i];
                if (next.Start <= last.Start + last.Length)
                {
                    merged[^1] = new TextRange(last.Start, Math.Max(last.Start + last.Length, next.Start + next.Length) - last.Start);
                }
                else
                {
                    merged.Add(next);
                }
            }

            return merged;
        }
    }
}
