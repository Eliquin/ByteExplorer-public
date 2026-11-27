// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ByteSearcher
{
    /// <summary>Receives the terms a <see cref="ContentTokenizer"/> finds.</summary>
    internal interface ITokenReceiver
    {
        /// <summary>
        /// A folded term. <paramref name="start"/> and <paramref name="end"/> are char offsets in the text fed so far, for
        /// snippets. Return false to stop (the tokenizer then ignores the rest).
        /// </summary>
        bool OnToken(ReadOnlySpan<char> term, long start, long end);
    }

    /// <summary>
    /// Splits text into the terms of the content index, streaming (text arrives in pieces from the extractors, and a
    /// word may straddle two pieces). The same code folds the query's words, so a term means the same on both sides.
    /// <list type="bullet">
    /// <item>A term is a run of letters and digits; anything else separates (so "e-mail_2024" is "mail" and "2024").</item>
    /// <item>Folded: lower case (invariant) and without diacritics ("Café" and "cafe" are one term), from a 64K-entry
    /// table built once, so folding costs one array read per char. Combining marks of decomposed text are skipped.</item>
    /// <item>Terms shorter than <see cref="MinLength"/> are dropped (single letters would make the largest posting lists
    /// and are useless to search for); longer than <see cref="MaxLength"/> are cut there, on both sides, so a long word
    /// typed in full still matches.</item>
    /// <item>Chinese and Japanese, written without spaces, give each ideograph or kana as a term and each pair of
    /// neighbours as another, so a query of several characters matches by its pairs.</item>
    /// <item>Characters outside the Basic Multilingual Plane (emoji, rare scripts) separate.</item>
    /// </list>
    /// </summary>
    internal sealed class ContentTokenizer
    {
        public const int MinLength = 2;
        public const int MaxLength = 48;

        private const char Separator = '\0';
        private const char Skip = '￿';

        private static readonly Lazy<char[]> FoldTable = new Lazy<char[]>(BuildFoldTable);

        private readonly char[] _token = new char[MaxLength];
        private readonly char[] _pair = new char[2];
        private int _length;
        private long _tokenStart;
        private long _position;
        private char _previousCjk;
        private bool _stopped;

        /// <summary>The receiver asked to stop.</summary>
        public bool IsStopped => _stopped;

        /// <summary>Chars fed so far.</summary>
        public long Position => _position;

        /// <summary>Feeds the next piece of text.</summary>
        public void Feed(ReadOnlySpan<char> text, ITokenReceiver receiver)
        {
            if (_stopped)
            {
                return;
            }

            char[] table = FoldTable.Value;
            for (int i = 0; i < text.Length; i++, _position++)
            {
                char folded = table[text[i]];
                if (folded == Skip)
                {
                    continue;
                }

                if (folded == Separator)
                {
                    EndToken(receiver);
                    _previousCjk = '\0';
                }
                else if (IsCjk(folded))
                {
                    EndToken(receiver);
                    _pair[0] = folded;
                    if (!Emit(receiver, _pair.AsSpan(0, 1), _position, _position + 1))
                    {
                        return;
                    }

                    if (_previousCjk != '\0')
                    {
                        _pair[0] = _previousCjk;
                        _pair[1] = folded;
                        if (!Emit(receiver, _pair, _position - 1, _position + 1))
                        {
                            return;
                        }
                    }

                    _previousCjk = folded;
                }
                else
                {
                    _previousCjk = '\0';
                    if (_length == 0)
                    {
                        _tokenStart = _position;
                    }

                    if (_length < MaxLength)
                    {
                        _token[_length] = folded;
                    }

                    _length++;
                }

                if (_stopped)
                {
                    return;
                }
            }
        }

        /// <summary>Ends the text: the last word is a term too.</summary>
        public void Finish(ITokenReceiver receiver)
        {
            if (!_stopped)
            {
                EndToken(receiver);
            }

            _previousCjk = '\0';
        }

        private void EndToken(ITokenReceiver receiver)
        {
            if (_length == 0)
            {
                return;
            }

            int length = Math.Min(_length, MaxLength);
            int full = _length;
            _length = 0;
            if (length >= MinLength)
            {
                Emit(receiver, _token.AsSpan(0, length), _tokenStart, _tokenStart + full);
            }
        }

        private bool Emit(ITokenReceiver receiver, ReadOnlySpan<char> term, long start, long end)
        {
            if (!receiver.OnToken(term, start, end))
            {
                _stopped = true;
                return false;
            }

            return true;
        }

        /// <summary>A folded char of Chinese or Japanese writing (kana, CJK ideographs).</summary>
        public static bool IsCjk(char c) =>
            (c >= '぀' && c <= 'ヿ') || (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿') || (c >= '豈' && c <= '﫿');

        /// <summary>
        /// The terms of a query word or phrase, in order. For Chinese and Japanese only the pairs are kept when there are
        /// any (they are far more selective than the single characters).
        /// </summary>
        public static List<string> TermsOf(string text)
        {
            var collector = new TermCollector();
            var tokenizer = new ContentTokenizer();
            tokenizer.Feed((text ?? string.Empty).AsSpan(), collector);
            tokenizer.Finish(collector);
            List<string> terms = collector.Terms;
            bool hasPairs = terms.Exists(t => t.Length == 2 && IsCjk(t[0]) && IsCjk(t[1]));
            if (hasPairs)
            {
                terms.RemoveAll(t => t.Length == 1);
            }

            return terms;
        }

        /// <summary>One char folded as the index folds it ('\0' for a separator).</summary>
        public static char FoldChar(char c)
        {
            char folded = FoldTable.Value[c];
            return folded == Skip ? Separator : folded;
        }

        private static char[] BuildFoldTable()
        {
            var table = new char[65536];
            for (int i = 0; i < table.Length; i++)
            {
                char c = (char)i;
                if (c < 128)
                {
                    table[i] = c >= 'A' && c <= 'Z' ? (char)(c + 32) : (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : Separator;
                    continue;
                }

                if (char.IsSurrogate(c))
                {
                    table[i] = Separator;
                    continue;
                }

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark || category == UnicodeCategory.Format)
                {
                    table[i] = Skip; // a combining accent of decomposed text, or a soft hyphen / zero-width joiner
                    continue;
                }

                if (!char.IsLetterOrDigit(c))
                {
                    table[i] = Separator;
                    continue;
                }

                char lower = char.ToLowerInvariant(c);
                table[i] = StripDiacritic(lower);
            }

            return table;
        }

        /// <summary>"é" → "e": the base letter when the char decomposes into one letter plus combining marks.</summary>
        private static char StripDiacritic(char c)
        {
            string decomposed;
            try
            {
                decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                return c;
            }

            if (decomposed.Length < 2 || !char.IsLetter(decomposed[0]))
            {
                return c;
            }

            for (int i = 1; i < decomposed.Length; i++)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(decomposed[i]) != UnicodeCategory.NonSpacingMark)
                {
                    return c;
                }
            }

            return char.ToLowerInvariant(decomposed[0]);
        }

        private sealed class TermCollector : ITokenReceiver
        {
            public List<string> Terms { get; } = new List<string>();

            public bool OnToken(ReadOnlySpan<char> term, long start, long end)
            {
                Terms.Add(term.ToString());
                return Terms.Count < 64;
            }
        }
    }
}
