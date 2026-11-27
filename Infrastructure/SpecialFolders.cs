// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The user's well-known folders on every OS.
    /// <para>
    /// Windows and macOS: <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, plus ~/Downloads (no
    /// SpecialFolder value exists for it).
    /// </para>
    /// <para>
    /// Linux: the XDG user directories (~/.config/user-dirs.dirs, written by xdg-user-dirs and localized: "Bilder",
    /// "Téléchargements", ...). .NET reads that file for Desktop, Music, Pictures and Videos but maps MyDocuments to the
    /// home folder itself and has no Downloads at all, so all six are read here the same way, falling back to the English
    /// default under the home folder.
    /// </para>
    /// <para>
    /// <see cref="RecycleBin"/>: the trash as a browsable folder where one exists (the freedesktop.org home trash's files/
    /// folder on Linux, ~/.Trash on macOS); on Windows the Recycle Bin is a shell namespace, not a folder, so it is null
    /// and the caller opens it in Explorer.
    /// </para>
    /// </summary>
    public static class SpecialFolders
    {
        private static readonly Lazy<Dictionary<string, string>> XdgUserDirs = new Lazy<Dictionary<string, string>>(ReadXdgUserDirs);

        public static string Desktop => Resolve("XDG_DESKTOP_DIR", "Desktop", Environment.SpecialFolder.DesktopDirectory);
        public static string Documents => Resolve("XDG_DOCUMENTS_DIR", "Documents", Environment.SpecialFolder.MyDocuments);
        public static string Pictures => Resolve("XDG_PICTURES_DIR", "Pictures", Environment.SpecialFolder.MyPictures);
        public static string Music => Resolve("XDG_MUSIC_DIR", "Music", Environment.SpecialFolder.MyMusic);
        public static string Videos => Resolve("XDG_VIDEOS_DIR", OperatingSystem.IsMacOS() ? "Movies" : "Videos", Environment.SpecialFolder.MyVideos);
        public static string Downloads => Resolve("XDG_DOWNLOAD_DIR", "Downloads", null);

        public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>The trash as a folder (Linux/macOS), or null on Windows.</summary>
        public static string RecycleBin
        {
            get
            {
                if (OperatingSystem.IsWindows())
                {
                    return null;
                }

                if (OperatingSystem.IsMacOS())
                {
                    return Path.Combine(Home, ".Trash");
                }

                string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
                {
                    dataHome = Path.Combine(Home, ".local", "share");
                }

                return Path.Combine(dataHome, "Trash", "files");
            }
        }

        private static string Resolve(string xdgKey, string defaultName, Environment.SpecialFolder? folder)
        {
            string home = Home;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            {
                if (XdgUserDirs.Value.TryGetValue(xdgKey, out string configured) && !string.IsNullOrEmpty(configured))
                {
                    return configured;
                }

                return string.IsNullOrEmpty(home) ? null : Path.Combine(home, defaultName);
            }

            if (folder.HasValue)
            {
                string path = Environment.GetFolderPath(folder.Value);
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }

            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, defaultName);
        }

        /// <summary>
        /// Parses user-dirs.dirs: lines of XDG_xxx_DIR="$HOME/Name" or an absolute path, '#' comments. A value equal to
        /// $HOME itself means "disabled" in the spec; it is skipped so the default name is used instead.
        /// </summary>
        private static Dictionary<string, string> ReadXdgUserDirs()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string home = Home;
            string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome))
            {
                configHome = Path.Combine(home ?? string.Empty, ".config");
            }

            string file = Path.Combine(configHome, "user-dirs.dirs");
            string[] lines;
            try
            {
                if (!File.Exists(file))
                {
                    return result;
                }

                lines = File.ReadAllLines(file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return result;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim().Trim('"');
                if (value.StartsWith("$HOME", StringComparison.Ordinal))
                {
                    value = (home ?? string.Empty) + value.Substring("$HOME".Length);
                }

                if (!Path.IsPathRooted(value))
                {
                    continue;
                }

                string trimmed = value.TrimEnd('/');
                if (trimmed.Length > 0 && !string.Equals(trimmed, home?.TrimEnd('/'), StringComparison.Ordinal))
                {
                    result[key] = trimmed;
                }
            }

            return result;
        }
    }
}
