// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Paths saved in the settings that follow the computer they are used on. ByteExplorer is portable: its settings file
    /// travels with it to another computer, where "C:\Users\anna\Documents" means nothing (another user name, another OS,
    /// a Documents folder redirected to OneDrive). So the user's well-known folders, and anything inside them, are saved
    /// as tokens — "::documents", "::desktop/Projects" — and resolved on each computer to its own folders.
    /// <para>
    /// Settings written before tokens existed hold absolute paths from wherever they were saved. <see cref="Resolve"/>
    /// recognizes a user-profile path that does not exist on this computer (C:\Users\name\…, /home/name/…,
    /// /Users/name/…) and maps it to the same place in this computer's profile: a well-known folder by its usual name
    /// (Desktop, Downloads, Documents, Pictures, Music, Videos or Movies, also under OneDrive), anything else to the same
    /// subfolder of this home when that exists. A path that exists here is never touched (it may be another user's
    /// folder on purpose), and one that cannot be matched stays as it is (shown dimmed, the person unpins it).
    /// </para>
    /// </summary>
    public static class PortablePaths
    {
        private const string Prefix = "::";

        private static readonly Regex ForeignProfile = new Regex(
            @"^(?:[A-Za-z]:[\\/](?:Users|Documents and Settings)[\\/][^\\/]+|/home/[^/]+|/Users/[^/]+|/root)(?:[\\/](?<rest>.*))?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Token, the folder's current path, and the folder names it has under a user profile on any OS.</summary>
        private static readonly (string Token, Func<string> Current, string[] Names)[] KnownFolders =
        {
            ("desktop", () => SpecialFolders.Desktop, new[] { "Desktop" }),
            ("downloads", () => SpecialFolders.Downloads, new[] { "Downloads" }),
            ("documents", () => SpecialFolders.Documents, new[] { "Documents", "My Documents" }),
            ("pictures", () => SpecialFolders.Pictures, new[] { "Pictures", "My Pictures" }),
            ("music", () => SpecialFolders.Music, new[] { "Music", "My Music" }),
            ("videos", () => SpecialFolders.Videos, new[] { "Videos", "Movies", "My Videos" }),
        };

        /// <summary>
        /// The form to save: a token for a well-known folder or a path inside one (or inside the home folder); anything
        /// else (other drives, network paths, the trash key) unchanged.
        /// </summary>
        public static string ToPortable(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(Prefix, StringComparison.Ordinal) || ByteExplorer.Core.Platform.NetworkLocations.IsNetworkAddress(path))
            {
                return path;
            }

            foreach ((string token, Func<string> current, _) in KnownFolders)
            {
                string portable = Relate(path, current(), token);
                if (portable != null)
                {
                    return portable;
                }
            }

            return Relate(path, SpecialFolders.Home, "home") ?? path;
        }

        /// <summary>
        /// The path on this computer for a saved value: tokens resolved, another computer's profile paths mapped (see the
        /// class remarks), everything else unchanged.
        /// </summary>
        public static string Resolve(string saved)
        {
            if (string.IsNullOrEmpty(saved))
            {
                return saved;
            }

            if (saved.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return ResolveToken(saved) ?? saved;
            }

            return MapForeignProfilePath(saved) ?? saved;
        }

        private static string Relate(string path, string folder, string token)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            if (ByteExplorer.Core.PathUtil.AreSame(path, folder))
            {
                return Prefix + token;
            }

            if (!ByteExplorer.Core.PathUtil.IsInside(path, folder))
            {
                return null;
            }

            string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? null : Prefix + token + "/" + relative;
        }

        private static string ResolveToken(string saved)
        {
            string body = saved.Substring(Prefix.Length);
            int slash = body.IndexOf('/');
            string token = slash < 0 ? body : body.Substring(0, slash);
            string relative = slash < 0 ? null : body.Substring(slash + 1);
            string folder = string.Equals(token, "home", StringComparison.OrdinalIgnoreCase) ? SpecialFolders.Home : null;
            foreach ((string known, Func<string> current, _) in KnownFolders)
            {
                if (string.Equals(known, token, StringComparison.OrdinalIgnoreCase))
                {
                    folder = current();
                    break;
                }
            }

            if (string.IsNullOrEmpty(folder))
            {
                return null; // an unknown token (the trash key, a newer version's token): left for the caller
            }

            return string.IsNullOrEmpty(relative) ? folder : Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string MapForeignProfilePath(string saved)
        {
            Match match = ForeignProfile.Match(saved);
            if (!match.Success || Exists(saved))
            {
                return null;
            }

            string home = SpecialFolders.Home;
            string[] segments = match.Groups["rest"].Success
                ? match.Groups["rest"].Value.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            if (segments.Length == 0)
            {
                return string.IsNullOrEmpty(home) ? null : home; // another computer's home folder: this one's
            }

            int first = 0;
            if (segments.Length > 1 && segments[0].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                first = 1; // Desktop and Documents backed up to OneDrive: C:\Users\name\OneDrive\Documents
            }

            foreach ((_, Func<string> current, string[] names) in KnownFolders)
            {
                if (Array.Exists(names, n => string.Equals(n, segments[first], StringComparison.OrdinalIgnoreCase)))
                {
                    string folder = current();
                    return string.IsNullOrEmpty(folder) ? null : Combine(folder, segments, first + 1);
                }
            }

            if (string.IsNullOrEmpty(home))
            {
                return null;
            }

            string candidate = Combine(home, segments, 0);
            return Exists(candidate) ? candidate : null;
        }

        private static string Combine(string root, string[] segments, int start)
        {
            string path = root;
            for (int i = start; i < segments.Length; i++)
            {
                path = Path.Combine(path, segments[i]);
            }

            return path;
        }

        private static bool Exists(string path)
        {
            try
            {
                return Directory.Exists(path) || File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
