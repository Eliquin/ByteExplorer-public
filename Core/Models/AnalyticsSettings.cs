// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Local-only usage analytics behavior. Nothing this section
    /// controls is ever transmitted anywhere — analytics.json lives
    /// next to the executable (same portable, no-AppData policy as
    /// settings.json) and is read only by this app's own Home
    /// dashboard. EnableUsageAnalytics is the person's off switch;
    /// when false, every tracking call becomes a cheap no-op and
    /// nothing new is written to disk.
    /// </summary>
    public sealed class AnalyticsSettings
    {
        /// <summary>
        /// Master on/off switch. When false, UserAnalyticsService's
        /// Track* methods all short-circuit immediately — no data is
        /// collected, in memory or on disk, until turned back on.
        /// Existing previously-collected data is left untouched (use
        /// the dashboard's "Clear data" action to remove it). Default:
        /// true.
        /// </summary>
        public bool EnableUsageAnalytics { get; set; } = true;

        /// <summary>
        /// Hard cap on distinct folder paths tracked. Once exceeded, the
        /// lowest-frecency (least frequently AND least recently visited)
        /// entries are evicted first — see UserAnalyticsService's
        /// FrecencyScore — so the folders that survive are always the
        /// most relevant ones, not just whichever were added first or
        /// most recently. Default: 500. Range: 50-5000.
        /// </summary>
        public int MaxTrackedFolders { get; set; } = 500;

        /// <summary>
        /// Hard cap on each rolling log (recent searches, recent file
        /// operations) — oldest entries drop off once exceeded. These
        /// feed the dashboard's "recent activity" lists, not the
        /// aggregate totals (which are separate running counters that
        /// never shrink). Default: 200. Range: 20-2000.
        /// </summary>
        public int MaxLogEntries { get; set; } = 200;

        /// <summary>
        /// Hard cap on session history entries retained (one per app
        /// run). Used for streak/average-session/total-usage-time
        /// insights. Default: 120 (roughly 4 months of daily use).
        /// Range: 10-2000.
        /// </summary>
        public int MaxSessionHistory { get; set; } = 120;

        /// <summary>
        /// How long a burst of tracked events must go quiet before
        /// analytics.json is actually written to disk — coalesces rapid
        /// activity (fast folder browsing) into one write instead of
        /// one per event, the same debounce principle used for live
        /// folder watching elsewhere in this app. A clean app shutdown
        /// always flushes immediately regardless of this value. Default:
        /// 5000. Range: 500-60000.
        /// </summary>
        public int SaveDebounceMs { get; set; } = 5000;

        /// <summary>
        /// Half-life, in days, used by the frecency (frequency +
        /// recency) score that ranks "Favorite folders" on the
        /// dashboard and decides which tracked folders survive
        /// MaxTrackedFolders eviction. A folder's contribution from past
        /// visits decays by half every this-many days — lower values
        /// favor what's been used recently; higher values favor
        /// folders with a long history of heavy use even if not
        /// touched in a while. Default: 14. Range: 1-365.
        /// </summary>
        public double FavoriteFolderHalfLifeDays { get; set; } = 14.0;

        public void Sanitize()
        {
            if (MaxTrackedFolders < 50)
            {
                MaxTrackedFolders = 50;
            }
            else if (MaxTrackedFolders > 5000)
            {
                MaxTrackedFolders = 5000;
            }

            if (MaxLogEntries < 20)
            {
                MaxLogEntries = 20;
            }
            else if (MaxLogEntries > 2000)
            {
                MaxLogEntries = 2000;
            }

            if (MaxSessionHistory < 10)
            {
                MaxSessionHistory = 10;
            }
            else if (MaxSessionHistory > 2000)
            {
                MaxSessionHistory = 2000;
            }

            if (SaveDebounceMs < 500)
            {
                SaveDebounceMs = 500;
            }
            else if (SaveDebounceMs > 60000)
            {
                SaveDebounceMs = 60000;
            }

            if (double.IsNaN(FavoriteFolderHalfLifeDays) || FavoriteFolderHalfLifeDays < 1.0)
            {
                FavoriteFolderHalfLifeDays = 1.0;
            }
            else if (FavoriteFolderHalfLifeDays > 365.0)
            {
                FavoriteFolderHalfLifeDays = 365.0;
            }
        }
    }
}
