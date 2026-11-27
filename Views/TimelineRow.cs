// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Globalization;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// One row of the Timeline view: a file, plus its date group's header when it is the first file of that group.
    /// <para>
    /// The WPF build gave every date group its own ListBox inside a plain ScrollViewer, which realized every row of every
    /// group (no virtualization) and needed code to merge the groups' selections, clear the other groups on a plain
    /// click, and forward the mouse wheel. Flattening the groups into one list of rows, with the header drawn above a
    /// group's first row, gives Timeline a single virtualized ListBox: one selection (Shift ranges and keyboard
    /// navigation cross group boundaries like a normal list), and only the visible rows exist.
    /// </para>
    /// </summary>
    public sealed class TimelineRow
    {
        private TimelineRow(FileListItemViewModel item, string header, int groupCount, bool isFirstGroup)
        {
            Item = item;
            Header = header;
            CountText = header == null ? null : string.Format(CultureInfo.CurrentCulture, "({0:N0})", groupCount);
            IsFirstGroup = isFirstGroup;
        }

        public FileListItemViewModel Item { get; }

        /// <summary>The group's header ("Today", "Last week", ...) on its first row; null on every other row.</summary>
        public string Header { get; }

        public string CountText { get; }

        public bool HasHeader => Header != null;

        /// <summary>The topmost group's header needs no gap above it.</summary>
        public bool IsFirstGroup { get; }

        /// <summary>Flattens the view model's groups, in order, into rows. Empty groups contribute nothing.</summary>
        public static List<TimelineRow> Flatten(IReadOnlyList<TimelineGroup> groups)
        {
            int total = 0;
            if (groups != null)
            {
                foreach (TimelineGroup group in groups)
                {
                    total += group.Count;
                }
            }

            var rows = new List<TimelineRow>(total);
            if (groups == null)
            {
                return rows;
            }

            foreach (TimelineGroup group in groups)
            {
                IReadOnlyList<FileListItemViewModel> items = group.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    rows.Add(i == 0
                        ? new TimelineRow(items[i], group.Header, items.Count, rows.Count == 0)
                        : new TimelineRow(items[i], null, 0, false));
                }
            }

            return rows;
        }
    }
}
