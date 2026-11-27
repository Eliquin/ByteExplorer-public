// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// File-list behavior, persisted to settings.json.
    /// </summary>
    public sealed class FileListSettings
    {
        /// <summary>
        /// How many fully-hydrated row view models (formatted strings,
        /// icon) are kept resident at once, regardless of folder size —
        /// this is the "only ~500 loaded at a time" knob. The
        /// lightweight per-entry index behind it covers the whole
        /// folder, even a million entries, so the scrollbar and jump-
        /// to-index are still instant; this setting only bounds the
        /// richer, per-row objects built on top of it.
        /// </summary>
        public int ResidentItemWindow { get; set; } = 500;

        /// <summary>
        /// How long a network path gets to finish enumerating before
        /// the file list reports a timeout instead of hanging on
        /// "Loading...". Mirrors NavigationSettings.NetworkDriveTimeoutMs,
        /// sized larger since listing a full folder takes longer than a
        /// drive-readiness check.
        /// </summary>
        public int NetworkEnumerationTimeoutMs { get; set; } = 8000;

        /// <summary>
        /// Whether the currently-open folder live-updates when its
        /// contents change on disk — a file/folder created, deleted,
        /// renamed, or resized, whether from this app's own operations,
        /// the real Explorer context menu invoked from the tree, or any
        /// other program touching the same folder. Backed by
        /// FileSystemWatcher, which is best-effort by nature — leave
        /// this on unless a specific environment causes trouble; the
        /// file list still works exactly as it always has (manual
        /// navigate-away-and-back, or the toolbar refresh) with it off.
        /// Default: true.
        /// </summary>
        public bool EnableLiveFolderWatching { get; set; } = true;

        /// <summary>
        /// Opening an archive browses it like a folder (read-only: open, copy and extract its items), as Explorer does for
        /// ZIP files: ZIP and TAR (.tar, .tar.gz) always, 7z, RAR, ISO, CAB and xz/bzip2/zstd files when an archiver is
        /// installed (<see cref="Services.Archives.ArchiveFormats"/>). Off: archives open in their associated application.
        /// "Open with" works either way. Default: true. The name stays "OpenZipFilesAsFolders" so existing settings
        /// files keep their choice.
        /// </summary>
        public bool OpenZipFilesAsFolders { get; set; } = true;

        /// <summary>
        /// Count the size of every folder in the open folder as it opens (local drives only; network drives and phones
        /// are counted only when asked, with Calculate folder sizes). Default: off, as counting reads whole trees.
        /// </summary>
        public bool AutoCalculateFolderSizes { get; set; }

        /// <summary>
        /// How long a run of raw change notifications for the open
        /// folder must go quiet before the file list actually reloads.
        /// Exists so a bulk operation (a large copy, an extraction, a
        /// build writing hundreds of output files) triggers one reload,
        /// not hundreds. Default: 400. Range: 100-5000.
        /// </summary>
        public int LiveRefreshDebounceMs { get; set; } = 400;

        /// <summary>
        /// How many recently-visited folders' indexes are kept cached.
        /// Revisiting a cached folder within DirectoryIndexCacheTtlMs
        /// skips enumeration entirely — this is what makes back/forward
        /// navigation feel instant instead of re-scanning disk every time.
        /// </summary>
        public int DirectoryIndexCacheCapacity { get; set; } = 8;

        /// <summary>
        /// How long a cached folder index is considered fresh enough to
        /// serve without re-enumerating. Deliberately short — this is a
        /// "you just came from here a moment ago" cache, not a
        /// long-lived one; a stale cache silently hiding real disk
        /// changes for minutes would be worse than the lag it saves.
        /// </summary>
        public int DirectoryIndexCacheTtlMs { get; set; } = 5000;

        /// <summary>
        /// How long a tree selection has to "settle" before it triggers
        /// a real folder load. Clicking or arrow-keying quickly through
        /// several folders only loads the one the user actually stops
        /// on — folders passed through along the way never touch disk.
        /// 0 disables debouncing.
        /// </summary>
        public int SelectionDebounceMs { get; set; } = 80;

        /// <summary>
        /// How many rows beyond the visible viewport get hydrated
        /// proactively, in the scroll direction, on a background
        /// thread — so by the time the panel actually asks for them
        /// during a fast scroll, they're already built. Kept
        /// meaningfully smaller than ResidentItemWindow so prefetching
        /// ahead can never itself evict what's currently on screen.
        /// 0 disables prefetching.
        /// </summary>
        public int PrefetchBufferSize { get; set; } = 150;

        /// <summary>
        /// Reserved for Phase 3b (List/Icons/Tiles views + zoom). Only
        /// "Details" is implemented so far; the field exists now so
        /// settings.json's shape won't need to change when it lands.
        /// </summary>
        public string DefaultViewMode { get; set; } = "Details";

        /// <summary>
        /// Icon size (pixels) used in the Icons view, persisted across
        /// restarts. Updated both by picking a named preset (Small/
        /// Medium/Large/Extra large icons) and by Ctrl+scroll zoom.
        /// </summary>
        public double DefaultIconSize { get; set; } = 64;

        /// <summary>
        /// Folder options › "Show Windows icons": the file views, the folder tree and the search results draw the icons
        /// Windows Explorer shows (the system image list: each program's own icon, special folders, drives), at every
        /// size; off, ByteExplorer's own glyphs colored by kind. Windows only (Infrastructure/SystemIcons); elsewhere
        /// the glyphs are drawn and this value is kept for when the file is used on Windows.
        /// </summary>
        public bool UseShellIcons { get; set; } = true;

        /// <summary>
        /// Show image thumbnails instead of the file-type icon in the Icons (medium and larger) and Tiles views. Decoded
        /// in the background at the shown size. Default true.
        /// </summary>
        public bool ShowThumbnails { get; set; } = true;

        /// <summary>
        /// Folder options › "Show previews of documents": the details pane previews Word, Excel, PowerPoint, PDF and other
        /// documents. On Windows through the system's preview handlers (the ones File Explorer's preview pane uses, run out
        /// of process); elsewhere a PDF's first page through pdftoppm when installed, and the text of .docx/.odt files.
        /// Off: those files show "no preview", and no preview handler is ever started. Default true.
        /// </summary>
        public bool ShowDocumentPreviews { get; set; } = true;

        /// <summary>
        /// When an item of a copy, move or delete fails: ask what to do (Retry, Skip, Skip all, Cancel), as Total Commander
        /// does, or skip it and report every failure at the end. Default: ask.
        /// </summary>
        public bool AskOnFileOperationErrors { get; set; } = true;

        /// <summary>
        /// Empty space between items, in pixels, per view: a gap the rubber band can start in (a press on an item starts a
        /// drag instead), and breathing room. Details and List: between rows; Icons (every size) and Tiles: around each
        /// item. Range 0-40.
        /// </summary>
        public int ItemSpacingDetails { get; set; } = DefaultItemSpacingDetails;

        public int ItemSpacingList { get; set; } = DefaultItemSpacingList;

        public int ItemSpacingIcons { get; set; } = DefaultItemSpacingIcons;

        public int ItemSpacingTiles { get; set; } = DefaultItemSpacingTiles;

        public const int DefaultItemSpacingDetails = 4;
        public const int DefaultItemSpacingList = 6;
        public const int DefaultItemSpacingIcons = 16;
        public const int DefaultItemSpacingTiles = 16;
        public const int MaxItemSpacing = 40;

        /// <summary>
        /// Caps how many brand-new tile containers VirtualizingWrapPanel
        /// (Icons view) will build in a single synchronous layout pass.
        /// Protects against a real, confirmed issue: a large scroll
        /// jump at small icon sizes (many tiles per row) could otherwise
        /// force hundreds of containers to be built in one blocking UI-
        /// thread call, freezing — and at the extreme, crashing — the
        /// app. 60 is an untested initial guess, not a measured value —
        /// tune it if scrolling in Icons view still stutters on jumps
        /// (lower = smoother but slower to finish filling in) or if it
        /// feels unnecessarily conservative on faster hardware (higher).
        /// </summary>
        /// <remarks>Not used by the cross-platform build (its wrap panel recycles containers and needs no per-pass cap);
        /// kept so settings.json stays compatible with the WPF build.</remarks>
        public int MaxNewTilesPerScrollPass { get; set; } = 60;

        /// <summary>
        /// Before a Move or Delete, copy the originals to a temp backup
        /// location first, so a sudden interruption (power loss, app
        /// crash, network drop) during the real operation leaves a
        /// recoverable copy rather than lost data. Copy is deliberately
        /// excluded — it never touches or removes the source, so a
        /// pre-copy backup would double I/O for close to zero
        /// protective value. Default true, reflecting the priority this
        /// was explicitly built for; set to false to disable entirely
        /// (or set BackupMaxTotalSizeBytes to 0 as an equivalent).
        ///
        /// Honest limitation: a backup on the same physical drive as
        /// the source protects against interruption, NOT against that
        /// drive itself failing — see BackupFolderPath if that specific
        /// protection matters.
        /// </summary>
        public bool BackupBeforeFileOperation { get; set; } = true;

        /// <summary>
        /// Selections larger than this are operated on WITHOUT a
        /// backup — backing up a huge selection would meaningfully slow
        /// down every large Move/Delete, working against the
        /// "shouldn't compromise speed" balance this was built with.
        /// Default: 3 GB (3,221,225,472 bytes), matching the original
        /// suggestion this feature was built from.
        /// </summary>
        public long BackupMaxTotalSizeBytes { get; set; } = 3L * 1024 * 1024 * 1024;

        /// <summary>
        /// Where backups are stored. Empty/null (the default) uses the
        /// system temp folder — fine for protection against
        /// interruption, but NOT against that drive itself failing.
        /// Point this at a different physical drive for that specific
        /// protection.
        /// </summary>
        public string BackupFolderPath { get; set; } = null;

        /// <summary>
        /// How long a backup is kept before a future app-startup cleanup
        /// pass removes it, for backups left behind by a session that
        /// ended before its own cleanup ran (crashed, or the operation
        /// failed and the backup was deliberately kept as a recovery
        /// path). Default: 7 days. Range: 0-365.
        /// </summary>
        public int BackupRetentionDays { get; set; } = 7;

        /// <summary>
        /// How every copied or moved file is checked before it is committed to its name (and a moved file's source is
        /// deleted; see FileVerification, FileOperationService) —
        /// "None", "Size", or "Sha256". String rather than the enum
        /// directly, matching DefaultViewMode's pattern, since the
        /// hand-rolled settings.json builder works in plain JSON
        /// primitives. Default: "Size" — a cheap, meaningful safety net
        /// (no extra I/O beyond what's already needed to stat both
        /// files) without Sha256's real cost of reading the whole
        /// destination back a second time.
        /// </summary>
        public string VerificationMode { get; set; } = "Size";

        /// <summary>
        /// How many times a transient failure (a momentary AV-scan
        /// lock, a brief network hiccup — see NativeFileOperations.
        /// IsTransientError) is retried before giving up on that item.
        /// Permanent errors (access denied, disk full) are never
        /// retried regardless of this value. Default: 3. Range: 0-20.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// First retry delay; each subsequent retry doubles this, up to
        /// MaxRetryDelayMs. Default: 250. Range: 0-60000.
        /// </summary>
        public int InitialRetryDelayMs { get; set; } = 250;

        /// <summary>
        /// Retry backoff never waits longer than this between attempts.
        /// Default: 5000. Range: must be >= InitialRetryDelayMs.
        /// </summary>
        public int MaxRetryDelayMs { get; set; } = 5000;

        /// <summary>
        /// Whether the left navigation pane is collapsed when the app
        /// starts. Only affects startup state — the pane can still be
        /// toggled normally afterward via its status-bar button.
        /// Default: false.
        /// </summary>
        public bool StartWithLeftPanelCollapsed { get; set; } = false;

        /// <summary>
        /// Same as StartWithLeftPanelCollapsed, for the right details
        /// pane. Default: true — most of the time the details pane
        /// isn't needed, and starting with more room for the file list
        /// itself is the more useful default; still just one click away
        /// via its status-bar button when wanted.
        /// </summary>
        public bool StartWithRightPanelCollapsed { get; set; } = true;

        /// <summary>
        /// Whether the status bar is collapsed when the app starts.
        /// Default: false.
        /// </summary>
        public bool StartWithStatusBarCollapsed { get; set; } = false;

        /// <summary>
        /// Whether the command toolbar (Modern layout) is collapsed. Like the other three, it is remembered: hiding or
        /// showing a panel from the menu or its button updates these settings. Default: false.
        /// </summary>
        public bool StartWithToolbarCollapsed { get; set; } = false;

        /// <summary>
        /// Hard cap on folder item count for Timeline and Nexus views.
        /// Both group items visually (by date, or into clusters) using a
        /// plain ScrollViewer rather than a true virtualizing panel — see
        /// VirtualizingWrapPanel.cs's remarks — so unlike every other
        /// view here, a single large group/cluster can force WPF to
        /// realize every item in it at once. Past this many items in the
        /// current folder, FileListViewModel refuses to switch into
        /// either view (and automatically switches back to Details if
        /// already in one and navigating into a folder this large),
        /// rather than risking an actual UI freeze. A deliberately
        /// blunt guard — not a fix for the underlying limitation — until
        /// Timeline/Nexus get proper virtualization of their own.
        /// Default: 5000. Range: 100-1000000.
        /// </summary>
        public int MaxItemsForGroupedViews { get; set; } = 5000;

        /// <summary>
        /// The Details view's columns, in order, with their widths (right-click a column header to hide one or choose
        /// others). Empty: the default (Name, Date modified, Type, Size, Tags). Shared by every pane and tab.
        /// </summary>
        public System.Collections.Generic.List<DetailsColumnEntry> DetailsColumns { get; set; } = new System.Collections.Generic.List<DetailsColumnEntry>();

        public void Sanitize()
        {
            DetailsColumns ??= new System.Collections.Generic.List<DetailsColumnEntry>();
            DetailsColumns.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Key));
            if (DetailsColumns.Count > 64)
            {
                DetailsColumns.RemoveRange(64, DetailsColumns.Count - 64);
            }

            if (ResidentItemWindow < 50)
            {
                ResidentItemWindow = 50;
            }
            else if (ResidentItemWindow > 20000)
            {
                ResidentItemWindow = 20000;
            }

            if (NetworkEnumerationTimeoutMs < 1000)
            {
                NetworkEnumerationTimeoutMs = 1000;
            }
            else if (NetworkEnumerationTimeoutMs > 120000)
            {
                NetworkEnumerationTimeoutMs = 120000;
            }

            ItemSpacingDetails = System.Math.Clamp(ItemSpacingDetails, 0, MaxItemSpacing);
            ItemSpacingList = System.Math.Clamp(ItemSpacingList, 0, MaxItemSpacing);
            ItemSpacingIcons = System.Math.Clamp(ItemSpacingIcons, 0, MaxItemSpacing);
            ItemSpacingTiles = System.Math.Clamp(ItemSpacingTiles, 0, MaxItemSpacing);

            if (MaxItemsForGroupedViews < 100)
            {
                MaxItemsForGroupedViews = 100;
            }
            else if (MaxItemsForGroupedViews > 1000000)
            {
                MaxItemsForGroupedViews = 1000000;
            }

            if (LiveRefreshDebounceMs < 100)
            {
                LiveRefreshDebounceMs = 100;
            }
            else if (LiveRefreshDebounceMs > 5000)
            {
                LiveRefreshDebounceMs = 5000;
            }

            if (DirectoryIndexCacheCapacity < 1)
            {
                DirectoryIndexCacheCapacity = 1;
            }
            else if (DirectoryIndexCacheCapacity > 100)
            {
                DirectoryIndexCacheCapacity = 100;
            }

            if (DirectoryIndexCacheTtlMs < 0)
            {
                DirectoryIndexCacheTtlMs = 0;
            }
            else if (DirectoryIndexCacheTtlMs > 300000)
            {
                DirectoryIndexCacheTtlMs = 300000;
            }

            if (SelectionDebounceMs < 0)
            {
                SelectionDebounceMs = 0;
            }
            else if (SelectionDebounceMs > 1000)
            {
                SelectionDebounceMs = 1000;
            }

            if (PrefetchBufferSize < 0)
            {
                PrefetchBufferSize = 0;
            }
            else if (PrefetchBufferSize > 5000)
            {
                PrefetchBufferSize = 5000;
            }

            // Prefetching ahead must never be able to evict what's
            // currently visible — keep it to at most half the resident
            // window as a safety margin, not just "less than."
            int maxSafePrefetch = ResidentItemWindow / 2;
            if (PrefetchBufferSize > maxSafePrefetch)
            {
                PrefetchBufferSize = maxSafePrefetch;
            }

            if (string.IsNullOrWhiteSpace(DefaultViewMode))
            {
                DefaultViewMode = "Details";
            }

            if (DefaultIconSize < 32)
            {
                DefaultIconSize = 32;
            }
            else if (DefaultIconSize > 256)
            {
                DefaultIconSize = 256;
            }

            if (MaxNewTilesPerScrollPass < 5)
            {
                MaxNewTilesPerScrollPass = 5;
            }
            else if (MaxNewTilesPerScrollPass > 2000)
            {
                MaxNewTilesPerScrollPass = 2000;
            }

            if (BackupMaxTotalSizeBytes < 0)
            {
                BackupMaxTotalSizeBytes = 0;
            }

            if (BackupRetentionDays < 0)
            {
                BackupRetentionDays = 0;
            }
            else if (BackupRetentionDays > 365)
            {
                BackupRetentionDays = 365;
            }

            if (!string.Equals(VerificationMode, "None", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(VerificationMode, "Size", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(VerificationMode, "Sha256", StringComparison.OrdinalIgnoreCase))
            {
                VerificationMode = "Size";
            }

            if (MaxRetries < 0)
            {
                MaxRetries = 0;
            }
            else if (MaxRetries > 20)
            {
                MaxRetries = 20;
            }

            if (InitialRetryDelayMs < 0)
            {
                InitialRetryDelayMs = 0;
            }
            else if (InitialRetryDelayMs > 60000)
            {
                InitialRetryDelayMs = 60000;
            }

            if (MaxRetryDelayMs < InitialRetryDelayMs)
            {
                MaxRetryDelayMs = InitialRetryDelayMs;
            }
        }
    }
}
