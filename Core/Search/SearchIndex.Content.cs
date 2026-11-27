// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>The name index's side of content search: asking the content index, and turning its paths into entries.</summary>
    public sealed partial class SearchIndex
    {
        /// <summary>The most phrase candidates confirmed by reading the file; beyond, the index's word match is trusted.</summary>
        internal const int MaxPhraseChecks = 400;

        private int _activeSearches;

        /// <summary>The content index searched by <c>content:</c> and, when asked, by the words; null when there is none.</summary>
        internal ContentIndex Content { get; set; }

        /// <summary>A search is running (the content indexer waits meanwhile).</summary>
        public bool IsSearching => Volatile.Read(ref _activeSearches) > 0;

        /// <summary>The index state searches use now (the content indexer's list of files).</summary>
        internal IndexData CurrentData => Current;

        /// <summary>
        /// The content matches of a search as entry ids: the content: filter's files (words and phrases, phrases confirmed
        /// by reading up to <see cref="MaxPhraseChecks"/> files), and the files whose contents hold the words when the
        /// options ask for it. Null when contents play no part.
        /// </summary>
        internal static ContentHits FindContent(IndexData data, ContentIndex content, SearchQuery query, SearchOptions options, CancellationToken cancellationToken)
        {
            bool alternative = options.SearchContents && query.Terms.Count > 0 && content != null;
            if (!query.HasContentFilter && !alternative)
            {
                return null;
            }

            var hits = new ContentHits();
            var words = new List<string>(query.ContentWords);
            words.AddRange(query.ContentPhrases);
            if (query.HasContentFilter)
            {
                if (content == null)
                {
                    hits.FilterIds = Array.Empty<int>();
                }
                else
                {
                    var terms = ContentMatcher.TermsForWords(query.ContentWords);
                    foreach (string phrase in query.ContentPhrases)
                    {
                        terms.AddRange(ContentMatcher.TermsForPhrase(phrase));
                    }

                    List<string> paths = terms.Count == 0 ? new List<string>() : content.Search(terms, cancellationToken);
                    if (query.ContentPhrases.Count > 0 && paths.Count > 0)
                    {
                        paths = ConfirmPhrases(paths, query.ContentPhrases, cancellationToken);
                    }

                    hits.FilterIds = ResolvePaths(data, paths);
                }
            }

            if (alternative)
            {
                List<ContentTerm> terms = ContentMatcher.TermsForWords(query.Terms);
                if (terms.Count > 0)
                {
                    hits.AlternativeIds = ResolvePaths(data, content.Search(terms, cancellationToken));
                    words.AddRange(query.Terms);
                }
            }

            hits.Words = words.Distinct(StringComparer.Ordinal).ToArray();
            return hits;
        }

        private static List<string> ConfirmPhrases(List<string> paths, List<string> phrases, CancellationToken cancellationToken)
        {
            int checkedCount = Math.Min(paths.Count, MaxPhraseChecks);
            var keep = new bool[checkedCount];
            Parallel.For(0, checkedCount, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
            {
                keep[i] = phrases.All(phrase => ContentMatcher.ContainsPhrase(paths[i], phrase, cancellationToken));
            });

            var confirmed = new List<string>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                if (i >= checkedCount || keep[i])
                {
                    confirmed.Add(paths[i]);
                }
            }

            return confirmed;
        }

        /// <summary>
        /// The entry ids of full paths (sorted; paths not in the index are dropped). The roots are found once, then each
        /// path walks down one name per folder; a folder's children are put in a dictionary the first time a path passes
        /// through it, so many files in one folder cost one pass over that folder.
        /// </summary>
        internal static int[] ResolvePaths(IndexData data, IReadOnlyCollection<string> paths)
        {
            if (data == null || paths == null || paths.Count == 0)
            {
                return Array.Empty<int>();
            }

            var roots = new List<(string Prefix, int Id)>();
            foreach (int rootId in data.RootIds())
            {
                if ((data.FlagsOf(rootId) & EntryFlags.Deleted) == 0)
                {
                    roots.Add((PathUtil.WithTrailingSeparator(PathUtil.TrimTrailingSeparator(data.GetName(rootId))), rootId));
                }
            }

            roots.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length)); // the longest (innermost) root first
            var children = new Dictionary<int, Dictionary<string, int>>();
            var ids = new List<int>(paths.Count);
            foreach (string path in paths)
            {
                int id = Resolve(data, roots, children, path);
                if (id >= 0 && data.IsAlive(id))
                {
                    ids.Add(id);
                }
            }

            ids.Sort();
            return ids.ToArray();
        }

        private static int Resolve(IndexData data, List<(string Prefix, int Id)> roots, Dictionary<int, Dictionary<string, int>> children, string path)
        {
            foreach ((string prefix, int rootId) in roots)
            {
                if (!path.StartsWith(prefix, PathUtil.Comparison))
                {
                    continue;
                }

                int current = rootId;
                int position = prefix.Length;
                while (position < path.Length && current >= 0)
                {
                    int end = position;
                    while (end < path.Length && !PathUtil.IsSeparator(path[end]))
                    {
                        end++;
                    }

                    if (!children.TryGetValue(current, out Dictionary<string, int> names))
                    {
                        names = new Dictionary<string, int>(PathUtil.Comparer);
                        foreach (int child in data.Children(current))
                        {
                            if ((data.FlagsOf(child) & EntryFlags.Deleted) == 0)
                            {
                                names[data.GetName(child)] = child;
                            }
                        }

                        children[current] = names;
                    }

                    current = names.TryGetValue(path.Substring(position, end - position), out int next) ? next : -1;
                    position = end + 1;
                }

                return current;
            }

            return -1;
        }
    }
}
