// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>
    /// Reads <c>bsdtar -tvf</c> listings, an "ls -l" style line per entry:
    /// <code>
    /// drwxr-xr-x  0 user   group       0 Nov 26 21:21 dir/
    /// -rw-r--r--  0 0      0        1234 Mar  4  2020 dir/file name.txt
    /// lrwxrwxrwx  0 0      0           0 Jan  1  2021 link -> target
    /// </code>
    /// The date is "month day time" within six months of now and "month day  year" otherwise; locales that put the day
    /// first print "day month …", and on Windows the month name follows the user's locale (Linux and macOS children run
    /// with a UTF-8 C/English locale, see <see cref="ProcessToolRunner"/>). A date that can't be read is left unknown
    /// rather than guessed. bsdtar escapes unprintable bytes in names as <c>\ooo</c> (and a backslash as <c>\\</c>):
    /// those are decoded back. Links, devices and FIFOs are dropped (never browsed or extracted).
    /// </summary>
    internal static class BsdtarListing
    {
        private static readonly Regex Line = new Regex(
            @"^(?<mode>[-dlbcphs?][-rwxsStTlL?]{9}[.+@]?)\s+(?<links>\d+)\s+(?<user>\S+)\s+(?<group>\S+)\s+(?<size>\d+(?:,\s*\d+)?)\s+(?<a>\S+)\s+(?<b>\S+)\s+(?<c>\d{1,2}:\d{2}|\d{4}) (?<name>.*)$",
            RegexOptions.CultureInvariant);

        private static readonly string[] EnglishMonths = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        public static IReadOnlyList<ArchiveEntryInfo> Parse(string text, DateTime nowLocal) =>
            Parse(new StringReader(text ?? string.Empty), int.MaxValue, nowLocal);

        public static IReadOnlyList<ArchiveEntryInfo> Parse(TextReader reader, int maxEntries, DateTime nowLocal)
        {
            var entries = new List<ArchiveEntryInfo>();
            string line;
            int index = -1;
            while (entries.Count < maxEntries && (line = reader.ReadLine()) != null)
            {
                Match match = Line.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                index++;
                char type = match.Groups["mode"].Value[0];
                string name = match.Groups["name"].Value;
                if (type == 'l' || type == 'b' || type == 'c' || type == 'p' || type == 's' || type == 'h' || name.Contains(" link to ", StringComparison.Ordinal))
                {
                    continue; // links (hard links print " link to target"), devices, FIFOs, sockets
                }

                bool isFolder = type == 'd';
                long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long size);
                DateTime modified = ParseDate(match.Groups["a"].Value, match.Groups["b"].Value, match.Groups["c"].Value, nowLocal);
                entries.Add(new ArchiveEntryInfo(Unescape(name), isFolder, isFolder ? 0 : size, modified, index));
            }

            return entries;
        }

        /// <summary>"Nov 26" + "21:21" or "Mar 4" + "2020" (either order of day and month) to UTC; default when unreadable.</summary>
        internal static DateTime ParseDate(string a, string b, string timeOrYear, DateTime nowLocal)
        {
            int day;
            int month;
            if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out day))
            {
                month = ParseMonth(b);
            }
            else if (int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out day))
            {
                month = ParseMonth(a);
            }
            else
            {
                return default;
            }

            if (month == 0 || day < 1 || day > 31)
            {
                return default;
            }

            try
            {
                int colon = timeOrYear.IndexOf(':');
                if (colon < 0)
                {
                    int year = int.Parse(timeOrYear, CultureInfo.InvariantCulture);
                    return Local(year, month, day, 0, 0);
                }

                int hour = int.Parse(timeOrYear.AsSpan(0, colon), CultureInfo.InvariantCulture);
                int minute = int.Parse(timeOrYear.AsSpan(colon + 1), CultureInfo.InvariantCulture);
                // No year: bsdtar prints this form within six months of now, before or after. Pick the closest year.
                DateTime best = default;
                foreach (int year in new[] { nowLocal.Year - 1, nowLocal.Year, nowLocal.Year + 1 })
                {
                    if (day > DateTime.DaysInMonth(year, month))
                    {
                        continue;
                    }

                    var candidate = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
                    if (best == default || Math.Abs((candidate - nowLocal).Ticks) < Math.Abs((best - nowLocal).Ticks))
                    {
                        best = candidate;
                    }
                }

                return best == default ? default : best.ToUniversalTime();
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentOutOfRangeException)
            {
                return default;
            }
        }

        private static DateTime Local(int year, int month, int day, int hour, int minute) =>
            day > DateTime.DaysInMonth(year, month) ? default : new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

        /// <summary>1–12 from an English or current-culture abbreviation ("Nov", "nov.", "Nov."), or "11月"-style; 0 if unknown.</summary>
        internal static int ParseMonth(string text)
        {
            string token = text.Trim().TrimEnd('.').ToLowerInvariant();
            if (token.Length == 0)
            {
                return 0;
            }

            for (int i = 0; i < EnglishMonths.Length; i++)
            {
                if (token.StartsWith(EnglishMonths[i], StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
            foreach (string[] names in new[] { format.AbbreviatedMonthNames, format.AbbreviatedMonthGenitiveNames, format.MonthNames, format.MonthGenitiveNames })
            {
                for (int i = 0; i < 12 && i < names.Length; i++)
                {
                    string name = names[i].Trim().TrimEnd('.').ToLower(CultureInfo.CurrentCulture);
                    if (name.Length > 0 && string.Equals(name, text.Trim().TrimEnd('.').ToLower(CultureInfo.CurrentCulture), StringComparison.Ordinal))
                    {
                        return i + 1;
                    }
                }
            }

            int digits = 0;
            while (digits < token.Length && char.IsAsciiDigit(token[digits]))
            {
                digits++;
            }

            return digits > 0 && int.TryParse(token.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= 12
                ? number
                : 0;
        }

        /// <summary>Undoes bsdtar's escaping of names: "\ooo" bytes (UTF-8 sequences, usually), "\\", "\n" and friends.</summary>
        internal static string Unescape(string name)
        {
            if (name.IndexOf('\\') < 0)
            {
                return name;
            }

            var bytes = new List<byte>(name.Length + 8);
            var run = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '\\' && i + 1 < name.Length)
                {
                    char next = name[i + 1];
                    int value = -1;
                    int length = 0;
                    if (i + 3 < name.Length && IsOctal(next) && IsOctal(name[i + 2]) && IsOctal(name[i + 3]))
                    {
                        value = ((next - '0') << 6) | ((name[i + 2] - '0') << 3) | (name[i + 3] - '0');
                        length = 3;
                    }
                    else
                    {
                        value = next switch
                        {
                            '\\' => '\\',
                            'a' => '\a',
                            'b' => '\b',
                            'f' => '\f',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            'v' => '\v',
                            _ => -1,
                        };
                        length = 1;
                    }

                    if (value >= 0 && value <= 0xFF)
                    {
                        bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
                        run.Clear();
                        bytes.Add((byte)value);
                        i += length;
                        continue;
                    }
                }

                run.Append(c);
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(run.ToString()));
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static bool IsOctal(char c) => c >= '0' && c <= '7';
    }

    /// <summary>
    /// Reads 7-Zip's technical listing (<c>7z l -slt</c>): after a "----------" line, one block of "Key = Value" lines
    /// per entry, blocks separated by blank lines.
    /// <code>
    /// Path = dir/file.txt
    /// Folder = -
    /// Size = 1234
    /// Modified = 2024-01-02 03:04:05.1234567
    /// Attributes = A -rw-r--r--
    /// Encrypted = +
    /// </code>
    /// The block before the separator describes the archive itself and is skipped. Times are local. Paths use "\" on
    /// Windows. Symbolic links (a Unix mode starting with 'l') are dropped.
    /// </summary>
    internal static class SevenZipListing
    {
        public static IReadOnlyList<ArchiveEntryInfo> Parse(string text) =>
            Parse(new StringReader(text ?? string.Empty), int.MaxValue);

        public static IReadOnlyList<ArchiveEntryInfo> Parse(TextReader reader, int maxEntries)
        {
            var entries = new List<ArchiveEntryInfo>();
            var block = new Dictionary<string, string>(StringComparer.Ordinal);
            bool started = false;
            int index = -1;
            string line;
            while (entries.Count < maxEntries && (line = reader.ReadLine()) != null)
            {
                if (!started)
                {
                    started = line.StartsWith("----------", StringComparison.Ordinal);
                    continue;
                }

                if (line.Length == 0)
                {
                    Flush(block, entries, ref index);
                    continue;
                }

                int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                if (equals > 0)
                {
                    block[line.Substring(0, equals)] = line.Substring(equals + 3);
                }
                else if (line.EndsWith(" =", StringComparison.Ordinal))
                {
                    block[line.Substring(0, line.Length - 2)] = string.Empty;
                }
            }

            if (entries.Count < maxEntries)
            {
                Flush(block, entries, ref index);
            }

            return entries;
        }

        private static void Flush(Dictionary<string, string> block, List<ArchiveEntryInfo> entries, ref int index)
        {
            if (block.Count == 0)
            {
                return;
            }

            if (block.TryGetValue("Path", out string path) && path.Length > 0)
            {
                index++;
                block.TryGetValue("Attributes", out string attributes);
                attributes ??= string.Empty;
                string unixMode = attributes.Length > 0 && attributes.LastIndexOf(' ') >= 0 ? attributes.Substring(attributes.LastIndexOf(' ') + 1) : string.Empty;
                bool isLink = unixMode.Length == 10 && unixMode[0] == 'l';
                if (!isLink)
                {
                    bool isFolder = (block.TryGetValue("Folder", out string folder) && folder == "+")
                        || (attributes.Length > 0 && attributes[0] == 'D');
                    block.TryGetValue("Size", out string sizeText);
                    long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size);
                    block.TryGetValue("Modified", out string modifiedText);
                    bool encrypted = block.TryGetValue("Encrypted", out string encryptedText) && encryptedText == "+";
                    entries.Add(new ArchiveEntryInfo(path, isFolder, isFolder ? 0 : size, ParseTime(modifiedText), index, encrypted));
                }
            }

            block.Clear();
        }

        /// <summary>"2024-01-02 03:04:05[.fraction]" (local) to UTC; default when missing.</summary>
        internal static DateTime ParseTime(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 19)
            {
                return default;
            }

            return DateTime.TryParseExact(text.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime local)
                ? local.ToUniversalTime()
                : default;
        }
    }
}
