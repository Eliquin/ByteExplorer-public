// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ByteExplorer.Core.Platform
{
    /// <summary>An application that can open a file, as listed in the custom "Open with" menu.</summary>
    public sealed class OpenWithApplication
    {
        /// <summary>Stands for the file's path in <see cref="Arguments"/>.</summary>
        public const string PathPlaceholder = "\u0000path\u0000";

        public OpenWithApplication(string id, string name, string executable, IReadOnlyList<string> arguments, bool isDefault)
        {
            Id = id;
            Name = name;
            Executable = executable;
            Arguments = arguments;
            IsDefault = isDefault;
        }

        /// <summary>Desktop file id ("org.gnome.TextEditor.desktop") or bundle path; stable across calls.</summary>
        public string Id { get; }
        public string Name { get; }
        public string Executable { get; }
        public IReadOnlyList<string> Arguments { get; }
        public bool IsDefault { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// The applications registered for a file's type.
    /// <para>
    /// Linux: the freedesktop.org specifications GNOME, KDE and the others share. The file's MIME type comes from
    /// shared-mime-info's globs2 (by name), with /etc/mime.types as a fallback; the applications are the .desktop entries
    /// in $XDG_DATA_HOME/applications and each $XDG_DATA_DIRS/applications (so Flatpak and Snap exports are included)
    /// whose MimeType lists that type or its media-type wildcard; the default comes from mimeapps.list ([Default
    /// Applications], user file first). Exec lines are parsed per the Desktop Entry spec (quoting, %f/%F/%u/%U, the
    /// deprecated codes dropped). Entries are read once and cached for a minute, so building the right-click menu does no
    /// disk work.
    /// </para>
    /// <para>
    /// Windows and macOS: an empty list. Windows has its own "Open with" dialog (<see cref="ShellLauncher.TryShowSystemOpenWithDialog"/>);
    /// macOS keeps type bindings in LaunchServices, which has no file-based registry, so the menu offers "Choose
    /// application…" (an .app picked from /Applications, started with "open -a").
    /// </para>
    /// </summary>
    public static class OpenWithApplications
    {
        private const int MaxApplications = 12;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
        private static readonly object CacheLock = new object();
        private static Registry _cache;
        private static long _cacheTicks;

        /// <summary>The applications for this file, the default first; empty if none or not on Linux.</summary>
        public static IReadOnlyList<OpenWithApplication> For(string path)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            {
                return Array.Empty<OpenWithApplication>();
            }

            try
            {
                Registry registry = GetRegistry();
                string mime = Directory.Exists(path) ? "inode/directory" : registry.MimeTypeOf(PathUtil.GetName(path));
                return registry.ApplicationsFor(mime);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Serilog.Log.Logger.Debug(ex, "Could not read the desktop application registry");
                return Array.Empty<OpenWithApplication>();
            }
        }

        /// <summary>Splits a desktop entry's Exec value into argv (exposed for tests).</summary>
        /// <summary>
        /// The file's MIME type by name, from the same shared-mime-info tables as <see cref="For"/> ("inode/directory" for
        /// a folder); null when not on Linux/FreeBSD or the tables cannot be read.
        /// </summary>
        public static string MimeTypeFor(string path)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            {
                return null;
            }

            try
            {
                return Directory.Exists(path) ? "inode/directory" : GetRegistry().MimeTypeOf(PathUtil.GetName(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Serilog.Log.Logger.Debug(ex, "Could not read the MIME tables");
                return null;
            }
        }

        internal static List<string> ParseDesktopExec(string exec) => Registry.ParseExec(exec);

        /// <summary>Starts a macOS application bundle on the file ("open -a App.app file").</summary>
        public static OpenWithApplication ForMacBundle(string bundlePath) =>
            new OpenWithApplication(bundlePath, Path.GetFileNameWithoutExtension(bundlePath), "/usr/bin/open",
                new[] { "-a", bundlePath, OpenWithApplication.PathPlaceholder }, isDefault: false);

        private static Registry GetRegistry()
        {
            lock (CacheLock)
            {
                long now = Environment.TickCount64;
                if (_cache == null || now - _cacheTicks > (long)CacheLifetime.TotalMilliseconds)
                {
                    _cache = Registry.Load();
                    _cacheTicks = now;
                }

                return _cache;
            }
        }

        private sealed class DesktopEntry
        {
            public string Id;
            public string Name;
            public string Executable;
            public List<string> Arguments;
            public string[] MimeTypes;
        }

        private sealed class Registry
        {
            private readonly Dictionary<string, DesktopEntry> _entries = new Dictionary<string, DesktopEntry>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<string>> _defaults = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> _added = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _extensionMimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _literalMimes = new Dictionary<string, string>(StringComparer.Ordinal);

            public static Registry Load()
            {
                var registry = new Registry();
                List<string> dataDirs = DataDirectories();

                // Lowest priority first, so the user's own files overwrite system ones with the same id.
                for (int i = dataDirs.Count - 1; i >= 0; i--)
                {
                    registry.LoadMimeGlobs(Path.Combine(dataDirs[i], "mime", "globs2"));
                    registry.LoadDesktopEntries(Path.Combine(dataDirs[i], "applications"));
                }

                registry.LoadMimeTypesFile("/etc/mime.types");

                foreach (string file in MimeAppsListFiles(dataDirs).Reverse())
                {
                    registry.LoadMimeAppsList(file);
                }

                return registry;
            }

            public string MimeTypeOf(string fileName)
            {
                if (_literalMimes.TryGetValue(fileName, out string literal))
                {
                    return literal;
                }

                // Longest matching extension wins (".tar.gz" before ".gz"), as shared-mime-info does.
                for (int dot = fileName.IndexOf('.'); dot >= 0 && dot < fileName.Length - 1; dot = fileName.IndexOf('.', dot + 1))
                {
                    if (_extensionMimes.TryGetValue(fileName.Substring(dot), out string mime))
                    {
                        return mime;
                    }
                }

                return "application/octet-stream";
            }

            public IReadOnlyList<OpenWithApplication> ApplicationsFor(string mime)
            {
                var ordered = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string defaultId = null;

                void AddIds(IEnumerable<string> ids)
                {
                    foreach (string id in ids)
                    {
                        if (_entries.ContainsKey(id) && seen.Add(id))
                        {
                            ordered.Add(id);
                        }
                    }
                }

                if (_defaults.TryGetValue(mime, out List<string> defaults))
                {
                    AddIds(defaults);
                    defaultId = ordered.FirstOrDefault();
                }

                if (_added.TryGetValue(mime, out List<string> added))
                {
                    AddIds(added);
                }

                string wildcard = mime.Contains('/') ? mime.Substring(0, mime.IndexOf('/')) + "/*" : null;
                AddIds(_entries.Values
                    .Where(e => e.MimeTypes.Any(m => string.Equals(m, mime, StringComparison.OrdinalIgnoreCase) ||
                                                     (wildcard != null && string.Equals(m, wildcard, StringComparison.OrdinalIgnoreCase))))
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => e.Id));

                var result = new List<OpenWithApplication>(Math.Min(ordered.Count, MaxApplications));
                foreach (string id in ordered.Take(MaxApplications))
                {
                    DesktopEntry entry = _entries[id];
                    result.Add(new OpenWithApplication(entry.Id, entry.Name, entry.Executable, entry.Arguments, id == defaultId));
                }

                return result;
            }

            private void LoadDesktopEntries(string applicationsDir)
            {
                if (!Directory.Exists(applicationsDir))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(applicationsDir, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    // The desktop file id: the path below applications/ with '/' replaced by '-'.
                    string id = Path.GetRelativePath(applicationsDir, file).Replace(Path.DirectorySeparatorChar, '-');
                    DesktopEntry entry = ParseDesktopEntry(file, id);
                    if (entry == null)
                    {
                        _entries.Remove(id); // a hidden user entry masks the system one
                    }
                    else
                    {
                        _entries[id] = entry;
                    }
                }
            }

            private static DesktopEntry ParseDesktopEntry(string file, string id)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }

                bool inMainGroup = false;
                string name = null, localizedName = null, exec = null, mimeTypes = null, type = null, tryExec = null;
                bool hidden = false;
                string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        inMainGroup = line == "[Desktop Entry]";
                        continue;
                    }

                    if (!inMainGroup)
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    switch (key)
                    {
                        case "Name": name = value; break;
                        case "Exec": exec = value; break;
                        case "MimeType": mimeTypes = value; break;
                        case "Type": type = value; break;
                        case "TryExec": tryExec = value; break;
                        case "Hidden": hidden |= value == "true"; break;
                        case "NoDisplay": break; // still a valid "Open with" target (many viewers set it)
                        default:
                            if (key == "Name[" + language + "]")
                            {
                                localizedName = value;
                            }

                            break;
                    }
                }

                if (hidden || type != "Application" || string.IsNullOrEmpty(exec) || string.IsNullOrEmpty(mimeTypes))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(tryExec) && ShellLauncher.FindOnPath(tryExec) == null)
                {
                    return null;
                }

                List<string> argv = ParseExec(exec);
                if (argv.Count == 0)
                {
                    return null;
                }

                string executable = ShellLauncher.FindOnPath(argv[0]);
                if (executable == null)
                {
                    return null;
                }

                return new DesktopEntry
                {
                    Id = id,
                    Name = localizedName ?? name ?? Path.GetFileNameWithoutExtension(id),
                    Executable = executable,
                    Arguments = argv.Skip(1).ToList(),
                    MimeTypes = mimeTypes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                };
            }

            /// <summary>
            /// Splits an Exec value into argv per the Desktop Entry spec: double quotes group, backslash escapes inside
            /// them, "%%" is a literal '%', %f %F %u %U become the path (once), %i %c %k and the deprecated codes vanish.
            /// </summary>
            internal static List<string> ParseExec(string exec)
            {
                var argv = new List<string>();
                var current = new StringBuilder();
                bool inQuotes = false, hasToken = false, pathPlaced = false;

                for (int i = 0; i < exec.Length; i++)
                {
                    char c = exec[i];
                    if (inQuotes)
                    {
                        if (c == '\\' && i + 1 < exec.Length)
                        {
                            current.Append(exec[++i]);
                        }
                        else if (c == '"')
                        {
                            inQuotes = false;
                        }
                        else
                        {
                            current.Append(c);
                        }

                        continue;
                    }

                    if (c == '"')
                    {
                        inQuotes = true;
                        hasToken = true;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        if (hasToken)
                        {
                            argv.Add(current.ToString());
                            current.Clear();
                            hasToken = false;
                        }
                    }
                    else if (c == '%' && i + 1 < exec.Length)
                    {
                        char code = exec[++i];
                        if (code == '%')
                        {
                            current.Append('%');
                            hasToken = true;
                        }
                        else if (code == 'f' || code == 'F' || code == 'u' || code == 'U')
                        {
                            if (!pathPlaced)
                            {
                                if (current.Length > 0)
                                {
                                    argv.Add(current.ToString());
                                    current.Clear();
                                }

                                argv.Add(OpenWithApplication.PathPlaceholder);
                                pathPlaced = true;
                                hasToken = false;
                            }
                        }

                        // Every other field code (%i %c %k %d %D %n %N %v %m) is dropped.
                    }
                    else
                    {
                        current.Append(c);
                        hasToken = true;
                    }
                }

                if (hasToken)
                {
                    argv.Add(current.ToString());
                }

                return argv;
            }

            private void LoadMimeGlobs(string globs2)
            {
                if (!File.Exists(globs2))
                {
                    return;
                }

                foreach (string line in ReadLinesSafe(globs2))
                {
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    // weight:mime:glob[:flags]
                    string[] parts = line.Split(':');
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    string mime = parts[1];
                    string glob = parts[2];
                    if (glob.StartsWith("*.", StringComparison.Ordinal) && glob.IndexOfAny(new[] { '*', '?', '[' }, 1) < 0)
                    {
                        _extensionMimes.TryAdd(glob.Substring(1), mime); // globs2 is sorted by weight, first wins
                    }
                    else if (glob.IndexOfAny(new[] { '*', '?', '[' }) < 0)
                    {
                        _literalMimes.TryAdd(glob, mime);
                    }
                }
            }

            private void LoadMimeTypesFile(string path)
            {
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string line in ReadLinesSafe(path))
                {
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        _extensionMimes.TryAdd("." + parts[i], parts[0]);
                    }
                }
            }

            private void LoadMimeAppsList(string path)
            {
                Dictionary<string, List<string>> target = null;
                foreach (string raw in ReadLinesSafe(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        target = line == "[Default Applications]" ? _defaults : line == "[Added Associations]" ? _added : null;
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (target == null || equals <= 0)
                    {
                        continue;
                    }

                    // Higher-priority files are loaded last and go in front.
                    string mime = line.Substring(0, equals).Trim();
                    var ids = line.Substring(equals + 1).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (target.TryGetValue(mime, out List<string> existing))
                    {
                        ids.AddRange(existing.Where(e => !ids.Contains(e)));
                    }

                    target[mime] = ids;
                }
            }

            private static IEnumerable<string> ReadLinesSafe(string path)
            {
                try
                {
                    return File.ReadAllLines(path, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return Array.Empty<string>();
                }
            }

            /// <summary>$XDG_DATA_HOME then $XDG_DATA_DIRS, highest priority first.</summary>
            private static List<string> DataDirectories()
            {
                var dirs = new List<string>();
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                dirs.Add(!string.IsNullOrEmpty(dataHome) && Path.IsPathRooted(dataHome) ? dataHome : Path.Combine(home, ".local", "share"));

                string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
                if (string.IsNullOrWhiteSpace(dataDirs))
                {
                    dataDirs = "/usr/local/share:/usr/share";
                }

                foreach (string dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Path.IsPathRooted(dir) && !dirs.Contains(dir))
                    {
                        dirs.Add(dir);
                    }
                }

                return dirs;
            }

            /// <summary>mimeapps.list files, highest priority first (the spec's lookup order, desktop-specific files omitted).</summary>
            private static IEnumerable<string> MimeAppsListFiles(List<string> dataDirs)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome))
                {
                    configHome = Path.Combine(home, ".config");
                }

                var files = new List<string> { Path.Combine(configHome, "mimeapps.list") };
                string configDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
                foreach (string dir in (string.IsNullOrWhiteSpace(configDirs) ? "/etc/xdg" : configDirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    files.Add(Path.Combine(dir, "mimeapps.list"));
                }

                foreach (string dir in dataDirs)
                {
                    files.Add(Path.Combine(dir, "applications", "mimeapps.list"));
                    files.Add(Path.Combine(dir, "applications", "defaults.list"));
                }

                return files.Where(File.Exists);
            }
        }
    }
}
