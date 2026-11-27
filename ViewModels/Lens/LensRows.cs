// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Threading;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.Search;
using ByteSearcher;

namespace ByteExplorer.ViewModels.Lens
{
    /// <summary>A time group's heading row in a lens ("This afternoon", "Last week", "March 2025") with its file count.</summary>
    public sealed class LensGroupHeader
    {
        public LensGroupHeader(string label, int count)
        {
            Label = label;
            Count = count;
        }

        public string Label { get; }
        public int Count { get; }
        public string CountText => Count == 1 ? "1 file" : $"{Count:N0} files";
    }

    /// <summary>One bar of the time river: a month (or, further back, a year) and how many of the lens's files changed in it.</summary>
    public sealed class LensRiverBucket : ViewModelBase
    {
        /// <summary>The longest bar (the busiest period), in pixels; the others in proportion, at least a sliver.</summary>
        public const double MaxBarWidth = 40;

        private bool _isCurrent;

        public LensRiverBucket(string label, string tip, int count, double fraction, int firstRow)
        {
            Label = label;
            Tip = tip;
            Count = count;
            Fraction = fraction;
            FirstRow = firstRow;
        }

        /// <summary>"Jun", or a year ("2024") for older periods.</summary>
        public string Label { get; }

        /// <summary>"June 2026: 412 files".</summary>
        public string Tip { get; }

        public int Count { get; }

        /// <summary>0..1 of the busiest bar (the bar's length).</summary>
        public double Fraction { get; }

        /// <summary>The list row where this period starts (its group heading or first file), or -1 when it has none.</summary>
        public int FirstRow { get; }

        /// <summary>The bar's length: in proportion to the busiest period; a sliver for an empty one.</summary>
        public double BarWidth => Count == 0 ? 3 : Math.Max(6, Math.Round(Fraction * MaxBarWidth));

        /// <summary>A period with files (an empty one can't be jumped to).</summary>
        public bool HasFiles => FirstRow >= 0;

        /// <summary>The period the list's top row is in (drawn in the accent color).</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }
    }

    /// <summary>
    /// The time layout of a lens's results (newest first): where each time group starts and the river's buckets, worked
    /// out in one pass over the modification times (no paths built), off the UI thread.
    /// </summary>
    public sealed class LensLayout
    {
        private const int RiverMonths = 12;
        private const int RiverYears = 6;

        private LensLayout(int[] groupStarts, string[] groupLabels, int itemCount, IReadOnlyList<LensRiverBucket> river)
        {
            GroupStarts = groupStarts;
            GroupLabels = groupLabels;
            ItemCount = itemCount;
            River = river;
        }

        /// <summary>The first item index of each group, ascending.</summary>
        public int[] GroupStarts { get; }

        public string[] GroupLabels { get; }

        public int ItemCount { get; }

        public IReadOnlyList<LensRiverBucket> River { get; }

        public int RowCount => ItemCount + GroupStarts.Length;

        public static LensLayout Empty { get; } = new LensLayout(Array.Empty<int>(), Array.Empty<string>(), 0, Array.Empty<LensRiverBucket>());

        /// <summary>The layout of <paramref name="count"/> results whose times <paramref name="modifiedUtcAt"/> gives (newest first).</summary>
        public static LensLayout Build(int count, Func<int, DateTime> modifiedUtcAt, DateTime nowLocal, CancellationToken token = default)
        {
            if (count <= 0)
            {
                return Empty;
            }

            var starts = new List<int>();
            var labels = new List<string>();
            DateTime thisMonth = new DateTime(nowLocal.Year, nowLocal.Month, 1);

            // River: the last twelve months one bar each, then older years one bar each.
            var monthCounts = new int[RiverMonths];
            var monthFirst = new int[RiverMonths];
            var yearCounts = new int[RiverYears];
            var yearFirst = new int[RiverYears];
            Array.Fill(monthFirst, -1);
            Array.Fill(yearFirst, -1);
            int firstYearBucket = thisMonth.AddMonths(-RiverMonths).Year; // the year of the month just before the twelve

            string previous = null;
            for (int i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                DateTime local = modifiedUtcAt(i).ToLocalTime();
                string label = GroupLabel(local, nowLocal);
                if (!string.Equals(label, previous, StringComparison.Ordinal))
                {
                    starts.Add(i);
                    labels.Add(label);
                    previous = label;
                }

                int monthsAgo = (thisMonth.Year - local.Year) * 12 + thisMonth.Month - local.Month;
                if (monthsAgo < RiverMonths)
                {
                    int m = Math.Max(0, monthsAgo); // a date in the future (a wrong clock) counts as this month
                    monthCounts[m]++;
                    if (monthFirst[m] < 0)
                    {
                        monthFirst[m] = i;
                    }
                }
                else
                {
                    int y = Math.Min(RiverYears - 1, Math.Max(0, firstYearBucket - local.Year));
                    yearCounts[y]++;
                    if (yearFirst[y] < 0)
                    {
                        yearFirst[y] = i;
                    }
                }
            }

            int[] groupStarts = starts.ToArray();
            int max = 1;
            foreach (int c in monthCounts)
            {
                max = Math.Max(max, c);
            }

            foreach (int c in yearCounts)
            {
                max = Math.Max(max, c);
            }

            var river = new List<LensRiverBucket>(RiverMonths + RiverYears);
            for (int m = 0; m < RiverMonths; m++)
            {
                DateTime month = thisMonth.AddMonths(-m);
                river.Add(new LensRiverBucket(month.ToString("MMM", CultureInfo.CurrentCulture), $"{month:MMMM yyyy}: {Files(monthCounts[m])}",
                    monthCounts[m], (double)monthCounts[m] / max, RowOf(monthFirst[m], groupStarts)));
            }

            // Older years only where they hold files (an empty decade of bars says nothing), the last one "and before".
            int lastYear = -1;
            for (int y = RiverYears - 1; y >= 0; y--)
            {
                if (yearCounts[y] > 0)
                {
                    lastYear = y;
                    break;
                }
            }

            for (int y = 0; y <= lastYear; y++)
            {
                int year = firstYearBucket - y;
                bool earlier = y == RiverYears - 1;
                river.Add(new LensRiverBucket(earlier ? "Older" : year.ToString(CultureInfo.CurrentCulture),
                    (earlier ? $"{year} and before" : year.ToString(CultureInfo.CurrentCulture)) + ": " + Files(yearCounts[y]),
                    yearCounts[y], (double)yearCounts[y] / max, RowOf(yearFirst[y], groupStarts)));
            }

            return new LensLayout(groupStarts, labels.ToArray(), count, river);
        }

        private static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

        /// <summary>The row of item <paramref name="item"/>: its group's heading when it is the group's first item.</summary>
        private static int RowOf(int item, int[] groupStarts)
        {
            if (item < 0)
            {
                return -1;
            }

            int group = GroupOf(item, groupStarts);
            return groupStarts[group] == item ? groupStarts[group] + group : item + group + 1;
        }

        internal static int GroupOf(int item, int[] groupStarts)
        {
            int index = Array.BinarySearch(groupStarts, item);
            return index >= 0 ? index : ~index - 1;
        }

        /// <summary>
        /// The heading a time falls under, as people say it: today split into morning, afternoon and evening, then
        /// yesterday, earlier this week, last week, earlier this month, the months of this year, then month and year.
        /// </summary>
        public static string GroupLabel(DateTime local, DateTime nowLocal)
        {
            DateTime today = nowLocal.Date;
            if (local >= today)
            {
                return local.Hour < 12 ? "This morning" : local.Hour < 18 ? "This afternoon" : "This evening";
            }

            if (local >= today.AddDays(-1))
            {
                return "Yesterday";
            }

            int fromMonday = ((int)today.DayOfWeek + 6) % 7;
            DateTime weekStart = today.AddDays(-fromMonday);
            if (local >= weekStart)
            {
                return "Earlier this week";
            }

            if (local >= weekStart.AddDays(-7))
            {
                return "Last week";
            }

            var monthStart = new DateTime(today.Year, today.Month, 1);
            if (local >= monthStart)
            {
                return "Earlier this month";
            }

            if (local.Year == today.Year)
            {
                return local.ToString("MMMM", CultureInfo.CurrentCulture);
            }

            return local.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// A lens's rows for the list: each time group's heading followed by its files, built on demand from the results
    /// (a million photos cost two arrays of group starts, not a million row objects). Never changes; a new lens or
    /// filter makes a new list.
    /// </summary>
    public sealed class LensRowList : IList, IReadOnlyList<object>, INotifyCollectionChanged
    {
        private readonly SearchResultList _items;
        private readonly LensLayout _layout;
        private readonly LensGroupHeader[] _headers;

        public LensRowList(SearchResults results, LensLayout layout)
        {
            _items = new SearchResultList(results);
            _layout = layout ?? LensLayout.Empty;
            _headers = new LensGroupHeader[_layout.GroupStarts.Length];
        }

        public static LensRowList Empty { get; } = new LensRowList(SearchResults.Empty, LensLayout.Empty);

        public LensLayout Layout => _layout;

        /// <summary>The files alone (selection, open, drag work on these).</summary>
        public SearchResultList Items => _items;

        public int Count => Math.Min(_layout.RowCount, _items.Count + _layout.GroupStarts.Length);

        public object this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                int[] starts = _layout.GroupStarts;
                // The group of a row: the last group whose heading row (start + group) is at or before it.
                int low = 0, high = starts.Length - 1, group = 0;
                while (low <= high)
                {
                    int mid = (low + high) >> 1;
                    if (starts[mid] + mid <= index)
                    {
                        group = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                int headerRow = starts[group] + group;
                if (index == headerRow)
                {
                    return _headers[group] ??= new LensGroupHeader(_layout.GroupLabels[group], GroupSize(group));
                }

                return _items[index - group - 1];
            }
        }

        /// <summary>The height of a heading row and of a file row (the view draws them at these sizes).</summary>
        public const double HeadingHeight = 40;
        public const double ItemHeight = 36;

        private double[] _groupOffsets;

        /// <summary>Where each group's heading starts, from the top of the first row (worked out once).</summary>
        private double[] GroupOffsets()
        {
            if (_groupOffsets != null)
            {
                return _groupOffsets;
            }

            int[] starts = _layout.GroupStarts;
            var offsets = new double[starts.Length];
            double y = 0;
            for (int g = 0; g < starts.Length; g++)
            {
                offsets[g] = y;
                y += HeadingHeight + GroupSize(g) * ItemHeight;
            }

            return _groupOffsets = offsets;
        }

        /// <summary>The top of a row, from the top of the first row (rows have fixed heights: no layout needed).</summary>
        public double OffsetOfRow(int row)
        {
            int group = GroupOfRow(row);
            if (group < 0)
            {
                return 0;
            }

            int headerRow = _layout.GroupStarts[group] + group;
            return GroupOffsets()[group] + (row == headerRow ? 0 : HeadingHeight + (row - headerRow - 1) * ItemHeight);
        }

        /// <summary>The row at a distance from the top of the first row (the row the list's top edge is on).</summary>
        public int RowAtOffset(double y)
        {
            double[] offsets = GroupOffsets();
            if (offsets.Length == 0)
            {
                return -1;
            }

            int index = Array.BinarySearch(offsets, Math.Max(0, y));
            int group = index >= 0 ? index : Math.Max(0, ~index - 1);
            int headerRow = _layout.GroupStarts[group] + group;
            double within = Math.Max(0, y) - offsets[group];
            if (within < HeadingHeight)
            {
                return headerRow;
            }

            int item = (int)((within - HeadingHeight) / ItemHeight);
            return Math.Min(Count - 1, Math.Min(headerRow + 1 + item, headerRow + GroupSize(group)));
        }

        /// <summary>The row of a file (by its index among the results).</summary>
        public int RowOfItem(int item) => item < 0 ? -1 : item + LensLayout.GroupOf(item, _layout.GroupStarts) + 1;

        /// <summary>The group a row belongs to (headings belong to their own group); -1 for none.</summary>
        public int GroupOfRow(int row)
        {
            if (row < 0 || row >= Count || _layout.GroupStarts.Length == 0)
            {
                return -1;
            }

            int[] starts = _layout.GroupStarts;
            int group = 0;
            for (int low = 0, high = starts.Length - 1; low <= high;)
            {
                int mid = (low + high) >> 1;
                if (starts[mid] + mid <= row)
                {
                    group = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return group;
        }

        private int GroupSize(int group)
        {
            int[] starts = _layout.GroupStarts;
            int end = group + 1 < starts.Length ? starts[group + 1] : _layout.ItemCount;
            return end - starts[group];
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged { add { } remove { } }

        object IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => true;
        object ICollection.SyncRoot => this;
        bool ICollection.IsSynchronized => false;

        int IList.Add(object value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object value) => ((IList)this).IndexOf(value) >= 0;

        int IList.IndexOf(object value)
        {
            if (value is SearchResultRow row && ReferenceEquals(row.Owner, _items))
            {
                return RowOfItem(row.Index);
            }

            if (value is LensGroupHeader header)
            {
                int group = Array.IndexOf(_headers, header);
                return group < 0 ? -1 : _layout.GroupStarts[group] + group;
            }

            return -1;
        }

        void IList.Insert(int index, object value) => throw new NotSupportedException();
        void IList.Remove(object value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator<object> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
