// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Reads and writes settings.json next to the executable (portable,
    /// per project policy — no AppData). Every failure mode is
    /// contained here, and every one of them is logged so a problem
    /// with settings shows up in the logs even though it never crashes
    /// the app:
    ///   - Missing file: treated as first run, not an error. A default
    ///     file is written so it's easy to find and hand-edit.
    ///   - Corrupt JSON: the bad file is preserved as ".corrupt" for
    ///     inspection, and the app falls back to defaults rather than
    ///     failing to start.
    ///   - Any I/O failure on save: swallowed. Worst case, changes only
    ///     last for the current session — the app never crashes over a
    ///     failed settings write (e.g. file locked, disk full, no
    ///     write permission).
    ///   - Save is atomic: written to a .tmp file first, then swapped
    ///     into place, so a crash mid-write can never leave a
    ///     half-written settings.json behind.
    ///
    /// The file is self-documenting: every save writes out a `//`
    /// comment above each setting explaining what it does, its default,
    /// and (where relevant) its valid range. These are regenerated from
    /// a static description table on every save, not preserved from
    /// what was on disk before — plain JSON has no way to round-trip
    /// comments through JsonSerializer, so "preserve the user's own
    /// comments" isn't achievable without a much heavier JSON-with-
    /// comments library. The trade-off (documented in the file's own
    /// header) is: values you edit are always kept; any comments you
    /// add yourself get replaced by the canonical ones on the next
    /// save. Reading is tolerant of comments and trailing commas
    /// regardless, so a hand-edited file never fails to load over this.
    /// </summary>
    public sealed class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly string _settingsFilePath;
        private readonly object _currentLock = new object();
        private readonly ILogger _logger;
        private AppSettings _current = new AppSettings();

        public SettingsService(ILogger logger = null)
            : this(ByteExplorer.Core.AppPaths.Combine("settings.json"), logger)
        {
        }

        public SettingsService(string settingsFilePath, ILogger logger = null)
        {
            _settingsFilePath = settingsFilePath ?? throw new ArgumentNullException(nameof(settingsFilePath));
            _logger = (logger ?? Log.Logger).ForContext<SettingsService>();
        }

        /// <summary>The settings file's full path (shown and opened by the settings window).</summary>
        public string FilePath => _settingsFilePath;

        /// <summary>
        /// Reads a settings file chosen by the person (import) with the same tolerance as the startup load (comments,
        /// trailing commas, missing sections, out-of-range values sanitized). False, with a reason, when it is not one.
        /// </summary>
        public static bool TryParse(string json, out AppSettings settings, out string error)
        {
            settings = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The file is empty.";
                return false;
            }

            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
            }
            catch (JsonException ex)
            {
                error = "The file is not valid settings JSON (" + ex.Message + ").";
                return false;
            }

            if (settings == null)
            {
                error = "The file holds no settings.";
                return false;
            }

            settings.Migrate();
            settings.Sanitize();
            return true;
        }

        /// <summary>
        /// Replaces every setting (import, reset to defaults) and saves at once. Code that cached a value at startup
        /// keeps it until the next start; the settings window asks for a restart.
        /// </summary>
        public async Task ReplaceAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings.Sanitize();
            Current = settings;
            _logger.Information("All settings replaced (import or reset)");
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        public AppSettings Current
        {
            get
            {
                lock (_currentLock)
                {
                    return _current;
                }
            }
            private set
            {
                lock (_currentLock)
                {
                    _current = value;
                }
            }
        }

        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    _logger.Information("No settings.json found at {Path} — writing defaults (first run)", _settingsFilePath);
                    var defaults = new AppSettings();
                    defaults.Sanitize();
                    Current = defaults;
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                string json;
                try
                {
                    json = await Task.Run(() => File.ReadAllText(_settingsFilePath), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _logger.Warning(ex, "Could not read settings.json at {Path} — using in-memory defaults for this session", _settingsFilePath);
                    Current = new AppSettings();
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.Warning(ex, "Access denied reading settings.json at {Path} — using in-memory defaults for this session", _settingsFilePath);
                    Current = new AppSettings();
                    return;
                }

                AppSettings loaded;
                try
                {
                    loaded = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
                }
                catch (JsonException ex)
                {
                    _logger.Warning(ex, "settings.json at {Path} is corrupt — backing it up and falling back to defaults", _settingsFilePath);
                    TryBackupCorruptFile();
                    loaded = new AppSettings();
                }

                bool migrated = loaded.SchemaVersion < AppSettings.CurrentSchemaVersion;
                loaded.Migrate();
                loaded.Sanitize();
                Current = loaded;
                _logger.Information("Settings loaded from {Path}", _settingsFilePath);
                if (migrated)
                {
                    _logger.Information("Settings file brought up to schema version {Version}", AppSettings.CurrentSchemaVersion);
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Last-resort guard: settings must never prevent the app
                // from starting, whatever goes wrong.
                _logger.Error(ex, "Unexpected error loading settings — falling back to in-memory defaults");
                Current = new AppSettings();
            }
        }

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            // Saves come from several places (view-settings debounce, toggles, dialogs) and may overlap. Two writers
            // on the same .tmp file fail with a sharing violation and that change is lost, so they run one at a
            // time; each one serializes Current when its turn comes, so the last save writes the latest state.
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);

        private async Task SaveCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                AppSettings snapshot = Current;
                snapshot.Sanitize();
                string json = BuildCommentedJson(snapshot);
                string tempPath = _settingsFilePath + ".tmp";

                await Task.Run(() =>
                {
                    File.WriteAllText(tempPath, json);

                    if (File.Exists(_settingsFilePath))
                    {
                        File.Replace(tempPath, _settingsFilePath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tempPath, _settingsFilePath);
                    }
                }, cancellationToken).ConfigureAwait(false);

                _logger.Debug("Settings saved to {Path}", _settingsFilePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Swallow: a failed save must not crash or destabilize
                // the app. The in-memory settings still apply for the
                // rest of this session.
                _logger.Warning(ex, "Failed to save settings.json at {Path} — changes will only last this session", _settingsFilePath);
            }
        }

        private void TryBackupCorruptFile()
        {
            try
            {
                string backupPath = _settingsFilePath + ".corrupt";
                File.Copy(_settingsFilePath, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not back up corrupt settings.json — continuing with defaults anyway");
            }
        }

        /// <summary>
        /// Hand-built rather than JsonSerializer-generated, specifically
        /// so explanatory comments can sit alongside real values. Kept
        /// simple and direct — there are only a dozen or so fields — a
        /// generic "commented JSON writer" would be more machinery than
        /// this settings file will ever need. String values are still
        /// run through JsonSerializer.Serialize individually for their
        /// quoting/escaping, so this never reinvents JSON string
        /// escaping by hand.
        /// </summary>
        private static string BuildCommentedJson(AppSettings settings)
        {
            var sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  // ByteExplorer settings.json");
            sb.AppendLine("  // Safe to hand-edit while the app is closed. Values you change here");
            sb.AppendLine("  // are always kept on the next save. These comments, however, are");
            sb.AppendLine("  // regenerated fresh every time the app saves settings — plain JSON");
            sb.AppendLine("  // can't round-trip comments, so any notes you add yourself will be");
            sb.AppendLine("  // replaced by these canonical ones on the next save.");
            sb.AppendLine();

            AppendComment(sb, 1, "The file's shape version: an older file is brought up to date once when it is loaded.");
            AppendIntLine(sb, 1, "SchemaVersion", settings.SchemaVersion, isLast: false);
            sb.AppendLine();

            sb.AppendLine("  \"Navigation\": {");
            AppendComment(sb, 2, "Show hidden folders/files, both in the left-hand tree and the", "main file list. Default: false (matches Windows Explorer's default).");
            AppendBoolLine(sb, 2, "ShowHiddenItems", settings.Navigation.ShowHiddenItems, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Show system folders/files, both in the left-hand tree and the", "main file list. Kept separate from ShowHiddenItems since a hidden", "item and a system item are independent attributes. Default: false —", "system items are sensitive enough to stay off until explicitly", "turned on.");
            AppendBoolLine(sb, 2, "ShowSystemItems", settings.Navigation.ShowSystemItems, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a network drive gets to respond when expanding it", "in the tree before that node reports a timeout instead of hanging", "on \"Loading...\". Default: 4000. Range: 500-60000.");
            AppendIntLine(sb, 2, "NetworkDriveTimeoutMs", settings.Navigation.NetworkDriveTimeoutMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a network drive gets to respond to the lightweight", "presence check used when building the drive list and by the", "periodic background refresh (every few seconds) that keeps it in", "sync. Kept short since this runs silently and repeatedly — a slow", "network drive should fail this check fast rather than delay every", "refresh cycle. Default: 1500. Range: 250-10000.");
            AppendIntLine(sb, 2, "NetworkDriveProbeTimeoutMs", settings.Navigation.NetworkDriveProbeTimeoutMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether expanded folders in the tree live-update when their", "contents change on disk (FileSystemWatcher-backed). Default: true.");
            AppendBoolLine(sb, 2, "EnableLiveFolderWatching", settings.Navigation.EnableLiveFolderWatching, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the navigation tree expands to the folder shown and selects it, wherever it was", "opened from. Off: the tree selects it only when its row is already visible. Default: true.");
            AppendBoolLine(sb, 2, "ExpandTreeToCurrentFolder", settings.Navigation.ExpandTreeToCurrentFolder, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the navigation tree follows the folder shown in the file list at all.", "Off: the tree changes only when it is used itself. Default: true.");
            AppendBoolLine(sb, 2, "TreeFollowsCurrentFolder", settings.Navigation.TreeFollowsCurrentFolder, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a run of change notifications for an expanded folder", "must go quiet before the tree reloads it — coalesces a burst of", "many rapid changes into a single reload. Default: 500.", "Range: 100-5000.");
            AppendIntLine(sb, 2, "LiveRefreshDebounceMs", settings.Navigation.LiveRefreshDebounceMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Hard cap on how many expanded tree folders may have an active live", "watcher at once, regardless of how many are actually expanded.", "Default: 64. Range: 4-500.");
            AppendIntLine(sb, 2, "MaxLiveWatchedFolders", settings.Navigation.MaxLiveWatchedFolders, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Quick access (pinned places), in sidebar order: Path, optional Name (shown instead of the", "folder's name) and Color. Edited from the app: right-click, Ctrl+D, drag to pin and reorder.");
            AppendRawLine(sb, 2, "Pinned", settings.Navigation.Pinned == null ? "null" : JsonSerializer.Serialize(settings.Navigation.Pinned), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Sidebar sections shown collapsed.");
            AppendRawLine(sb, 2, "CollapsedSidebarSections", JsonSerializer.Serialize(settings.Navigation.CollapsedSidebarSections ?? new List<string>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "The left panel's sections in order: Places, QuickAccess, Recent, Drives, Tags, Network.", "Empty: each layout's own order. Set from Settings > Left panel.");
            AppendRawLine(sb, 2, "SidebarSectionOrder", JsonSerializer.Serialize(settings.Navigation.SidebarSectionOrder ?? new List<string>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "The left panel's design in the Modern layout: Classic, Quiet, Cards, Guides or Chips.", "Set from Settings > Left panel.");
            AppendRawLine(sb, 2, "SidebarDesign", JsonSerializer.Serialize(settings.Navigation.SidebarDesign ?? NavigationSettings.SidebarDesignClassic), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Network locations in the sidebar: \\\\server\\share (Windows), smb://server/share, sftp://host/path (Linux, macOS).");
            AppendRawLine(sb, 2, "NetworkLocations", JsonSerializer.Serialize(settings.Navigation.NetworkLocations ?? new List<string>()), isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            sb.AppendLine("  \"Appearance\": {");
            AppendComment(sb, 2, "Font family used across the whole app window.", "Default: \"Segoe UI Variable Text\".");
            AppendStringLine(sb, 2, "FontFamily", settings.Appearance.FontFamily, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Base font size (points) used across the whole app window.", "Default: 12.5.");
            AppendDoubleLine(sb, 2, "FontSize", settings.Appearance.FontSize, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Reserved for future theming (selection highlight, accents).", "Not applied to the UI yet. Default: \"#0078D4\".");
            AppendStringLine(sb, 2, "AccentColorHex", settings.Appearance.AccentColorHex, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Background color of the main window.", "Default: \"#FFFFFF\".");
            AppendStringLine(sb, 2, "BackgroundColorHex", settings.Appearance.BackgroundColorHex, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Window layout at startup: \"Modern\", \"Trove\" or \"Folio\" (same functionality,", "different layout; \"Dark\" is read as \"Modern\", \"Nemo\" and \"Dori\" as \"Trove\", \"Finder\" as \"Folio\"). Takes effect on the next", "launch. Default: \"Modern\".");
            AppendStringLine(sb, 2, "WindowLayout", settings.Appearance.WindowLayout, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Color theme: \"System\" (follow the operating system), \"Light\" or", "\"Dark\". Also switchable from the toolbar's \"...\" menu. Default: \"System\".");
            AppendStringLine(sb, 2, "Theme", settings.Appearance.Theme, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How the application menu shows a category's items: \"Horizontal\" (a submenu beside the", "menu, the classic way) or \"Vertical\" (unfolded in place). Default: \"Horizontal\".");
            AppendStringLine(sb, 2, "MenuSubmenuStyle", settings.Appearance.MenuSubmenuStyle, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Customized window areas (AddressBar, Toolbar, LeftPanel, MainView, DetailsPanel, StatusBar):",
                "TextScale (0.75-1.6), TextColorHex and BackgroundColorHex (\"#RRGGBB\", empty = the theme's).",
                "Set from Settings > Appearance; \"Reset to default\" empties it.");
            AppendRawLine(sb, 2, "Areas", JsonSerializer.Serialize(settings.Appearance.Areas ?? new List<AreaAppearance>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "The color folders are drawn in (\"#RRGGBB\"); empty = the default yellow.");
            AppendStringLine(sb, 2, "FolderColorHex", settings.Appearance.FolderColorHex, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "An image drawn behind the main view's files (full path; empty = none), how strongly it shows", "(0.05-1) and how it fills the view: Fill, Fit, Stretch, Tile or Center.");
            AppendStringLine(sb, 2, "MainViewBackgroundImage", settings.Appearance.MainViewBackgroundImage, isLast: false);
            AppendDoubleLine(sb, 2, "MainViewBackgroundImageOpacity", settings.Appearance.MainViewBackgroundImageOpacity, isLast: false);
            AppendStringLine(sb, 2, "MainViewBackgroundImageFit", settings.Appearance.MainViewBackgroundImageFit, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            sb.AppendLine("  \"FileList\": {");
            AppendComment(sb, 2, "How many fully-built rows (icon, formatted size/date) are kept", "in memory at once in the middle file list, regardless of how", "many files the folder actually has. This is the \"only ~500", "loaded at a time\" setting. Default: 500. Range: 50-20000.");
            AppendIntLine(sb, 2, "ResidentItemWindow", settings.FileList.ResidentItemWindow, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a network folder listing gets before it reports a", "timeout instead of hanging on \"Loading...\". Default: 8000.", "Range: 1000-120000.");
            AppendIntLine(sb, 2, "NetworkEnumerationTimeoutMs", settings.FileList.NetworkEnumerationTimeoutMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Hard cap on folder item count for Timeline and Nexus views — both", "can realize an entire large group/cluster at once rather than", "truly virtualizing. Past this many items, switching to either is", "blocked (and reverted automatically if already active when", "navigating into a folder this large). Default: 5000.", "Range: 100-1000000.");
            AppendIntLine(sb, 2, "MaxItemsForGroupedViews", settings.FileList.MaxItemsForGroupedViews, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the currently-open folder live-updates when its contents", "change on disk (FileSystemWatcher-backed). Default: true.");
            AppendBoolLine(sb, 2, "EnableLiveFolderWatching", settings.FileList.EnableLiveFolderWatching, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Opening an archive (ZIP, TAR; 7z, RAR, ISO... with bsdtar or 7-Zip) browses it like a folder", "(read-only). Off: it opens in its associated application. Default: true.");
            AppendBoolLine(sb, 2, "OpenZipFilesAsFolders", settings.FileList.OpenZipFilesAsFolders, isLast: false);
            AppendComment(sb, 2, "Count the size of each folder in the open folder as it opens (local drives only).", "Off: folder sizes are counted when asked (Calculate folder sizes, Alt+Shift+Enter). Default: false.");
            AppendBoolLine(sb, 2, "AutoCalculateFolderSizes", settings.FileList.AutoCalculateFolderSizes, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a run of change notifications for the open folder", "must go quiet before the file list reloads — coalesces a burst of", "many rapid changes into a single reload. Default: 400.", "Range: 100-5000.");
            AppendIntLine(sb, 2, "LiveRefreshDebounceMs", settings.FileList.LiveRefreshDebounceMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How many recently-visited folders' listings stay cached, so", "revisiting them can skip re-scanning the disk. Default: 8.", "Range: 1-100.");
            AppendIntLine(sb, 2, "DirectoryIndexCacheCapacity", settings.FileList.DirectoryIndexCacheCapacity, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a cached folder listing is considered fresh enough", "to reuse without re-scanning. Default: 5000. Range: 0-300000.");
            AppendIntLine(sb, 2, "DirectoryIndexCacheTtlMs", settings.FileList.DirectoryIndexCacheTtlMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a folder selection in the tree must \"settle\"", "before it actually loads — prevents wasted disk scans when", "quickly clicking/arrow-keying through several folders.", "Default: 80. Range: 0-1000.");
            AppendIntLine(sb, 2, "SelectionDebounceMs", settings.FileList.SelectionDebounceMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How many rows beyond the visible area get pre-built in the", "background while scrolling, in the scroll direction, so", "scrolling stays smooth. Default: 150. Range: 0 up to half of", "ResidentItemWindow.");
            AppendIntLine(sb, 2, "PrefetchBufferSize", settings.FileList.PrefetchBufferSize, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Which layout the file list uses. Only \"Details\" exists today;", "more options (List, Icons, Tiles) are planned. Default: \"Details\".");
            AppendStringLine(sb, 2, "DefaultViewMode", settings.FileList.DefaultViewMode, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Icon size (pixels) for the Icons view — set by picking a named", "preset (Small/Medium/Large/Extra large icons) or by Ctrl+scroll", "zoom over the file list. Default: 64. Range: 32-256.");
            AppendDoubleLine(sb, 2, "DefaultIconSize", settings.FileList.DefaultIconSize, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Folder options › \"Show Windows icons\": File Explorer's icons for files and drives", "(folders keep ByteExplorer's folder icon). Windows only. Default: true.");
            AppendBoolLine(sb, 2, "UseShellIcons", settings.FileList.UseShellIcons, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "The Details view's columns in order, with widths (right-click a column header to change).", "Keys: Name, DateModified, Type, Size, Tags, DateCreated, DateAccessed, Extension, Attributes,", "FolderPath, Owner, Dimensions, DateTaken, Camera, Length. Empty: the default columns.");
            AppendRawLine(sb, 2, "DetailsColumns", JsonSerializer.Serialize(settings.FileList.DetailsColumns ?? new List<DetailsColumnEntry>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Show image thumbnails instead of the file-type icon in the Icons", "(medium and larger) and Tiles views, decoded in the background.", "Default: true.");
            AppendBoolLine(sb, 2, "ShowThumbnails", settings.FileList.ShowThumbnails, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Preview Word, Excel, PowerPoint, PDF and other documents in the details pane. Windows: the",
                "system's preview handlers (as File Explorer, run out of process). Linux/macOS: a PDF's first page",
                "(needs pdftoppm from poppler-utils) and the text of .docx/.odt files. Default: true.");
            AppendBoolLine(sb, 2, "ShowDocumentPreviews", settings.FileList.ShowDocumentPreviews, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Empty space between items in pixels (0-40), per view: between rows in Details and List,",
                "around each item in Icons and Tiles. The rubber band starts in these gaps. Defaults: 4, 6, 16, 16.");
            AppendComment(sb, 2, "When an item of a copy, move or delete fails: ask (Retry, Skip, Skip all, Cancel) when true,", "or skip it and report every failure at the end. Default: true.");
            AppendBoolLine(sb, 2, "AskOnFileOperationErrors", settings.FileList.AskOnFileOperationErrors, isLast: false);
            sb.AppendLine();
            AppendIntLine(sb, 2, "ItemSpacingDetails", settings.FileList.ItemSpacingDetails, isLast: false);
            AppendIntLine(sb, 2, "ItemSpacingList", settings.FileList.ItemSpacingList, isLast: false);
            AppendIntLine(sb, 2, "ItemSpacingIcons", settings.FileList.ItemSpacingIcons, isLast: false);
            AppendIntLine(sb, 2, "ItemSpacingTiles", settings.FileList.ItemSpacingTiles, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Caps how many brand-new Icons-view tiles get built in a single", "scroll layout pass — protects against a large scroll jump at", "small icon sizes freezing the UI thread. Untested initial guess,", "not a measured value: lower if scrolling still stutters on big", "jumps, raise if it feels unnecessarily conservative. Default: 60.", "Range: 5-2000.");
            AppendIntLine(sb, 2, "MaxNewTilesPerScrollPass", settings.FileList.MaxNewTilesPerScrollPass, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Before a Move or Delete, back up the originals to a temp folder", "first, so a sudden interruption (power loss, crash, network drop)", "leaves a recoverable copy instead of lost data. Copy is excluded —", "the source is never at risk there. Default: true.");
            AppendBoolLine(sb, 2, "BackupBeforeFileOperation", settings.FileList.BackupBeforeFileOperation, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Selections larger than this run WITHOUT a backup, to avoid slowing", "down large operations too much. Default: 3221225472 (3 GB).");
            AppendLongLine(sb, 2, "BackupMaxTotalSizeBytes", settings.FileList.BackupMaxTotalSizeBytes, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Where backups are stored. Empty uses the system temp folder — fine", "against interruption, NOT against that drive itself failing. Point", "this at a different physical drive for that specific protection.");
            AppendStringLine(sb, 2, "BackupFolderPath", settings.FileList.BackupFolderPath, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long an abandoned backup (left by a crashed session, or kept", "after a failed operation) is retained before a startup cleanup", "pass removes it. Default: 7. Range: 0-365.");
            AppendIntLine(sb, 2, "BackupRetentionDays", settings.FileList.BackupRetentionDays, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How every copied or moved file is checked before it takes its name (and a moved file's", "source is deleted): \"None\", \"Size\", or \"Sha256\". Size is cheap; Sha256 reads both", "files again (the copy from the disk, not the cache), catching silent corruption a size", "match wouldn't. Default: \"Size\".");
            AppendStringLine(sb, 2, "VerificationMode", settings.FileList.VerificationMode, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How many times a transient failure (a momentary lock, a brief", "network hiccup) is retried before giving up on that item.", "Permanent errors (access denied, disk full) are never retried", "regardless of this value. Default: 3. Range: 0-20.");
            AppendIntLine(sb, 2, "MaxRetries", settings.FileList.MaxRetries, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "First retry delay in ms; each subsequent retry doubles this, up to", "MaxRetryDelayMs. Default: 250. Range: 0-60000.");
            AppendIntLine(sb, 2, "InitialRetryDelayMs", settings.FileList.InitialRetryDelayMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Retry backoff never waits longer than this between attempts.", "Default: 5000. Must be >= InitialRetryDelayMs.");
            AppendIntLine(sb, 2, "MaxRetryDelayMs", settings.FileList.MaxRetryDelayMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the left navigation pane / right details pane / status bar /", "toolbar are collapsed. Remembered: showing or hiding a panel updates them.", "Defaults: left false, right true, status bar false, toolbar false.");
            AppendBoolLine(sb, 2, "StartWithLeftPanelCollapsed", settings.FileList.StartWithLeftPanelCollapsed, isLast: false);
            AppendBoolLine(sb, 2, "StartWithRightPanelCollapsed", settings.FileList.StartWithRightPanelCollapsed, isLast: false);
            AppendBoolLine(sb, 2, "StartWithStatusBarCollapsed", settings.FileList.StartWithStatusBarCollapsed, isLast: false);
            AppendBoolLine(sb, 2, "StartWithToolbarCollapsed", settings.FileList.StartWithToolbarCollapsed, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            sb.AppendLine("  \"Analytics\": {");
            AppendComment(sb, 2, "Local-only usage analytics for the Home dashboard — nothing here is", "ever transmitted anywhere; analytics.json lives next to the", "executable, same as this file. Master off switch. Default: true.");
            AppendBoolLine(sb, 2, "EnableUsageAnalytics", settings.Analytics.EnableUsageAnalytics, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Hard cap on distinct folder paths tracked — least relevant (by", "frecency) evicted first once exceeded. Default: 500. Range: 50-5000.");
            AppendIntLine(sb, 2, "MaxTrackedFolders", settings.Analytics.MaxTrackedFolders, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Hard cap on each rolling activity log (recent searches, recent file", "operations) shown on the dashboard. Default: 200. Range: 20-2000.");
            AppendIntLine(sb, 2, "MaxLogEntries", settings.Analytics.MaxLogEntries, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Hard cap on session history entries (one per app run) retained for", "streak/average-session insights. Default: 120. Range: 10-2000.");
            AppendIntLine(sb, 2, "MaxSessionHistory", settings.Analytics.MaxSessionHistory, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) a burst of tracked activity must go quiet before", "analytics.json is actually written — a clean shutdown always", "flushes immediately regardless. Default: 5000. Range: 500-60000.");
            AppendIntLine(sb, 2, "SaveDebounceMs", settings.Analytics.SaveDebounceMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Half-life (days) for the frecency score behind \"Favorite folders\" —", "lower favors recent use, higher favors long-standing heavy use.", "Default: 14. Range: 1-365.");
            AppendDoubleLine(sb, 2, "FavoriteFolderHalfLifeDays", settings.Analytics.FavoriteFolderHalfLifeDays, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            sb.AppendLine("  \"Index\": {");
            AppendComment(sb, 2, "Whether the search index rebuilds itself automatically on a", "schedule, on top of the normal load-if-valid/build-if-not check", "every app start already does. Default: true.");
            AppendBoolLine(sb, 2, "EnableAutoRebuild", settings.Index.EnableAutoRebuild, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How often (hours) the index is rebuilt in the background, measured", "from the last build. Search keeps using the previous index meanwhile.", "Default: 6. Range: 0.5-720.");
            AppendDoubleLine(sb, 2, "AutoRebuildIntervalHours", settings.Index.AutoRebuildIntervalHours, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the index follows changes on disk between builds (file system", "watchers on the indexed drives; on Linux only when the folders fit the", "system's watch limit). The app's own operations always update it. Default: true.");
            AppendBoolLine(sb, 2, "WatchForChanges", settings.Index.WatchForChanges, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Places to index (full paths). Empty: automatic (every drive on Windows;", "the home folder and mounted volumes on Linux and macOS).");
            AppendRawLine(sb, 2, "Locations", JsonSerializer.Serialize(settings.Index.Locations ?? new List<string>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Folders never indexed, with everything inside them (full paths).");
            AppendRawLine(sb, 2, "ExcludedFolders", JsonSerializer.Serialize(settings.Index.ExcludedFolders ?? new List<string>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "File and folder names never indexed, wherever they are (case-insensitive),", "e.g. [\"node_modules\", \".git\"].");
            AppendRawLine(sb, 2, "ExcludedNames", JsonSerializer.Serialize(settings.Index.ExcludedNames ?? new List<string>()), isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the text inside files is indexed too (text and code, Office, OpenDocument,", "RTF, PDF) for content: searches, in the background at low priority. Turning it", "off deletes the content index. Default: true.");
            AppendBoolLine(sb, 2, "IndexFileContents", settings.Index.IndexFileContents, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether the words typed in the search box also find files by their contents,", "listed after the name matches. Needs IndexFileContents. Default: true.");
            AppendBoolLine(sb, 2, "SearchFileContents", settings.Index.SearchFileContents, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Files larger than this (MB) are not read for the content index.", "Default: 20. Range: 1-500.");
            AppendIntLine(sb, 2, "ContentMaxFileSizeMB", settings.Index.ContentMaxFileSizeMB, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Controlled by the app — UTC timestamp of the last successful", "build, or empty if none has ever succeeded.");
            AppendStringLine(sb, 2, "LastBuildUtc", settings.Index.LastBuildUtc, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            sb.AppendLine("  \"Greeting\": {");
            AppendComment(sb, 2, "Whether a time-of-day greeting toast shows once, briefly, when the", "app starts. Default: false.");
            AppendBoolLine(sb, 2, "EnableGreeting", settings.Greeting.EnableGreeting, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether a time-of-day goodbye toast shows once, briefly, when the", "app is closing (delays the actual close by GoodbyeDwellMs so", "there's time to see it). Default: false.");
            AppendBoolLine(sb, 2, "EnableGoodbye", settings.Greeting.EnableGoodbye, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether a short system sound plays alongside the greeting/goodbye", "toasts. Independent of whether the toasts themselves are shown.", "Default: true.");
            AppendBoolLine(sb, 2, "EnableSoundFeedback", settings.Greeting.EnableSoundFeedback, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether a short system sound plays for file operation outcomes —", "a distinct sound for a successful copy/move/delete, and another", "for any operation that didn't fully succeed. Independent of", "EnableSoundFeedback above. Default: true.");
            AppendBoolLine(sb, 2, "EnableActionSounds", settings.Greeting.EnableActionSounds, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Whether a short system sound plays for everyday navigation —", "opening a folder (file list or left tree) or a file. Off by", "default, unlike the other sound toggles here — this is by far", "the most frequent trigger of the three, so it's opt-in rather", "than opt-out. Default: false.");
            AppendBoolLine(sb, 2, "EnableNavigationSounds", settings.Greeting.EnableNavigationSounds, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "Minimum time (ms) since the last navigation sound before another", "is allowed to play — prevents fast browsing (holding an arrow", "key, rapid clicks) from firing overlapping sounds. Default: 150.", "Range: 0-2000 (0 disables throttling).");
            AppendIntLine(sb, 2, "NavigationSoundThrottleMs", settings.Greeting.NavigationSoundThrottleMs, isLast: false);
            sb.AppendLine();
            AppendComment(sb, 2, "How long (ms) the goodbye toast stays visible before the window", "actually closes. Only relevant when EnableGoodbye is true.", "Default: 1800. Range: 500-6000.");
            AppendIntLine(sb, 2, "GoodbyeDwellMs", settings.Greeting.GoodbyeDwellMs, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            FolderOptionsSettings folder = settings.FolderOptions ?? new FolderOptionsSettings();
            sb.AppendLine("  \"FolderOptions\": {");
            AppendComment(sb, 2, "Windows Explorer's Folder Options (Settings > Folder options).", "Where a new window opens: Home, ThisPc, Folder (StartupFolder) or LastSession. Default: Home.");
            AppendStringLine(sb, 2, "OpenAt", folder.OpenAt, isLast: false);
            AppendComment(sb, 2, "The folder for OpenAt = Folder.");
            AppendStringLine(sb, 2, "StartupFolder", folder.StartupFolder, isLast: false);
            AppendComment(sb, 2, "One click opens an item; Ctrl+click and Shift+click still select. Default: false.");
            AppendBoolLine(sb, 2, "OpenWithSingleClick", folder.OpenWithSingleClick, isLast: false);
            AppendComment(sb, 2, "Recently used files on Home and This PC. Default: true.");
            AppendBoolLine(sb, 2, "ShowRecentFiles", folder.ShowRecentFiles, isLast: false);
            AppendComment(sb, 2, "Frequently used folders on Home and in the left panel. Default: true.");
            AppendBoolLine(sb, 2, "ShowFrequentFolders", folder.ShowFrequentFolders, isLast: false);
            AppendComment(sb, 2, "\"report\" instead of \"report.pdf\" for known file types. Default: false.");
            AppendBoolLine(sb, 2, "HideKnownExtensions", folder.HideKnownExtensions, isLast: false);
            AppendComment(sb, 2, "Leave out drives with no media (card readers without a card). Default: false.");
            AppendBoolLine(sb, 2, "HideEmptyDrives", folder.HideEmptyDrives, isLast: false);
            AppendComment(sb, 2, "Windows: drive letters in drive names, and before the name instead of after it.");
            AppendBoolLine(sb, 2, "ShowDriveLetters", folder.ShowDriveLetters, isLast: false);
            AppendBoolLine(sb, 2, "DriveLettersFirst", folder.DriveLettersFirst, isLast: false);
            AppendComment(sb, 2, "The window title shows the open folder's full path. Default: false.");
            AppendBoolLine(sb, 2, "FullPathInTitleBar", folder.FullPathInTitleBar, isLast: false);
            AppendComment(sb, 2, "A check box on each item to select it. Default: false.");
            AppendBoolLine(sb, 2, "UseCheckBoxes", folder.UseCheckBoxes, isLast: false);
            AppendComment(sb, 2, "Hover tips on items, and whether they include sizes. Default: true, true.");
            AppendBoolLine(sb, 2, "ShowItemTooltips", folder.ShowItemTooltips, isLast: false);
            AppendBoolLine(sb, 2, "ShowSizeInTooltips", folder.ShowSizeInTooltips, isLast: false);
            AppendComment(sb, 2, "Typing in the file list: Select (jump to the item) or Search (type into the search box).");
            AppendStringLine(sb, 2, "TypingInList", folder.TypingInList, isLast: false);
            AppendComment(sb, 2, "The right-click menu: ByteExplorer (the app's), Windows (the shell's, with every program's", "entries) or Hybrid (the app's, with \"Show more options\" for the Windows one). Windows only. Default: Hybrid.");
            AppendStringLine(sb, 2, "ContextMenu", folder.ContextMenu, isLast: false);
            AppendComment(sb, 2, "The home folder above This PC in the folder tree. Default: false.");
            AppendBoolLine(sb, 2, "ShowAllFolders", folder.ShowAllFolders, isLast: false);
            AppendComment(sb, 2, "Phones and cameras connected by USB in This PC and the tree. false: the app never talks to them. Default: true.");
            AppendBoolLine(sb, 2, "ShowPhones", folder.ShowPhones, isLast: false);
            AppendComment(sb, 2, "Kept by the app for OpenAt = LastSession: the folders open when it last closed.");
            AppendStringLine(sb, 2, "LastSessionFolder", folder.LastSessionFolder, isLast: false);
            AppendStringLine(sb, 2, "LastSessionSecondFolder", folder.LastSessionSecondFolder, isLast: false);
            AppendBoolLine(sb, 2, "LastSessionSplitView", folder.LastSessionSplitView, isLast: true);
            sb.AppendLine("  },");
            sb.AppendLine();

            TabSettings tabs = settings.Tabs ?? new TabSettings();
            sb.AppendLine("  \"Tabs\": {");
            AppendComment(sb, 2, "Settings > Tabs. Where the tab bar sits: TitleBar (Modern layout; on top in the others), Top, Left or Right. Default: TitleBar.");
            AppendStringLine(sb, 2, "Placement", tabs.Placement, isLast: false);
            AppendComment(sb, 2, "Vertical tabs (Left, Right): shrunk to their icons, and the bar's width (150-480). Default: false, 232.");
            AppendBoolLine(sb, 2, "VerticalCollapsed", tabs.VerticalCollapsed, isLast: false);
            AppendDoubleLine(sb, 2, "VerticalWidth", tabs.VerticalWidth, isLast: false);
            AppendComment(sb, 2, "Show the tab bar while only one tab is open. Default: true.");
            AppendBoolLine(sb, 2, "ShowWithOneTab", tabs.ShowWithOneTab, isLast: false);
            AppendComment(sb, 2, "What a new tab shows: Home, ThisPc, or Current (the folder of the tab it was opened from). Default: Home.");
            AppendStringLine(sb, 2, "NewTabOpens", tabs.NewTabOpens, isLast: false);
            AppendComment(sb, 2, "A middle click on a folder opens it in a NewTab or in the OtherPane. Default: NewTab.");
            AppendStringLine(sb, 2, "MiddleClickOpens", tabs.MiddleClickOpens, isLast: false);
            AppendComment(sb, 2, "A folder opened in a new tab is shown at once instead of opening behind. Default: false.");
            AppendBoolLine(sb, 2, "SwitchToNewTab", tabs.SwitchToNewTab, isLast: false);
            AppendComment(sb, 2, "Kept by the app: the tabs open at the last close (pinned tabs always come back; the others with", "Folder options' OpenAt = LastSession). Locations are folder paths, or ::home, ::thispc and ::trash.");
            AppendRawLine(sb, 2, "SessionTabs", JsonSerializer.Serialize(tabs.SessionTabs ?? new List<TabSessionEntry>()), isLast: false);
            AppendIntLine(sb, 2, "SessionSelectedTab", tabs.SessionSelectedTab, isLast: true);
            sb.AppendLine("  }");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendComment(StringBuilder sb, int indentLevel, params string[] lines)
        {
            string indent = new string(' ', indentLevel * 2);
            foreach (string line in lines)
            {
                sb.Append(indent).Append("// ").AppendLine(line);
            }
        }

        private static void AppendBoolLine(StringBuilder sb, int indentLevel, string name, bool value, bool isLast)
        {
            AppendRawLine(sb, indentLevel, name, value ? "true" : "false", isLast);
        }

        private static void AppendIntLine(StringBuilder sb, int indentLevel, string name, int value, bool isLast)
        {
            AppendRawLine(sb, indentLevel, name, value.ToString(CultureInfo.InvariantCulture), isLast);
        }

        private static void AppendLongLine(StringBuilder sb, int indentLevel, string name, long value, bool isLast)
        {
            AppendRawLine(sb, indentLevel, name, value.ToString(CultureInfo.InvariantCulture), isLast);
        }

        private static void AppendDoubleLine(StringBuilder sb, int indentLevel, string name, double value, bool isLast)
        {
            AppendRawLine(sb, indentLevel, name, value.ToString(CultureInfo.InvariantCulture), isLast);
        }

        private static void AppendStringLine(StringBuilder sb, int indentLevel, string name, string value, bool isLast)
        {
            // Reuses JsonSerializer purely for correct string quoting and
            // escaping — no hand-rolled escaping logic here.
            string quoted = JsonSerializer.Serialize(value ?? string.Empty);
            AppendRawLine(sb, indentLevel, name, quoted, isLast);
        }

        private static void AppendRawLine(StringBuilder sb, int indentLevel, string name, string rawValue, bool isLast)
        {
            string indent = new string(' ', indentLevel * 2);
            sb.Append(indent).Append('"').Append(name).Append("\": ").Append(rawValue);
            if (!isLast)
            {
                sb.Append(',');
            }
            sb.AppendLine();
        }
    }
}
