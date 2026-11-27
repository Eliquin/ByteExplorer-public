// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// Everything persisted to analytics.json — one JSON object,
    /// written and read by UserAnalyticsService only. Never read or
    /// written directly by any ViewModel; go through
    /// IUserAnalyticsService's Track*/GetSummary methods instead, which
    /// own the locking, capping, and eviction rules that keep this
    /// bounded regardless of how long or how heavily the app is used.
    ///
    /// SchemaVersion exists from day one, matching AppSettings' own
    /// reasoning — the hook a future migration step needs if a field is
    /// ever renamed or restructured, so an existing analytics.json from
    /// an older version doesn't just silently lose data.
    /// </summary>
    public sealed class UserAnalyticsData
    {
        public int SchemaVersion { get; set; } = 1;

        public DateTime FirstUseUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Keyed by full folder path, with the OS's path comparer (case-insensitive on Windows and macOS, case-sensitive on
        /// Linux, where "Docs" and "docs" are two folders). It used to be case-insensitive everywhere, which merged two
        /// Linux folders into one entry until the next start rebuilt the dictionary.
        /// </summary>
        public Dictionary<string, FolderUsageStat> Folders { get; set; } = new Dictionary<string, FolderUsageStat>(PathUtil.Comparer);

        /// <summary>Keyed by lowercased extension including the leading dot (e.g. ".jpg"), or "(none)".</summary>
        public Dictionary<string, FileTypeUsageStat> FileTypes { get; set; } = new Dictionary<string, FileTypeUsageStat>(StringComparer.OrdinalIgnoreCase);

        public List<SearchLogEntry> RecentSearches { get; set; } = new List<SearchLogEntry>();

        /// <summary>
        /// Bounded, rolling log of individual file-open events (path,
        /// name, timestamp) — distinct from FileTypes above, which only
        /// tracks aggregate counts per extension with no per-file
        /// identity. Feeds the Home dashboard's "Recent videos/music/
        /// documents" sections.
        /// </summary>
        public List<RecentFileOpenEntry> RecentFileOpens { get; set; } = new List<RecentFileOpenEntry>();

        public List<FileOperationLogEntry> RecentOperations { get; set; } = new List<FileOperationLogEntry>();

        public List<AppSessionRecord> Sessions { get; set; } = new List<AppSessionRecord>();

        /// <summary>Keyed by FileListViewMode.ToString() (e.g. "Details", "Icons").</summary>
        public Dictionary<string, int> ViewModeUsage { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Index 0 = midnight-1am local time, ... index 23 = 11pm-midnight. Always exactly 24 entries.</summary>
        public int[] HourlyActivityCounts { get; set; } = new int[24];

        /// <summary>Index matches System.DayOfWeek (0 = Sunday ... 6 = Saturday). Always exactly 7 entries.</summary>
        public int[] DayOfWeekActivityCounts { get; set; } = new int[7];

        // Running totals — these never shrink, unlike the rolling logs
        // above, which are capped. This is what "total folders visited
        // since you started using ByteExplorer" means even after the
        // per-folder detail behind an old visit has long since been
        // evicted from Folders by MaxTrackedFolders.
        public long TotalFolderVisits { get; set; }
        public long TotalFilesOpened { get; set; }
        public long TotalSearches { get; set; }
        public long TotalFileOperationsSucceeded { get; set; }
        public long TotalFileOperationsFailed { get; set; }
        public long TotalFoldersCreated { get; set; }
        public long TotalItemsRenamed { get; set; }
        public long TotalNetworkOperations { get; set; }
        public long TotalNetworkTimeouts { get; set; }
    }
}
