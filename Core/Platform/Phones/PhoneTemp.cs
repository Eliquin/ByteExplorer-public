// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Platform.Phones
{
    /// <summary>
    /// Copies of phone files on the PC: an application can't open a file that only exists on a Windows phone, so opening
    /// one copies it here first (as Explorer does). Each copy gets its own folder, so the file keeps its name (the
    /// application shows it) without clashing. Copies older than a day are removed at startup.
    /// </summary>
    public static class PhoneTemp
    {
        public static string Root => Path.Combine(Path.GetTempPath(), "ByteExplorer", "phone");

        /// <summary>Copies a phone file to a new temporary folder and returns the copy's path. Throws IOException.</summary>
        public static string Download(string phonePath, IProgress<string> progress, CancellationToken cancellationToken)
        {
            IPhoneProvider phone = Phones.Require(phonePath);
            PhoneItemInfo info = phone.GetInfo(phonePath) ?? throw new FileNotFoundException($"\"{PhonePaths.GetName(phonePath)}\" isn't on the phone any more.");
            string folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string name = PathUtil.GetName(phonePath);
            string local = Path.Combine(folder, PhonePaths.SanitizeName(name));
            try
            {
                using (Stream input = phone.OpenRead(phonePath))
                using (var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buffer = new byte[256 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        copied += read;
                        progress?.Report($"{name}: {copied * 100 / Math.Max(1, info.Size)}%");
                    }
                }

                if (new FileInfo(local).Length != info.Size)
                {
                    throw new IOException($"\"{name}\" arrived incomplete from the phone. Try again.");
                }

                if (info.ModifiedUtc != default)
                {
                    File.SetLastWriteTimeUtc(local, info.ModifiedUtc);
                }

                return local;
            }
            catch
            {
                TryDelete(folder);
                throw;
            }
        }

        /// <summary>Whether a path is one of these copies (the app warns that changes stay on the PC).</summary>
        public static bool IsTemporaryCopy(string path) =>
            !string.IsNullOrEmpty(path) && path.StartsWith(Root + Path.DirectorySeparatorChar, PathUtil.Comparison);

        /// <summary>Removes copies older than <paramref name="age"/>. Best effort, never throws.</summary>
        public static void Cleanup(TimeSpan age)
        {
            try
            {
                if (!Directory.Exists(Root))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow - age;
                foreach (string folder in Directory.EnumerateDirectories(Root))
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                    {
                        TryDelete(folder);
                    }
                }

                foreach (string file in Directory.EnumerateFiles(Root))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file); // phone-to-phone transfer leftovers
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Cleaning old phone copies failed");
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Couldn't remove {Folder}", folder);
            }
        }
    }
}
