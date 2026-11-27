// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// One entry in the bounded, rolling recent-searches log (see
    /// AnalyticsSettings.MaxLogEntries) — feeds the dashboard's "Recent
    /// searches" list. The running total search count lives separately
    /// on UserAnalyticsData and is unaffected by this log's cap.
    /// </summary>
    public sealed class SearchLogEntry
    {
        public string Query { get; set; }
        public int ResultCount { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
