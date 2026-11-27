// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using ByteExplorer.Core;

namespace ByteExplorer.ViewModels
{
    /// <summary>What a history entry shows: a folder, or one of the two pages that are not folders.</summary>
    public enum HistoryPlace
    {
        Folder,
        Home,
        ThisPc,
        Trash
    }

    /// <summary>
    /// One place in a pane's back/forward history. Home and This PC have no path, so they are kinds of their own: with
    /// a null path standing for both, going back to This PC used to open Home, and moving between the two pages was not
    /// recorded at all.
    /// </summary>
    public readonly struct HistoryEntry : IEquatable<HistoryEntry>
    {
        private HistoryEntry(HistoryPlace place, string path)
        {
            Place = place;
            Path = path;
        }

        public static HistoryEntry Home { get; } = new HistoryEntry(HistoryPlace.Home, null);

        public static HistoryEntry ThisPc { get; } = new HistoryEntry(HistoryPlace.ThisPc, null);

        public static HistoryEntry Trash { get; } = new HistoryEntry(HistoryPlace.Trash, null);

        public static HistoryEntry Folder(string path) =>
            new HistoryEntry(HistoryPlace.Folder, path ?? throw new ArgumentNullException(nameof(path)));

        public HistoryPlace Place { get; }

        /// <summary>The folder, or null for Home, This PC and the trash.</summary>
        public string Path { get; }

        /// <summary>The name shown in the history menus.</summary>
        public string DisplayName => Place switch
        {
            HistoryPlace.Home => "Home",
            HistoryPlace.ThisPc => "This PC",
            HistoryPlace.Trash => Core.Platform.Trash.DisplayName,
            _ => PathUtil.GetName(Path)
        };

        public bool Equals(HistoryEntry other) =>
            Place == other.Place && (Place != HistoryPlace.Folder || string.Equals(
                PathUtil.TrimTrailingSeparator(Path), PathUtil.TrimTrailingSeparator(other.Path), PathUtil.Comparison));

        public override bool Equals(object obj) => obj is HistoryEntry other && Equals(other);

        public override int GetHashCode() =>
            Place == HistoryPlace.Folder ? PathUtil.Comparer.GetHashCode(PathUtil.TrimTrailingSeparator(Path)) : (int)Place;

        public override string ToString() => Place == HistoryPlace.Folder ? Path : DisplayName;
    }

    /// <summary>
    /// A pane's back/forward history, as in a browser: arriving somewhere new pushes the place left onto the back list
    /// and clears the forward list; moving through the history (one step, or several from the Back/Forward menus) shifts
    /// entries between the two lists without recording anything. The first place of the session is the starting point,
    /// not something to go back to. Each list keeps at most <see cref="MaxDepth"/> entries, dropping the oldest.
    /// Not thread-safe: the pane uses it on the UI thread.
    /// </summary>
    public sealed class NavigationHistory
    {
        public const int MaxDepth = 100;

        // Index 0 = the oldest; the last entry is the nearest to the current place.
        private readonly List<HistoryEntry> _back = new List<HistoryEntry>();
        private readonly List<HistoryEntry> _forward = new List<HistoryEntry>();
        private HistoryEntry? _current;

        public HistoryEntry? Current => _current;

        public bool CanGoBack => _back.Count > 0;

        public bool CanGoForward => _forward.Count > 0;

        /// <summary>The places behind the current one, nearest first (for the Back menu).</summary>
        public IReadOnlyList<HistoryEntry> BackEntries => NearestFirst(_back);

        /// <summary>The places ahead of the current one, nearest first (for the Forward menu).</summary>
        public IReadOnlyList<HistoryEntry> ForwardEntries => NearestFirst(_forward);

        /// <summary>
        /// Records arriving at <paramref name="entry"/>. Returns true when the lists changed. Arriving where the pane
        /// already is (a refresh, the same folder opened again) records nothing.
        /// </summary>
        public bool Arrive(HistoryEntry entry)
        {
            if (_current is HistoryEntry current)
            {
                if (current.Equals(entry))
                {
                    return false;
                }

                Push(_back, current);
            }

            _current = entry;
            bool changed = _forward.Count > 0 || _back.Count > 0;
            _forward.Clear();
            return changed;
        }

        /// <summary>
        /// Moves <paramref name="steps"/> places through the history (negative = back, positive = forward) and returns
        /// the place to show. False when there are not that many places, or steps is 0; nothing changes then.
        /// </summary>
        public bool TryMove(int steps, out HistoryEntry target)
        {
            target = default;
            List<HistoryEntry> from = steps < 0 ? _back : _forward;
            List<HistoryEntry> to = steps < 0 ? _forward : _back;
            int count = Math.Abs(steps);
            if (steps == 0 || count > from.Count || _current is not HistoryEntry current)
            {
                return false;
            }

            // The places passed over go to the other list in the order that puts the one next to the target on top.
            Push(to, current);
            for (int i = 1; i < count; i++)
            {
                Push(to, from[from.Count - i]);
            }

            target = from[from.Count - count];
            from.RemoveRange(from.Count - count, count);
            _current = target;
            return true;
        }

        /// <summary>Forgets everything (a new window state).</summary>
        public void Clear()
        {
            _back.Clear();
            _forward.Clear();
            _current = null;
        }

        private static void Push(List<HistoryEntry> list, HistoryEntry entry)
        {
            list.Add(entry);
            if (list.Count > MaxDepth)
            {
                list.RemoveAt(0);
            }
        }

        private static HistoryEntry[] NearestFirst(List<HistoryEntry> list)
        {
            var result = new HistoryEntry[list.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = list[list.Count - 1 - i];
            }

            return result;
        }
    }
}
