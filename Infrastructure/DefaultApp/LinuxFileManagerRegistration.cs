// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ByteExplorer.Infrastructure.DefaultApp
{
    /// <summary>
    /// Makes ByteExplorer the desktop's file manager on Linux, for the current user: a desktop entry
    /// (~/.local/share/applications/byteexplorer-filemanager.desktop, MimeType inode/directory) made the default for
    /// folders with <c>xdg-mime default</c>, which GNOME, KDE, Xfce, Cinnamon and the "Open folder" of other programs
    /// follow (xdg-open, the file chooser's "Open containing folder", a browser's "Show in folder").
    /// <para>
    /// The handler before it is saved (default-file-manager.json in the data folder) and given back by
    /// <see cref="Unregister"/>; with none saved, ByteExplorer's line is taken out of ~/.config/mimeapps.list so the
    /// desktop's own default applies again. Commands run through a delegate (tests use a fake).
    /// </para>
    /// </summary>
    public sealed class LinuxFileManagerRegistration
    {
        public const string DesktopFileName = "byteexplorer-filemanager.desktop";
        private const string DirectoryMimeType = "inode/directory";

        private readonly Func<string, string[], (int ExitCode, string Output)> _run;
        private readonly string _applicationsDirectory;
        private readonly string _configDirectory;
        private readonly string _backupFile;

        /// <param name="run">Runs a program (found on PATH) with arguments: its exit code and standard output.</param>
        public LinuxFileManagerRegistration(Func<string, string[], (int ExitCode, string Output)> run, string applicationsDirectory, string configDirectory, string backupFile)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _applicationsDirectory = applicationsDirectory;
            _configDirectory = configDirectory;
            _backupFile = backupFile;
        }

        public string DesktopFilePath => Path.Combine(_applicationsDirectory, DesktopFileName);

        /// <summary>The desktop file folders open with now ("org.gnome.Nautilus.desktop"), or null when unknown.</summary>
        public string CurrentHandler
        {
            get
            {
                (int code, string output) = _run("xdg-mime", new[] { "query", "default", DirectoryMimeType });
                string handler = code == 0 ? output?.Trim() : null;
                return string.IsNullOrEmpty(handler) ? null : handler;
            }
        }

        public bool IsRegistered => string.Equals(CurrentHandler, DesktopFileName, StringComparison.Ordinal) && File.Exists(DesktopFilePath);

        /// <summary>The Exec line of the registered desktop entry (null when there is none).</summary>
        public string RegisteredExec =>
            File.Exists(DesktopFilePath)
                ? File.ReadLines(DesktopFilePath).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))?.Substring(5)
                : null;

        /// <summary>Writes the desktop entry for <paramref name="program"/> and makes it the folders' default.</summary>
        public void Register(IReadOnlyList<string> program, string iconPath)
        {
            string previous = CurrentHandler;
            if (!File.Exists(_backupFile) && previous != DesktopFileName)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_backupFile)!);
                File.WriteAllText(_backupFile, JsonSerializer.Serialize(new Backup { Previous = previous }));
            }

            Directory.CreateDirectory(_applicationsDirectory);
            File.WriteAllText(DesktopFilePath, BuildDesktopEntry(program, iconPath), new UTF8Encoding(false));
            _run("update-desktop-database", new[] { _applicationsDirectory }); // optional: refreshes the desktop's cache where installed

            (int code, _) = _run("xdg-mime", new[] { "default", DesktopFileName, DirectoryMimeType });
            if (code != 0 || !IsRegistered)
            {
                throw new IOException("xdg-mime did not make ByteExplorer the default for folders (exit code " + code + ").");
            }
        }

        /// <summary>Gives folders back to the file manager they had before, and removes the desktop entry.</summary>
        public void Unregister()
        {
            string previous = ReadBackup()?.Previous;
            if (!string.IsNullOrEmpty(previous) && previous != DesktopFileName)
            {
                _run("xdg-mime", new[] { "default", previous, DirectoryMimeType });
            }

            RemoveFromMimeApps();
            if (File.Exists(DesktopFilePath))
            {
                File.Delete(DesktopFilePath);
            }

            _run("update-desktop-database", new[] { _applicationsDirectory });
            if (File.Exists(_backupFile))
            {
                File.Delete(_backupFile);
            }
        }

        /// <summary>Takes ByteExplorer's entry out of the user's mimeapps.list (what is left falls back to the desktop's default).</summary>
        private void RemoveFromMimeApps()
        {
            string file = Path.Combine(_configDirectory, "mimeapps.list");
            if (!File.Exists(file))
            {
                return;
            }

            var lines = File.ReadAllLines(file).ToList();
            bool changed = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                int equals = line.IndexOf('=');
                if (equals < 0 || !line.Contains(DesktopFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                string[] handlers = line.Substring(equals + 1).Split(';', StringSplitOptions.RemoveEmptyEntries);
                string[] kept = handlers.Where(h => h != DesktopFileName).ToArray();
                string key = line.Substring(0, equals);
                if (kept.Length == 0)
                {
                    lines.RemoveAt(i--);
                }
                else
                {
                    lines[i] = key + "=" + string.Join(";", kept) + ";";
                }

                changed = true;
            }

            if (changed)
            {
                File.WriteAllLines(file, lines);
            }
        }

        private Backup ReadBackup()
        {
            try
            {
                return File.Exists(_backupFile) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(_backupFile)) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }

        private sealed class Backup
        {
            public string Previous { get; set; }
        }

        /// <summary>The desktop entry: Exec quoted by the Desktop Entry Specification's rules; %U gives the folder (as a file:// URI or a path).</summary>
        internal static string BuildDesktopEntry(IReadOnlyList<string> program, string iconPath)
        {
            string exec = string.Join(" ", program.Select(QuoteExecArgument)) + " " + Launch.LaunchRequest.FromShellSwitch + " %U";
            var entry = new StringBuilder();
            entry.Append("[Desktop Entry]\n");
            entry.Append("Type=Application\n");
            entry.Append("Name=ByteExplorer\n");
            entry.Append("GenericName=File Manager\n");
            entry.Append("Comment=Browse files and folders\n");
            entry.Append("Exec=").Append(EscapeString(exec)).Append('\n');
            entry.Append("Icon=").Append(EscapeString(string.IsNullOrEmpty(iconPath) ? "system-file-manager" : iconPath)).Append('\n');
            entry.Append("Terminal=false\n");
            entry.Append("Categories=System;FileTools;FileManager;\n");
            entry.Append("MimeType=inode/directory;\n");
            entry.Append("StartupNotify=true\n");
            return entry.ToString();
        }

        /// <summary>An Exec argument: quoted when it has reserved characters, with ", `, $ and \ escaped inside, and % doubled.</summary>
        internal static string QuoteExecArgument(string argument)
        {
            string text = argument.Replace("%", "%%");
            if (text.Length > 0 && text.IndexOfAny(" \t\n\"'\\><~|&;$*?#()`".ToCharArray()) < 0)
            {
                return text;
            }

            var quoted = new StringBuilder("\"");
            foreach (char c in text)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    quoted.Append('\\');
                }

                quoted.Append(c);
            }

            return quoted.Append('"').ToString();
        }

        /// <summary>A desktop file's string value: backslashes doubled (the format's own escape, applied after the Exec quoting).</summary>
        private static string EscapeString(string value) => value.Replace("\\", "\\\\");
    }
}
