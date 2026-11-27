// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ByteSearcher
{
    /// <summary>
    /// The text of an RTF document: control words are dropped, groups that are not body text (font and color tables,
    /// style sheets, document info, pictures, embedded objects, field instructions, and every <c>{\*\…}</c> destination)
    /// are skipped, <c>\par</c>, <c>\line</c>, <c>\tab</c>, <c>\cell</c> separate words, <c>\uN</c> gives Unicode chars
    /// (skipping their <c>\ucN</c> fallback chars), and <c>\'hh</c> bytes are decoded in the document's <c>\ansicpg</c>
    /// code page (Windows-1252 when it names none or one this system lacks). <c>\binN</c> data is skipped unread.
    /// </summary>
    internal static class RtfTextExtractor
    {
        private static readonly HashSet<string> SkippedDestinations = new HashSet<string>(StringComparer.Ordinal)
        {
            "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "themedata", "datastore", "latentstyles",
            "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "filetbl", "revtbl", "fldinst", "mmathPr",
            "colorschememapping", "wgrffmtfilter", "pgdsctbl", "bkmkstart", "bkmkend", "blipuid", "panose", "falt", "sp",
            "passwordhash", "protusertbl", "userprops", "operator", "author", "title", "subject", "keywords", "comment",
            "doccomm", "company", "nonshppict", "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr",
            "footerf", "xe", "tc", "template", "listtext", "pntext", "pntxta", "pntxtb", "fldtype"
        };

        private static readonly HashSet<string> Breaks = new HashSet<string>(StringComparer.Ordinal)
        {
            "par", "line", "tab", "cell", "row", "sect", "page", "column", "emdash", "endash", "bullet", "emspace", "enspace", "qmspace"
        };

        public static bool Extract(byte[] data, ContentSink sink, CancellationToken cancellationToken)
        {
            if (data.Length < 5 || data[0] != (byte)'{' || data[1] != (byte)'\\' || data[2] != (byte)'r' || data[3] != (byte)'t' || data[4] != (byte)'f')
            {
                return false;
            }

            Encoding codePage = Windows1252();
            var groups = new Stack<(bool Skip, int Uc)>();
            bool skip = false;
            int uc = 1;
            int pendingFallback = 0;
            var text = new StringBuilder(4096);
            var word = new StringBuilder(32);
            var hexBytes = new List<byte>();

            void Flush()
            {
                if (hexBytes.Count > 0)
                {
                    if (!skip)
                    {
                        text.Append(codePage.GetString(hexBytes.ToArray()));
                    }

                    hexBytes.Clear();
                }

                if (text.Length > 0)
                {
                    foreach (ReadOnlyMemory<char> chunk in text.GetChunks())
                    {
                        sink.Append(chunk.Span);
                    }

                    text.Clear();
                }
            }

            int i = 0;
            while (i < data.Length && !sink.IsFull)
            {
                if ((i & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                byte c = data[i];
                if (c != (byte)'\\' || i + 1 >= data.Length || data[i + 1] != (byte)'\'')
                {
                    // Bytes of \'hh runs are decoded together (a double-byte code page needs both).
                    if (hexBytes.Count > 0)
                    {
                        Flush();
                    }
                }

                switch (c)
                {
                    case (byte)'{':
                        groups.Push((skip, uc));
                        i++;
                        if (i + 1 < data.Length && data[i] == (byte)'\\' && data[i + 1] == (byte)'*')
                        {
                            skip = true; // {\*\destination}: unknown to readers that do not know it
                            i += 2;
                        }

                        continue;

                    case (byte)'}':
                        Flush();
                        if (groups.Count > 0)
                        {
                            (skip, uc) = groups.Pop();
                        }

                        pendingFallback = 0;
                        i++;
                        continue;

                    case (byte)'\r':
                    case (byte)'\n':
                        i++;
                        continue;

                    case (byte)'\\':
                        break;

                    default:
                        if (pendingFallback > 0)
                        {
                            pendingFallback--;
                        }
                        else if (!skip)
                        {
                            text.Append(c < 128 ? (char)c : codePage.GetString(data, i, 1)[0]);
                        }

                        if (text.Length >= 4096)
                        {
                            Flush();
                        }

                        i++;
                        continue;
                }

                // A control: \word[N], \'hh, or a control symbol.
                i++;
                if (i >= data.Length)
                {
                    break;
                }

                byte next = data[i];
                if (next == (byte)'\'')
                {
                    if (i + 2 < data.Length && TryHex(data[i + 1], data[i + 2], out byte value))
                    {
                        if (pendingFallback > 0)
                        {
                            pendingFallback--;
                        }
                        else
                        {
                            hexBytes.Add(value);
                        }
                    }

                    i += 3;
                    continue;
                }

                if (!IsLetter(next))
                {
                    i++;
                    switch (next)
                    {
                        case (byte)'\\':
                        case (byte)'{':
                        case (byte)'}':
                            if (pendingFallback > 0)
                            {
                                pendingFallback--;
                            }
                            else if (!skip)
                            {
                                text.Append((char)next);
                            }

                            break;
                        case (byte)'~':
                            if (!skip)
                            {
                                text.Append(' ');
                            }

                            break;
                        case (byte)'_':
                        case (byte)'-':
                            break; // non-breaking and optional hyphens: inside a word
                        case (byte)'\r':
                        case (byte)'\n':
                            if (!skip)
                            {
                                text.Append('\n'); // "\" before a line end is \par
                            }

                            break;
                        case (byte)'*':
                            skip = true;
                            break;
                    }

                    continue;
                }

                word.Clear();
                while (i < data.Length && IsLetter(data[i]) && word.Length < 32)
                {
                    word.Append((char)data[i++]);
                }

                bool hasParameter = false;
                bool negative = false;
                int parameter = 0;
                if (i < data.Length && data[i] == (byte)'-')
                {
                    negative = true;
                    i++;
                }

                while (i < data.Length && data[i] >= (byte)'0' && data[i] <= (byte)'9')
                {
                    hasParameter = true;
                    if (parameter < 100_000_000)
                    {
                        parameter = parameter * 10 + (data[i] - '0');
                    }

                    i++;
                }

                if (negative)
                {
                    parameter = -parameter;
                }

                if (i < data.Length && data[i] == (byte)' ')
                {
                    i++; // the delimiter space belongs to the control word
                }

                string name = word.ToString();
                switch (name)
                {
                    case "u" when hasParameter:
                        if (!skip)
                        {
                            Flush();
                            text.Append((char)(parameter < 0 ? parameter + 65536 : parameter));
                        }

                        pendingFallback = uc;
                        continue;
                    case "uc" when hasParameter:
                        uc = Math.Clamp(parameter, 0, 10);
                        continue;
                    case "ansicpg" when hasParameter:
                        codePage = CodePage(parameter) ?? codePage;
                        continue;
                    case "bin" when hasParameter:
                        i += Math.Max(0, parameter);
                        continue;
                }

                if (SkippedDestinations.Contains(name))
                {
                    skip = true;
                }
                else if (Breaks.Contains(name) && !skip)
                {
                    text.Append('\n');
                }
            }

            Flush();
            return true;
        }

        private static bool IsLetter(byte b) => (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');

        private static bool TryHex(byte high, byte low, out byte value)
        {
            int h = HexValue(high);
            int l = HexValue(low);
            value = (byte)((h << 4) | l);
            return h >= 0 && l >= 0;
        }

        internal static int HexValue(byte b) =>
            b >= (byte)'0' && b <= (byte)'9' ? b - '0' : b >= (byte)'a' && b <= (byte)'f' ? b - 'a' + 10 : b >= (byte)'A' && b <= (byte)'F' ? b - 'A' + 10 : -1;

        /// <summary>A Windows code page from the shared framework's provider (not registered globally), or null.</summary>
        private static Encoding CodePage(int number)
        {
            if (number == 65001)
            {
                return new UTF8Encoding(false);
            }

            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(number) ?? (number == 28591 ? Encoding.Latin1 : null);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        internal static Encoding Windows1252() => CodePage(1252) ?? Encoding.Latin1;
    }
}
