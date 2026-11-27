// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Navigation-pane behavior. Every field here is safe to hand-edit
    /// in settings.json — SettingsService clamps values to sane ranges
    /// after load, so an invalid or missing value can't break the app.
    /// </summary>
    public sealed class NavigationSettings
    {
        /// <summary>
        /// Whether hidden folders/files show, both in the tree and the
        /// main file list. Off by default, matching Explorer's default
        /// behavior — a fresh install never shows hidden items until
        /// explicitly turned on.
        /// </summary>
        public bool ShowHiddenItems { get; set; } = false;

        /// <summary>
        /// Whether system folders/files show, both in the tree and the
        /// main file list. Off by default and kept separate from
        /// ShowHiddenItems — a folder or file can be hidden without
        /// being a system item and vice versa (DirectoryIndexEntry.
        /// IsHidden/IsSystem are independent FileAttributes flags), and
        /// system items in particular are sensitive enough to warrant
        /// their own explicit, deliberate opt-in rather than being
        /// bundled with the more common "just show me dotfiles" case.
        /// </summary>
        public bool ShowSystemItems { get; set; } = false;

        /// <summary>
        /// How long a network drive gets to respond to a subdirectory
        /// enumeration (expanding a node in the tree) before the node
        /// reports a timeout instead of hanging on "Loading...". Local
        /// drives are never subject to this — only a network drive or a
        /// folder underneath one uses it.
        /// </summary>
        public int NetworkDriveTimeoutMs { get; set; } = 4000;

        /// <summary>
        /// How long a network drive gets to respond to the lightweight
        /// presence/readiness probe used when first building the drive
        /// list and by the periodic background refresh that keeps it in
        /// sync (every few seconds). Deliberately shorter than
        /// NetworkDriveTimeoutMs: this runs silently and repeatedly in
        /// the background, so it needs to fail fast rather than let a
        /// single unresponsive server slow down every refresh cycle.
        /// </summary>
        public int NetworkDriveProbeTimeoutMs { get; set; } = 1500;

        /// <summary>
        /// Whether expanded folders in the tree live-update when their
        /// contents change on disk (a subfolder created/renamed/deleted,
        /// whether from this app's own operations, the real Explorer
        /// context menu invoked from the tree, or any other program).
        /// Backed by FileSystemWatcher, which is best-effort by nature —
        /// leave this on unless a specific environment (an exotic NAS,
        /// a filesystem that doesn't support change notifications)
        /// causes trouble; turning it off costs nothing but the live
        /// refresh itself; the tree still works exactly as it always
        /// has via manual expand/collapse or the "Retry"/reload paths.
        /// Default: true.
        /// </summary>
        public bool EnableLiveFolderWatching { get; set; } = true;

        /// <summary>
        /// Whether the navigation tree expands down to the folder the active pane shows and selects it, wherever the
        /// folder was opened from (the file list, the address bar, Quick access, history). Off: the tree only selects the
        /// folder when its row is already visible. Default: true.
        /// </summary>
        public bool ExpandTreeToCurrentFolder { get; set; } = true;

        /// <summary>
        /// Whether the navigation tree follows the folder shown in the file list at all (selects its row, and expands to it
        /// when <see cref="ExpandTreeToCurrentFolder"/> is on). Off: the tree only changes when it is used itself. Default:
        /// true.
        /// </summary>
        public bool TreeFollowsCurrentFolder { get; set; } = true;

        /// <summary>
        /// How long a run of raw change notifications for an expanded
        /// folder must go quiet before the tree actually reloads that
        /// node's children. Exists so a bulk operation (a large copy,
        /// an extraction) that touches a folder hundreds of times in a
        /// few hundred milliseconds triggers one reload, not hundreds.
        /// Default: 500. Range: 100-5000.
        /// </summary>
        public int LiveRefreshDebounceMs { get; set; } = 500;

        /// <summary>
        /// Hard cap on how many expanded tree folders may have an
        /// active live watcher at the same time. The tree already only
        /// watches folders that are currently expanded (a bounded,
        /// visible scope on its own — collapsing a node releases its
        /// watcher), so this exists purely as defense against the edge
        /// case of a person expanding an unusually large number of
        /// nodes at once; a folder beyond the cap simply doesn't
        /// auto-refresh until something else is collapsed, rather than
        /// this app accumulating an unbounded number of OS watch
        /// handles. Default: 64. Range: 4-500.
        /// </summary>
        public int MaxLiveWatchedFolders { get; set; } = 64;

        /// <summary>
        /// The sidebar's Quick access, in order. Null until it is first set up (the defaults: the special folders that
        /// exist, and the trash, followed by any older <see cref="Bookmarks"/>). At most 200.
        /// </summary>
        public System.Collections.Generic.List<PinnedFolderSettings> Pinned { get; set; }

        /// <summary>
        /// Read only from older settings files: these folders were bookmarks before bookmarks and pins became one list; they
        /// are moved into <see cref="Pinned"/> once and no longer written.
        /// </summary>
        public System.Collections.Generic.List<string> Bookmarks { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Sidebar sections the person collapsed ("QuickAccess", "Network", "Recent").</summary>
        public System.Collections.Generic.List<string> CollapsedSidebarSections { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// The left panel's sections in the order chosen in the settings (keys: Places, QuickAccess, Recent, Drives, Tags,
        /// Network; see Views.Sidebar.SidebarOrder). Empty: each layout's own order. Sections a layout lacks are skipped.
        /// </summary>
        public System.Collections.Generic.List<string> SidebarSectionOrder { get; set; } = new System.Collections.Generic.List<string>();

        public const string SidebarDesignClassic = "Classic";
        public const string SidebarDesignQuiet = "Quiet";
        public const string SidebarDesignCards = "Cards";
        public const string SidebarDesignGuides = "Guides";
        public const string SidebarDesignChips = "Chips";

        /// <summary>The designs, in the order the settings list them.</summary>
        public static readonly string[] SidebarDesigns = { SidebarDesignClassic, SidebarDesignQuiet, SidebarDesignCards, SidebarDesignGuides, SidebarDesignChips };

        /// <summary>
        /// The left panel's look in the Modern layout (Settings › Left panel › Design): Classic, Quiet, Cards, Guides or
        /// Chips. Only the look; what the sections do is the same in each. Unknown values read as Classic.
        /// </summary>
        public string SidebarDesign { get; set; } = SidebarDesignClassic;

        /// <summary>
        /// Network locations the person added (sidebar "Network"): \\server\share on Windows; smb://, sftp://, ftp:// or
        /// dav:// addresses, or mounted paths, on Linux and macOS. At most 200.
        /// </summary>
        public System.Collections.Generic.List<string> NetworkLocations { get; set; } = new System.Collections.Generic.List<string>();

        public void Sanitize()
        {
            Bookmarks = CleanList(Bookmarks);
            NetworkLocations = CleanList(NetworkLocations);
            CollapsedSidebarSections = CleanList(CollapsedSidebarSections);
            SidebarSectionOrder = CleanList(SidebarSectionOrder);
            SidebarDesign = System.Array.Find(SidebarDesigns, d => string.Equals(d, SidebarDesign, System.StringComparison.OrdinalIgnoreCase)) ?? SidebarDesignClassic;
            Pinned = CleanPins(Pinned);

            if (NetworkDriveTimeoutMs < 500)
            {
                NetworkDriveTimeoutMs = 500;
            }
            else if (NetworkDriveTimeoutMs > 60000)
            {
                NetworkDriveTimeoutMs = 60000;
            }

            if (NetworkDriveProbeTimeoutMs < 250)
            {
                NetworkDriveProbeTimeoutMs = 250;
            }
            else if (NetworkDriveProbeTimeoutMs > 10000)
            {
                NetworkDriveProbeTimeoutMs = 10000;
            }

            if (LiveRefreshDebounceMs < 100)
            {
                LiveRefreshDebounceMs = 100;
            }
            else if (LiveRefreshDebounceMs > 5000)
            {
                LiveRefreshDebounceMs = 5000;
            }

            if (MaxLiveWatchedFolders < 4)
            {
                MaxLiveWatchedFolders = 4;
            }
            else if (MaxLiveWatchedFolders > 500)
            {
                MaxLiveWatchedFolders = 500;
            }
        }

        /// <summary>No blank paths, no duplicates (by the OS's path rules), names trimmed, unknown colors dropped, at most 200.</summary>
        private static System.Collections.Generic.List<PinnedFolderSettings> CleanPins(System.Collections.Generic.List<PinnedFolderSettings> pins)
        {
            if (pins == null)
            {
                return null; // not set up yet: the app creates the defaults
            }

            var clean = new System.Collections.Generic.List<PinnedFolderSettings>();
            var seen = new System.Collections.Generic.HashSet<string>(PathUtil.Comparer);
            foreach (PinnedFolderSettings pin in pins)
            {
                string path = pin?.Path?.Trim();
                if (string.IsNullOrEmpty(path) || clean.Count >= 200 || !seen.Add(path))
                {
                    continue;
                }

                string name = pin.Name?.Trim();
                clean.Add(new PinnedFolderSettings
                {
                    Path = path,
                    Name = string.IsNullOrEmpty(name) ? null : name.Length > 100 ? name.Substring(0, 100) : name,
                    Color = PinColors.Normalize(pin.Color)
                });
            }

            return clean;
        }

        /// <summary>No blanks, no duplicates (exact text), at most 200 entries, order kept.</summary>
        private static System.Collections.Generic.List<string> CleanList(System.Collections.Generic.List<string> list)
        {
            var clean = new System.Collections.Generic.List<string>();
            if (list == null)
            {
                return clean;
            }

            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (string item in list)
            {
                string trimmed = item?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed) && clean.Count < 200)
                {
                    clean.Add(trimmed);
                }
            }

            return clean;
        }
    }
}
