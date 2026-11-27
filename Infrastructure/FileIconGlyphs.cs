// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using ByteExplorer.Icons;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Extension-to-glyph mapping for the file list, using the bundled
    /// Fluent UI System Icons font (Icons/Symbol.cs; Segoe MDL2
    /// Assets, used by the WPF build, exists only on Windows) —
    /// zero extraction cost, just a dictionary lookup, so it never
    /// becomes a bottleneck no matter how fast the list scrolls.
    ///
    /// This is a deliberate scope choice: it gives every file a
    /// sensible, distinct icon by category (document, image, archive,
    /// executable, etc.) rather than the exact per-application Shell
    /// icon Explorer shows. True Shell icon extraction (SHGetFileInfo,
    /// per-icon caching, HICON conversion) is its own subsystem and a
    /// good candidate for a later polish pass once the virtualization
    /// engine itself is proven — it's orthogonal to "handle a million
    /// files without lag," which is what this phase is actually about.
    /// </summary>
    public static class FileIconGlyphs
    {
        private const Symbol Folder = Symbol.Folder;
        private const Symbol Generic = Symbol.Document;
        private const Symbol Document = Symbol.DocumentText;
        private const Symbol Image = Symbol.Image;
        private const Symbol Archive = Symbol.FolderZip;
        private const Symbol Executable = Symbol.Apps;
        private const Symbol Audio = Symbol.MusicNote2;
        private const Symbol Video = Symbol.Video;
        private const Symbol Spreadsheet = Symbol.Table;
        private const Symbol WordDoc = Symbol.DocumentText;
        private const Symbol Pdf = Symbol.DocumentPdf;
        private const Symbol Code = Symbol.Code;

        private static readonly Dictionary<string, Symbol> ExtensionGlyphs = new Dictionary<string, Symbol>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = Document, [".log"] = Document, [".md"] = Document, [".rtf"] = Document,
            [".jpg"] = Image, [".jpeg"] = Image, [".png"] = Image, [".gif"] = Image, [".bmp"] = Image, [".svg"] = Image, [".webp"] = Image,
            [".zip"] = Archive, [".rar"] = Archive, [".7z"] = Archive, [".tar"] = Archive, [".gz"] = Archive,
            [".exe"] = Executable, [".msi"] = Executable, [".bat"] = Executable, [".cmd"] = Executable,
            [".mp3"] = Audio, [".wav"] = Audio, [".flac"] = Audio, [".aac"] = Audio,
            [".mp4"] = Video, [".avi"] = Video, [".mkv"] = Video, [".mov"] = Video, [".wmv"] = Video,
            [".xlsx"] = Spreadsheet, [".xls"] = Spreadsheet, [".csv"] = Spreadsheet,
            [".docx"] = WordDoc, [".doc"] = WordDoc,
            [".pdf"] = Pdf,
            [".cs"] = Code, [".js"] = Code, [".ts"] = Code, [".py"] = Code, [".json"] = Code, [".xml"] = Code, [".html"] = Code, [".css"] = Code, [".xaml"] = Code
        };

        /// <summary>Whether the extension (".pdf") has its own icon here.</summary>
        public static bool HasSpecificIcon(string extension) => !string.IsNullOrEmpty(extension) && ExtensionGlyphs.ContainsKey(extension);

        public static Symbol For(string fileName, bool isDirectory)
        {
            if (isDirectory)
            {
                return Folder;
            }

            string extension = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(extension) && ExtensionGlyphs.TryGetValue(extension, out Symbol glyph))
            {
                return glyph;
            }

            // A password-protected item: a lock (not in the table above, which also decides which extensions count as
            // "known" and get hidden: ".bxlock" stays visible, so "Photos.bxlock" is never mistaken for the folder).
            if (string.Equals(extension, Core.Security.ProtectedFormat.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return Symbol.LockClosed;
            }

            return Generic;
        }

        /// <summary>Extra mappings beyond the WPF set, for common Linux/macOS file types.</summary>
        static FileIconGlyphs()
        {
            foreach (string ext in new[] { ".sh", ".rs", ".go", ".c", ".h", ".cpp", ".java", ".kt", ".rb", ".php", ".yaml", ".yml", ".toml", ".axaml" })
            {
                ExtensionGlyphs[ext] = Code;
            }

            foreach (string ext in new[] { ".xz", ".bz2", ".zst", ".tgz", ".deb", ".rpm", ".dmg", ".iso" })
            {
                ExtensionGlyphs[ext] = Archive;
            }

            foreach (string ext in new[] { ".appimage", ".app", ".pkg" })
            {
                ExtensionGlyphs[ext] = Executable;
            }

            foreach (string ext in new[] { ".heic", ".tif", ".tiff", ".ico" })
            {
                ExtensionGlyphs[ext] = Image;
            }

            foreach (string ext in new[] { ".ogg", ".opus", ".m4a" })
            {
                ExtensionGlyphs[ext] = Audio;
            }

            foreach (string ext in new[] { ".webm", ".m4v" })
            {
                ExtensionGlyphs[ext] = Video;
            }

            foreach (string ext in new[] { ".odt" })
            {
                ExtensionGlyphs[ext] = WordDoc;
            }

            foreach (string ext in new[] { ".ods" })
            {
                ExtensionGlyphs[ext] = Spreadsheet;
            }
        }

        /// <summary>The folder color of the default look (a warm yellow).</summary>
        public const string DefaultFolderColorHex = "#F5C242";

        private static string s_folderColorHex = DefaultFolderColorHex;
        private static Avalonia.Media.IBrush s_folderBrush = Converters.HexToBrushConverter.BrushFor(DefaultFolderColorHex);

        /// <summary>
        /// The color folders are drawn in (Settings > Appearance > Folder color): read by every view as it draws, so a
        /// change shows at the next repaint. Set to empty for the default.
        /// </summary>
        public static string FolderColorHex
        {
            get => Volatile.Read(ref s_folderColorHex);
            set
            {
                string hex = Core.Models.AreaAppearance.NormalizeColor(value);
                hex = hex.Length == 0 ? DefaultFolderColorHex : hex;
                Volatile.Write(ref s_folderBrush, Converters.HexToBrushConverter.BrushFor(hex));
                Volatile.Write(ref s_folderColorHex, hex);
            }
        }

        /// <summary>The cached brush of <see cref="FolderColorHex"/> (no lookup per folder per repaint).</summary>
        public static Avalonia.Media.IBrush FolderBrush => Volatile.Read(ref s_folderBrush);

        public static string ColorHexFor(string fileName, bool isDirectory)
        {
            if (isDirectory)
            {
                return FolderColorHex;
            }

            string extension = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(extension))
            {
                return "#8B8B8B";
            }

            switch (extension.ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".bmp": case ".svg": case ".webp":
                    return "#378ADD";
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz":
                    return "#8B8B8B";
                case ".exe": case ".msi": case ".bat": case ".cmd":
                    return "#666666";
                case ".mp3": case ".wav": case ".flac": case ".aac":
                    return "#D4537E";
                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv":
                    return "#D4537E";
                case ".xlsx": case ".xls": case ".csv":
                    return "#1D9E75";
                case ".docx": case ".doc":
                    return "#378ADD";
                case ".pdf":
                    return "#D85A30";
                case ".bxlock":
                    return "#C9A227";
                default:
                    return "#8B8B8B";
            }
        }
    }
}
