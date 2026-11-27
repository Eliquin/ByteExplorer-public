// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The Folder Options as the views read them while drawing (no settings service at hand in a control): the current
    /// settings, a version that changes with them (for per-item caches), and a change event the windows listen to.
    /// </summary>
    public static class FolderOptions
    {
        private static readonly FolderOptionsSettings Defaults = new FolderOptionsSettings();
        private static Func<FolderOptionsSettings> s_source;
        private static int s_version;

        /// <summary>Raised on the UI thread after a folder option changed.</summary>
        public static event EventHandler Changed;

        public static FolderOptionsSettings Current => s_source?.Invoke() ?? Defaults;

        /// <summary>Changes whenever an option changes, so cached display names are rebuilt.</summary>
        public static int Version => Volatile.Read(ref s_version);

        public static void Attach(Func<FolderOptionsSettings> source)
        {
            s_source = source;
            Interlocked.Increment(ref s_version);
        }

        public static void NotifyChanged()
        {
            Interlocked.Increment(ref s_version);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Whether the app knows the file's type (it has an icon or a category for it): what "known file types" means for
        /// <see cref="FolderOptionsSettings.HideKnownExtensions"/>. Explorer asks the registry; this works on every OS.
        /// </summary>
        public static bool IsKnownType(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(extension) || extension.Length < 2)
            {
                return false;
            }

            return FileIconGlyphs.HasSpecificIcon(extension) ||
                   FileCategoryClassifier.Matches(fileName, false, FileCategoryFilter.Images) ||
                   FileCategoryClassifier.Matches(fileName, false, FileCategoryFilter.Videos) ||
                   FileCategoryClassifier.Matches(fileName, false, FileCategoryFilter.Music) ||
                   FileCategoryClassifier.Matches(fileName, false, FileCategoryFilter.Documents) ||
                   FileCategoryClassifier.Matches(fileName, false, FileCategoryFilter.Archives);
        }

        /// <summary>
        /// The name to show: without its extension when extensions of known types are hidden. A name that is only an
        /// extension (".bashrc") stays whole.
        /// </summary>
        public static string DisplayNameFor(string name, bool isDirectory)
        {
            if (isDirectory || string.IsNullOrEmpty(name) || !Current.HideKnownExtensions || !IsKnownType(name))
            {
                return name;
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? name : stem;
        }

        /// <summary>The extension an edited name keeps when extensions are hidden ("" when they are shown).</summary>
        public static string HiddenExtensionOf(string name, bool isDirectory) =>
            DisplayNameFor(name, isDirectory) is string shown && shown.Length < (name?.Length ?? 0) ? name.Substring(shown.Length) : string.Empty;
    }
}
