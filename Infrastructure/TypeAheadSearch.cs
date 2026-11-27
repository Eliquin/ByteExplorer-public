// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Type-to-select for a list, with the rules file managers share (Explorer, Finder, GNOME Files):
    /// <list type="bullet">
    /// <item>Typed characters accumulate into a prefix while they come less than <see cref="Timeout"/> apart; a pause
    /// starts a new prefix.</item>
    /// <item>The search starts at the current item, so typing "rep" after "re" stays on "report.txt" if it still matches,
    /// and moves on otherwise.</item>
    /// <item>Repeating one character ("a", "aa", "aaa") cycles through the items starting with it instead of looking
    /// for a name starting with "aaa".</item>
    /// <item>The search wraps around the end. Case is ignored (ordinal, so it never allocates and costs microseconds per
    /// thousand names).</item>
    /// </list>
    /// Names come through a callback, so a virtualized list answers from its index without creating item objects.
    /// One instance per list or view; not thread-safe (UI thread only).
    /// </summary>
    public sealed class TypeAheadSearch
    {
        private string _prefix = string.Empty;
        private long _lastInputTicks;

        /// <summary>The longest pause between keystrokes that still extends the prefix.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(1000);

        /// <summary>The prefix typed so far (empty once timed out).</summary>
        public string Prefix => _prefix;

        /// <summary>Whether a prefix is being typed, i.e. a space should be treated as part of the name.</summary>
        public bool IsActive(long nowTicks) => _prefix.Length > 0 && nowTicks - _lastInputTicks <= Timeout.Ticks;

        public void Reset() => _prefix = string.Empty;

        /// <summary>
        /// Adds typed text and returns the index to select, or -1 when nothing matches (the selection stays).
        /// <paramref name="currentIndex"/> is the focused item (-1 for none); <paramref name="nameAt"/> may return null for
        /// rows that are not items (they are skipped).
        /// </summary>
        public int Next(string text, int currentIndex, int count, Func<int, string> nameAt, long nowTicks)
        {
            if (string.IsNullOrEmpty(text) || count <= 0 || nameAt == null)
            {
                return -1;
            }

            if (nowTicks - _lastInputTicks > Timeout.Ticks)
            {
                _prefix = string.Empty;
            }

            _lastInputTicks = nowTicks;
            _prefix += text;

            bool cycling = IsSingleRepeatedCharacter(_prefix);
            string search = cycling ? _prefix.Substring(0, 1) : _prefix;
            int start = cycling ? currentIndex + 1 : Math.Max(currentIndex, 0);

            for (int step = 0; step < count; step++)
            {
                int index = (((start + step) % count) + count) % count;
                string name = nameAt(index);
                if (name != null && name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool IsSingleRepeatedCharacter(string value)
        {
            for (int i = 1; i < value.Length; i++)
            {
                if (char.ToUpperInvariant(value[i]) != char.ToUpperInvariant(value[0]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
