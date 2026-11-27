// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// One favorite-folder entry, ranked by frecency (frequency +
    /// recency decay — see UserAnalyticsService.FrecencyScore). Score
    /// is exposed mainly for diagnostics/testing; the dashboard just
    /// renders the list in the order it's given.
    /// </summary>
    public sealed class FavoriteFolderInsight
    {
        public FavoriteFolderInsight(string fullPath, string displayName, int visitCount, DateTime lastVisitedUtc, double score)
        {
            FullPath = fullPath;
            DisplayName = displayName;
            VisitCount = visitCount;
            LastVisitedUtc = lastVisitedUtc;
            Score = score;
        }

        public string FullPath { get; }
        public string DisplayName { get; }
        public int VisitCount { get; }
        public DateTime LastVisitedUtc { get; }
        public double Score { get; }
    }

    /// <summary>One row of the file-type breakdown. PercentOfMax is 0-100, scaled against the single most-used type — ready to bind directly to a bar width.</summary>
    public sealed class FileTypeInsight
    {
        public FileTypeInsight(string extension, int openCount, double percentOfMax)
        {
            Extension = extension;
            OpenCount = openCount;
            PercentOfMax = percentOfMax;
        }

        public string Extension { get; }
        public int OpenCount { get; }
        public double PercentOfMax { get; }
    }

    /// <summary>One hour's activity level (0-23, local time). PercentOfMax is 0-100, scaled against the single busiest hour — ready to bind directly to a bar height.</summary>
    public sealed class HourlyActivityInsight
    {
        public HourlyActivityInsight(int hour, int count, double percentOfMax)
        {
            Hour = hour;
            Count = count;
            PercentOfMax = percentOfMax;
        }

        public int Hour { get; }
        public int Count { get; }
        public double PercentOfMax { get; }
    }

    /// <summary>One recent search, newest first.</summary>
    public sealed class RecentSearchInsight
    {
        public RecentSearchInsight(string query, int resultCount, DateTime timestampUtc)
        {
            Query = query;
            ResultCount = resultCount;
            TimestampUtc = timestampUtc;
        }

        public string Query { get; }
        public int ResultCount { get; }
        public DateTime TimestampUtc { get; }
    }

    /// <summary>
    /// One recently-opened file, newest first — the raw, unfiltered-by-
    /// category shape AnalyticsDashboardViewModel splits into "Recent
    /// videos/music/documents" (see its own remarks on why that split
    /// happens there, not in UserAnalyticsService).
    /// </summary>
    public sealed class RecentFileInsight
    {
        public RecentFileInsight(string fullPath, string name, DateTime timestampUtc)
        {
            FullPath = fullPath;
            Name = name;
            TimestampUtc = timestampUtc;
        }

        public string FullPath { get; }
        public string Name { get; }
        public DateTime TimestampUtc { get; }
    }

    /// <summary>
    /// Everything the Home dashboard needs, computed in one pass under
    /// a single brief lock by UserAnalyticsService.GetSummary — the
    /// dashboard makes exactly one call to build its entire view,
    /// rather than several separate calls each re-acquiring the lock
    /// and re-deriving overlapping numbers.
    ///
    /// All the small DTOs above exist purely to describe pieces of
    /// this one response shape and are never meaningfully used on
    /// their own — grouped into this single file as a deliberate,
    /// documented exception to this codebase's usual one-type-per-file
    /// convention, rather than the strict rule taken literally beyond
    /// the point it aids readability.
    /// </summary>
    public sealed class AnalyticsSummary
    {
        public AnalyticsSummary(
            bool isEnabled,
            DateTime firstUseUtc,
            int daysSinceFirstUse,
            long totalFolderVisits,
            int uniqueFoldersVisited,
            long totalFilesOpened,
            long totalSearches,
            long totalFileOperationsSucceeded,
            long totalFileOperationsFailed,
            double operationSuccessRatePercent,
            long totalFoldersCreated,
            long totalItemsRenamed,
            double networkReliabilityPercent,
            long totalNetworkOperations,
            long totalNetworkTimeouts,
            int mostActiveHour,
            string mostActiveDayOfWeek,
            TimeSpan averageSessionLength,
            int currentStreakDays,
            int longestStreakDays,
            TimeSpan totalUsageTime,
            int totalSessions,
            IReadOnlyList<FavoriteFolderInsight> favoriteFolders,
            IReadOnlyList<FileTypeInsight> fileTypeBreakdown,
            IReadOnlyList<HourlyActivityInsight> hourlyActivity,
            IReadOnlyList<RecentSearchInsight> recentSearches,
            IReadOnlyList<RecentFileInsight> recentFileOpens,
            IReadOnlyList<FavoriteFolderInsight> recentlyVisitedFolders)
        {
            IsEnabled = isEnabled;
            FirstUseUtc = firstUseUtc;
            DaysSinceFirstUse = daysSinceFirstUse;
            TotalFolderVisits = totalFolderVisits;
            UniqueFoldersVisited = uniqueFoldersVisited;
            TotalFilesOpened = totalFilesOpened;
            TotalSearches = totalSearches;
            TotalFileOperationsSucceeded = totalFileOperationsSucceeded;
            TotalFileOperationsFailed = totalFileOperationsFailed;
            OperationSuccessRatePercent = operationSuccessRatePercent;
            TotalFoldersCreated = totalFoldersCreated;
            TotalItemsRenamed = totalItemsRenamed;
            NetworkReliabilityPercent = networkReliabilityPercent;
            TotalNetworkOperations = totalNetworkOperations;
            TotalNetworkTimeouts = totalNetworkTimeouts;
            MostActiveHour = mostActiveHour;
            MostActiveDayOfWeek = mostActiveDayOfWeek;
            AverageSessionLength = averageSessionLength;
            CurrentStreakDays = currentStreakDays;
            LongestStreakDays = longestStreakDays;
            TotalUsageTime = totalUsageTime;
            TotalSessions = totalSessions;
            FavoriteFolders = favoriteFolders ?? Array.Empty<FavoriteFolderInsight>();
            FileTypeBreakdown = fileTypeBreakdown ?? Array.Empty<FileTypeInsight>();
            HourlyActivity = hourlyActivity ?? Array.Empty<HourlyActivityInsight>();
            RecentSearches = recentSearches ?? Array.Empty<RecentSearchInsight>();
            RecentFileOpens = recentFileOpens ?? Array.Empty<RecentFileInsight>();
            RecentlyVisitedFolders = recentlyVisitedFolders ?? Array.Empty<FavoriteFolderInsight>();
        }

        public bool IsEnabled { get; }
        public DateTime FirstUseUtc { get; }
        public int DaysSinceFirstUse { get; }

        public long TotalFolderVisits { get; }
        public int UniqueFoldersVisited { get; }
        public long TotalFilesOpened { get; }
        public long TotalSearches { get; }

        public long TotalFileOperationsSucceeded { get; }
        public long TotalFileOperationsFailed { get; }
        public double OperationSuccessRatePercent { get; }
        public long TotalFoldersCreated { get; }
        public long TotalItemsRenamed { get; }

        public double NetworkReliabilityPercent { get; }
        public long TotalNetworkOperations { get; }
        public long TotalNetworkTimeouts { get; }

        public int MostActiveHour { get; }
        public string MostActiveDayOfWeek { get; }
        public TimeSpan AverageSessionLength { get; }
        public int CurrentStreakDays { get; }
        public int LongestStreakDays { get; }
        public TimeSpan TotalUsageTime { get; }
        public int TotalSessions { get; }

        public IReadOnlyList<FavoriteFolderInsight> FavoriteFolders { get; }
        public IReadOnlyList<FileTypeInsight> FileTypeBreakdown { get; }
        public IReadOnlyList<HourlyActivityInsight> HourlyActivity { get; }
        public IReadOnlyList<RecentSearchInsight> RecentSearches { get; }
        public IReadOnlyList<RecentFileInsight> RecentFileOpens { get; }
        public IReadOnlyList<FavoriteFolderInsight> RecentlyVisitedFolders { get; }
    }
}
