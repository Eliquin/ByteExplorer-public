// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ByteSearcher;

namespace ByteExplorer.Core.Tags
{
    /// <summary>
    /// Searching by tag: <c>tag:Work</c>, <c>tag:"Work/Project A"</c>, several (<c>tag:Work tag:Urgent</c>, items with all
    /// of them), plus words the names must contain (<c>tag:Invoices 2024</c>). A tag includes its sub-tags; a name without a
    /// parent finds the tag of that name wherever it sits. The results come straight from the tag store, so tagged items
    /// outside the indexed locations are found too, and they are shown by the usual results view (sorting, filters, the
    /// file menu).
    /// </summary>
    public static class TagSearch
    {
        public const string Prefix = "tag:";

        /// <summary>The query in the search box for one tag ("tag:\"Work/Project A\"").</summary>
        public static string QueryFor(string fullName) =>
            fullName.IndexOfAny(new[] { ' ', '"' }) >= 0 ? Prefix + "\"" + fullName.Replace("\"", string.Empty) + "\"" : Prefix + fullName;

        /// <summary>Splits the query into tag names and the remaining words; false when it names no tag.</summary>
        public static bool TryParse(string query, out IReadOnlyList<string> tagNames, out string rest)
        {
            var names = new List<string>();
            var remaining = new StringBuilder();
            string text = query ?? string.Empty;
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    remaining.Append(' ');
                    i++;
                    continue;
                }

                bool atWordStart = i == 0 || char.IsWhiteSpace(text[i - 1]);
                if (atWordStart && string.Compare(text, i, Prefix, 0, Prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    i += Prefix.Length;
                    string name;
                    if (i < text.Length && text[i] == '"')
                    {
                        int end = text.IndexOf('"', i + 1);
                        name = end < 0 ? text.Substring(i + 1) : text.Substring(i + 1, end - i - 1);
                        i = end < 0 ? text.Length : end + 1;
                    }
                    else
                    {
                        int end = i;
                        while (end < text.Length && !char.IsWhiteSpace(text[end]))
                        {
                            end++;
                        }

                        name = text.Substring(i, end - i);
                        i = end;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name.Trim());
                    }

                    continue;
                }

                remaining.Append(text[i]);
                i++;
            }

            tagNames = names;
            rest = remaining.ToString().Trim();
            return names.Count > 0;
        }

        /// <summary>
        /// The tags a name refers to: the one with that full name, or every tag with that name (any parent). Empty when
        /// none.
        /// </summary>
        public static IReadOnlyList<TagDefinition> Resolve(TagStore store, string name)
        {
            TagDefinition exact = store.FindByFullName(name);
            if (exact != null)
            {
                return new[] { exact };
            }

            string own = name.Contains('/') ? name.Substring(name.LastIndexOf('/') + 1).Trim() : name.Trim();
            return store.Tags.Where(t => string.Equals(t.Name, own, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        /// <summary>
        /// Runs a tag query (on the calling thread: it reads each item's size and date). Missing items are left out;
        /// <paramref name="kind"/> and <paramref name="foldersOnly"/> are the results view's filter chips.
        /// </summary>
        public static SearchResults Run(TagStore store, string query, SearchOptions options, CancellationToken cancellationToken, out string unknownTag)
        {
            var stopwatch = Stopwatch.StartNew();
            unknownTag = null;
            if (!TryParse(query, out IReadOnlyList<string> names, out string rest))
            {
                return SearchResults.Empty;
            }

            HashSet<string> paths = null;
            foreach (string name in names)
            {
                IReadOnlyList<TagDefinition> tags = Resolve(store, name);
                if (tags.Count == 0)
                {
                    unknownTag = name;
                    return SearchResults.FromHits(Array.Empty<SearchHit>(), query, options, stopwatch.Elapsed);
                }

                var withTag = new HashSet<string>(tags.SelectMany(t => store.ItemsWithTag(t.Id, includeSubTags: true)), PathUtil.Comparer);
                if (paths == null)
                {
                    paths = withTag;
                }
                else
                {
                    paths.IntersectWith(withTag);
                }
            }

            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SearchQuery.Fold).ToArray();
            var hits = new List<SearchHit>();
            int id = 0;
            foreach (string path in paths ?? new HashSet<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = PathUtil.GetName(path);
                string folded = SearchQuery.Fold(name);
                if (!words.All(w => folded.Contains(w, StringComparison.Ordinal)))
                {
                    continue;
                }

                SearchHit hit = Describe(path, name, folded, words, id);
                if (hit == null || options?.FoldersOnly == true && !hit.IsDirectory || options?.FoldersOnly == false && hit.IsDirectory ||
                    options?.Kind != null && (hit.IsDirectory || hit.Kind != options.Kind))
                {
                    continue;
                }

                hits.Add(hit);
                id++;
            }

            SearchResults results = SearchResults.FromHits(hits, rest, options, stopwatch.Elapsed);
            SearchSort sort = options?.Sort ?? SearchSort.Relevance;
            return results.Sorted(sort == SearchSort.Relevance ? SearchSort.Name : sort, options?.Descending ?? false, cancellationToken);
        }

        private static SearchHit Describe(string path, string name, string folded, string[] words, int id)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (directory.Exists)
                {
                    return new SearchHit(id, name, path, true, (directory.Attributes & FileAttributes.Hidden) != 0, 0, directory.LastWriteTimeUtc,
                        FileKind.None, Highlights(folded, words), (directory.Attributes & FileAttributes.System) != 0);
                }

                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return null;
                }

                return new SearchHit(id, name, path, false, (file.Attributes & FileAttributes.Hidden) != 0, file.Length, file.LastWriteTimeUtc,
                    FileKinds.Of(name), Highlights(folded, words), (file.Attributes & FileAttributes.System) != 0);
            }
            catch (Exception)
            {
                return null; // unreadable now: left out of this list, its tags kept
            }
        }

        private static IReadOnlyList<TextRange> Highlights(string folded, string[] words)
        {
            if (words.Length == 0)
            {
                return Array.Empty<TextRange>();
            }

            var ranges = new List<TextRange>();
            foreach (string word in words)
            {
                int at = folded.IndexOf(word, StringComparison.Ordinal);
                if (at >= 0)
                {
                    ranges.Add(new TextRange(at, word.Length));
                }
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            return ranges;
        }
    }
}
