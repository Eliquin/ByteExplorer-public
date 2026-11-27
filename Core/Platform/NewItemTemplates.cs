// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>One kind of new file the New menu offers: "Microsoft Word Document" (.docx), made empty, from a template file or from bytes.</summary>
    public sealed class NewItemTemplate
    {
        public NewItemTemplate(string displayName, string extension, string baseName, byte[] data = null, string templateFile = null)
        {
            DisplayName = displayName;
            Extension = extension ?? string.Empty;
            BaseName = baseName;
            Data = data;
            TemplateFile = templateFile;
        }

        /// <summary>What the menu shows: "Microsoft Word Document".</summary>
        public string DisplayName { get; }

        /// <summary>".docx" (empty for a template without one).</summary>
        public string Extension { get; }

        /// <summary>The new file's name before its number and extension: "New Microsoft Word Document".</summary>
        public string BaseName { get; }

        /// <summary>The new file's contents, when given as bytes (an empty ZIP's header); null otherwise.</summary>
        public byte[] Data { get; }

        /// <summary>A file copied as the new one (Office's templates, a Templates folder's files); null otherwise.</summary>
        public string TemplateFile { get; }

        /// <summary>Writes the new file's contents (nothing for an empty file).</summary>
        public void WriteContent(Stream destination)
        {
            if (TemplateFile != null)
            {
                using FileStream source = new FileStream(TemplateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                source.CopyTo(destination);
            }
            else if (Data != null)
            {
                destination.Write(Data, 0, Data.Length);
            }
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The New menu's file kinds, found the way the system's own file manager finds them:
    /// <list type="bullet">
    /// <item>Windows: the registry's ShellNew entries, as Explorer's New menu does (HKEY_CLASSES_ROOT\.ext\ShellNew, or
    /// \.ext\ProgId\ShellNew), so what installed programs add is there: Word, Excel, PowerPoint, Access and Publisher
    /// documents when Office is installed, Rich Text, Bitmap image, Compressed (zipped) Folder… Entries made by running a
    /// command (the old Briefcase) are left out, and so is one whose template file can't be found (a broken file would be
    /// made instead).</item>
    /// <item>Linux and macOS: the files in the Templates folder (XDG_TEMPLATES_DIR, ~/Templates), as GNOME Files and
    /// Dolphin do.</item>
    /// </list>
    /// Read once, off the UI thread (the registry has thousands of file types), and kept.
    /// </summary>
    public static class NewItemTemplates
    {
        private static readonly object Gate = new object();
        private static Task<IReadOnlyList<NewItemTemplate>> s_loading;

        /// <summary>Kinds the app offers itself (Folder, Shortcut, Text Document): not repeated from the registry.</summary>
        private static readonly HashSet<string> BuiltIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".lnk", ".library-ms", ".contact", ".url" };

        /// <summary>The kinds, sorted by name; the first call starts reading them.</summary>
        public static Task<IReadOnlyList<NewItemTemplate>> GetAsync()
        {
            lock (Gate)
            {
                return s_loading ??= Task.Run(Load);
            }
        }

        /// <summary>Already read (or reading): the menu shows them at once.</summary>
        public static bool IsLoaded => s_loading?.IsCompletedSuccessfully == true;

        /// <summary>Tests: start over (and read <paramref name="templatesFolder"/> instead of the system's).</summary>
        internal static void ResetForTests(string templatesFolder = null)
        {
            lock (Gate)
            {
                s_templatesFolderForTests = templatesFolder;
                s_loading = null;
            }
        }

        private static string s_templatesFolderForTests;

        private static IReadOnlyList<NewItemTemplate> Load()
        {
            try
            {
                List<NewItemTemplate> found = s_templatesFolderForTests != null
                    ? ScanTemplatesFolder(s_templatesFolderForTests)
                    : OperatingSystem.IsWindows() ? ScanShellNew() : ScanTemplatesFolder(UnixTemplatesFolder());
                return found
                    .GroupBy(t => t.DisplayName + "|" + t.Extension, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Reading the New menu's file kinds failed");
                return Array.Empty<NewItemTemplate>();
            }
        }

        // ------------------------------------------------------------------ Linux, macOS

        /// <summary>XDG_TEMPLATES_DIR from user-dirs.dirs, else ~/Templates.</summary>
        private static string UnixTemplatesFolder()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                string dirs = Path.Combine(string.IsNullOrEmpty(config) ? Path.Combine(home, ".config") : config, "user-dirs.dirs");
                if (File.Exists(dirs))
                {
                    foreach (string line in File.ReadLines(dirs))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("XDG_TEMPLATES_DIR=", StringComparison.Ordinal))
                        {
                            string value = trimmed.Substring("XDG_TEMPLATES_DIR=".Length).Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
                            // A templates folder set to the home folder itself means "none" (xdg-user-dirs' way to turn it off).
                            return PathUtil.AreSame(value, home) ? null : value;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Couldn't read user-dirs.dirs");
            }

            return Path.Combine(home, "Templates");
        }

        internal static List<NewItemTemplate> ScanTemplatesFolder(string folder)
        {
            var result = new List<NewItemTemplate>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return result;
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                string stem = Path.GetFileNameWithoutExtension(file);
                string extension = Path.GetExtension(file);
                result.Add(new NewItemTemplate(stem, extension, stem, templateFile: file));
            }

            return result;
        }

        // ------------------------------------------------------------------ Windows

        [SupportedOSPlatform("windows")]
        private static List<NewItemTemplate> ScanShellNew()
        {
            var result = new List<NewItemTemplate>();
            using RegistryKey root = Registry.ClassesRoot;
            foreach (string extension in root.GetSubKeyNames())
            {
                if (extension.Length < 2 || extension[0] != '.' || BuiltIn.Contains(extension))
                {
                    continue;
                }

                try
                {
                    using RegistryKey type = root.OpenSubKey(extension);
                    if (type == null)
                    {
                        continue;
                    }

                    string progId = type.GetValue(null) as string;
                    using RegistryKey shellNew = type.OpenSubKey("ShellNew") ??
                                                 (string.IsNullOrEmpty(progId) ? null : type.OpenSubKey(progId + "\\ShellNew"));
                    if (shellNew == null)
                    {
                        continue;
                    }

                    NewItemTemplate template = FromShellNew(extension, progId, shellNew);
                    if (template != null)
                    {
                        result.Add(template);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                {
                    // A key we may not read: that kind isn't offered.
                }
            }

            return result;
        }

        [SupportedOSPlatform("windows")]
        private static NewItemTemplate FromShellNew(string extension, string progId, RegistryKey shellNew)
        {
            if (shellNew.GetValue("Command") != null && shellNew.GetValue("NullFile") == null &&
                shellNew.GetValue("FileName") == null && shellNew.GetValue("Data") == null)
            {
                return null; // made by running a program (the Briefcase): not supported
            }

            string name = TypeName(extension, progId, shellNew);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string baseName = "New " + name;
            if (shellNew.GetValue("FileName") is string fileName && !string.IsNullOrWhiteSpace(fileName))
            {
                string template = ResolveTemplateFile(Environment.ExpandEnvironmentVariables(fileName));
                return template == null ? null : new NewItemTemplate(name, extension, baseName, templateFile: template);
            }

            object data = shellNew.GetValue("Data");
            if (data is byte[] bytes)
            {
                return new NewItemTemplate(name, extension, baseName, data: bytes);
            }

            if (data is string text)
            {
                return new NewItemTemplate(name, extension, baseName, data: Encoding.Default.GetBytes(text));
            }

            return shellNew.GetValue("NullFile") != null ? new NewItemTemplate(name, extension, baseName) : null;
        }

        /// <summary>
        /// The type's name as Explorer shows it: ShellNew's ItemName, else the ProgId's FriendlyTypeName or default value
        /// ("Microsoft Word Document"). "@dll,-id" names are read from their resource.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string TypeName(string extension, string progId, RegistryKey shellNew)
        {
            string itemName = Resolve(shellNew.GetValue("ItemName") as string);
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                return itemName.StartsWith("New ", StringComparison.OrdinalIgnoreCase) ? itemName.Substring(4) : itemName;
            }

            if (!string.IsNullOrEmpty(progId))
            {
                using RegistryKey prog = Registry.ClassesRoot.OpenSubKey(progId);
                if (prog != null)
                {
                    string friendly = Resolve(prog.GetValue("FriendlyTypeName") as string);
                    if (!string.IsNullOrWhiteSpace(friendly))
                    {
                        return friendly;
                    }

                    string description = Resolve(prog.GetValue(null) as string);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return description;
                    }
                }
            }

            return extension.TrimStart('.').ToUpperInvariant() + " File";
        }

        [SupportedOSPlatform("windows")]
        private static string Resolve(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '@')
            {
                return value;
            }

            var buffer = new StringBuilder(512);
            return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
        }

        /// <summary>A ShellNew FileName: a full path, or a name in the ShellNew and Templates folders.</summary>
        [SupportedOSPlatform("windows")]
        private static string ResolveTemplateFile(string fileName)
        {
            if (Path.IsPathRooted(fileName))
            {
                return File.Exists(fileName) ? fileName : null;
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string folder in new[]
            {
                Path.Combine(windows, "ShellNew"),
                Environment.GetFolderPath(Environment.SpecialFolder.Templates),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates)
            })
            {
                if (string.IsNullOrEmpty(folder))
                {
                    continue;
                }

                string candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputLength, IntPtr reserved);
    }
}
