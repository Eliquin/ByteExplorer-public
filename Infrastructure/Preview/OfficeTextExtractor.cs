// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>The text of a document, as the preview shows it.</summary>
    public sealed class DocumentText
    {
        public DocumentText(string text, bool truncated)
        {
            Text = text;
            Truncated = truncated;
        }

        public string Text { get; }

        /// <summary>The document has more text than the preview holds.</summary>
        public bool Truncated { get; }
    }

    /// <summary>
    /// Reads the plain text of a Word (.docx, .docm, .dotx, .dotm) or OpenDocument text (.odt, .ott) file for a text-only
    /// preview where no real document preview exists (Linux, macOS; Windows without a preview handler for the type). Both
    /// are ZIP packages with the body in one XML part (<c>word/document.xml</c>, <c>content.xml</c>); paragraphs become
    /// lines, tabs and line breaks are kept, and everything else (formatting, pictures, tables' layout, deleted revisions,
    /// field codes) is left out. Only System.IO.Compression and XmlReader: no Office library.
    /// <para>
    /// Hostile files are expected: DTDs are refused (no entity expansion), reading stops after <c>maxChars</c> characters,
    /// and at most <see cref="MaxXmlBytes"/> uncompressed bytes of the part are read, so a ZIP bomb costs a bounded read.
    /// </para>
    /// </summary>
    public static class OfficeTextExtractor
    {
        /// <summary>How much of the XML part is read at most (uncompressed).</summary>
        public const long MaxXmlBytes = 64L * 1024 * 1024;

        private static readonly HashSet<string> WordNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
            "http://purl.oclc.org/ooxml/wordprocessingml/main", // Strict Open XML
        };

        private const string OdfTextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

        /// <summary>
        /// The document's text, at most <paramref name="maxChars"/> characters; null when the file is not a readable package
        /// of that kind (corrupt, encrypted — an encrypted .docx is an OLE file, not a ZIP — or another format).
        /// </summary>
        public static DocumentText Extract(string path, int maxChars, CancellationToken token)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            bool word = extension is ".docx" or ".docm" or ".dotx" or ".dotm";
            bool odf = extension is ".odt" or ".ott";
            if (!word && !odf)
            {
                return null;
            }

            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.GetEntry(word ? "word/document.xml" : "content.xml");
                if (entry == null)
                {
                    return null;
                }

                using Stream part = new LimitedStream(entry.Open(), MaxXmlBytes);
                return word ? ReadWord(part, maxChars, token) : ReadOdf(part, maxChars, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        private static XmlReaderSettings ReaderSettings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        /// <summary>WordprocessingML: text is in w:t; w:p ends a paragraph; w:tab, w:br and w:cr are a tab and line breaks.</summary>
        private static DocumentText ReadWord(Stream part, int maxChars, CancellationToken token)
        {
            var text = new TextCollector(maxChars);
            using XmlReader reader = XmlReader.Create(part, ReaderSettings());
            bool inText = false;
            while (!text.IsFull && Read(reader, text))
            {
                token.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when WordNamespaces.Contains(reader.NamespaceURI):
                        switch (reader.LocalName)
                        {
                            case "t":
                                inText = !reader.IsEmptyElement;
                                break;
                            case "p" when reader.IsEmptyElement: // <w:p/>: an empty paragraph, no end tag follows
                                text.Append('\n');
                                break;
                            case "tab":
                                text.Append('\t');
                                break;
                            case "br":
                            case "cr":
                                text.Append('\n');
                                break;
                        }

                        break;
                    case XmlNodeType.EndElement when WordNamespaces.Contains(reader.NamespaceURI):
                        if (reader.LocalName == "t")
                        {
                            inText = false;
                        }
                        else if (reader.LocalName == "p")
                        {
                            text.Append('\n');
                        }

                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.CDATA:
                        if (inText)
                        {
                            text.Append(reader.Value);
                        }

                        break;
                }
            }

            return text.ToResult();
        }

        /// <summary>
        /// OpenDocument text: text:p and text:h are paragraphs (text anywhere inside them, spans included); text:s is
        /// spaces (text:c of them), text:tab a tab, text:line-break a line break.
        /// </summary>
        private static DocumentText ReadOdf(Stream part, int maxChars, CancellationToken token)
        {
            var text = new TextCollector(maxChars);
            using XmlReader reader = XmlReader.Create(part, ReaderSettings());
            int paragraphDepth = 0;
            while (!text.IsFull && Read(reader, text))
            {
                token.ThrowIfCancellationRequested();
                bool odfText = reader.NamespaceURI == OdfTextNamespace;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when odfText:
                        switch (reader.LocalName)
                        {
                            case "p":
                            case "h":
                                if (!reader.IsEmptyElement)
                                {
                                    paragraphDepth++;
                                }
                                else
                                {
                                    text.Append('\n');
                                }

                                break;
                            case "s" when paragraphDepth > 0:
                                int count = int.TryParse(reader.GetAttribute("c", OdfTextNamespace), out int c) ? Math.Clamp(c, 1, 1000) : 1;
                                text.Append(new string(' ', count));
                                break;
                            case "tab" when paragraphDepth > 0:
                                text.Append('\t');
                                break;
                            case "line-break" when paragraphDepth > 0:
                                text.Append('\n');
                                break;
                        }

                        break;
                    case XmlNodeType.EndElement when odfText && (reader.LocalName == "p" || reader.LocalName == "h"):
                        paragraphDepth = Math.Max(0, paragraphDepth - 1);
                        text.Append('\n');
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.CDATA:
                        if (paragraphDepth > 0)
                        {
                            text.Append(reader.Value);
                        }

                        break;
                }
            }

            return text.ToResult();
        }

        /// <summary>Reads the next node; a part cut off by the byte limit ends the text (marked truncated) instead of failing.</summary>
        private static bool Read(XmlReader reader, TextCollector text)
        {
            try
            {
                return reader.Read();
            }
            catch (LimitReachedException)
            {
                text.MarkTruncated();
                return false;
            }
        }

        /// <summary>Collects text up to a limit; trailing blank lines are dropped at the end.</summary>
        private sealed class TextCollector
        {
            private readonly StringBuilder _builder = new StringBuilder();
            private readonly int _limit;
            private bool _truncated;

            public TextCollector(int limit)
            {
                _limit = Math.Max(1, limit);
            }

            public bool IsFull => _truncated;

            public void Append(char c)
            {
                if (_builder.Length >= _limit)
                {
                    _truncated = true;
                    return;
                }

                _builder.Append(c);
            }

            public void Append(string s)
            {
                if (string.IsNullOrEmpty(s))
                {
                    return;
                }

                int room = _limit - _builder.Length;
                if (s.Length > room)
                {
                    _builder.Append(s, 0, Math.Max(0, room));
                    _truncated = true;
                    return;
                }

                _builder.Append(s);
            }

            public void MarkTruncated() => _truncated = true;

            public DocumentText ToResult() => new DocumentText(_builder.ToString().TrimEnd('\n', '\r', ' ', '\t'), _truncated);
        }

        private sealed class LimitReachedException : IOException
        {
        }

        /// <summary>A read-only stream that fails with <see cref="LimitReachedException"/> once more than a limit was read.</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _limit;
            private long _read;

            public LimitedStream(Stream inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _read;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_read >= _limit)
                {
                    throw new LimitReachedException();
                }

                int read = _inner.Read(buffer, offset, (int)Math.Min(count, _limit - _read));
                _read += read;
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
