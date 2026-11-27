// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Search index behavior: the rebuild schedule, live updates and exclusions. LastBuildUtc is the app's own record of
    /// the last completed build (for the schedule), not meant for hand-editing.
    /// </summary>
    public sealed class IndexSettings
    {
        /// <summary>
        /// Whether the search index rebuilds itself automatically on a
        /// schedule, in addition to the normal "load if valid, build if
        /// not" behavior every app start already has. Turning this off
        /// still leaves manual rebuild (the search icon's "Refresh
        /// Index") fully available. Default: true.
        /// </summary>
        public bool EnableAutoRebuild { get; set; } = true;

        /// <summary>
        /// How often the index automatically rebuilds itself in the
        /// background, measured from the last successful build. A
        /// rebuild never blocks or slows down the app while it runs —
        /// search keeps using the previous complete index the entire
        /// time, swapping to the new one only once it's fully ready
        /// (see SearchIndex's own remarks on atomic snapshot publishing).
        /// It does consume real background CPU and disk I/O for its
        /// duration, though, which on a very large drive can be
        /// several minutes — lower this if that matters on your
        /// hardware, raise it if your files rarely change. Default: 6.
        /// Range: 0.5-720 (30 days).
        /// </summary>
        public double AutoRebuildIntervalHours { get; set; } = 6.0;

        /// <summary>
        /// Whether the index follows changes on disk between builds (file system watchers on the indexed drives, on top of
        /// the app's own operations and the folders being viewed, which always update it). On Linux, where watching a tree
        /// costs one kernel watch per folder, it is used only when the indexed folders fit well within the system's limit.
        /// Default: true.
        /// </summary>
        public bool WatchForChanges { get; set; } = true;

        /// <summary>
        /// The places to index (full paths). Empty: automatic, every local, removable and network drive on Windows, the
        /// home folder and mounted data volumes on Linux and macOS.
        /// </summary>
        public System.Collections.Generic.List<string> Locations { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Folders never indexed, with everything inside them (full paths).</summary>
        public System.Collections.Generic.List<string> ExcludedFolders { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>File and folder names never indexed, wherever they are (case-insensitive), on top of the system's own (Recycle Bin, System Volume Information...).</summary>
        public System.Collections.Generic.List<string> ExcludedNames { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// Whether the text inside files is indexed too (plain text and source code, Word/Excel/PowerPoint, OpenDocument,
        /// RTF and PDF), so <c>content:</c> searches and "Search file contents" work. Read in the background at low
        /// priority, only files that changed, outside system folders and hidden places. Turning it off deletes the
        /// content index. Default: true.
        /// </summary>
        public bool IndexFileContents { get; set; } = true;

        /// <summary>
        /// Whether the words typed in the search box also find files by their contents (after the files whose names match).
        /// Needs IndexFileContents; the content: filter works either way. Default: true.
        /// </summary>
        public bool SearchFileContents { get; set; } = true;

        /// <summary>Files larger than this (megabytes) are not read for the content index. Default: 20. Range: 1-500.</summary>
        public int ContentMaxFileSizeMB { get; set; } = 20;

        /// <summary>
        /// UTC timestamp of the last successful build, round-trip
        /// ("o") formatted, or empty if none has ever succeeded. Stored
        /// as a plain string rather than a native JSON date type to
        /// keep this hand-written settings file's serializer simple —
        /// parsed with DateTime.TryParse wherever it's read, which
        /// degrades to "treat as never built" on anything unparseable
        /// rather than throwing.
        /// </summary>
        public string LastBuildUtc { get; set; } = string.Empty;

        public void Sanitize()
        {
            if (double.IsNaN(AutoRebuildIntervalHours) || AutoRebuildIntervalHours < 0.5)
            {
                AutoRebuildIntervalHours = 0.5;
            }
            else if (AutoRebuildIntervalHours > 720.0)
            {
                AutoRebuildIntervalHours = 720.0;
            }

            ContentMaxFileSizeMB = Math.Clamp(ContentMaxFileSizeMB, 1, 500);
            LastBuildUtc = LastBuildUtc ?? string.Empty;
            ExcludedFolders ??= new System.Collections.Generic.List<string>();
            Locations ??= new System.Collections.Generic.List<string>();
            ExcludedNames ??= new System.Collections.Generic.List<string>();
        }

        /// <summary>Parses LastBuildUtc, returning DateTime.MinValue for anything empty or unparseable rather than throwing.</summary>
        public DateTime GetLastBuildUtcOrMinValue()
        {
            if (string.IsNullOrEmpty(LastBuildUtc))
            {
                return DateTime.MinValue;
            }

            return DateTime.TryParse(
                LastBuildUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed
                : DateTime.MinValue;
        }
    }
}
