// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ByteExplorer.Core.Analytics;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One favorite-folder row — FullPath is what a click navigates to;
    /// everything else is display-ready.
    /// </summary>
    public sealed class FavoriteFolderRowViewModel
    {
        public FavoriteFolderRowViewModel(FavoriteFolderInsight insight)
        {
            FullPath = insight.FullPath;
            DisplayName = insight.DisplayName;
            VisitCount = insight.VisitCount;
            SubText = insight.VisitCount == 1 ? "1 visit" : $"{insight.VisitCount:N0} visits";
        }

        public string FullPath { get; }
        public string DisplayName { get; }
        public int VisitCount { get; }
        public string SubText { get; }
    }

    /// <summary>One file-type breakdown row — BarWidth is a pixel value (0-MaxBarWidthPx), ready to bind directly to a bar's Width.</summary>
    public sealed class FileTypeRowViewModel
    {
        private const double MaxBarWidthPx = 180.0;
        private const double MinBarWidthPx = 3.0; // a sliver even at zero, so every row's bar is visible

        public FileTypeRowViewModel(FileTypeInsight insight)
        {
            Extension = string.Equals(insight.Extension, "(none)", StringComparison.OrdinalIgnoreCase)
                ? "No extension"
                : insight.Extension;
            OpenCount = insight.OpenCount;
            BarWidth = Math.Max(MinBarWidthPx, insight.PercentOfMax / 100.0 * MaxBarWidthPx);
            ColorHex = FileIconGlyphs.ColorHexFor("x" + insight.Extension, isDirectory: false);
        }

        public string Extension { get; }
        public int OpenCount { get; }
        public double BarWidth { get; }
        public string ColorHex { get; }
    }

    /// <summary>One hour of the 24-bar activity chart — BarHeight is a pixel value ready to bind directly to a Rectangle's Height.</summary>
    public sealed class HourBarViewModel
    {
        private const double MaxBarHeightPx = 56.0;
        private const double MinBarHeightPx = 2.0; // a sliver even at zero, so the row of 24 bars still reads as a baseline

        public HourBarViewModel(HourlyActivityInsight insight)
        {
            Hour = insight.Hour;
            Count = insight.Count;
            BarHeight = Math.Max(MinBarHeightPx, insight.PercentOfMax / 100.0 * MaxBarHeightPx);
            ToolTipText = $"{FormatHour(insight.Hour)} — {insight.Count:N0} activit{(insight.Count == 1 ? "y" : "ies")}";
        }

        public int Hour { get; }
        public int Count { get; }
        public double BarHeight { get; }
        public string ToolTipText { get; }

        private static string FormatHour(int hour)
        {
            return new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>One recent-search row.</summary>
    public sealed class RecentSearchRowViewModel
    {
        public RecentSearchRowViewModel(RecentSearchInsight insight)
        {
            Query = insight.Query;
            SubText = $"{insight.ResultCount:N0} result{(insight.ResultCount == 1 ? string.Empty : "s")} · {FormatRelativeTime(insight.TimestampUtc)}";
        }

        public string Query { get; }
        public string SubText { get; }

        private static string FormatRelativeTime(DateTime utc)
        {
            TimeSpan age = DateTime.UtcNow - utc;
            if (age < TimeSpan.Zero)
            {
                return "just now";
            }

            if (age.TotalMinutes < 1)
            {
                return "just now";
            }

            if (age.TotalMinutes < 60)
            {
                return $"{(int)age.TotalMinutes}m ago";
            }

            if (age.TotalHours < 24)
            {
                return $"{(int)age.TotalHours}h ago";
            }

            return $"{(int)age.TotalDays}d ago";
        }
    }

    /// <summary>
    /// One recent-file row — shared by the "Recent videos", "Latest
    /// music", and "Latest documents" sections (the category split
    /// itself happens in AnalyticsDashboardViewModel's own constructor,
    /// via FileCategoryClassifier — see its remarks there). FullPath is
    /// what a click opens.
    /// </summary>
    public sealed class RecentFileRowViewModel
    {
        public RecentFileRowViewModel(RecentFileInsight insight)
        {
            FullPath = insight.FullPath;
            Name = insight.Name;
            SubText = FormatRelativeTime(insight.TimestampUtc);
        }

        public string FullPath { get; }
        public string Name { get; }
        public string SubText { get; }

        private static string FormatRelativeTime(DateTime utc)
        {
            TimeSpan age = DateTime.UtcNow - utc;
            if (age < TimeSpan.Zero || age.TotalMinutes < 1)
            {
                return "just now";
            }

            if (age.TotalMinutes < 60)
            {
                return $"{(int)age.TotalMinutes}m ago";
            }

            if (age.TotalHours < 24)
            {
                return $"{(int)age.TotalHours}h ago";
            }

            return $"{(int)age.TotalDays}d ago";
        }
    }

    /// <summary>
    /// Wraps one AnalyticsSummary snapshot with everything the Home
    /// dashboard's XAML binds to directly — plain display strings and
    /// bar-ready numeric values, so the view stays free of value
    /// converters or inline formatting logic. Immutable: a refresh
    /// creates a new instance (via FileListViewModel.LoadHomeAsync)
    /// rather than mutating this one in place, matching how
    /// AnalyticsSummary itself is a fresh snapshot every time it's
    /// requested.
    ///
    /// The several small row-model types above exist purely to shape
    /// pieces of this one dashboard and are never meaningfully used on
    /// their own — grouped into this one file as the same documented,
    /// deliberate exception to the one-type-per-file convention used
    /// for AnalyticsSummary's own DTOs in Core.Analytics.
    /// </summary>
    public sealed class AnalyticsDashboardViewModel
    {
        public AnalyticsDashboardViewModel(AnalyticsSummary summary)
        {
            IsEnabled = summary.IsEnabled;

            SinceText = summary.DaysSinceFirstUse <= 0
                ? "You started using ByteExplorer today"
                : summary.DaysSinceFirstUse == 1
                    ? "You've been using ByteExplorer for 1 day"
                    : $"You've been using ByteExplorer for {summary.DaysSinceFirstUse:N0} days";

            TotalFolderVisitsText = summary.TotalFolderVisits.ToString("N0", CultureInfo.CurrentCulture);
            UniqueFoldersText = summary.UniqueFoldersVisited.ToString("N0", CultureInfo.CurrentCulture);
            TotalFilesOpenedText = summary.TotalFilesOpened.ToString("N0", CultureInfo.CurrentCulture);
            TotalSearchesText = summary.TotalSearches.ToString("N0", CultureInfo.CurrentCulture);

            long totalOps = summary.TotalFileOperationsSucceeded + summary.TotalFileOperationsFailed;
            TotalOperationsText = totalOps.ToString("N0", CultureInfo.CurrentCulture);
            OperationSuccessRateText = totalOps > 0
                ? $"{summary.OperationSuccessRatePercent:N1}% succeeded"
                : "No operations yet";

            TotalFoldersCreatedText = summary.TotalFoldersCreated.ToString("N0", CultureInfo.CurrentCulture);
            TotalItemsRenamedText = summary.TotalItemsRenamed.ToString("N0", CultureInfo.CurrentCulture);

            NetworkReliabilityText = summary.TotalNetworkOperations > 0
                ? $"{summary.NetworkReliabilityPercent:N1}% reliable"
                : "No network activity yet";
            NetworkOperationsSubText = summary.TotalNetworkOperations > 0
                ? $"{summary.TotalNetworkTimeouts:N0} timeout{(summary.TotalNetworkTimeouts == 1 ? string.Empty : "s")} out of {summary.TotalNetworkOperations:N0} operation{(summary.TotalNetworkOperations == 1 ? string.Empty : "s")}"
                : "Nothing tracked yet — this fills in as you browse network drives";

            MostActiveHourText = summary.TotalSessions > 0 && summary.HourlyActivity.Any(h => h.Count > 0)
                ? FormatHour(summary.MostActiveHour)
                : "Not enough data yet";
            MostActiveDayText = string.IsNullOrEmpty(summary.MostActiveDayOfWeek) || summary.MostActiveDayOfWeek == "Unknown"
                ? "Not enough data yet"
                : summary.MostActiveDayOfWeek;

            CurrentStreakText = summary.CurrentStreakDays <= 0
                ? "No current streak"
                : summary.CurrentStreakDays == 1
                    ? "1 day"
                    : $"{summary.CurrentStreakDays:N0} days";
            LongestStreakSubText = summary.LongestStreakDays > summary.CurrentStreakDays
                ? $"Longest: {summary.LongestStreakDays:N0} day{(summary.LongestStreakDays == 1 ? string.Empty : "s")}"
                : summary.LongestStreakDays > 0
                    ? "This is your longest streak"
                    : string.Empty;

            AverageSessionText = FormatDuration(summary.AverageSessionLength);
            TotalUsageTimeText = FormatDuration(summary.TotalUsageTime);
            TotalSessionsText = summary.TotalSessions == 1 ? "1 session" : $"{summary.TotalSessions:N0} sessions";

            FavoriteFolders = summary.FavoriteFolders.Select(f => new FavoriteFolderRowViewModel(f)).ToList();
            HasFavoriteFolders = FavoriteFolders.Count > 0;

            FileTypes = summary.FileTypeBreakdown.Select(f => new FileTypeRowViewModel(f)).ToList();
            HasFileTypes = FileTypes.Count > 0;

            HourlyActivity = summary.HourlyActivity.Select(h => new HourBarViewModel(h)).ToList();
            HasAnyActivity = summary.HourlyActivity.Any(h => h.Count > 0);

            RecentSearches = summary.RecentSearches.Select(s => new RecentSearchRowViewModel(s)).ToList();
            HasRecentSearches = RecentSearches.Count > 0;

            // Category split happens here, not in UserAnalyticsService —
            // this view model already references FileCategoryClassifier
            // (ByteExplorer.Infrastructure) for other purposes, and
            // Core.Services deliberately doesn't take a dependency on
            // Infrastructure the other layer already avoids elsewhere.
            // Filtering the RAW, unfiltered-by-category
            // summary.RecentFileOpens by category first, THEN taking 20
            // of each, is also the only order that's actually correct:
            // taking the top 20 most recent opens overall and filtering
            // afterward could let a run of recently-opened documents
            // crowd out an occasional video that was genuinely opened
            // recently, just not recently enough to be in that first-20
            // window.
            RecentVideos = summary.RecentFileOpens
                .Where(f => FileCategoryClassifier.Matches(f.Name, isDirectory: false, FileCategoryFilter.Videos))
                .Take(20)
                .Select(f => new RecentFileRowViewModel(f))
                .ToList();
            HasRecentVideos = RecentVideos.Count > 0;

            RecentMusic = summary.RecentFileOpens
                .Where(f => FileCategoryClassifier.Matches(f.Name, isDirectory: false, FileCategoryFilter.Music))
                .Take(20)
                .Select(f => new RecentFileRowViewModel(f))
                .ToList();
            HasRecentMusic = RecentMusic.Count > 0;

            RecentDocuments = summary.RecentFileOpens
                .Where(f => FileCategoryClassifier.Matches(f.Name, isDirectory: false, FileCategoryFilter.Documents))
                .Take(20)
                .Select(f => new RecentFileRowViewModel(f))
                .ToList();
            HasRecentDocuments = RecentDocuments.Count > 0;

            // Already sorted-by-recency and capped at 20 by
            // UserAnalyticsService (RecentlyVisitedFolders is a
            // different query from FavoriteFolders above — recency, not
            // frecency) — no further filtering needed here, just
            // wrapped in the same row type FavoriteFolders already uses,
            // since the underlying shape (FullPath/DisplayName/
            // VisitCount/LastVisitedUtc) is identical.
            RecentFolders = summary.RecentlyVisitedFolders.Select(f => new FavoriteFolderRowViewModel(f)).ToList();
            HasRecentFolders = RecentFolders.Count > 0;
        }

        public bool IsEnabled { get; }
        public string SinceText { get; }

        public string TotalFolderVisitsText { get; }
        public string UniqueFoldersText { get; }
        public string TotalFilesOpenedText { get; }
        public string TotalSearchesText { get; }

        public string TotalOperationsText { get; }
        public string OperationSuccessRateText { get; }
        public string TotalFoldersCreatedText { get; }
        public string TotalItemsRenamedText { get; }

        public string NetworkReliabilityText { get; }
        public string NetworkOperationsSubText { get; }

        public string MostActiveHourText { get; }
        public string MostActiveDayText { get; }
        public string CurrentStreakText { get; }
        public string LongestStreakSubText { get; }
        public string AverageSessionText { get; }
        public string TotalUsageTimeText { get; }
        public string TotalSessionsText { get; }

        public IReadOnlyList<FavoriteFolderRowViewModel> FavoriteFolders { get; }
        public bool HasFavoriteFolders { get; }

        public IReadOnlyList<FileTypeRowViewModel> FileTypes { get; }
        public bool HasFileTypes { get; }

        public IReadOnlyList<HourBarViewModel> HourlyActivity { get; }
        public bool HasAnyActivity { get; }

        public IReadOnlyList<RecentSearchRowViewModel> RecentSearches { get; }
        public bool HasRecentSearches { get; }

        public IReadOnlyList<RecentFileRowViewModel> RecentVideos { get; }
        public bool HasRecentVideos { get; }

        public IReadOnlyList<RecentFileRowViewModel> RecentMusic { get; }
        public bool HasRecentMusic { get; }

        public IReadOnlyList<RecentFileRowViewModel> RecentDocuments { get; }
        public bool HasRecentDocuments { get; }

        public IReadOnlyList<FavoriteFolderRowViewModel> RecentFolders { get; }
        public bool HasRecentFolders { get; }

        private static string FormatHour(int hour)
        {
            return new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", CultureInfo.CurrentCulture);
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span <= TimeSpan.Zero)
            {
                return "0m";
            }

            if (span.TotalDays >= 1)
            {
                return $"{(int)span.TotalDays}d {span.Hours}h";
            }

            if (span.TotalHours >= 1)
            {
                return $"{(int)span.TotalHours}h {span.Minutes}m";
            }

            return $"{Math.Max(1, span.Minutes)}m";
        }
    }
}
