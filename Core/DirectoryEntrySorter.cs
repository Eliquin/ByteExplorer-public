// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;

namespace ByteExplorer.Core
{
    /// <summary>What a folder listing is ordered by.</summary>
    public enum EntryOrder
    {
        Name,
        DateModified,
        Type,
        Size,

        /// <summary>By the items' tags (their full names); untagged items last in either direction.</summary>
        Tags,
        DateCreated,
        DateAccessed
    }

    /// <summary>
    /// Orders folder entries for display: folders first, then files, each group by the chosen key (ties by natural
    /// name, then ordinal, so the order is total and the same on every run). Fast on huge folders:
    /// <list type="bullet">
    /// <item>The two groups are split once, so no comparison checks "folder or file".</item>
    /// <item>A group of more than <see cref="ParallelThreshold"/> entries is sorted in slices on several cores and
    /// merged (about 3x faster on 4 cores for 100,000 names).</item>
    /// <item>A list already in name order (the index is built that way) needs no sort for name order, and only a
    /// reversal of each group for descending name order.</item>
    /// <item>No allocation per comparison (the Type key is a span of the name).</item>
    /// </list>
    /// </summary>
    public static class DirectoryEntrySorter
    {
        /// <summary>Groups smaller than this are sorted on the calling thread.</summary>
        public const int ParallelThreshold = 16_384;

        /// <summary>
        /// Returns <paramref name="entries"/> ordered for display as a new array. <paramref name="inNameOrder"/>: the input
        /// is already folders first in ascending natural name order (an index straight from the disk), which makes name
        /// order free.
        /// </summary>
        /// <param name="folderSize">
        /// Sorting by size: a folder's counted size (-1 when not counted: sorted as the smallest, as Total Commander does).
        /// Without it folders keep name order under a size sort, as in Explorer.
        /// </param>
        public static DirectoryIndexEntry[] Sort(IReadOnlyList<DirectoryIndexEntry> entries, EntryOrder order, bool ascending, bool inNameOrder = false,
            Func<DirectoryIndexEntry, string> tagKey = null, Func<DirectoryIndexEntry, long> folderSize = null)
        {
            int count = entries.Count;
            var result = new DirectoryIndexEntry[count];
            int folders = 0;
            for (int i = 0; i < count; i++)
            {
                if (entries[i].IsDirectory)
                {
                    folders++;
                }
            }

            // Split: folders to the front, files after, each keeping its input order.
            int folderSlot = 0;
            int fileSlot = folders;
            for (int i = 0; i < count; i++)
            {
                DirectoryIndexEntry entry = entries[i];
                result[entry.IsDirectory ? folderSlot++ : fileSlot++] = entry;
            }

            if (inNameOrder && order == EntryOrder.Name)
            {
                if (!ascending)
                {
                    Array.Reverse(result, 0, folders);
                    Array.Reverse(result, folders, count - folders);
                }

                return result;
            }

            if (order == EntryOrder.Tags)
            {
                SortByTags(result, 0, folders, tagKey, ascending);
                SortByTags(result, folders, count - folders, tagKey, ascending);
                return result;
            }

            IComparer<DirectoryIndexEntry> comparer = ComparerFor(order, ascending);
            if (order == EntryOrder.Size && folderSize != null)
            {
                SortFoldersBySize(result, folders, folderSize, ascending);
                SortRange(result, folders, count - folders, comparer);
                return result;
            }

            SortRange(result, 0, folders, comparer);
            SortRange(result, folders, count - folders, comparer);
            return result;
        }

        private static void SortFoldersBySize(DirectoryIndexEntry[] result, int folders, Func<DirectoryIndexEntry, long> folderSize, bool ascending)
        {
            if (folders < 2)
            {
                return;
            }

            // Each size looked up once, not once per comparison.
            var keys = new long[folders];
            for (int i = 0; i < folders; i++)
            {
                keys[i] = folderSize(result[i]);
            }

            var order = new int[folders];
            for (int i = 0; i < folders; i++)
            {
                order[i] = i;
            }

            DirectoryIndexEntry[] slice = result[..folders];
            int sign = ascending ? 1 : -1;
            Array.Sort(order, (a, b) =>
            {
                int bySize = keys[a].CompareTo(keys[b]);
                return sign * (bySize != 0 ? bySize : CompareNames(slice[a].Name, slice[b].Name));
            });
            for (int i = 0; i < folders; i++)
            {
                result[i] = slice[order[i]];
            }
        }

        /// <summary>The comparison used for one group (both entries are folders, or both are files).</summary>
        public static IComparer<DirectoryIndexEntry> ComparerFor(EntryOrder order, bool ascending) => order switch
        {
            EntryOrder.Size => ascending ? SizeComparer.Ascending : SizeComparer.Descending,
            EntryOrder.DateModified => ascending ? DateComparer.Ascending : DateComparer.Descending,
            EntryOrder.DateCreated => ascending ? DateComparer.CreatedAscending : DateComparer.CreatedDescending,
            EntryOrder.DateAccessed => ascending ? DateComparer.AccessedAscending : DateComparer.AccessedDescending,
            EntryOrder.Type => ascending ? TypeComparer.Ascending : TypeComparer.Descending,
            _ => ascending ? NameComparer.Ascending : NameComparer.Descending
        };

        /// <summary>Sorts a slice of an array, in parallel slices and merges when it is large.</summary>
        public static void SortRange<T>(T[] array, int start, int length, IComparer<T> comparer)
        {
            if (length < 2)
            {
                return;
            }

            int slices = Math.Min(Environment.ProcessorCount, 8);
            if (length < ParallelThreshold || slices < 2)
            {
                Array.Sort(array, start, length, comparer);
                return;
            }

            // Sort the slices side by side, then merge neighbours pass by pass (each pass in parallel too).
            int[] bounds = new int[slices + 1];
            for (int s = 0; s <= slices; s++)
            {
                bounds[s] = start + (int)((long)length * s / slices);
            }

            Parallel.For(0, slices, s => Array.Sort(array, bounds[s], bounds[s + 1] - bounds[s], comparer));

            // A plain array, freed with the sort. Not ArrayPool.Shared: it keeps large rented arrays for reuse and trims
            // them only under memory pressure, so a merge buffer of a big folder would stay for the session.
            MergeRuns(array, new T[array.Length], bounds, start, length, comparer);
        }

        /// <summary>Merges the sorted slices between <paramref name="bounds"/> pass by pass, ending with the result in <paramref name="array"/>.</summary>
        private static void MergeRuns<T>(T[] array, T[] buffer, int[] bounds, int start, int length, IComparer<T> comparer)
        {
            T[] source = array;
            T[] target = buffer;
            var runs = new List<int>(bounds);
            while (runs.Count > 2)
            {
                var merged = new List<int> { runs[0] };
                int pairs = (runs.Count - 1) / 2;
                T[] from = source;
                T[] to = target;
                List<int> current = runs;
                Parallel.For(0, pairs, p =>
                {
                    int lo = current[2 * p];
                    int mid = current[2 * p + 1];
                    int hi = current[2 * p + 2];
                    Merge(from, lo, mid, hi, to, comparer);
                });

                for (int p = 0; p < pairs; p++)
                {
                    merged.Add(runs[2 * p + 2]);
                }

                if ((runs.Count - 1) % 2 == 1)
                {
                    // An odd run out: carried over unchanged.
                    int lo = runs[runs.Count - 2];
                    int hi = runs[runs.Count - 1];
                    Array.Copy(source, lo, target, lo, hi - lo);
                    merged.Add(hi);
                }

                runs = merged;
                (source, target) = (target, source);
            }

            if (!ReferenceEquals(source, array))
            {
                Array.Copy(source, start, array, start, length);
            }
        }

        private static void Merge<T>(T[] from, int lo, int mid, int hi, T[] to, IComparer<T> comparer)
        {
            int i = lo;
            int j = mid;
            int k = lo;
            while (i < mid && j < hi)
            {
                to[k++] = comparer.Compare(from[j], from[i]) < 0 ? from[j++] : from[i++];
            }

            while (i < mid)
            {
                to[k++] = from[i++];
            }

            while (j < hi)
            {
                to[k++] = from[j++];
            }
        }

        private static int CompareNames(string a, string b) => NaturalStringComparer.Instance.Compare(a, b);

        /// <summary>
        /// Sorts a group by tag key: each entry's key is computed once and sorted along with it (entries are structs, so
        /// a key lookup by entry during comparisons is not possible), tagged items in key order, untagged ones after them
        /// whichever the direction (they are not "smaller" than a tag), ties by natural name.
        /// </summary>
        private static void SortByTags(DirectoryIndexEntry[] array, int start, int length, Func<DirectoryIndexEntry, string> tagKey, bool ascending)
        {
            if (length < 2)
            {
                return;
            }

            var keyed = new KeyedEntry[length];
            for (int i = 0; i < length; i++)
            {
                DirectoryIndexEntry entry = array[start + i];
                keyed[i] = new KeyedEntry(tagKey?.Invoke(entry) ?? string.Empty, entry);
            }

            SortRange(keyed, 0, length, ascending ? TagComparer.Ascending : TagComparer.Descending);
            for (int i = 0; i < length; i++)
            {
                array[start + i] = keyed[i].Entry;
            }
        }

        private readonly record struct KeyedEntry(string Key, DirectoryIndexEntry Entry);

        private sealed class TagComparer : IComparer<KeyedEntry>
        {
            public static readonly TagComparer Ascending = new TagComparer(1);
            public static readonly TagComparer Descending = new TagComparer(-1);
            private readonly int _sign;

            private TagComparer(int sign) => _sign = sign;

            public int Compare(KeyedEntry a, KeyedEntry b)
            {
                if (a.Key.Length == 0 || b.Key.Length == 0)
                {
                    if (a.Key.Length != b.Key.Length)
                    {
                        return a.Key.Length == 0 ? 1 : -1;
                    }
                }
                else
                {
                    int byTag = string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
                    if (byTag != 0)
                    {
                        return _sign * byTag;
                    }
                }

                return CompareNames(a.Entry.Name, b.Entry.Name);
            }
        }

        private sealed class NameComparer : IComparer<DirectoryIndexEntry>
        {
            public static readonly NameComparer Ascending = new NameComparer(1);
            public static readonly NameComparer Descending = new NameComparer(-1);
            private readonly int _sign;

            private NameComparer(int sign) => _sign = sign;

            public int Compare(DirectoryIndexEntry a, DirectoryIndexEntry b) => _sign * CompareNames(a.Name, b.Name);
        }

        private sealed class SizeComparer : IComparer<DirectoryIndexEntry>
        {
            public static readonly SizeComparer Ascending = new SizeComparer(1);
            public static readonly SizeComparer Descending = new SizeComparer(-1);
            private readonly int _sign;

            private SizeComparer(int sign) => _sign = sign;

            public int Compare(DirectoryIndexEntry a, DirectoryIndexEntry b)
            {
                int result = a.SizeBytes.CompareTo(b.SizeBytes);
                return _sign * (result != 0 ? result : CompareNames(a.Name, b.Name));
            }
        }

        /// <summary>By one of the three times (modified, created, accessed), then by name.</summary>
        private sealed class DateComparer : IComparer<DirectoryIndexEntry>
        {
            public static readonly DateComparer Ascending = new DateComparer(1, 0);
            public static readonly DateComparer Descending = new DateComparer(-1, 0);
            public static readonly DateComparer CreatedAscending = new DateComparer(1, 1);
            public static readonly DateComparer CreatedDescending = new DateComparer(-1, 1);
            public static readonly DateComparer AccessedAscending = new DateComparer(1, 2);
            public static readonly DateComparer AccessedDescending = new DateComparer(-1, 2);
            private readonly int _sign;
            private readonly int _which;

            private DateComparer(int sign, int which)
            {
                _sign = sign;
                _which = which;
            }

            private DateTime Of(in DirectoryIndexEntry entry) => _which switch
            {
                1 => entry.CreationTimeUtc,
                2 => entry.LastAccessTimeUtc,
                _ => entry.LastWriteTimeUtc
            };

            public int Compare(DirectoryIndexEntry a, DirectoryIndexEntry b)
            {
                int result = Of(a).CompareTo(Of(b));
                return _sign * (result != 0 ? result : CompareNames(a.Name, b.Name));
            }
        }

        /// <summary>By extension (folders have none), case-insensitively, then by name.</summary>
        private sealed class TypeComparer : IComparer<DirectoryIndexEntry>
        {
            public static readonly TypeComparer Ascending = new TypeComparer(1);
            public static readonly TypeComparer Descending = new TypeComparer(-1);
            private readonly int _sign;

            private TypeComparer(int sign) => _sign = sign;

            public int Compare(DirectoryIndexEntry a, DirectoryIndexEntry b)
            {
                ReadOnlySpan<char> extensionA = a.IsDirectory ? default : Path.GetExtension(a.Name.AsSpan());
                ReadOnlySpan<char> extensionB = b.IsDirectory ? default : Path.GetExtension(b.Name.AsSpan());
                int result = extensionA.CompareTo(extensionB, StringComparison.OrdinalIgnoreCase);
                return _sign * (result != 0 ? result : CompareNames(a.Name, b.Name));
            }
        }
    }
}
