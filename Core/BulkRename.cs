// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteExplorer.Core
{
    /// <summary>How "Change case" rewrites the name (the part before the extension; a folder's whole name).</summary>
    public enum BulkRenameCase
    {
        Lower,
        Upper,
        Title,
        Sentence
    }

    /// <summary>What the "Extension" rule does to a file's extension (folders have none).</summary>
    public enum BulkRenameExtensionMode
    {
        ChangeTo,
        Lower,
        Upper,
        Remove
    }

    /// <summary>Where "Insert date" puts the modified date.</summary>
    public enum BulkRenameDatePosition
    {
        BeforeName,
        AfterName
    }

    /// <summary>One item to rename: where it is, whether it is a folder, and its modified date (for "Insert date").</summary>
    public sealed class BulkRenameItem
    {
        public BulkRenameItem(string fullPath, bool isDirectory, DateTime lastWriteTime)
        {
            FullPath = PathUtil.TrimTrailingSeparator(fullPath ?? string.Empty);
            Name = Path.GetFileName(FullPath);
            Folder = Path.GetDirectoryName(FullPath) ?? string.Empty;
            IsDirectory = isDirectory;
            LastWriteTime = lastWriteTime;
        }

        public string FullPath { get; }
        public string Folder { get; }
        public string Name { get; }
        public bool IsDirectory { get; }

        /// <summary>Local time, as the date column shows it.</summary>
        public DateTime LastWriteTime { get; }
    }

    /// <summary>
    /// The rules of a bulk rename. Each rule has an on/off switch, so the dialog can keep what was typed in a rule that is
    /// switched off. The rules run top to bottom in the order of the properties below (the dialog shows them in that
    /// order): find and replace, remove characters, new name with numbering, add text, insert date, change case,
    /// extension.
    /// </summary>
    public sealed class BulkRenameOptions
    {
        // 1. Find and replace
        public bool FindEnabled { get; set; }
        public string Find { get; set; } = string.Empty;
        public string Replace { get; set; } = string.Empty;
        public bool UseRegex { get; set; }
        public bool MatchCase { get; set; }

        /// <summary>Only the first match is replaced (otherwise all of them).</summary>
        public bool FirstOnly { get; set; }

        /// <summary>Find and replace also sees the extension (otherwise only the part before it).</summary>
        public bool IncludeExtension { get; set; }

        // 2. Remove characters
        public bool RemoveEnabled { get; set; }

        /// <summary>1-based: 1 removes from the first character.</summary>
        public int RemovePosition { get; set; } = 1;
        public int RemoveCount { get; set; } = 1;

        // 3. New name with numbering
        public bool NumberingEnabled { get; set; }

        /// <summary>
        /// "{n}" is the number and "{name}" the name so far. A pattern without "{n}" numbers as Explorer does when one
        /// name is typed for several items: "Holiday" gives "Holiday (1)", "Holiday (2)"…
        /// </summary>
        public string Pattern { get; set; } = string.Empty;
        public int Start { get; set; } = 1;
        public int Step { get; set; } = 1;

        /// <summary>Minimum digits: 2 gives 01, 02…; 1 means no padding.</summary>
        public int Digits { get; set; } = 1;
        public bool KeepExtension { get; set; } = true;

        // 4. Add text
        public bool AddTextEnabled { get; set; }
        public string Prefix { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;

        // 5. Insert date
        public bool DateEnabled { get; set; }
        public BulkRenameDatePosition DatePosition { get; set; }
        public string DateFormat { get; set; } = "yyyy-MM-dd";
        public string DateSeparator { get; set; } = " ";

        // 6. Change case
        public bool CaseEnabled { get; set; }
        public BulkRenameCase Case { get; set; }

        // 7. Extension
        public bool ExtensionEnabled { get; set; }
        public BulkRenameExtensionMode ExtensionMode { get; set; }
        public string NewExtension { get; set; } = string.Empty;
    }

    /// <summary>One line of the preview: the item, its new name, and why that name can't be used (null when it can).</summary>
    public sealed class BulkRenameRow
    {
        internal BulkRenameRow(BulkRenameItem item, string newName)
        {
            Item = item;
            NewName = newName;
        }

        public BulkRenameItem Item { get; }
        public string NewName { get; }
        public bool IsChanged => !string.Equals(Item.Name, NewName, StringComparison.Ordinal);
        public string Problem { get; internal set; }
        public string NewPath => Path.Combine(Item.Folder, NewName);
    }

    /// <summary>One rename to perform: from a path to a new path in the same folder.</summary>
    public sealed class BulkRenameMove
    {
        public BulkRenameMove(string from, string to, bool isDirectory)
        {
            From = from;
            To = to;
            IsDirectory = isDirectory;
        }

        public string From { get; }
        public string To { get; }
        public bool IsDirectory { get; }
    }

    /// <summary>The preview of a bulk rename: every item's new name and problems, or one error that stops all of them.</summary>
    public sealed class BulkRenamePlan
    {
        internal BulkRenamePlan(IReadOnlyList<BulkRenameRow> rows, string error)
        {
            Rows = rows;
            Error = error;
            foreach (BulkRenameRow row in rows)
            {
                if (row.IsChanged)
                {
                    ChangedCount++;
                }

                if (row.Problem != null)
                {
                    ProblemCount++;
                }
            }
        }

        public IReadOnlyList<BulkRenameRow> Rows { get; }

        /// <summary>A problem with the rules themselves (a bad regular expression or date format); null when none.</summary>
        public string Error { get; }

        public int ChangedCount { get; }
        public int ProblemCount { get; }

        /// <summary>OK is only offered when the rules work, no name has a problem and at least one name changes.</summary>
        public bool CanApply => Error == null && ProblemCount == 0 && ChangedCount > 0;

        /// <summary>The renames to perform (the changed rows), in list order.</summary>
        public IReadOnlyList<BulkRenameMove> Moves
        {
            get
            {
                var moves = new List<BulkRenameMove>(ChangedCount);
                foreach (BulkRenameRow row in Rows)
                {
                    if (row.IsChanged)
                    {
                        moves.Add(new BulkRenameMove(row.Item.FullPath, row.NewPath, row.Item.IsDirectory));
                    }
                }

                return moves;
            }
        }
    }

    /// <summary>
    /// The bulk rename rules, without any UI or I/O, so the preview and the tests compute exactly what a rename will do.
    /// Names are split as Explorer does: the extension is the last ".xyz" of a file's name, a name like ".bashrc" has no
    /// extension, and a folder's whole name is its name. Every new name is checked with the OS's rules
    /// (<see cref="FileNameRules"/>), against the other new names (two items can't end up with the same name) and against
    /// the other items already in the folder (a rename never replaces anything).
    /// </summary>
    public static class BulkRename
    {
        /// <summary>A regular expression that runs longer than this on one name is treated as broken (catastrophic backtracking).</summary>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Computes every item's new name, in the given order (numbering follows it). <paramref name="existsInFolder"/>
        /// says whether a name is taken in a folder (folder, name); the items themselves may be reported too, they are
        /// told apart. <paramref name="nameComparer"/> is how the file system compares names (default: the OS's).
        /// </summary>
        public static BulkRenamePlan Plan(IReadOnlyList<BulkRenameItem> items, BulkRenameOptions options,
            Func<string, string, bool> existsInFolder, StringComparer nameComparer = null)
        {
            items ??= Array.Empty<BulkRenameItem>();
            options ??= new BulkRenameOptions();
            nameComparer ??= PathUtil.Comparer;

            Regex regex = null;
            string error = null;
            if (options.FindEnabled && options.UseRegex && !string.IsNullOrEmpty(options.Find))
            {
                try
                {
                    RegexOptions flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                    regex = new Regex(options.Find, flags, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    error = "The regular expression isn't valid: " + ex.Message;
                }
            }

            var rows = new List<BulkRenameRow>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string newName = items[i].Name;
                if (error == null)
                {
                    try
                    {
                        newName = NewName(items[i], i, options, regex);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        error = "The regular expression takes too long on \"" + items[i].Name + "\"; simplify it.";
                    }
                    catch (FormatException)
                    {
                        error = "The date format isn't valid. Use letters such as yyyy, MM, dd, HH and mm.";
                    }
                }

                rows.Add(new BulkRenameRow(items[i], newName));
            }

            if (error != null)
            {
                // Nothing can be renamed with broken rules: the preview shows the names unchanged.
                rows = new List<BulkRenameRow>(items.Count);
                foreach (BulkRenameItem item in items)
                {
                    rows.Add(new BulkRenameRow(item, item.Name));
                }

                return new BulkRenamePlan(rows, error);
            }

            FindProblems(rows, existsInFolder, nameComparer);
            return new BulkRenamePlan(rows, null);
        }

        /// <summary>Splits a name into the part before the extension and the extension (with its dot); folders have none.</summary>
        public static (string Stem, string Extension) Split(string name, bool isDirectory)
        {
            name ??= string.Empty;
            if (isDirectory)
            {
                return (name, string.Empty);
            }

            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                return (name, string.Empty); // ".bashrc", "notes", "file."
            }

            return (name.Substring(0, dot), name.Substring(dot));
        }

        /// <summary>One item's new name with the rules applied in order.</summary>
        private static string NewName(BulkRenameItem item, int index, BulkRenameOptions options, Regex regex)
        {
            (string stem, string extension) = Split(item.Name, item.IsDirectory);

            if (options.FindEnabled && !string.IsNullOrEmpty(options.Find))
            {
                if (options.IncludeExtension && !item.IsDirectory)
                {
                    (stem, extension) = Split(Replace(stem + extension, options, regex), false);
                }
                else
                {
                    stem = Replace(stem, options, regex);
                }
            }

            if (options.RemoveEnabled && options.RemoveCount > 0)
            {
                int start = Math.Max(1, options.RemovePosition) - 1;
                if (start < stem.Length)
                {
                    stem = stem.Remove(start, Math.Min(options.RemoveCount, stem.Length - start));
                }
            }

            if (options.NumberingEnabled)
            {
                stem = Number(options, index, stem);
                if (!options.KeepExtension)
                {
                    extension = string.Empty;
                }
            }

            if (options.AddTextEnabled)
            {
                stem = (options.Prefix ?? string.Empty) + stem + (options.Suffix ?? string.Empty);
            }

            if (options.DateEnabled)
            {
                string format = string.IsNullOrWhiteSpace(options.DateFormat) ? "yyyy-MM-dd" : options.DateFormat;
                string date = item.LastWriteTime.ToString(format, CultureInfo.InvariantCulture);
                string separator = options.DateSeparator ?? string.Empty;
                stem = options.DatePosition == BulkRenameDatePosition.BeforeName ? date + separator + stem : stem + separator + date;
            }

            if (options.CaseEnabled)
            {
                stem = ChangeCase(stem, options.Case);
            }

            if (options.ExtensionEnabled && !item.IsDirectory)
            {
                extension = options.ExtensionMode switch
                {
                    BulkRenameExtensionMode.Lower => extension.ToLowerInvariant(),
                    BulkRenameExtensionMode.Upper => extension.ToUpperInvariant(),
                    BulkRenameExtensionMode.Remove => string.Empty,
                    _ => NormalizeExtension(options.NewExtension)
                };
            }

            return stem + extension;
        }

        private static string Replace(string text, BulkRenameOptions options, Regex regex)
        {
            string replacement = options.Replace ?? string.Empty;
            if (regex != null)
            {
                return options.FirstOnly ? regex.Replace(text, replacement, 1) : regex.Replace(text, replacement);
            }

            StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var result = new StringBuilder(text.Length);
            int from = 0;
            while (from <= text.Length)
            {
                int at = text.IndexOf(options.Find, from, comparison);
                if (at < 0)
                {
                    break;
                }

                result.Append(text, from, at - from).Append(replacement);
                from = at + options.Find.Length;
                if (options.FirstOnly)
                {
                    break;
                }
            }

            result.Append(text, from, text.Length - from);
            return result.ToString();
        }

        private static string Number(BulkRenameOptions options, int index, string stem)
        {
            long number = (long)options.Start + (long)index * options.Step;
            int digits = Math.Clamp(options.Digits, 1, 18);
            string text = number < 0
                ? "-" + Math.Abs(number).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')
                : number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');

            string pattern = options.Pattern ?? string.Empty;
            if (pattern.IndexOf("{n}", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // Explorer's way: one name typed for several items becomes "Name (1)", "Name (2)"…
                pattern = (pattern.Length == 0 ? "{name}" : pattern) + " ({n})";
            }

            return ReplaceToken(ReplaceToken(pattern, "{name}", stem), "{n}", text);
        }

        private static string ReplaceToken(string text, string token, string value) =>
            text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        private static string ChangeCase(string text, BulkRenameCase mode)
        {
            switch (mode)
            {
                case BulkRenameCase.Lower:
                    return text.ToLowerInvariant();
                case BulkRenameCase.Upper:
                    return text.ToUpperInvariant();
                case BulkRenameCase.Title:
                    // ToTitleCase leaves words in capitals alone, so lower everything first: "MY holiday" -> "My Holiday".
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
                default:
                    string lower = text.ToLowerInvariant();
                    for (int i = 0; i < lower.Length; i++)
                    {
                        if (char.IsLetter(lower[i]))
                        {
                            return lower.Substring(0, i) + char.ToUpperInvariant(lower[i]) + lower.Substring(i + 1);
                        }
                    }

                    return lower;
            }
        }

        /// <summary>"txt", ".txt" and " .txt " all mean ".txt"; nothing means no extension.</summary>
        private static string NormalizeExtension(string extension)
        {
            string trimmed = (extension ?? string.Empty).Trim().TrimStart('.');
            return trimmed.Length == 0 ? string.Empty : "." + trimmed;
        }

        private static string Key(string folder, string name) => PathUtil.TrimTrailingSeparator(folder) + "\0" + name;

        private static void FindProblems(List<BulkRenameRow> rows, Func<string, string, bool> existsInFolder, StringComparer comparer)
        {
            var originals = new HashSet<string>(comparer);
            var finalCounts = new Dictionary<string, int>(comparer);
            foreach (BulkRenameRow row in rows)
            {
                originals.Add(Key(row.Item.Folder, row.Item.Name));
                string key = Key(row.Item.Folder, row.NewName);
                finalCounts[key] = finalCounts.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            foreach (BulkRenameRow row in rows)
            {
                string key = Key(row.Item.Folder, row.NewName);
                if (row.IsChanged)
                {
                    string invalid = FileNameRules.Validate(row.NewName);
                    if (invalid != null)
                    {
                        row.Problem = invalid.Replace("\n", " ", StringComparison.Ordinal);
                        continue;
                    }

                    // ".jpg" from "photo.jpg": nothing is left before the extension (and the file would turn hidden on Unix).
                    bool lostStem = !row.Item.IsDirectory &&
                                    row.NewName.StartsWith(".", StringComparison.Ordinal) &&
                                    !row.Item.Name.StartsWith(".", StringComparison.Ordinal) &&
                                    Split(row.Item.Name, false).Extension.Length > 0;
                    if (lostStem)
                    {
                        row.Problem = "Nothing would be left before the extension.";
                        continue;
                    }
                }

                if (finalCounts[key] > 1)
                {
                    row.Problem = "Another selected item would get the same name.";
                    continue;
                }

                if (row.IsChanged && !originals.Contains(key) && existsInFolder != null && existsInFolder(row.Item.Folder, row.NewName))
                {
                    row.Problem = "An item with this name already exists in the folder.";
                }
            }
        }
    }
}
