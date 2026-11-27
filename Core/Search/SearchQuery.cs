// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ByteExplorer.Core;

namespace ByteSearcher
{
    /// <summary>How search results are ordered.</summary>
    public enum SearchSort
    {
        Relevance,
        Name,
        Folder,
        Size,
        Modified,
        Type
    }

    /// <summary>What a search looks at besides its text.</summary>
    public sealed class SearchOptions
    {
        public SearchSort Sort { get; set; } = SearchSort.Relevance;

        public bool Descending { get; set; }

        /// <summary>Whether hidden items (and everything inside hidden folders) are found.</summary>
        public bool IncludeHidden { get; set; } = true;

        /// <summary>Only items inside this folder (the "this folder" scope); null for everywhere.</summary>
        public string ScopeFolder { get; set; }

        /// <summary>Only this kind of file (a quick filter of the results view), or null.</summary>
        public FileKind? Kind { get; set; }

        /// <summary>True: folders only; false: files only; null: both.</summary>
        public bool? FoldersOnly { get; set; }

        /// <summary>
        /// The most matches kept for ordering and showing; matches beyond it are still counted. A million keeps a
        /// one-letter search on a large disk responsive (ids and scores only: 8 MB).
        /// </summary>
        public int MaxResults { get; set; } = 1_000_000;

        /// <summary>
        /// Folders whose contents are left out (the lenses leave out Windows, program folders and application data, so
        /// "Photos" means the person's pictures, not every program's icons). Folders not in the index are ignored.
        /// </summary>
        public IReadOnlyList<string> ExcludedFolders { get; set; }

        /// <summary>
        /// Whether the words also match file contents (Settings › Search › "Search file contents"): files whose name
        /// holds every word come first, then files whose indexed contents do. Needs the content index; the
        /// <c>content:</c> filter works either way.
        /// </summary>
        public bool SearchContents { get; set; }
    }

    /// <summary>
    /// A search line taken apart. The syntax, close to what Windows search and Everything accept:
    /// <list type="bullet">
    /// <item>words: every word must appear in the name, in any order (<c>tax 2024</c>); case is ignored.</item>
    /// <item><c>"exact phrase"</c>: the phrase as one word, spaces included.</item>
    /// <item><c>-word</c> (or <c>!word</c>): names containing it are left out.</item>
    /// <item><c>*</c> and <c>?</c>: a pattern for the whole name (<c>*.pdf</c>, <c>report-??.docx</c>).</item>
    /// <item><c>photos/beach</c> (or <c>\</c>): "beach" in the name, inside a folder whose name contains "photos".</item>
    /// <item><c>ext:pdf</c> (several: <c>ext:jpg;png</c>), <c>kind:</c> or <c>type:</c> image, video, audio, document,
    /// archive, code, folder, file.</item>
    /// <item><c>size:</c> <c>&gt;10mb</c>, <c>&lt;=1g</c>, <c>1mb..5mb</c>, or empty, tiny, small, medium, large, huge, gigantic.</item>
    /// <item><c>modified:</c> (or <c>dm:</c>, <c>date:</c>) today, yesterday, week, month, year, <c>2024</c>, <c>2024-05</c>,
    /// <c>&gt;2024-05-01</c>, <c>2023..2024</c>.</item>
    /// <item><c>in:</c> a folder path (<c>in:C:\Projects</c>, only inside it) or part of a folder name (<c>in:backup</c>).</item>
    /// <item><c>content:</c> (or <c>contains:</c>) a word the file's text must contain (<c>content:invoice</c>, words of three
    /// letters or more also match longer words starting with them), or <c>content:"exact phrase"</c>; needs the content
    /// index (Settings › Search).</item>
    /// </list>
    /// A filter whose value cannot be read is searched for as plain text instead, so nothing typed is silently dropped.
    /// </summary>
    public sealed class SearchQuery
    {
        private SearchQuery()
        {
        }

        /// <summary>Folded words that must all appear in the name.</summary>
        public List<string> Terms { get; } = new List<string>();

        /// <summary>Folded words that must not appear in the name.</summary>
        public List<string> ExcludedTerms { get; } = new List<string>();

        /// <summary>Folded whole-name patterns with * and ?.</summary>
        public List<string> Patterns { get; } = new List<string>();

        public List<string> ExcludedPatterns { get; } = new List<string>();

        /// <summary>Folder words of path terms ("photos" in photos/beach), each list from the outermost folder in.</summary>
        public List<string[]> FolderSegments { get; } = new List<string[]>();

        /// <summary>Folded parts of folder names (in:backup): some folder above the item must contain each.</summary>
        public List<string> InFolderNames { get; } = new List<string>();

        /// <summary>Absolute folders (in:C:\Projects): the item must be inside one of them.</summary>
        public List<string> InFolders { get; } = new List<string>();

        /// <summary>Folded extensions with their dot (".pdf").</summary>
        public List<string> Extensions { get; } = new List<string>();

        public List<FileKind> Kinds { get; } = new List<FileKind>();

        /// <summary>True: folders only; false: files only.</summary>
        public bool? FoldersOnly { get; set; }

        public long MinSize { get; set; } = long.MinValue;

        public long MaxSize { get; set; } = long.MaxValue;

        public long MinTicks { get; set; } = long.MinValue;

        public long MaxTicks { get; set; } = long.MaxValue;

        /// <summary>Words of content: filters, as typed: the file's text must contain each.</summary>
        public List<string> ContentWords { get; } = new List<string>();

        /// <summary>Phrases of content: filters (content:"exact phrase"), as typed.</summary>
        public List<string> ContentPhrases { get; } = new List<string>();

        /// <summary>The query has a content: filter.</summary>
        public bool HasContentFilter => ContentWords.Count > 0 || ContentPhrases.Count > 0;

        /// <summary>The query as typed.</summary>
        public string Text { get; private set; }

        /// <summary>Nothing to match by: no words, patterns or filters.</summary>
        public bool IsEmpty =>
            Terms.Count == 0 && ExcludedTerms.Count == 0 && Patterns.Count == 0 && ExcludedPatterns.Count == 0 &&
            FolderSegments.Count == 0 && InFolderNames.Count == 0 && InFolders.Count == 0 && Extensions.Count == 0 &&
            Kinds.Count == 0 && FoldersOnly == null && MinSize == long.MinValue && MaxSize == long.MaxValue &&
            MinTicks == long.MinValue && MaxTicks == long.MaxValue && !HasContentFilter;

        /// <summary>The texts to highlight in result names: the words and the literal parts of the patterns.</summary>
        public IReadOnlyList<string> HighlightTerms
        {
            get
            {
                var terms = new List<string>(Terms);
                foreach (string pattern in Patterns)
                {
                    foreach (string part in pattern.Split('*', '?'))
                    {
                        if (part.Length > 0)
                        {
                            terms.Add(part);
                        }
                    }
                }

                return terms;
            }
        }

        /// <summary>Lower case as the index folds names: invariant, per UTF-16 char.</summary>
        public static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return string.Create(text.Length, text, static (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    span[i] = c < 128 ? (c >= 'A' && c <= 'Z' ? (char)(c + 32) : c) : char.ToLowerInvariant(c);
                }
            });
        }

        public static SearchQuery Parse(string text, DateTime? nowLocal = null)
        {
            var query = new SearchQuery { Text = text ?? string.Empty };
            DateTime now = nowLocal ?? DateTime.Now;
            foreach ((string token, bool quoted) in Tokenize(query.Text))
            {
                query.AddToken(token, quoted, now);
            }

            return query;
        }

        private void AddToken(string token, bool quoted, DateTime now)
        {
            bool negate = false;
            if (!quoted && token.Length > 1 && (token[0] == '-' || token[0] == '!'))
            {
                negate = true;
                token = token.Substring(1);
            }

            int colon = quoted ? -1 : token.IndexOf(':');
            if (colon > 1 && colon < token.Length - 1 && !negate &&
                TryAddFilter(token.Substring(0, colon).ToLowerInvariant(), Unquote(token.Substring(colon + 1)), token[colon + 1] == '"', now))
            {
                return;
            }

            token = token.Replace("\0", string.Empty);
            if (token.Length == 0)
            {
                return;
            }

            string folded = Fold(token);
            if (!quoted && folded.IndexOfAny(new[] { '/', '\\' }) >= 0 && !negate)
            {
                string[] segments = folded.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    return;
                }

                if (folded.EndsWith('/') || folded.EndsWith('\\'))
                {
                    // "photos/": inside such a folder, any name.
                    FolderSegments.Add(segments);
                    return;
                }

                if (segments.Length > 1)
                {
                    FolderSegments.Add(segments[..^1]);
                }

                folded = segments[^1];
            }

            bool pattern = !quoted && (folded.Contains('*') || folded.Contains('?'));
            if (pattern && folded.Trim('*').Length == 0)
            {
                return; // "*" alone: no constraint
            }

            (pattern ? (negate ? ExcludedPatterns : Patterns) : (negate ? ExcludedTerms : Terms)).Add(folded);
        }

        private bool TryAddFilter(string key, string value, bool quotedValue, DateTime now)
        {
            switch (key)
            {
                case "content":
                case "contents":
                case "contains":
                    value = value.Replace("\0", string.Empty).Trim();
                    if (value.Length == 0)
                    {
                        return false;
                    }

                    // A quoted value with several words is a phrase; anything else is one word (the tokenizer splits
                    // "e-mail" into its parts, all required).
                    (quotedValue && value.IndexOf(' ') > 0 ? ContentPhrases : ContentWords).Add(value);
                    return true;

                case "ext":
                case "extension":
                    foreach (string part in value.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string extension = Fold(part.TrimStart('*').TrimStart('.'));
                        if (extension.Length > 0)
                        {
                            Extensions.Add("." + extension);
                        }
                    }

                    return true;

                case "kind":
                case "type":
                    string word = value.ToLowerInvariant();
                    if (word is "folder" or "folders" or "dir" or "directory")
                    {
                        FoldersOnly = true;
                        return true;
                    }

                    if (word is "file" or "files")
                    {
                        FoldersOnly = false;
                        return true;
                    }

                    FileKind? kind = FileKinds.Parse(word);
                    if (kind == null)
                    {
                        return false;
                    }

                    Kinds.Add(kind.Value);
                    return true;

                case "size":
                    if (!TryParseSizeRange(value, out long minSize, out long maxSize))
                    {
                        return false;
                    }

                    MinSize = Math.Max(MinSize, minSize);
                    MaxSize = Math.Min(MaxSize, maxSize);
                    return true;

                case "modified":
                case "dm":
                case "date":
                case "datemodified":
                    if (!TryParseDateRange(value, now, out long minTicks, out long maxTicks))
                    {
                        return false;
                    }

                    MinTicks = Math.Max(MinTicks, minTicks);
                    MaxTicks = Math.Min(MaxTicks, maxTicks);
                    return true;

                case "in":
                case "path":
                case "folder":
                    if (Path.IsPathFullyQualified(value) || value.StartsWith('/') || value.StartsWith('~'))
                    {
                        string expanded = value.StartsWith('~')
                            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value.Substring(1)
                            : value;
                        InFolders.Add(PathUtil.TrimTrailingSeparator(expanded));
                    }
                    else
                    {
                        string name = Fold(value.Trim('/', '\\'));
                        if (name.Length == 0)
                        {
                            return false;
                        }

                        InFolderNames.Add(name);
                    }

                    return true;

                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ sizes

        /// <summary>Parses "10mb", "&gt;1g", "&lt;=500k", "1mb..5mb" or a Windows size word into an inclusive byte range.</summary>
        public static bool TryParseSizeRange(string value, out long min, out long max)
        {
            min = long.MinValue;
            max = long.MaxValue;
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "empty": min = 0; max = 0; return true;
                case "tiny": min = 0; max = 16 * 1024; return true;
                case "small": min = 16 * 1024; max = 1024 * 1024; return true;
                case "medium": min = 1024 * 1024; max = 128L * 1024 * 1024; return true;
                case "large": min = 128L * 1024 * 1024; max = 1024L * 1024 * 1024; return true;
                case "huge": min = 1024L * 1024 * 1024; max = 4L * 1024 * 1024 * 1024; return true;
                case "gigantic": min = 4L * 1024 * 1024 * 1024; return true;
            }

            int range = value.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                bool hasLow = TryParseSize(value.Substring(0, range), out long low);
                bool hasHigh = TryParseSize(value.Substring(range + 2), out long high);
                if (!hasLow && !hasHigh)
                {
                    return false;
                }

                min = hasLow ? low : long.MinValue;
                max = hasHigh ? high : long.MaxValue;
                return true;
            }

            (string op, string rest) = SplitComparison(value);
            if (!TryParseSize(rest, out long size))
            {
                return false;
            }

            switch (op)
            {
                case ">": min = size + 1; break;
                case ">=": min = size; break;
                case "<": max = size - 1; break;
                case "<=": max = size; break;
                default: min = size; max = size; break;
            }

            return true;
        }

        private static bool TryParseSize(string text, out long bytes)
        {
            bytes = 0;
            text = text.Trim();
            int split = 0;
            while (split < text.Length && (char.IsDigit(text[split]) || text[split] == '.' || text[split] == ','))
            {
                split++;
            }

            if (split == 0 || !double.TryParse(text.AsSpan(0, split).ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return false;
            }

            double multiplier;
            switch (text.Substring(split).Trim())
            {
                case "": case "b": case "byte": case "bytes": multiplier = 1; break;
                case "k": case "kb": case "kib": multiplier = 1024; break;
                case "m": case "mb": case "mib": multiplier = 1024d * 1024; break;
                case "g": case "gb": case "gib": multiplier = 1024d * 1024 * 1024; break;
                case "t": case "tb": case "tib": multiplier = 1024d * 1024 * 1024 * 1024; break;
                default: return false;
            }

            double value = number * multiplier;
            if (value < 0 || value > long.MaxValue / 2)
            {
                return false;
            }

            bytes = (long)Math.Round(value);
            return true;
        }

        // ------------------------------------------------------------------ dates

        /// <summary>Parses a date word, a year/month/day, a comparison or a range into an inclusive range of UTC ticks.</summary>
        public static bool TryParseDateRange(string value, DateTime nowLocal, out long minTicks, out long maxTicks)
        {
            minTicks = long.MinValue;
            maxTicks = long.MaxValue;
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            DateTime today = nowLocal.Date;
            switch (value)
            {
                case "today": return Range(today, DateTime.MaxValue, out minTicks, out maxTicks);
                case "yesterday": return Range(today.AddDays(-1), today, out minTicks, out maxTicks);
                case "week": case "thisweek": case "lastweek": return Range(today.AddDays(-6), DateTime.MaxValue, out minTicks, out maxTicks);
                case "month": case "thismonth": return Range(today.AddDays(-30), DateTime.MaxValue, out minTicks, out maxTicks);
                case "year": case "thisyear": return Range(today.AddDays(-365), DateTime.MaxValue, out minTicks, out maxTicks);
            }

            int range = value.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                bool hasLow = TryParsePeriod(value.Substring(0, range), out DateTime lowStart, out _);
                bool hasHigh = TryParsePeriod(value.Substring(range + 2), out _, out DateTime highEnd);
                if (!hasLow && !hasHigh)
                {
                    return false;
                }

                return Range(hasLow ? lowStart : DateTime.MinValue, hasHigh ? highEnd : DateTime.MaxValue, out minTicks, out maxTicks);
            }

            (string op, string rest) = SplitComparison(value);
            if (!TryParsePeriod(rest, out DateTime start, out DateTime end))
            {
                return false;
            }

            return op switch
            {
                ">" => Range(end, DateTime.MaxValue, out minTicks, out maxTicks),
                ">=" => Range(start, DateTime.MaxValue, out minTicks, out maxTicks),
                "<" => Range(DateTime.MinValue, start, out minTicks, out maxTicks),
                "<=" => Range(DateTime.MinValue, end, out minTicks, out maxTicks),
                _ => Range(start, end, out minTicks, out maxTicks)
            };
        }

        /// <summary>A year, year-month or date as a local period [start, end).</summary>
        private static bool TryParsePeriod(string text, out DateTime start, out DateTime end)
        {
            start = end = default;
            text = text.Trim();
            string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "dd.MM.yyyy", "yyyy-MM", "yyyy/MM", "yyyy" };
            foreach (string format in formats)
            {
                if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                {
                    start = parsed.Date;
                    end = format == "yyyy" ? start.AddYears(1) : format.Contains("dd") ? start.AddDays(1) : start.AddMonths(1);
                    return true;
                }
            }

            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime any))
            {
                start = any.Date;
                end = start.AddDays(1);
                return true;
            }

            return false;
        }

        /// <summary>A local [start, end) range as inclusive UTC ticks.</summary>
        private static bool Range(DateTime startLocal, DateTime endLocal, out long minTicks, out long maxTicks)
        {
            minTicks = startLocal == DateTime.MinValue ? long.MinValue : DateTime.SpecifyKind(startLocal, DateTimeKind.Local).ToUniversalTime().Ticks;
            maxTicks = endLocal == DateTime.MaxValue ? long.MaxValue : DateTime.SpecifyKind(endLocal, DateTimeKind.Local).ToUniversalTime().Ticks - 1;
            return true;
        }

        private static (string Op, string Value) SplitComparison(string value)
        {
            foreach (string op in new[] { ">=", "<=", ">", "<", "=" })
            {
                if (value.StartsWith(op, StringComparison.Ordinal))
                {
                    return (op == "=" ? string.Empty : op, value.Substring(op.Length));
                }
            }

            return (string.Empty, value);
        }

        // ------------------------------------------------------------------ tokens

        private static string Unquote(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value.Substring(1, value.Length - 2) : value;

        /// <summary>Splits on white space; double quotes group (and a quoted token is never a filter or a pattern).</summary>
        private static IEnumerable<(string Token, bool Quoted)> Tokenize(string text)
        {
            var current = new StringBuilder();
            bool inQuotes = false;
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    if (inQuotes)
                    {
                        inQuotes = false;
                        // A quote closing a filter value (in:"My Folder") stays part of the token.
                        if (current.Length > 0 && current.ToString().Contains(':') && !quoted)
                        {
                            current.Append('"');
                        }
                    }
                    else
                    {
                        inQuotes = true;
                        if (current.Length == 0)
                        {
                            quoted = true;
                        }
                        else if (current.ToString().Contains(':'))
                        {
                            current.Append('"');
                        }
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        yield return (current.ToString(), quoted);
                    }

                    current.Clear();
                    quoted = false;
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                yield return (current.ToString(), quoted);
            }
        }
    }
}
