// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using ByteExplorer.Core.Security;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>
    /// Which archive files ByteExplorer can browse, and with what (<see cref="ArchiveProvider"/>), decided by name:
    /// <list type="table">
    /// <item><term>.zip</term><description>built in (System.IO.Compression), every OS.</description></item>
    /// <item><term>.tar, .tar.gz, .tgz, .gz</term><description>built in (System.Formats.Tar, GZipStream), every OS.</description></item>
    /// <item><term>.tar.xz/.txz, .tar.bz2/.tbz2/.tbz, .tar.zst/.tzst</term><description>built-in TAR reader over a
    /// decompressor (xz, bzip2, zstd, bsdcat or 7-Zip), else bsdtar alone.</description></item>
    /// <item><term>.xz, .bz2, .zst</term><description>a decompressor (as above).</description></item>
    /// <item><term>.7z, .rar, .iso, .cab</term><description>bsdtar or 7-Zip (RAR prefers bsdtar: some 7-Zip builds
    /// leave RAR out; the others prefer 7-Zip, which reads more ISO variants and reports encryption).</description></item>
    /// </list>
    /// A type nothing installed can read isn't an archive to the app: it opens in its associated application, and
    /// <see cref="MissingToolHint"/> says what to install.
    /// </summary>
    public static class ArchiveFormats
    {
        private enum Kind
        {
            Zip,
            Tar,
            Single,
            External,
        }

        private readonly record struct Suffix(string Text, Kind Kind, ArchiveCodec Codec, string FormatName);

        /// <summary>Longest first: ".tar.gz" must win over ".gz".</summary>
        private static readonly Suffix[] Suffixes =
        {
            new Suffix(".tar.gz", Kind.Tar, ArchiveCodec.Gzip, "TAR archive"),
            new Suffix(".tar.xz", Kind.Tar, ArchiveCodec.Xz, "TAR archive"),
            new Suffix(".tar.bz2", Kind.Tar, ArchiveCodec.Bzip2, "TAR archive"),
            new Suffix(".tar.zst", Kind.Tar, ArchiveCodec.Zstd, "TAR archive"),
            new Suffix(".tbz2", Kind.Tar, ArchiveCodec.Bzip2, "TAR archive"),
            new Suffix(".tzst", Kind.Tar, ArchiveCodec.Zstd, "TAR archive"),
            new Suffix(".tgz", Kind.Tar, ArchiveCodec.Gzip, "TAR archive"),
            new Suffix(".txz", Kind.Tar, ArchiveCodec.Xz, "TAR archive"),
            new Suffix(".tbz", Kind.Tar, ArchiveCodec.Bzip2, "TAR archive"),
            new Suffix(".tar", Kind.Tar, ArchiveCodec.None, "TAR archive"),
            new Suffix(".zip", Kind.Zip, ArchiveCodec.None, "ZIP file"),
            new Suffix(".bz2", Kind.Single, ArchiveCodec.Bzip2, "bzip2 file"),
            new Suffix(".zst", Kind.Single, ArchiveCodec.Zstd, "zstd file"),
            new Suffix(".gz", Kind.Single, ArchiveCodec.Gzip, "gzip file"),
            new Suffix(".xz", Kind.Single, ArchiveCodec.Xz, "xz file"),
            new Suffix(".7z", Kind.External, ArchiveCodec.None, "7z archive"),
            new Suffix(".rar", Kind.External, ArchiveCodec.None, "RAR archive"),
            new Suffix(".iso", Kind.External, ArchiveCodec.None, "disc image"),
            new Suffix(".cab", Kind.External, ArchiveCodec.None, "cabinet file"),
        };

        /// <summary>Substrings one of which every archive path contains (".gz" covers ".tgz" and ".tar.gz").</summary>
        private static readonly string[] Markers = { ".zip", ".tar", ".tgz", ".gz", ".tbz", ".txz", ".tzst", ".bz2", ".xz", ".zst", ".7z", ".rar", ".iso", ".cab" };

        /// <summary>True when the file name has an archive extension ByteExplorer knows, whether or not it can read it here.</summary>
        public static bool IsKnownArchiveName(string path) => Match(path) != null;

        /// <summary>True when the file can be browsed and extracted here (built in, or with an installed tool).</summary>
        public static bool IsBrowsable(string path)
        {
            Suffix? suffix = Match(path);
            if (suffix == null)
            {
                return false;
            }

            Suffix s = suffix.Value;
            switch (s.Kind)
            {
                case Kind.Zip:
                    return true;
                case Kind.Tar:
                case Kind.Single:
                    if (s.Codec == ArchiveCodec.None || s.Codec == ArchiveCodec.Gzip)
                    {
                        return true;
                    }

                    ArchiveToolset tools = ArchiveTools.Current;
                    return HasDecompressor(s.Codec, tools) || (s.Kind == Kind.Tar && tools.Bsdtar != null);
                default:
                    return ArchiveTools.Current.HasArchiver;
            }
        }

        /// <summary>
        /// For a known archive type that nothing installed can read: what to install, for a tooltip or the status bar
        /// ("To browse .7z files here, install …"). Null when the file is browsable or not an archive.
        /// </summary>
        public static string MissingToolHint(string path)
        {
            Suffix? suffix = Match(path);
            if (suffix == null || IsBrowsable(path))
            {
                return null;
            }

            string extension = suffix.Value.Text;
            if (OperatingSystem.IsWindows())
            {
                return suffix.Value.Kind == Kind.Single
                    ? $"To browse {extension} files in ByteExplorer, install 7-Zip (7-zip.org)."
                    : $"To browse {extension} files in ByteExplorer, install 7-Zip (7-zip.org); Windows 10 version 1803 and later include tar.exe, which also reads them.";
            }

            if (OperatingSystem.IsMacOS())
            {
                return $"To browse {extension} files in ByteExplorer, install 7-Zip or xz/zstd (for example with Homebrew: brew install sevenzip xz zstd).";
            }

            return $"To browse {extension} files in ByteExplorer, install bsdtar (package libarchive-tools) or 7-Zip (package p7zip-full or 7zip).";
        }

        /// <summary>"photos.tar.gz" → "photos": the name without its archive extension (the folder "Extract here" makes).</summary>
        public static string StemOf(string path)
        {
            string name = PathUtil.GetName(PathUtil.TrimTrailingSeparator(path ?? string.Empty)) ?? string.Empty;
            Suffix? suffix = Match(name);
            if (suffix != null)
            {
                return name.Substring(0, name.Length - suffix.Value.Text.Length);
            }

            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>A quick test before any disk access: could a segment of this path be an archive (or a protected item)?</summary>
        internal static bool MayContainArchive(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            foreach (string marker in Markers)
            {
                if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return path.IndexOf(ProtectedFormat.Extension, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The provider that reads this archive with what is installed, or null.</summary>
        internal static ArchiveProvider GetProvider(string archivePath) => GetProvider(archivePath, ArchiveTools.Current, ArchiveTools.Runner);

        internal static ArchiveProvider GetProvider(string archivePath, ArchiveToolset tools, IArchiveToolRunner runner)
        {
            if (ProtectedFormat.IsProtectedFile(archivePath))
            {
                return ZipArchiveProvider.Instance;
            }

            Suffix? match = Match(archivePath);
            if (match == null)
            {
                return null;
            }

            Suffix s = match.Value;
            switch (s.Kind)
            {
                case Kind.Zip:
                    return ZipArchiveProvider.Instance;
                case Kind.Tar:
                {
                    ArchiveStreamSource source = ArchiveStreamSource.For(s.Codec, tools, runner);
                    if (source != null)
                    {
                        return new TarArchiveProvider(source);
                    }

                    return tools.Bsdtar != null ? new ExternalArchiveProvider(ArchiveTool.Bsdtar, tools.Bsdtar, runner, s.FormatName) : null;
                }

                case Kind.Single:
                {
                    ArchiveStreamSource source = ArchiveStreamSource.For(s.Codec, tools, runner);
                    return source != null ? new CompressedFileProvider(source) : null;
                }

                default:
                {
                    bool preferBsdtar = s.Text == ".rar";
                    if (tools.Bsdtar != null && (preferBsdtar || tools.SevenZip == null))
                    {
                        return new ExternalArchiveProvider(ArchiveTool.Bsdtar, tools.Bsdtar, runner, s.FormatName);
                    }

                    return tools.SevenZip != null ? new ExternalArchiveProvider(ArchiveTool.SevenZip, tools.SevenZip, runner, s.FormatName) : null;
                }
            }
        }

        private static bool HasDecompressor(ArchiveCodec codec, ArchiveToolset tools) => ArchiveStreamSource.For(codec, tools, null) != null;

        private static Suffix? Match(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (Suffix suffix in Suffixes)
            {
                if (path.Length > suffix.Text.Length && path.EndsWith(suffix.Text, StringComparison.OrdinalIgnoreCase))
                {
                    return suffix;
                }
            }

            return null;
        }
    }
}
