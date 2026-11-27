// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using ByteExplorer.Core;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Lens
{
    /// <summary>
    /// One lens: a smart view over every indexed drive, defined as a search (the index's own filters: <c>modified:</c>,
    /// <c>kind:</c>, <c>size:</c>), optionally inside one folder. Files only, newest first.
    /// </summary>
    public sealed class LensDefinition
    {
        public LensDefinition(string id, string name, string description, Symbol icon, string query, Func<string> scope = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Icon = icon;
            Query = query;
            Scope = scope;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>The line under the title ("Every file changed today, from every folder").</summary>
        public string Description { get; }

        public Symbol Icon { get; }

        /// <summary>The search that defines it, in the search box's syntax.</summary>
        public string Query { get; }

        /// <summary>The folder it is limited to (the Downloads lens), or null for everywhere.</summary>
        public Func<string> Scope { get; }
    }

    /// <summary>The lenses, and the folders every lens leaves out.</summary>
    public static class LensCatalog
    {
        public static IReadOnlyList<LensDefinition> All { get; } = new[]
        {
            new LensDefinition("today", "Today", "Every file changed today, from every folder", Symbol.History, "modified:today"),
            new LensDefinition("week", "This week", "Files changed in the last seven days", Symbol.CalendarWorkWeek, "modified:week"),
            new LensDefinition("month", "This month", "Files changed in the last thirty days", Symbol.CalendarMonth, "modified:month"),
            new LensDefinition("photos", "Photos", "Every picture on your drives, newest first", Symbol.Image, "kind:image"),
            new LensDefinition("videos", "Videos", "Every video on your drives, newest first", Symbol.Video, "kind:video"),
            new LensDefinition("music", "Music", "Every song and recording, newest first", Symbol.MusicNote2, "kind:audio"),
            new LensDefinition("documents", "Documents", "Documents, spreadsheets, presentations and PDFs", Symbol.DocumentText, "kind:document"),
            new LensDefinition("archives", "Archives", "ZIP, 7z, RAR and other archives", Symbol.FolderZip, "kind:archive"),
            new LensDefinition("large", "Large files", "Files of 1 GB or more: where the space went", Symbol.Storage, "size:>1gb"),
            new LensDefinition("downloads", "Downloads", "What arrived in Downloads in the last thirty days", Symbol.ArrowDownload,
                "modified:month", () => SpecialFolders.Downloads),
        };

        /// <summary>
        /// Folders whose contents no lens shows: the operating system, installed programs and application data. Without
        /// them "Photos" would be every program's icons and "Today" every log and cache file the system wrote.
        /// </summary>
        public static IReadOnlyList<string> SystemFolders { get; } = BuildSystemFolders();

        private static IReadOnlyList<string> BuildSystemFolders()
        {
            var folders = new List<string>();
            void Add(string path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    folders.Add(PathUtil.TrimTrailingSeparator(path));
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
                Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
                Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                string systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (!string.IsNullOrEmpty(systemDrive))
                {
                    Add(Path.Combine(systemDrive, "$Recycle.Bin"));
                    Add(Path.Combine(systemDrive, "System Volume Information"));
                }
            }
            else
            {
                foreach (string system in new[] { "/usr", "/proc", "/sys", "/dev", "/var", "/opt", "/snap", "/etc", "/boot", "/lib", "/lib64", "/bin", "/sbin", "/run", "/tmp" })
                {
                    Add(system);
                }

                if (!string.IsNullOrEmpty(home))
                {
                    Add(Path.Combine(home, ".cache"));
                    Add(Path.Combine(home, ".local"));
                    Add(Path.Combine(home, ".config"));
                }

                if (OperatingSystem.IsMacOS())
                {
                    Add("/System");
                    Add("/Library");
                    Add("/Applications");
                    Add("/private");
                    if (!string.IsNullOrEmpty(home))
                    {
                        Add(Path.Combine(home, "Library"));
                    }
                }
            }

            return folders;
        }
    }
}
