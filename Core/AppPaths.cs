// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core
{
    /// <summary>
    /// Where ByteExplorer keeps its own files (settings, analytics, logs, search index). Portable by default: the
    /// folder next to the executable, as before, so existing data stays where it is. When that folder is not
    /// writable (installed under Program Files, a read-only share) every write there failed, so the data goes to
    /// %LOCALAPPDATA%\ByteExplorer instead. Decided once per process.
    /// </summary>
    public static class AppPaths
    {
        private static readonly Lazy<string> Root = new Lazy<string>(ResolveDataDirectory);

        public static string DataDirectory => Root.Value;

        public static string Combine(string relative) => Path.Combine(DataDirectory, relative);

        private static string ResolveDataDirectory()
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (IsWritable(baseDirectory))
            {
                return baseDirectory;
            }

            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteExplorer");
            try
            {
                Directory.CreateDirectory(local);
            }
            catch (Exception)
            {
                // Callers already handle failing writes; nothing better to fall back to.
            }

            return local;
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, ".byteexplorer-write-test-" + Environment.ProcessId);
                using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
