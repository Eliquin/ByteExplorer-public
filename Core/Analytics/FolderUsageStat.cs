// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// Running usage totals for one folder path. Deliberately a single
    /// aggregated record per folder rather than a list of individual
    /// visit timestamps — a power user opening the same folder
    /// thousands of times over months would otherwise make this
    /// unboundedly large for no analytical benefit; VisitCount plus the
    /// first/last timestamps is everything the dashboard and the
    /// frecency ranking actually need.
    /// </summary>
    public sealed class FolderUsageStat
    {
        public string Path { get; set; }
        public int VisitCount { get; set; }
        public DateTime FirstVisitedUtc { get; set; }
        public DateTime LastVisitedUtc { get; set; }
    }
}
