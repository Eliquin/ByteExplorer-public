// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;

namespace ByteExplorer.Core
{
    /// <summary>What a file is, by its extension. Folders are never a kind.</summary>
    public enum FileKind : byte
    {
        None = 0,
        Image = 1,
        Document = 2,
        Video = 3,
        Audio = 4,
        Archive = 5,
        Code = 6
    }

    /// <summary>
    /// The one table of file kinds by extension, shared by the file list's category filter, the previews and the search
    /// index (which stores each file's kind, so a "kind:" filter is a byte comparison). Each extension belongs to exactly
    /// one kind. Lookups are case-insensitive and allocation-free.
    /// </summary>
    public static class FileKinds
    {
        private static readonly Dictionary<string, FileKind> ByExtension = Build();
        private static readonly Dictionary<string, FileKind>.AlternateLookup<ReadOnlySpan<char>> BySpan = ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

        private static Dictionary<string, FileKind> Build()
        {
            var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
            void Add(FileKind kind, params string[] extensions)
            {
                foreach (string extension in extensions)
                {
                    map[extension] = kind;
                }
            }

            Add(FileKind.Image, ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".svg", ".ico", ".heic",
                ".heif", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".psd", ".avif", ".xcf", ".jxl");
            Add(FileKind.Document, ".doc", ".docx", ".pdf", ".txt", ".rtf", ".odt", ".ods", ".odp", ".xls", ".xlsx", ".xlsm", ".ppt",
                ".pptx", ".csv", ".md", ".log", ".pages", ".numbers", ".key", ".epub", ".mobi", ".tex", ".djvu", ".xps");
            Add(FileKind.Video, ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob",
                ".ogv", ".mts", ".m2ts");
            Add(FileKind.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".aiff", ".aif", ".alac", ".mid",
                ".midi", ".ape");
            Add(FileKind.Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst", ".iso", ".cab", ".tgz", ".lz", ".z",
                ".dmg", ".jar", ".deb", ".rpm");
            Add(FileKind.Code, ".cs", ".csproj", ".sln", ".axaml", ".xaml", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".py",
                ".js", ".mjs", ".ts", ".tsx", ".jsx", ".go", ".rs", ".rb", ".php", ".swift", ".m", ".sh", ".ps1", ".bat", ".cmd",
                ".sql", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".lua", ".dart",
                ".vue", ".fs", ".vb", ".gradle", ".cmake", ".makefile");
            return map;
        }

        /// <summary>The kind of a file name or path (None for no or an unknown extension).</summary>
        public static FileKind Of(ReadOnlySpan<char> nameOrPath)
        {
            ReadOnlySpan<char> extension = Path.GetExtension(nameOrPath);
            return extension.Length > 1 && BySpan.TryGetValue(extension, out FileKind kind) ? kind : FileKind.None;
        }

        /// <summary>The kind named by a search word ("image", "pictures", "music", "docs"...), or null.</summary>
        public static FileKind? Parse(string word)
        {
            switch ((word ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "image": case "images": case "picture": case "pictures": case "photo": case "photos": case "pic": case "pics":
                    return FileKind.Image;
                case "document": case "documents": case "doc": case "docs": case "text":
                    return FileKind.Document;
                case "video": case "videos": case "movie": case "movies":
                    return FileKind.Video;
                case "audio": case "music": case "song": case "songs": case "sound":
                    return FileKind.Audio;
                case "archive": case "archives": case "compressed": case "zip":
                    return FileKind.Archive;
                case "code": case "source": case "src":
                    return FileKind.Code;
                default:
                    return null;
            }
        }
    }
}
