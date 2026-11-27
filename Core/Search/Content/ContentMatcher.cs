// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ByteSearcher
{
    /// <summary>
    /// Query-side helpers of content search: the index terms of typed words, checking a phrase against a file's text
    /// (the index stores no positions, so a phrase is found by its words and then confirmed by reading), and the snippet
    /// shown under a result.
    /// </summary>
    public static class ContentMatcher
    {
        /// <summary>Words of three letters or more also find longer words starting with them ("invoic" finds "invoices").</summary>
        public const int PrefixMinLength = 3;

        /// <summary>The index terms of typed words: each word's terms, all required.</summary>
        internal static List<ContentTerm> TermsForWords(IEnumerable<string> words)
        {
            var terms = new List<ContentTerm>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string word in words ?? Array.Empty<string>())
            {
                foreach (string term in ContentTokenizer.TermsOf(word))
                {
                    if (seen.Add(term))
                    {
                        terms.Add(new ContentTerm(term, IsPrefixTerm(term)));
                    }
                }
            }

            return terms;
        }

        /// <summary>The index terms of a phrase: its words exactly, the last one as a prefix (it may still be typed).</summary>
        internal static List<ContentTerm> TermsForPhrase(string phrase)
        {
            List<string> words = ContentTokenizer.TermsOf(phrase);
            var terms = new List<ContentTerm>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                terms.Add(new ContentTerm(words[i], i == words.Count - 1 && IsPrefixTerm(words[i])));
            }

            return terms;
        }

        private static bool IsPrefixTerm(string term) => term.Length >= PrefixMinLength && !ContentTokenizer.IsCjk(term[0]);

        /// <summary>
        /// Whether the file's text has the phrase's words one after the other. Phrases of Chinese or Japanese (matched by
        /// character pairs) and unreadable files count as containing it: the index already found all its words there.
        /// </summary>
        internal static bool ContainsPhrase(string path, string phrase, CancellationToken cancellationToken)
        {
            List<string> words = ContentTokenizer.TermsOf(phrase);
            if (words.Count < 2 || words.Exists(w => ContentTokenizer.IsCjk(w[0])))
            {
                return true;
            }

            var matcher = new PhraseReceiver(words);
            var sink = new TokenizingSink(matcher, ContentIndexer.MaxCharsPerFile);
            try
            {
                ContentExtractor.Extract(path, sink, cancellationToken);
                sink.Finish();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;
            }

            return matcher.Found;
        }

        /// <summary>
        /// A line of the file's text around the first of the words (about <paramref name="maxChars"/> chars, white space
        /// collapsed, cut at word boundaries), or null when the file cannot be read or the words are not found in the
        /// part of the text that is scanned.
        /// </summary>
        public static string Snippet(string path, IReadOnlyList<string> words, int maxChars = 160, CancellationToken cancellationToken = default)
        {
            List<ContentTerm> terms = TermsForWords(words);
            if (terms.Count == 0 || string.IsNullOrEmpty(path))
            {
                return null;
            }

            var sink = new SnippetSink(terms, maxChars);
            try
            {
                ContentExtractor.Extract(path, sink, cancellationToken);
                sink.Finish();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            return sink.Result();
        }

        /// <summary>Collapses white space and control chars to single spaces.</summary>
        internal static string Collapse(ReadOnlySpan<char> text)
        {
            var builder = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    space = builder.Length > 0;
                    continue;
                }

                if (space)
                {
                    builder.Append(' ');
                    space = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private sealed class PhraseReceiver : ITokenReceiver
        {
            private readonly List<string> _words;
            private int _matched;

            public PhraseReceiver(List<string> words) => _words = words;

            public bool Found { get; private set; }

            public bool OnToken(ReadOnlySpan<char> term, long start, long end)
            {
                if (Matches(term, _matched))
                {
                    _matched++;
                }
                else
                {
                    _matched = Matches(term, 0) ? 1 : 0;
                }

                if (_matched == _words.Count)
                {
                    Found = true;
                    return false; // enough read
                }

                return true;
            }

            private bool Matches(ReadOnlySpan<char> term, int index)
            {
                string word = _words[index];
                return index == _words.Count - 1 && IsPrefixTerm(word) ? term.StartsWith(word, StringComparison.Ordinal) : term.SequenceEqual(word);
            }
        }

        /// <summary>Keeps the text and stops reading a little after the first matching word.</summary>
        private sealed class SnippetSink : ContentSink, ITokenReceiver
        {
            private const int MaxScan = 4 << 20;
            private readonly List<ContentTerm> _terms;
            private readonly int _maxChars;
            private readonly ContentTokenizer _tokenizer = new ContentTokenizer();
            private readonly StringBuilder _text = new StringBuilder();
            private long _matchStart = -1;
            private long _matchEnd = -1;

            public SnippetSink(List<ContentTerm> terms, int maxChars)
            {
                _terms = terms;
                _maxChars = Math.Max(40, maxChars);
            }

            public override bool IsFull => _text.Length >= MaxScan || (_matchEnd >= 0 && _text.Length >= _matchEnd + _maxChars);

            public override void Append(ReadOnlySpan<char> text)
            {
                if (IsFull)
                {
                    return;
                }

                _text.Append(text);
                if (_matchStart < 0)
                {
                    _tokenizer.Feed(text, this);
                }
            }

            public void Finish()
            {
                if (_matchStart < 0)
                {
                    _tokenizer.Finish(this);
                }
            }

            public bool OnToken(ReadOnlySpan<char> term, long start, long end)
            {
                foreach (ContentTerm candidate in _terms)
                {
                    if (candidate.Prefix ? term.StartsWith(candidate.Text, StringComparison.Ordinal) : term.SequenceEqual(candidate.Text))
                    {
                        _matchStart = start;
                        _matchEnd = end;
                        return false;
                    }
                }

                return true;
            }

            public string Result()
            {
                if (_matchStart < 0 || _matchStart >= _text.Length)
                {
                    return null;
                }

                // About a third of the line before the word, the rest after it; then cut to whole words.
                int before = _maxChars / 3;
                int start = (int)Math.Max(0, _matchStart - before * 2);
                int end = (int)Math.Min(_text.Length, _matchEnd + _maxChars);
                string window = _text.ToString(start, end - start);
                int matchAt = (int)(_matchStart - start);
                string head = Collapse(window.AsSpan(0, matchAt));
                string tail = Collapse(window.AsSpan(matchAt));
                bool cutHead = start > 0;
                if (head.Length > before)
                {
                    head = head.Substring(head.Length - before);
                    cutHead = true;
                }

                if (cutHead)
                {
                    int space = head.IndexOf(' ');
                    head = space >= 0 && space < head.Length - 1 ? head.Substring(space + 1) : head;
                }

                int room = Math.Max(10, _maxChars - head.Length);
                bool cutTail = end < _text.Length || tail.Length > room;
                if (tail.Length > room)
                {
                    int space = tail.LastIndexOf(' ', room);
                    tail = tail.Substring(0, space > room / 2 ? space : room);
                }

                bool spaceBefore = matchAt > 0 && head.Length > 0 && (char.IsWhiteSpace(window[matchAt - 1]) || char.IsControl(window[matchAt - 1]));
                string line = (cutHead ? "…" : string.Empty) + head + (spaceBefore ? " " : string.Empty) + tail + (cutTail ? "…" : string.Empty);
                return line.Trim();
            }
        }
    }
}
