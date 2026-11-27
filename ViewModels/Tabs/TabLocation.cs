// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.ViewModels.Tabs
{
    /// <summary>
    /// Where a pane is, as one string a tab can keep, duplicate, reopen and save: a folder path (also an archive or a
    /// phone path), or <see cref="Home"/> / <see cref="ThisPc"/> / <see cref="Trash"/> for the pages that aren't folders.
    /// No token can be a real path: a path is never relative and never starts with "::".
    /// </summary>
    public static class TabLocation
    {
        public const string Home = "::home";
        public const string ThisPc = "::thispc";

        /// <summary>The trash / Recycle Bin view (all of the system's trash folders, not one folder).</summary>
        public const string Trash = "::trash";

        /// <summary>The pane's place; null while it hasn't shown anything yet (a pane created a moment ago).</summary>
        public static string Of(FileListViewModel pane)
        {
            if (pane == null)
            {
                return null;
            }

            if (pane.IsHomeView)
            {
                return Home;
            }

            if (pane.IsThisPcView)
            {
                return ThisPc;
            }

            if (pane.IsTrashView)
            {
                return Trash;
            }

            string path = pane.CurrentPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        public static bool IsPage(string location) =>
            string.Equals(location, Home, StringComparison.Ordinal) || string.Equals(location, ThisPc, StringComparison.Ordinal) ||
            string.Equals(location, Trash, StringComparison.Ordinal);

        /// <summary>A location from the settings file: one of the page tokens, or a rooted path; anything else is not used.</summary>
        public static bool IsValid(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            if (IsPage(location) || Core.Platform.Phones.PhonePaths.IsPhonePath(location))
            {
                return true;
            }

            try
            {
                return System.IO.Path.IsPathRooted(location) && location.IndexOf('\0') < 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
