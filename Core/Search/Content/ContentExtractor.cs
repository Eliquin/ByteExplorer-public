// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Xml;

namespace ByteSearcher
{
    /// <summary>The kinds of file the content index reads.</summary>
    internal enum ContentFormat
    {
        None,
        Text,
        Html,
        WordProcessing,
        Spreadsheet,
        Presentation,
        OpenDocument,
        Rtf,
        Pdf
    }

    /// <summary>
    /// Gets the text out of the files the content index covers, with the class library only (no package, no network):
    /// <list type="bullet">
    /// <item><b>Text and source code</b>: the byte order mark decides the encoding; without one, UTF-16 is recognised by
    /// its zero bytes, valid UTF-8 is read as UTF-8 and anything else as Latin-1. Zero bytes in an 8 KB sample that are
    /// not UTF-16's mean a binary file, which is skipped. HTML is read without its tags.</item>
    /// <item><b>Office Open XML</b> (.docx, .xlsx, .pptx and their macro/template variants): the ZIP's XML parts, streamed
    /// with XmlReader (DTDs refused): Word's text runs with headers, footers, notes and comments; Excel's shared strings
    /// plus inline strings and numbers of every sheet; PowerPoint's slides in order, then the notes.</item>
    /// <item><b>OpenDocument</b> (.odt, .ods, .odp): the text of content.xml.</item>
    /// <item><b>RTF</b> and <b>PDF</b>: see <see cref="RtfTextExtractor"/> and <see cref="PdfTextExtractor"/>.</item>
    /// </list>
    /// Archives are read with caps on what is decompressed, so a ZIP bomb costs no more than a large file.
    /// </summary>
    internal static class ContentExtractor
    {
        /// <summary>The most bytes decompressed from one Office or OpenDocument file.</summary>
        private const long MaxUnzippedBytes = 128L * 1024 * 1024;

        private static readonly Dictionary<string, ContentFormat> Formats = BuildFormats();
        private static readonly Dictionary<string, ContentFormat>.AlternateLookup<ReadOnlySpan<char>> FormatsBySpan = Formats.GetAlternateLookup<ReadOnlySpan<char>>();

        /// <summary>The extensions read (without the dot, lower case), for the settings and the README.</summary>
        public static IEnumerable<string> Extensions => Formats.Keys;

        /// <summary>The format of a file name by its extension (case-insensitive), or None.</summary>
        public static ContentFormat FormatOf(ReadOnlySpan<char> name)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1 || name.Length - dot - 1 > 12)
            {
                return ContentFormat.None;
            }

            ReadOnlySpan<char> extension = name.Slice(dot + 1);
            Span<char> lower = stackalloc char[extension.Length];
            for (int i = 0; i < extension.Length; i++)
            {
                char c = extension[i];
                lower[i] = c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
            }

            return FormatsBySpan.TryGetValue(lower, out ContentFormat format) ? format : ContentFormat.None;
        }

        /// <summary>The format of a name as the name index stores it (folded UTF-8), without allocating.</summary>
        public static ContentFormat FormatOfUtf8(ReadOnlySpan<byte> foldedName)
        {
            int dot = foldedName.LastIndexOf((byte)'.');
            if (dot < 0 || dot == foldedName.Length - 1 || foldedName.Length - dot - 1 > 12)
            {
                return ContentFormat.None;
            }

            ReadOnlySpan<byte> extension = foldedName.Slice(dot + 1);
            Span<char> chars = stackalloc char[extension.Length];
            for (int i = 0; i < extension.Length; i++)
            {
                byte b = extension[i];
                if (b >= 0x80)
                {
                    return ContentFormat.None;
                }

                chars[i] = b >= (byte)'A' && b <= (byte)'Z' ? (char)(b + 32) : (char)b;
            }

            return FormatsBySpan.TryGetValue(chars, out ContentFormat format) ? format : ContentFormat.None;
        }

        /// <summary>
        /// Writes the text of <paramref name="path"/> to <paramref name="sink"/>. Returns false when the file holds no text
        /// this code can read (binary, unknown format). Throws IOException and UnauthorizedAccessException for files that
        /// cannot be opened; damaged documents give what could be read before the damage.
        /// </summary>
        public static bool Extract(string path, ContentSink sink, CancellationToken cancellationToken = default)
        {
            ContentFormat format = FormatOf(Path.GetFileName(path.AsSpan()));
            if (format == ContentFormat.None)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            return Extract(stream, format, sink, cancellationToken);
        }

        /// <summary>The same for an open stream (seekable for the ZIP-based formats).</summary>
        public static bool Extract(Stream stream, ContentFormat format, ContentSink sink, CancellationToken cancellationToken = default)
        {
            try
            {
                switch (format)
                {
                    case ContentFormat.Text:
                        return ExtractPlainText(stream, sink, stripTags: false, cancellationToken);
                    case ContentFormat.Html:
                        return ExtractPlainText(stream, sink, stripTags: true, cancellationToken);
                    case ContentFormat.Rtf:
                        return RtfTextExtractor.Extract(ReadAll(stream), sink, cancellationToken);
                    case ContentFormat.Pdf:
                        return PdfTextExtractor.Extract(ReadAll(stream), sink, cancellationToken);
                    case ContentFormat.WordProcessing:
                    case ContentFormat.Spreadsheet:
                    case ContentFormat.Presentation:
                    case ContentFormat.OpenDocument:
                        return ExtractZipped(stream, format, sink, cancellationToken);
                    default:
                        return false;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is XmlException || ex is DecoderFallbackException || ex is NotSupportedException ||
                                       ex is EndOfStreamException || ex is FormatException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                // A damaged or unusual document: what was read before stays in the sink.
                return false;
            }
        }

        // ------------------------------------------------------------------ plain text

        private static bool ExtractPlainText(Stream stream, ContentSink sink, bool stripTags, CancellationToken cancellationToken)
        {
            byte[] head = new byte[8192];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            Encoding encoding = DetectEncoding(head.AsSpan(0, read), out int bomLength);
            if (encoding == null)
            {
                return false; // binary
            }

            stream.Position = bomLength;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 32 * 1024, leaveOpen: true);
            char[] buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
            bool inTag = false;
            try
            {
                int count;
                while (!sink.IsFull && (count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stripTags)
                    {
                        inTag = AppendWithoutTags(buffer.AsSpan(0, count), sink, inTag);
                    }
                    else
                    {
                        sink.Append(buffer.AsSpan(0, count));
                    }
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }

            return true;
        }

        /// <summary>The text outside &lt;tags&gt; (a tag becomes a separator); whether the piece ended inside a tag.</summary>
        private static bool AppendWithoutTags(ReadOnlySpan<char> text, ContentSink sink, bool inTag)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!inTag && c == '<')
                {
                    if (i > start)
                    {
                        sink.Append(text.Slice(start, i - start));
                    }

                    sink.Append(" ");
                    inTag = true;
                }
                else if (inTag && c == '>')
                {
                    inTag = false;
                    start = i + 1;
                }
            }

            if (!inTag && start < text.Length)
            {
                sink.Append(text.Slice(start));
            }

            return inTag;
        }

        /// <summary>
        /// The encoding of a text file from its first bytes: a byte order mark; else UTF-16 when zero bytes sit in every
        /// other position; else UTF-8 when the sample is valid UTF-8, Latin-1 when not; null for binary (other zero bytes).
        /// </summary>
        internal static Encoding DetectEncoding(ReadOnlySpan<byte> head, out int bomLength)
        {
            bomLength = 0;
            if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                bomLength = 3;
                return new UTF8Encoding(false);
            }

            if (head.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
            {
                bomLength = 4;
                return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
            }

            if (head.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            {
                bomLength = 4;
                return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            }

            if (head.StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                bomLength = 2;
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            }

            if (head.StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                bomLength = 2;
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            }

            int evenZeros = 0;
            int oddZeros = 0;
            for (int i = 0; i < head.Length; i++)
            {
                if (head[i] == 0)
                {
                    if ((i & 1) == 0)
                    {
                        evenZeros++;
                    }
                    else
                    {
                        oddZeros++;
                    }
                }
            }

            if (evenZeros + oddZeros > 0)
            {
                int pairs = Math.Max(1, head.Length / 2);
                if (oddZeros > pairs / 3 && evenZeros < pairs / 20)
                {
                    return new UnicodeEncoding(bigEndian: false, byteOrderMark: false); // "a\0b\0": UTF-16 LE without a mark
                }

                if (evenZeros > pairs / 3 && oddZeros < pairs / 20)
                {
                    return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
                }

                return null;
            }

            // A sequence cut by the end of the sample is not an error.
            int end = head.Length;
            for (int back = 1; back <= 3 && back <= head.Length; back++)
            {
                byte b = head[head.Length - back];
                if ((b & 0xC0) == 0xC0)
                {
                    end = head.Length - back;
                    break;
                }

                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            return Utf8.IsValid(head.Slice(0, end)) ? new UTF8Encoding(false) : Encoding.Latin1;
        }

        // ------------------------------------------------------------------ ZIP + XML formats

        private static bool ExtractZipped(Stream stream, ContentFormat format, ContentSink sink, CancellationToken cancellationToken)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long budget = MaxUnzippedBytes;
            bool any = false;
            switch (format)
            {
                case ContentFormat.WordProcessing:
                {
                    var parts = new List<ZipArchiveEntry>();
                    AddEntry(parts, archive, "word/document.xml");
                    parts.AddRange(Numbered(archive, "word/header"));
                    parts.AddRange(Numbered(archive, "word/footer"));
                    AddEntry(parts, archive, "word/footnotes.xml");
                    AddEntry(parts, archive, "word/endnotes.xml");
                    AddEntry(parts, archive, "word/comments.xml");
                    var text = new HashSet<string>(StringComparer.Ordinal) { "t", "delText" };
                    var breaks = new HashSet<string>(StringComparer.Ordinal) { "p", "br", "tab", "cr", "tc" };
                    foreach (ZipArchiveEntry part in parts)
                    {
                        any |= ReadXmlPart(part, sink, text, breaks, ref budget, cancellationToken);
                    }

                    break;
                }

                case ContentFormat.Spreadsheet:
                {
                    var parts = new List<ZipArchiveEntry>();
                    AddEntry(parts, archive, "xl/sharedStrings.xml");
                    foreach (ZipArchiveEntry part in parts)
                    {
                        any |= ReadXmlPart(part, sink, new HashSet<string>(StringComparer.Ordinal) { "t" }, new HashSet<string>(StringComparer.Ordinal) { "si" }, ref budget, cancellationToken);
                    }

                    foreach (ZipArchiveEntry sheet in Numbered(archive, "xl/worksheets/sheet"))
                    {
                        any |= ReadSheet(sheet, sink, ref budget, cancellationToken);
                    }

                    break;
                }

                case ContentFormat.Presentation:
                {
                    var text = new HashSet<string>(StringComparer.Ordinal) { "t" };
                    var breaks = new HashSet<string>(StringComparer.Ordinal) { "p", "br" };
                    foreach (ZipArchiveEntry slide in Numbered(archive, "ppt/slides/slide").Concat(Numbered(archive, "ppt/notesSlides/notesSlide")))
                    {
                        any |= ReadXmlPart(slide, sink, text, breaks, ref budget, cancellationToken);
                    }

                    break;
                }

                case ContentFormat.OpenDocument:
                {
                    ZipArchiveEntry content = archive.GetEntry("content.xml");
                    if (content != null)
                    {
                        var breaks = new HashSet<string>(StringComparer.Ordinal) { "p", "h", "line-break", "tab", "s", "table-cell", "list-item" };
                        any = ReadXmlPart(content, sink, null, breaks, ref budget, cancellationToken);
                    }

                    break;
                }
            }

            return any;
        }

        private static void AddEntry(List<ZipArchiveEntry> parts, ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name);
            if (entry != null)
            {
                parts.Add(entry);
            }
        }

        /// <summary>The parts "prefix1.xml", "prefix2.xml"… in number order (slide10 after slide9).</summary>
        private static IEnumerable<ZipArchiveEntry> Numbered(ZipArchive archive, string prefix)
        {
            var found = new List<(int Number, ZipArchiveEntry Entry)>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - 4), out int number) && found.Count < 5000)
                {
                    found.Add((number, entry));
                }
            }

            return found.OrderBy(f => f.Number).Select(f => f.Entry);
        }

        private static XmlReader OpenXml(ZipArchiveEntry entry, ref long budget, out Stream limited)
        {
            long size = Math.Max(0, entry.Length);
            if (size > budget)
            {
                limited = null;
                return null;
            }

            budget -= size;
            limited = new LimitedReadStream(entry.Open(), size + 1024);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true
            };
            return XmlReader.Create(limited, settings);
        }

        /// <summary>
        /// The text of one XML part: inside elements named in <paramref name="textElements"/> (every text node when null),
        /// with a separator at each element named in <paramref name="breakElements"/>.
        /// </summary>
        private static bool ReadXmlPart(ZipArchiveEntry entry, ContentSink sink, HashSet<string> textElements, HashSet<string> breakElements, ref long budget, CancellationToken cancellationToken)
        {
            using XmlReader reader = OpenXml(entry, ref budget, out _);
            if (reader == null)
            {
                return false;
            }

            int inText = 0;
            int nodes = 0;
            while (!sink.IsFull && reader.Read())
            {
                if ((++nodes & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (breakElements.Contains(reader.LocalName))
                        {
                            sink.Break();
                        }

                        if (textElements != null && textElements.Contains(reader.LocalName) && !reader.IsEmptyElement)
                        {
                            inText++;
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (textElements != null && textElements.Contains(reader.LocalName) && inText > 0)
                        {
                            inText--;
                        }

                        if (breakElements.Contains(reader.LocalName))
                        {
                            sink.Break();
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        if (textElements == null || inText > 0)
                        {
                            sink.Append(reader.Value);
                        }

                        break;
                }
            }

            return true;
        }

        /// <summary>
        /// A worksheet: inline strings and the values of cells that are not shared strings (numbers, formula results);
        /// shared strings come from sharedStrings.xml once.
        /// </summary>
        private static bool ReadSheet(ZipArchiveEntry entry, ContentSink sink, ref long budget, CancellationToken cancellationToken)
        {
            using XmlReader reader = OpenXml(entry, ref budget, out _);
            if (reader == null)
            {
                return false;
            }

            string cellType = null;
            bool inValue = false;
            bool inInline = false;
            int nodes = 0;
            while (!sink.IsFull && reader.Read())
            {
                if ((++nodes & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        switch (reader.LocalName)
                        {
                            case "c":
                                cellType = reader.GetAttribute("t");
                                sink.Break();
                                break;
                            case "v":
                                inValue = !reader.IsEmptyElement && cellType != "s";
                                break;
                            case "is":
                                inInline = !reader.IsEmptyElement;
                                break;
                            case "row":
                                sink.Break();
                                break;
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (reader.LocalName == "v")
                        {
                            inValue = false;
                        }
                        else if (reader.LocalName == "is")
                        {
                            inInline = false;
                        }

                        break;

                    case XmlNodeType.Text:
                        if (inValue || inInline)
                        {
                            sink.Append(reader.Value);
                        }

                        break;
                }
            }

            return true;
        }

        internal static byte[] ReadAll(Stream stream)
        {
            if (stream.CanSeek)
            {
                long length = stream.Length - stream.Position;
                if (length > int.MaxValue / 2)
                {
                    throw new IOException("File too large to read.");
                }

                byte[] bytes = new byte[length];
                stream.ReadExactly(bytes);
                return bytes;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static Dictionary<string, ContentFormat> BuildFormats()
        {
            var formats = new Dictionary<string, ContentFormat>(StringComparer.Ordinal);
            foreach (string extension in new[]
            {
                "txt", "text", "md", "markdown", "rst", "adoc", "org", "csv", "tsv", "log", "json", "jsonl", "ndjson", "xml", "xaml", "axaml",
                "svg", "css", "scss", "sass", "less", "js", "mjs", "cjs", "jsx", "ts", "tsx", "vue", "svelte", "cs", "csx", "vb", "fs", "fsx",
                "py", "pyw", "rb", "php", "java", "kt", "kts", "scala", "groovy", "gradle", "go", "rs", "c", "h", "cpp", "cc", "cxx", "hpp",
                "hh", "hxx", "m", "mm", "swift", "dart", "lua", "pl", "pm", "r", "jl", "sh", "bash", "zsh", "fish", "ps1", "psm1", "psd1",
                "bat", "cmd", "ini", "cfg", "conf", "config", "toml", "yaml", "yml", "properties", "env", "sql", "tex", "bib", "srt", "vtt",
                "sln", "csproj", "vbproj", "fsproj", "props", "targets", "resx", "nfo", "eml", "vcf", "ics", "diff", "patch", "asm",
                "cmake", "makefile", "mk", "dockerfile", "gitignore", "editorconfig", "plist", "graphql", "proto"
            })
            {
                formats[extension] = ContentFormat.Text;
            }

            foreach (string extension in new[] { "html", "htm", "xhtml", "shtml", "mht" })
            {
                formats[extension] = ContentFormat.Html;
            }

            foreach (string extension in new[] { "docx", "docm", "dotx", "dotm" })
            {
                formats[extension] = ContentFormat.WordProcessing;
            }

            foreach (string extension in new[] { "xlsx", "xlsm", "xltx", "xltm" })
            {
                formats[extension] = ContentFormat.Spreadsheet;
            }

            foreach (string extension in new[] { "pptx", "pptm", "potx", "potm", "ppsx" })
            {
                formats[extension] = ContentFormat.Presentation;
            }

            foreach (string extension in new[] { "odt", "ods", "odp", "ott", "ots", "otp" })
            {
                formats[extension] = ContentFormat.OpenDocument;
            }

            formats["rtf"] = ContentFormat.Rtf;
            formats["pdf"] = ContentFormat.Pdf;
            return formats;
        }

        /// <summary>A read-only view of a stream that ends (as if at its end) after a number of bytes: the ZIP bomb cap.</summary>
        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;

            public LimitedReadStream(Stream inner, long limit)
            {
                _inner = inner;
                _remaining = limit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_remaining <= 0)
                {
                    return 0;
                }

                int read = _inner.Read(buffer.Slice(0, (int)Math.Min(buffer.Length, _remaining)));
                _remaining -= read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
