// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace ByteSearcher
{
    /// <summary>
    /// Best-effort text of a PDF, without a PDF library: enough for the text layer of most documents written by office
    /// software and printers to PDF, and honest about the rest.
    /// <list type="number">
    /// <item>Objects are found by scanning for "N G obj" (so a damaged cross-reference table does not matter), including
    /// the objects packed in compressed object streams (PDF 1.5+).</item>
    /// <item>Streams are read uncompressed or FlateDecode (ZLibStream, raw deflate as a fallback); other filters (LZW,
    /// DCT, JBIG2…) are skipped, as are images, fonts, metadata and embedded files.</item>
    /// <item>The page content streams (and form XObjects) are interpreted for the text operators <c>Tj</c>, <c>TJ</c>
    /// (a large negative kerning is a space), <c>'</c> and <c>"</c>, with literal strings (escapes, octal, nesting) and
    /// hex strings; text positioning operators separate lines.</item>
    /// <item>Strings are decoded with the font's ToUnicode CMap when it has one (fonts are matched by resource name,
    /// across the document), else as UTF-16 with a byte order mark, else as Windows-1252, the usual simple-font
    /// encoding.</item>
    /// </list>
    /// Limits: composite (CID) fonts without a ToUnicode map, custom simple-font encodings (/Differences), encrypted PDFs
    /// and scanned pages (images, no text layer; there is no OCR) yield nothing or little. Text of a stream that decodes to
    /// mostly control characters is dropped rather than indexed as noise.
    /// </summary>
    internal static class PdfTextExtractor
    {
        private const int MaxDecodedStream = 32 * 1024 * 1024;
        private const long MaxDecodedTotal = 256L * 1024 * 1024;

        public static bool Extract(byte[] data, ContentSink sink, CancellationToken cancellationToken)
        {
            if (data.Length < 8 || data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            {
                return false;
            }

            var document = new PdfDocument(data, cancellationToken);
            if (document.IsEncrypted)
            {
                return false;
            }

            document.ExtractText(sink);
            return true;
        }

        /// <summary>An object: where its body (dictionary, value) lies in a buffer, and its stream if it has one.</summary>
        private sealed class PdfObject
        {
            public byte[] Buffer;
            public int Start;
            public int End;
            public int StreamStart = -1;
            public int StreamLength;

            public ReadOnlySpan<byte> Body => Buffer.AsSpan(Start, End - Start);
        }

        private sealed class PdfDocument
        {
            private static readonly Encoding Latin = RtfTextExtractor.Windows1252();
            private readonly byte[] _data;
            private readonly CancellationToken _cancellationToken;
            private readonly Dictionary<int, PdfObject> _objects = new Dictionary<int, PdfObject>();
            private readonly Dictionary<string, int> _fontsByName = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<int, CMap> _cmaps = new Dictionary<int, CMap>();
            private long _decodedTotal;

            public PdfDocument(byte[] data, CancellationToken cancellationToken)
            {
                _data = data;
                _cancellationToken = cancellationToken;
                ScanObjects(data, data.Length);
                LoadObjectStreams();
                IsEncrypted = data.AsSpan().IndexOf("/Encrypt"u8) >= 0 && FindTrailerEncrypt();
            }

            public bool IsEncrypted { get; }

            public void ExtractText(ContentSink sink)
            {
                MapFonts();
                var contents = new List<int>();
                var seen = new HashSet<int>();
                foreach (KeyValuePair<int, PdfObject> pair in Sorted())
                {
                    ReadOnlySpan<byte> body = pair.Value.Body;
                    int at = body.IndexOf("/Contents"u8);
                    if (at >= 0 && body.IndexOf("/Page"u8) >= 0)
                    {
                        foreach (int reference in References(body.Slice(at + 9), untilArrayEnd: true))
                        {
                            if (seen.Add(reference))
                            {
                                contents.Add(reference);
                            }
                        }
                    }
                }

                // Form XObjects carry text too (headers, stamps, whole pages from some producers).
                foreach (KeyValuePair<int, PdfObject> pair in Sorted())
                {
                    if (pair.Value.StreamStart >= 0 && pair.Value.Body.IndexOf("/Form"u8) >= 0 && seen.Add(pair.Key))
                    {
                        contents.Add(pair.Key);
                    }
                }

                if (contents.Count == 0)
                {
                    // No page tree found: try every stream that is not obviously something else.
                    foreach (KeyValuePair<int, PdfObject> pair in Sorted())
                    {
                        if (pair.Value.StreamStart >= 0)
                        {
                            contents.Add(pair.Key);
                        }
                    }
                }

                foreach (int id in contents)
                {
                    if (sink.IsFull)
                    {
                        break;
                    }

                    _cancellationToken.ThrowIfCancellationRequested();
                    if (!_objects.TryGetValue(id, out PdfObject obj) || obj.StreamStart < 0 || !IsTextCandidate(obj.Body))
                    {
                        continue;
                    }

                    byte[] content = Decode(obj);
                    if (content == null)
                    {
                        continue;
                    }

                    string text = InterpretContent(content);
                    if (text.Length > 0 && LooksLikeText(text))
                    {
                        sink.Append(text);
                        sink.Break();
                    }
                }
            }

            private IEnumerable<KeyValuePair<int, PdfObject>> Sorted()
            {
                var list = new List<KeyValuePair<int, PdfObject>>(_objects);
                list.Sort((a, b) => a.Key.CompareTo(b.Key));
                return list;
            }

            // ---------------------------------------------------------------- objects

            /// <summary>Every "N G obj … endobj" in a buffer (the main file, or a decompressed object stream's body).</summary>
            private void ScanObjects(byte[] buffer, int length)
            {
                ReadOnlySpan<byte> span = buffer.AsSpan(0, length);
                int position = 0;
                while (position < span.Length)
                {
                    int found = span.Slice(position).IndexOf("obj"u8);
                    if (found < 0)
                    {
                        break;
                    }

                    int at = position + found;
                    position = at + 3;
                    if (at + 3 < span.Length && IsRegular(span[at + 3]))
                    {
                        continue; // "object", "objstm"…
                    }

                    // Backwards: white space, generation, white space, number.
                    int p = at - 1;
                    if (p < 0 || !IsWhite(span[p]))
                    {
                        continue;
                    }

                    while (p >= 0 && IsWhite(span[p]))
                    {
                        p--;
                    }

                    int genEnd = p;
                    while (p >= 0 && IsDigit(span[p]))
                    {
                        p--;
                    }

                    if (p == genEnd || p < 0 || !IsWhite(span[p]))
                    {
                        continue;
                    }

                    while (p >= 0 && IsWhite(span[p]))
                    {
                        p--;
                    }

                    int numEnd = p;
                    while (p >= 0 && IsDigit(span[p]))
                    {
                        p--;
                    }

                    if (p == numEnd || numEnd - p > 9 || (p >= 0 && IsRegular(span[p])))
                    {
                        continue;
                    }

                    int number = int.Parse(span.Slice(p + 1, numEnd - p), NumberStyles.None, CultureInfo.InvariantCulture);
                    int bodyStart = at + 3;
                    int endObj = span.Slice(bodyStart).IndexOf("endobj"u8);
                    int bodyEnd = endObj < 0 ? span.Length : bodyStart + endObj;
                    var obj = new PdfObject { Buffer = buffer, Start = bodyStart, End = bodyEnd };
                    int stream = FindStreamKeyword(span.Slice(bodyStart, bodyEnd - bodyStart));
                    if (stream >= 0)
                    {
                        int dataStart = bodyStart + stream + 6;
                        if (dataStart < span.Length && span[dataStart] == (byte)'\r')
                        {
                            dataStart++;
                        }

                        if (dataStart < span.Length && span[dataStart] == (byte)'\n')
                        {
                            dataStart++;
                        }

                        obj.End = bodyStart + stream;
                        obj.StreamStart = dataStart;
                        int declared = DirectInteger(obj.Body, "/Length"u8);
                        int endStream = span.Slice(dataStart).IndexOf("endstream"u8);
                        int available = (endStream < 0 ? span.Length : dataStart + endStream) - dataStart;
                        obj.StreamLength = declared > 0 && declared <= span.Length - dataStart ? declared : Math.Max(0, available);
                        position = Math.Max(position, dataStart + obj.StreamLength);
                    }

                    _objects[number] = obj; // a later revision of an object replaces the earlier one
                }
            }

            private static int FindStreamKeyword(ReadOnlySpan<byte> body)
            {
                int at = body.IndexOf("stream"u8);
                while (at >= 0)
                {
                    bool startOk = at == 0 || !IsRegular(body[at - 1]) || body[at - 1] == (byte)'>';
                    bool endOk = at + 6 >= body.Length || body[at + 6] == (byte)'\r' || body[at + 6] == (byte)'\n';
                    if (startOk && endOk)
                    {
                        return at;
                    }

                    int next = body.Slice(at + 6).IndexOf("stream"u8);
                    at = next < 0 ? -1 : at + 6 + next;
                }

                return -1;
            }

            /// <summary>The objects packed in /Type /ObjStm streams: "number offset" pairs, then the bodies from /First.</summary>
            private void LoadObjectStreams()
            {
                foreach (PdfObject container in new List<PdfObject>(_objects.Values))
                {
                    ReadOnlySpan<byte> body = container.Body;
                    if (container.StreamStart < 0 || body.IndexOf("/ObjStm"u8) < 0)
                    {
                        continue;
                    }

                    int count = DirectInteger(body, "/N"u8);
                    int first = DirectInteger(body, "/First"u8);
                    byte[] decoded = Decode(container);
                    if (decoded == null || count <= 0 || first <= 0 || first > decoded.Length)
                    {
                        continue;
                    }

                    var numbers = new List<(int Number, int Offset)>();
                    int position = 0;
                    for (int i = 0; i < count && i < 100_000; i++)
                    {
                        if (!ReadInteger(decoded, ref position, first, out int number) || !ReadInteger(decoded, ref position, first, out int offset))
                        {
                            break;
                        }

                        numbers.Add((number, first + offset));
                    }

                    for (int i = 0; i < numbers.Count; i++)
                    {
                        int start = numbers[i].Offset;
                        int end = i + 1 < numbers.Count ? numbers[i + 1].Offset : decoded.Length;
                        if (start < 0 || start > decoded.Length || end < start || end > decoded.Length)
                        {
                            continue;
                        }

                        if (!_objects.ContainsKey(numbers[i].Number))
                        {
                            _objects[numbers[i].Number] = new PdfObject { Buffer = decoded, Start = start, End = end };
                        }
                    }
                }
            }

            private bool FindTrailerEncrypt()
            {
                // "/Encrypt N 0 R" in a trailer or cross-reference stream dictionary (not merely the word in text).
                ReadOnlySpan<byte> span = _data;
                int at = span.IndexOf("/Encrypt"u8);
                while (at >= 0)
                {
                    int p = at + 8;
                    while (p < span.Length && IsWhite(span[p]))
                    {
                        p++;
                    }

                    if (p < span.Length && (IsDigit(span[p]) || span[p] == (byte)'<'))
                    {
                        return true;
                    }

                    int next = span.Slice(at + 8).IndexOf("/Encrypt"u8);
                    at = next < 0 ? -1 : at + 8 + next;
                }

                return false;
            }

            // ---------------------------------------------------------------- streams

            private static bool IsTextCandidate(ReadOnlySpan<byte> dictionary) =>
                dictionary.IndexOf("/Image"u8) < 0 && dictionary.IndexOf("/XRef"u8) < 0 && dictionary.IndexOf("/ObjStm"u8) < 0 &&
                dictionary.IndexOf("/Metadata"u8) < 0 && dictionary.IndexOf("/EmbeddedFile"u8) < 0 && dictionary.IndexOf("/Length1"u8) < 0 &&
                dictionary.IndexOf("/Length2"u8) < 0 && dictionary.IndexOf("/FontFile"u8) < 0 && dictionary.IndexOf("/CIDSet"u8) < 0;

            /// <summary>The stream's data, uncompressed or inflated; null for other filters or broken data.</summary>
            private byte[] Decode(PdfObject obj)
            {
                ReadOnlySpan<byte> dictionary = obj.Body;
                int length = Math.Min(obj.StreamLength, obj.Buffer.Length - obj.StreamStart);
                if (length <= 0 || _decodedTotal > MaxDecodedTotal)
                {
                    return null;
                }

                int filter = dictionary.IndexOf("/Filter"u8);
                if (filter < 0)
                {
                    return obj.Buffer.AsSpan(obj.StreamStart, length).ToArray();
                }

                ReadOnlySpan<byte> rest = dictionary.Slice(filter + 7);
                int p = 0;
                while (p < rest.Length && IsWhite(rest[p]))
                {
                    p++;
                }

                bool inArray = p < rest.Length && rest[p] == (byte)'[';
                if (inArray)
                {
                    p++;
                    while (p < rest.Length && IsWhite(rest[p]))
                    {
                        p++;
                    }
                }

                if (p >= rest.Length || rest[p] != (byte)'/')
                {
                    return null;
                }

                int nameEnd = p + 1;
                while (nameEnd < rest.Length && IsRegular(rest[nameEnd]))
                {
                    nameEnd++;
                }

                ReadOnlySpan<byte> name = rest.Slice(p + 1, nameEnd - p - 1);
                if (!name.SequenceEqual("FlateDecode"u8) && !name.SequenceEqual("Fl"u8))
                {
                    return null;
                }

                if (inArray)
                {
                    // An array is read only when FlateDecode is its single filter.
                    int after = nameEnd;
                    while (after < rest.Length && IsWhite(rest[after]))
                    {
                        after++;
                    }

                    if (after >= rest.Length || rest[after] != (byte)']')
                    {
                        return null;
                    }
                }

                if (dictionary.IndexOf("/Predictor"u8) >= 0 && dictionary.IndexOf("/ObjStm"u8) < 0)
                {
                    return null; // PNG predictors are used for images and cross-reference streams, not text
                }

                byte[] inflated = Inflate(obj.Buffer, obj.StreamStart, length, zlib: true) ?? Inflate(obj.Buffer, obj.StreamStart + 2, length - 2, zlib: false);
                if (inflated != null)
                {
                    _decodedTotal += inflated.Length;
                }

                return inflated;
            }

            private static byte[] Inflate(byte[] buffer, int start, int length, bool zlib)
            {
                if (length <= 0)
                {
                    return null;
                }

                using var input = new MemoryStream(buffer, start, length, writable: false);
                using Stream inflater = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                byte[] chunk = new byte[16 * 1024];
                try
                {
                    int read;
                    while ((read = inflater.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        output.Write(chunk, 0, read);
                        if (output.Length > MaxDecodedStream)
                        {
                            break;
                        }
                    }
                }
                catch (InvalidDataException)
                {
                    // Truncated or damaged data: keep what was inflated (common in real files), or give up when nothing was.
                    if (output.Length == 0)
                    {
                        return null;
                    }
                }

                return output.ToArray();
            }

            // ---------------------------------------------------------------- fonts

            /// <summary>
            /// Resource names (/F1) to font objects, and fonts to their ToUnicode CMaps. Names are matched across the whole
            /// document (the last definition wins): resolving each page's own resources would need the page tree, and
            /// producers reuse names consistently.
            /// </summary>
            private void MapFonts()
            {
                foreach (KeyValuePair<int, PdfObject> pair in Sorted())
                {
                    ReadOnlySpan<byte> body = pair.Value.Body;
                    int at = body.IndexOf("/Font"u8);
                    while (at >= 0)
                    {
                        int p = at + 5;
                        while (p < body.Length && IsWhite(body[p]))
                        {
                            p++;
                        }

                        if (p + 1 < body.Length && body[p] == (byte)'<' && body[p + 1] == (byte)'<')
                        {
                            AddFontNames(body.Slice(p + 2));
                        }
                        else if (p < body.Length && IsDigit(body[p]))
                        {
                            foreach (int reference in References(body.Slice(p), untilArrayEnd: false))
                            {
                                if (_objects.TryGetValue(reference, out PdfObject fonts))
                                {
                                    ReadOnlySpan<byte> inner = fonts.Body;
                                    int open = inner.IndexOf("<<"u8);
                                    if (open >= 0)
                                    {
                                        AddFontNames(inner.Slice(open + 2));
                                    }
                                }

                                break;
                            }
                        }

                        int next = body.Slice(at + 5).IndexOf("/Font"u8);
                        at = next < 0 ? -1 : at + 5 + next;
                    }
                }
            }

            /// <summary>"/F1 5 0 R /F2 6 0 R &gt;&gt;": the pairs up to the end of the dictionary.</summary>
            private void AddFontNames(ReadOnlySpan<byte> entries)
            {
                int p = 0;
                while (p < entries.Length)
                {
                    while (p < entries.Length && IsWhite(entries[p]))
                    {
                        p++;
                    }

                    if (p >= entries.Length || entries[p] != (byte)'/')
                    {
                        return; // ">>" or something unexpected
                    }

                    int nameStart = ++p;
                    while (p < entries.Length && IsRegular(entries[p]))
                    {
                        p++;
                    }

                    string name = Encoding.ASCII.GetString(entries.Slice(nameStart, p - nameStart));
                    if (!ReadInteger(entries, ref p, entries.Length, out int number) || !ReadInteger(entries, ref p, entries.Length, out _))
                    {
                        return;
                    }

                    while (p < entries.Length && IsWhite(entries[p]))
                    {
                        p++;
                    }

                    if (p >= entries.Length || entries[p] != (byte)'R')
                    {
                        return;
                    }

                    p++;
                    _fontsByName[name] = number;
                }
            }

            /// <summary>The font of a resource name: its CMap (or null) and whether it is a composite font (two-byte codes).</summary>
            private (CMap Map, bool Composite) FontInfo(string name)
            {
                if (name == null || !_fontsByName.TryGetValue(name, out int fontId) || !_objects.TryGetValue(fontId, out PdfObject font))
                {
                    return (null, false);
                }

                ReadOnlySpan<byte> body = font.Body;
                bool composite = body.IndexOf("/Type0"u8) >= 0;
                int at = body.IndexOf("/ToUnicode"u8);
                if (at < 0)
                {
                    return (null, composite);
                }

                foreach (int reference in References(body.Slice(at + 10), untilArrayEnd: false))
                {
                    if (!_cmaps.TryGetValue(reference, out CMap map))
                    {
                        map = _objects.TryGetValue(reference, out PdfObject cmapObject) && cmapObject.StreamStart >= 0
                            ? CMap.Parse(Decode(cmapObject))
                            : null;
                        _cmaps[reference] = map;
                    }

                    return (map, composite);
                }

                return (null, composite);
            }

            // ---------------------------------------------------------------- content

            /// <summary>The text shown by a content stream's operators.</summary>
            private string InterpretContent(byte[] content)
            {
                var text = new StringBuilder();
                var operands = new List<object>();
                List<object> array = null;
                (CMap Map, bool Composite) font = (null, false);
                var fonts = new Dictionary<string, (CMap, bool)>(StringComparer.Ordinal);
                string lastName = null;
                int p = 0;
                int steps = 0;
                while (p < content.Length)
                {
                    if ((++steps & 0x3FFF) == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    byte c = content[p];
                    if (IsWhite(c))
                    {
                        p++;
                        continue;
                    }

                    switch (c)
                    {
                        case (byte)'%':
                            while (p < content.Length && content[p] != (byte)'\n' && content[p] != (byte)'\r')
                            {
                                p++;
                            }

                            continue;
                        case (byte)'(':
                            (array ?? operands).Add(ReadLiteral(content, ref p));
                            continue;
                        case (byte)'<':
                            if (p + 1 < content.Length && content[p + 1] == (byte)'<')
                            {
                                p += 2;
                                continue;
                            }

                            (array ?? operands).Add(ReadHex(content, ref p));
                            continue;
                        case (byte)'>':
                            p++;
                            continue;
                        case (byte)'[':
                            array = new List<object>();
                            p++;
                            continue;
                        case (byte)']':
                            if (array != null)
                            {
                                operands.Add(array);
                                array = null;
                            }

                            p++;
                            continue;
                        case (byte)'/':
                        {
                            int start = ++p;
                            while (p < content.Length && IsRegular(content[p]))
                            {
                                p++;
                            }

                            lastName = Encoding.ASCII.GetString(content, start, p - start);
                            continue;
                        }

                        case (byte)'{':
                        case (byte)'}':
                        case (byte)')':
                            p++;
                            continue;
                    }

                    int tokenStart = p;
                    while (p < content.Length && IsRegular(content[p]))
                    {
                        p++;
                    }

                    if (p == tokenStart)
                    {
                        p++;
                        continue;
                    }

                    ReadOnlySpan<byte> token = content.AsSpan(tokenStart, p - tokenStart);
                    if (IsDigit(token[0]) || token[0] == (byte)'-' || token[0] == (byte)'+' || token[0] == (byte)'.')
                    {
                        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number);
                        (array ?? operands).Add(number);
                        continue;
                    }

                    if (array != null)
                    {
                        continue; // "true"/"null" inside an array
                    }

                    switch (Encoding.ASCII.GetString(token))
                    {
                        case "Tf":
                            if (lastName != null)
                            {
                                if (!fonts.TryGetValue(lastName, out font))
                                {
                                    font = FontInfo(lastName);
                                    fonts[lastName] = font;
                                }
                            }

                            break;
                        case "Tj":
                            AppendStrings(text, operands, font);
                            break;
                        case "'":
                        case "\"":
                            text.Append('\n');
                            AppendStrings(text, operands, font);
                            break;
                        case "TJ":
                            foreach (object operand in operands)
                            {
                                if (operand is List<object> items)
                                {
                                    foreach (object item in items)
                                    {
                                        if (item is byte[] bytes)
                                        {
                                            AppendDecoded(text, bytes, font);
                                        }
                                        else if (item is double kerning && kerning < -180)
                                        {
                                            text.Append(' '); // a gap wider than most letter spacing: a word break
                                        }
                                    }
                                }
                            }

                            break;
                        case "Td":
                        case "TD":
                        case "T*":
                        case "Tm":
                        case "ET":
                            text.Append('\n');
                            break;
                        case "BI":
                        {
                            // An inline image: skip its data up to "EI".
                            int ei = content.AsSpan(p).IndexOf("EI"u8);
                            p = ei < 0 ? content.Length : p + ei + 2;
                            break;
                        }
                    }

                    operands.Clear();
                    lastName = null;
                }

                return text.ToString();
            }

            private static void AppendStrings(StringBuilder text, List<object> operands, (CMap Map, bool Composite) font)
            {
                foreach (object operand in operands)
                {
                    if (operand is byte[] bytes)
                    {
                        AppendDecoded(text, bytes, font);
                    }
                }
            }

            private static void AppendDecoded(StringBuilder text, byte[] bytes, (CMap Map, bool Composite) font)
            {
                if (font.Map != null)
                {
                    font.Map.Decode(bytes, text);
                    return;
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    text.Append(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
                    return;
                }

                if (font.Composite)
                {
                    return; // glyph ids without a map to text
                }

                text.Append(Latin.GetString(bytes));
            }

            /// <summary>Mostly letters, digits, spaces and punctuation: not glyph ids decoded as if they were text.</summary>
            private static bool LooksLikeText(string text)
            {
                int control = 0;
                int letters = 0;
                foreach (char c in text)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        letters++;
                    }
                    else if (c < ' ' && c != '\n' && c != '\r' && c != '\t')
                    {
                        control++;
                    }
                }

                return letters > 0 && control * 4 < text.Length;
            }

            private static byte[] ReadLiteral(byte[] content, ref int p)
            {
                var bytes = new List<byte>();
                int depth = 0;
                p++; // (
                while (p < content.Length)
                {
                    byte c = content[p++];
                    if (c == (byte)'\\' && p < content.Length)
                    {
                        byte e = content[p++];
                        switch (e)
                        {
                            case (byte)'n': bytes.Add((byte)'\n'); break;
                            case (byte)'r': bytes.Add((byte)'\r'); break;
                            case (byte)'t': bytes.Add((byte)'\t'); break;
                            case (byte)'b': bytes.Add(8); break;
                            case (byte)'f': bytes.Add(12); break;
                            case (byte)'\r':
                                if (p < content.Length && content[p] == (byte)'\n')
                                {
                                    p++;
                                }

                                break; // a line continuation
                            case (byte)'\n':
                                break;
                            default:
                                if (e >= (byte)'0' && e <= (byte)'7')
                                {
                                    int value = e - '0';
                                    for (int k = 0; k < 2 && p < content.Length && content[p] >= (byte)'0' && content[p] <= (byte)'7'; k++)
                                    {
                                        value = value * 8 + (content[p++] - '0');
                                    }

                                    bytes.Add((byte)value);
                                }
                                else
                                {
                                    bytes.Add(e); // \( \) \\ and unknown escapes: the char itself
                                }

                                break;
                        }

                        continue;
                    }

                    if (c == (byte)'(')
                    {
                        depth++;
                    }
                    else if (c == (byte)')')
                    {
                        if (depth == 0)
                        {
                            break;
                        }

                        depth--;
                    }

                    bytes.Add(c);
                }

                return bytes.ToArray();
            }

            private static byte[] ReadHex(byte[] content, ref int p)
            {
                var bytes = new List<byte>();
                p++; // <
                int high = -1;
                while (p < content.Length && content[p] != (byte)'>')
                {
                    int value = RtfTextExtractor.HexValue(content[p++]);
                    if (value < 0)
                    {
                        continue;
                    }

                    if (high < 0)
                    {
                        high = value;
                    }
                    else
                    {
                        bytes.Add((byte)((high << 4) | value));
                        high = -1;
                    }
                }

                if (high >= 0)
                {
                    bytes.Add((byte)(high << 4)); // an odd digit count: a trailing 0 is implied
                }

                p++; // >
                return bytes.ToArray();
            }

            // ---------------------------------------------------------------- tokens

            /// <summary>"N G R" references in order, up to the end of an array ("]") or of the first reference.</summary>
            private static List<int> References(ReadOnlySpan<byte> text, bool untilArrayEnd)
            {
                var references = new List<int>();
                int p = 0;
                while (p < text.Length && IsWhite(text[p]))
                {
                    p++;
                }

                bool inArray = p < text.Length && text[p] == (byte)'[';
                if (inArray)
                {
                    p++;
                }

                while (p < text.Length && references.Count < 10_000)
                {
                    int save = p;
                    if (!ReadInteger(text, ref p, text.Length, out int number) || !ReadInteger(text, ref p, text.Length, out _))
                    {
                        break;
                    }

                    while (p < text.Length && IsWhite(text[p]))
                    {
                        p++;
                    }

                    if (p >= text.Length || text[p] != (byte)'R')
                    {
                        p = save;
                        break;
                    }

                    p++;
                    references.Add(number);
                    if (!inArray || !untilArrayEnd)
                    {
                        break;
                    }
                }

                return references;
            }

            /// <summary>The integer after a key ("/Length 1234"), or -1 when it is missing or a reference.</summary>
            private static int DirectInteger(ReadOnlySpan<byte> dictionary, ReadOnlySpan<byte> key)
            {
                int at = dictionary.IndexOf(key);
                while (at >= 0)
                {
                    int p = at + key.Length;
                    if (p < dictionary.Length && !IsRegular(dictionary[p]) || p >= dictionary.Length)
                    {
                        if (ReadInteger(dictionary, ref p, dictionary.Length, out int value))
                        {
                            int q = p;
                            if (ReadInteger(dictionary, ref q, dictionary.Length, out _))
                            {
                                while (q < dictionary.Length && IsWhite(dictionary[q]))
                                {
                                    q++;
                                }

                                if (q < dictionary.Length && dictionary[q] == (byte)'R')
                                {
                                    return -1; // an indirect length
                                }
                            }

                            return value;
                        }

                        return -1;
                    }

                    int next = dictionary.Slice(at + key.Length).IndexOf(key);
                    at = next < 0 ? -1 : at + key.Length + next;
                }

                return -1;
            }

            private static bool ReadInteger(ReadOnlySpan<byte> text, ref int p, int limit, out int value)
            {
                value = 0;
                limit = Math.Min(limit, text.Length);
                while (p < limit && IsWhite(text[p]))
                {
                    p++;
                }

                int start = p;
                while (p < limit && IsDigit(text[p]) && p - start < 10)
                {
                    value = value * 10 + (text[p] - '0');
                    p++;
                }

                return p > start && value >= 0;
            }

            private static bool ReadInteger(byte[] text, ref int p, int limit, out int value) => ReadInteger(text.AsSpan(), ref p, limit, out value);
        }

        /// <summary>A ToUnicode CMap: codes of one or two bytes to text.</summary>
        private sealed class CMap
        {
            private readonly Dictionary<int, string> _map = new Dictionary<int, string>();
            private int _codeLength = 1;

            public static CMap Parse(byte[] data)
            {
                if (data == null)
                {
                    return null;
                }

                var map = new CMap();
                string text = Encoding.Latin1.GetString(data);
                int space = text.IndexOf("begincodespacerange", StringComparison.Ordinal);
                if (space >= 0)
                {
                    int open = text.IndexOf('<', space);
                    int close = open < 0 ? -1 : text.IndexOf('>', open);
                    if (close > open)
                    {
                        map._codeLength = Math.Clamp((close - open - 1) / 2, 1, 2);
                    }
                }

                int position = 0;
                while ((position = text.IndexOf("beginbfchar", position, StringComparison.Ordinal)) >= 0)
                {
                    int end = text.IndexOf("endbfchar", position, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    List<string> tokens = HexTokens(text, position + 11, end);
                    for (int i = 0; i + 1 < tokens.Count; i += 2)
                    {
                        if (TryCode(tokens[i], out int code))
                        {
                            map._map[code] = Utf16(tokens[i + 1]);
                        }
                    }

                    position = end;
                }

                position = 0;
                while ((position = text.IndexOf("beginbfrange", position, StringComparison.Ordinal)) >= 0)
                {
                    int end = text.IndexOf("endbfrange", position, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    ParseRanges(map, text, position + 12, end);
                    position = end;
                }

                return map._map.Count > 0 ? map : null;
            }

            private static void ParseRanges(CMap map, string text, int start, int end)
            {
                int p = start;
                while (p < end)
                {
                    if (!NextHex(text, ref p, end, out string low) || !NextHex(text, ref p, end, out string high))
                    {
                        return;
                    }

                    while (p < end && char.IsWhiteSpace(text[p]))
                    {
                        p++;
                    }

                    if (!TryCode(low, out int from) || !TryCode(high, out int to) || to < from || to - from > 65535)
                    {
                        return;
                    }

                    if (p < end && text[p] == '[')
                    {
                        int close = text.IndexOf(']', p);
                        if (close < 0 || close > end)
                        {
                            return;
                        }

                        List<string> targets = HexTokens(text, p + 1, close);
                        for (int i = 0; i < targets.Count && from + i <= to; i++)
                        {
                            map._map[from + i] = Utf16(targets[i]);
                        }

                        p = close + 1;
                    }
                    else
                    {
                        if (!NextHex(text, ref p, end, out string target))
                        {
                            return;
                        }

                        string first = Utf16(target);
                        if (first.Length == 0)
                        {
                            continue;
                        }

                        // The last UTF-16 unit counts up through the range.
                        for (int code = from; code <= to; code++)
                        {
                            char last = (char)(first[^1] + (code - from));
                            map._map[code] = first.Substring(0, first.Length - 1) + last;
                        }
                    }
                }
            }

            public void Decode(byte[] bytes, StringBuilder text)
            {
                for (int i = 0; i + _codeLength <= bytes.Length; i += _codeLength)
                {
                    int code = _codeLength == 2 ? (bytes[i] << 8) | bytes[i + 1] : bytes[i];
                    if (_map.TryGetValue(code, out string value))
                    {
                        text.Append(value);
                    }
                    else if (_codeLength == 1 && code >= 32)
                    {
                        text.Append((char)code);
                    }
                }
            }

            private static List<string> HexTokens(string text, int start, int end)
            {
                var tokens = new List<string>();
                int p = start;
                while (NextHex(text, ref p, end, out string token))
                {
                    tokens.Add(token);
                }

                return tokens;
            }

            private static bool NextHex(string text, ref int p, int end, out string token)
            {
                token = null;
                int open = text.IndexOf('<', p, Math.Max(0, end - p));
                if (open < 0)
                {
                    return false;
                }

                int bracket = text.IndexOf('[', p, Math.Max(0, open - p));
                if (bracket >= 0)
                {
                    p = bracket;
                    return false; // the caller handles an array first
                }

                int close = text.IndexOf('>', open, end - open);
                if (close < 0)
                {
                    return false;
                }

                token = text.Substring(open + 1, close - open - 1);
                p = close + 1;
                return true;
            }

            private static bool TryCode(string hex, out int code) =>
                int.TryParse(hex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) && code >= 0;

            private static string Utf16(string hex)
            {
                var builder = new StringBuilder();
                string digits = hex.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
                for (int i = 0; i + 4 <= digits.Length; i += 4)
                {
                    if (int.TryParse(digits.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int unit))
                    {
                        builder.Append((char)unit);
                    }
                }

                if (builder.Length == 0 && digits.Length == 2 && int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int single))
                {
                    builder.Append((char)single);
                }

                return builder.ToString();
            }
        }

        private static bool IsWhite(byte b) => b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t' || b == 0 || b == 12;

        private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

        /// <summary>Not white space and not a PDF delimiter.</summary>
        private static bool IsRegular(byte b) =>
            !IsWhite(b) && b != (byte)'(' && b != (byte)')' && b != (byte)'<' && b != (byte)'>' && b != (byte)'[' && b != (byte)']' &&
            b != (byte)'{' && b != (byte)'}' && b != (byte)'/' && b != (byte)'%';
    }
}
