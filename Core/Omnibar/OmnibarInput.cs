// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ByteExplorer.Core.Omnibar
{
    public enum OmnibarInputKind
    {
        /// <summary>Nothing typed.</summary>
        Empty,

        /// <summary>"&gt; new folder": a command.</summary>
        Command,

        /// <summary>"? invoice": a search.</summary>
        Search,

        /// <summary>http:// or https://: opened in the browser.</summary>
        WebAddress,

        /// <summary>smb://, sftp://, … (Linux, macOS): connected, then browsed.</summary>
        NetworkAddress,

        /// <summary>Something that reads as a path: rooted, ~, ., .., an environment variable, or containing a separator.</summary>
        Path,

        /// <summary>Plain words: matched against places, history, the current folder and commands.</summary>
        Text
    }

    /// <summary>
    /// What the person typed in the omnibar, classified and, for paths, resolved: quotes stripped (pasted from a
    /// terminal), file:// addresses turned into paths, "~" expanded to the home folder, environment variables expanded
    /// (%USERPROFILE% on Windows, $HOME and ${HOME} elsewhere), relative paths taken from the current folder, ".." and "."
    /// collapsed. Then split into the folder to list and the partial name being typed, for completion.
    /// Pure: no file system access.
    /// </summary>
    public sealed class OmnibarInput
    {
        private static readonly Regex UnixVariable = new Regex(@"\$(\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly string[] NetworkSchemes = { "smb", "sftp", "ssh", "ftp", "ftps", "dav", "davs", "nfs", "afp" };

        private OmnibarInput(string raw, OmnibarInputKind kind, string query)
        {
            Raw = raw;
            Kind = kind;
            Query = query;
        }

        /// <summary>The text as typed.</summary>
        public string Raw { get; }

        public OmnibarInputKind Kind { get; }

        /// <summary>The text without its prefix and surrounding blanks ("&gt; new" → "new").</summary>
        public string Query { get; }

        /// <summary>Paths: the resolved full path.</summary>
        public string FullPath { get; private set; }

        /// <summary>Paths: the folder whose entries complete the partial name.</summary>
        public string Folder { get; private set; }

        /// <summary>Paths: the last, partly typed name ("" right after a separator).</summary>
        public string Partial { get; private set; }

        /// <summary>Paths: true when the text ends with a separator ("C:\Users\").</summary>
        public bool EndsWithSeparator { get; private set; }

        public static OmnibarInput Parse(string raw, string currentFolder, string homeFolder)
        {
            string text = Unquote(raw?.Trim() ?? string.Empty);
            if (text.Length == 0)
            {
                return new OmnibarInput(raw, OmnibarInputKind.Empty, string.Empty);
            }

            if (text[0] == '>')
            {
                return new OmnibarInput(raw, OmnibarInputKind.Command, text.Substring(1).Trim());
            }

            if (text[0] == '?')
            {
                return new OmnibarInput(raw, OmnibarInputKind.Search, text.Substring(1).Trim());
            }

            if (Platform.Phones.PhonePaths.IsPhonePath(text))
            {
                // A Windows phone's path ("phone://Galaxy S21/Internal shared storage/DCIM"): taken as written.
                var phone = new OmnibarInput(raw, OmnibarInputKind.Path, text);
                phone.FullPath = Platform.Phones.PhonePaths.Normalize(text);
                bool trailing = text.EndsWith("/", StringComparison.Ordinal) || text.EndsWith("\\", StringComparison.Ordinal);
                phone.EndsWithSeparator = trailing;
                phone.Folder = trailing ? phone.FullPath : PathUtil.GetParent(phone.FullPath) ?? phone.FullPath;
                phone.Partial = trailing ? string.Empty : PathUtil.GetName(phone.FullPath);
                return phone;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out Uri uri) && !uri.IsFile && !IsDrivePath(text))
            {
                string scheme = uri.Scheme.ToLowerInvariant();
                if (scheme == "http" || scheme == "https")
                {
                    return new OmnibarInput(raw, OmnibarInputKind.WebAddress, text);
                }

                if (Array.IndexOf(NetworkSchemes, scheme) >= 0)
                {
                    return new OmnibarInput(raw, OmnibarInputKind.NetworkAddress, text);
                }
            }

            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out Uri fileUri) && fileUri.IsFile)
            {
                text = fileUri.LocalPath;
            }

            if (!LooksLikePath(text))
            {
                return new OmnibarInput(raw, OmnibarInputKind.Text, text);
            }

            var input = new OmnibarInput(raw, OmnibarInputKind.Path, text);
            input.Resolve(text, currentFolder, homeFolder);
            return input.FullPath == null ? new OmnibarInput(raw, OmnibarInputKind.Text, text) : input;
        }

        /// <summary>True for text that can only be meant as a path (file names can't contain a separator).</summary>
        public static bool LooksLikePath(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            char first = text[0];
            if (first == '/' || first == '~' || first == '.' || (first == '\\' && OperatingSystem.IsWindows()))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() ? first == '%' : first == '$')
            {
                return true;
            }

            if (IsDrivePath(text))
            {
                return true;
            }

            return text.IndexOf('/') >= 0 || (OperatingSystem.IsWindows() && text.IndexOf('\\') >= 0);
        }

        /// <summary>"C:", "C:\…", "c:/…" (Windows drive syntax; never true on Unix, where ':' is an ordinary character).</summary>
        private static bool IsDrivePath(string text) =>
            OperatingSystem.IsWindows() && text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':' &&
            (text.Length == 2 || text[2] == '\\' || text[2] == '/');

        private void Resolve(string text, string currentFolder, string homeFolder)
        {
            string expanded = text;
            if (!string.IsNullOrEmpty(homeFolder) && (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) ||
                                                      (OperatingSystem.IsWindows() && expanded.StartsWith("~\\", StringComparison.Ordinal))))
            {
                expanded = homeFolder + expanded.Substring(1);
            }

            expanded = OperatingSystem.IsWindows()
                ? Environment.ExpandEnvironmentVariables(expanded)
                : UnixVariable.Replace(expanded, m => Environment.GetEnvironmentVariable(m.Groups["name"].Value) ?? m.Value);

            if (OperatingSystem.IsWindows())
            {
                expanded = expanded.Replace('/', '\\');
                if (expanded.Length == 2 && expanded[1] == ':')
                {
                    expanded += "\\"; // "C:" means the drive's root here, not "the current directory on C:"
                }
            }

            char separator = OperatingSystem.IsWindows() ? '\\' : '/';
            EndsWithSeparator = expanded.Length > 1 && (expanded[expanded.Length - 1] == separator) && !IsRoot(expanded);

            try
            {
                string full = Path.IsPathRooted(expanded)
                    ? Path.GetFullPath(expanded)
                    : string.IsNullOrEmpty(currentFolder) ? null : Path.GetFullPath(Path.Combine(currentFolder, expanded));
                if (full == null)
                {
                    return;
                }

                FullPath = IsRoot(full) ? full : PathUtil.TrimTrailingSeparator(full);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return;
            }

            // A trailing "." or ".." was collapsed by GetFullPath: then the folder itself is what's being completed.
            bool endsWithDots = expanded.EndsWith(separator + ".", StringComparison.Ordinal) || expanded.EndsWith(separator + "..", StringComparison.Ordinal) ||
                                expanded == "." || expanded == "..";
            if (EndsWithSeparator || IsRoot(FullPath) || endsWithDots)
            {
                Folder = FullPath;
                Partial = string.Empty;
                EndsWithSeparator = EndsWithSeparator || endsWithDots;
            }
            else
            {
                Folder = PathUtil.GetParent(FullPath) ?? FullPath;
                Partial = PathUtil.GetName(FullPath) ?? string.Empty;
            }
        }

        private static bool IsRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                string root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && string.Equals(PathUtil.TrimTrailingSeparator(root), PathUtil.TrimTrailingSeparator(path), PathUtil.Comparison);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>"\"C:\My Files\"" or '/home/me/x' (pasted from a terminal or Explorer's "Copy as path") → the bare path.</summary>
        public static string Unquote(string text)
        {
            if (text.Length >= 2 && ((text[0] == '"' && text[text.Length - 1] == '"') || (text[0] == '\'' && text[text.Length - 1] == '\'')))
            {
                return text.Substring(1, text.Length - 2).Trim();
            }

            return text;
        }
    }
}
