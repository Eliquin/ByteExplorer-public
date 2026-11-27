// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Text;

namespace ByteSearcher
{
    /// <summary>
    /// Where an extractor writes the text it finds, piece by piece, so a large document never has to be held as one
    /// string: the indexer tokenizes each piece as it arrives, a snippet keeps only a window. An extractor stops early
    /// when the sink is full.
    /// </summary>
    internal abstract class ContentSink
    {
        /// <summary>Nothing more is wanted: extractors stop reading.</summary>
        public abstract bool IsFull { get; }

        public abstract void Append(ReadOnlySpan<char> text);

        /// <summary>A paragraph, cell or line ends (separates words that would otherwise run together).</summary>
        public void Break() => Append("\n");
    }

    /// <summary>Feeds a <see cref="ContentTokenizer"/>, up to a number of chars (a cap on the work per file).</summary>
    internal sealed class TokenizingSink : ContentSink
    {
        private readonly ContentTokenizer _tokenizer = new ContentTokenizer();
        private readonly ITokenReceiver _receiver;
        private readonly long _maxChars;

        public TokenizingSink(ITokenReceiver receiver, long maxChars)
        {
            _receiver = receiver;
            _maxChars = maxChars;
        }

        public override bool IsFull => _tokenizer.IsStopped || _tokenizer.Position >= _maxChars;

        /// <summary>Chars read in all.</summary>
        public long Chars => _tokenizer.Position;

        public override void Append(ReadOnlySpan<char> text)
        {
            if (IsFull || text.IsEmpty)
            {
                return;
            }

            long room = _maxChars - _tokenizer.Position;
            _tokenizer.Feed(room < text.Length ? text.Slice(0, (int)room) : text, _receiver);
        }

        /// <summary>Ends the text (its last word becomes a term).</summary>
        public void Finish() => _tokenizer.Finish(_receiver);
    }

    /// <summary>Collects the text itself, up to a number of chars (tests, snippets).</summary>
    internal sealed class TextSink : ContentSink
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly int _maxChars;

        public TextSink(int maxChars = 1 << 20)
        {
            _maxChars = maxChars;
        }

        public override bool IsFull => _text.Length >= _maxChars;

        public override void Append(ReadOnlySpan<char> text)
        {
            int room = _maxChars - _text.Length;
            if (room > 0)
            {
                _text.Append(text.Length > room ? text.Slice(0, room) : text);
            }
        }

        public override string ToString() => _text.ToString();
    }
}
