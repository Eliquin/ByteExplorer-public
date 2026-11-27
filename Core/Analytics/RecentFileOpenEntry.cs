// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// One entry in the bounded, rolling recent-file-opens log (see
    /// AnalyticsSettings.MaxLogEntries) — feeds the Home dashboard's
    /// "Recent videos/music/documents" sections. Unlike FileTypeUsageStat
    /// (aggregate counts per extension, with no per-file identity),
    /// this captures the specific file that was opened and when, so the
    /// dashboard can show "these particular files," not just "you've
    /// opened 45 .mp4 files total."
    /// </summary>
    public sealed class RecentFileOpenEntry
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
