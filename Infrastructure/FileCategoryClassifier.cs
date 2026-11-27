// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;

using ByteExplorer.Core;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Category matching for FileCategoryFilter, on the shared extension table (Core/FileKinds, also used by the
    /// search index), so the file list, the previews and search agree on what an image or a document is.
    /// </summary>
    public static class FileCategoryClassifier
    {
        /// <summary>
        /// True if entryName (a file or folder name, not a full path)
        /// matches filter. Folders always match FoldersOnly and All;
        /// they never match a file-type category (Images/Documents/
        /// etc.) regardless of name, since a folder literally named
        /// "Report.pdf" is still a folder, not a document.
        ///
        /// Path.GetExtension is a pure string operation on entryName
        /// (not filesystem I/O), so this never touches disk — but it
        /// can throw ArgumentException for a name containing characters
        /// Path considers invalid. Real Windows filenames essentially
        /// never do, but an item that arrived via an unusual network
        /// share or a non-Windows source in principle could; guarded
        /// defensively rather than letting one oddly-named entry take
        /// down filtering for an entire folder.
        /// </summary>
        public static bool Matches(string entryName, bool isDirectory, FileCategoryFilter filter)
        {
            switch (filter)
            {
                case FileCategoryFilter.All:
                    return true;
                case FileCategoryFilter.FoldersOnly:
                    return isDirectory;
                case FileCategoryFilter.FilesOnly:
                    return !isDirectory;
                case FileCategoryFilter.Images:
                    return !isDirectory && FileKinds.Of(entryName) == FileKind.Image;
                case FileCategoryFilter.Documents:
                    return !isDirectory && FileKinds.Of(entryName) == FileKind.Document;
                case FileCategoryFilter.Videos:
                    return !isDirectory && FileKinds.Of(entryName) == FileKind.Video;
                case FileCategoryFilter.Music:
                    return !isDirectory && FileKinds.Of(entryName) == FileKind.Audio;
                case FileCategoryFilter.Archives:
                    return !isDirectory && FileKinds.Of(entryName) == FileKind.Archive;
                default:
                    return true;
            }
        }
    }
}
