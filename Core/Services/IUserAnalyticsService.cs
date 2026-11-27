// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using ByteExplorer.Core.Analytics;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Local-only usage analytics: every Track* method is a cheap,
    /// never-throwing, fire-and-forget call safe to make from the UI
    /// thread on any hot path in the app — none of them perform disk
    /// I/O synchronously (writes are debounced to a background timer;
    /// see UserAnalyticsService). GetSummary is the one read path,
    /// intended to be called once when the Home dashboard opens or
    /// refreshes, not repeatedly.
    /// </summary>
    public interface IUserAnalyticsService : IDisposable
    {
        /// <summary>Mirrors AnalyticsSettings.EnableUsageAnalytics — every Track* call is a no-op while this is false.</summary>
        bool IsEnabled { get; }

        /// <summary>Call once per app run, as early as practical after settings have loaded.</summary>
        void StartSession();

        /// <summary>Call once per app run, during shutdown. Flushes synchronously — this is the one place a delay is acceptable, since the process is about to exit anyway.</summary>
        void EndSession();

        void TrackFolderVisited(string path);

        /// <summary>
        /// fullPath, not just an extension — the extension is derived
        /// internally for the existing aggregate FileTypes tracking, and
        /// the full path/name is additionally logged to the bounded
        /// RecentFileOpens history that feeds the Home dashboard's
        /// "Recent videos/music/documents" sections. Callers no longer
        /// compute the extension themselves before calling this.
        /// </summary>
        void TrackFileOpened(string fullPath);
        void TrackSearchPerformed(string query, int resultCount);
        void TrackFileOperation(string operationType, int successCount, int failureCount, bool wasCancelled);
        void TrackFolderCreated();
        void TrackItemRenamed(bool success);

        /// <summary>An item was renamed or moved: stored folder and file paths at or under the old path follow it.</summary>
        void TrackPathMoved(string fromPath, string toPath);
        void TrackViewModeChanged(string viewModeName);

        /// <summary>Call for every network-path folder listing attempt (tree expand or center-pane open), success or not — timedOut distinguishes the two.</summary>
        void TrackNetworkOperation(bool timedOut);

        /// <summary>
        /// Lightweight alternative to GetSummary for the left sidebar's
        /// "Frequent folders" section — see UserAnalyticsService's own
        /// remarks on why this is a separate method rather than reusing
        /// GetSummary with a small topFolderCount.
        /// </summary>
        IReadOnlyList<FavoriteFolderInsight> GetTopFolders(int count = 3);

        /// <summary>
        /// Computes and returns everything the Home dashboard needs in
        /// one snapshot. Safe to call from the UI thread — the
        /// computation is CPU-only (no disk I/O) and bounded by however
        /// much data is currently tracked (capped by AnalyticsSettings),
        /// but it does briefly hold an internal lock shared with every
        /// Track* call, so avoid calling this in a tight loop.
        /// </summary>
        AnalyticsSummary GetSummary(int topFolderCount = 8, int topFileTypeCount = 8, int recentSearchCount = 10, int recentFileOpenCount = 200, int recentlyVisitedFolderCount = 20);

        /// <summary>
        /// Permanently erases all collected analytics data, both in
        /// memory and on disk, and starts a fresh session so ongoing
        /// use continues to be tracked (if enabled). Cannot be undone.
        /// </summary>
        void ClearAllData();
    }
}
