// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>The compression around a TAR stream, or of a single compressed file.</summary>
    internal enum ArchiveCodec
    {
        None,
        Gzip,
        Xz,
        Bzip2,
        Zstd,
    }

    /// <summary>
    /// Opens a (compressed) file as its decompressed byte stream: nothing to do for plain files, GZipStream for gzip
    /// (built in, also multi-member files), and a decompressing program's output for xz, bzip2 and zstd, which .NET
    /// doesn't read: the format's own tool (xz, bzip2, zstd), libarchive's bsdcat, or 7-Zip ("x -so"), whichever is
    /// installed. Nothing is decompressed to disk: TAR and single files are read straight from the stream.
    /// </summary>
    internal sealed class ArchiveStreamSource
    {
        /// <summary>Generous: a large .tar.xz takes minutes to decompress, and listing it reads the whole stream.</summary>
        private static readonly TimeSpan DecompressTimeout = TimeSpan.FromHours(2);

        private readonly string _program;
        private readonly Func<string, string[]> _arguments;
        private readonly IArchiveToolRunner _runner;

        private ArchiveStreamSource(ArchiveCodec codec, string program, Func<string, string[]> arguments, IArchiveToolRunner runner)
        {
            Codec = codec;
            _program = program;
            _arguments = arguments;
            _runner = runner;
        }

        public ArchiveCodec Codec { get; }

        /// <summary>The source for the codec with the tools available, or null when nothing installed can decompress it.</summary>
        public static ArchiveStreamSource For(ArchiveCodec codec, ArchiveToolset tools, IArchiveToolRunner runner)
        {
            switch (codec)
            {
                case ArchiveCodec.None:
                case ArchiveCodec.Gzip:
                    return new ArchiveStreamSource(codec, null, null, null);
            }

            string native = codec switch
            {
                ArchiveCodec.Xz => tools.Xz,
                ArchiveCodec.Bzip2 => tools.Bzip2,
                _ => tools.Zstd,
            };
            if (native != null)
            {
                // xz/bzip2/zstd share the flags: -d decompress, -c to standard output; "--" ends the options.
                return new ArchiveStreamSource(codec, native, path => new[] { "-d", "-c", "--", path }, runner);
            }

            if (tools.Bsdcat != null)
            {
                return new ArchiveStreamSource(codec, tools.Bsdcat, path => new[] { path }, runner);
            }

            if (tools.SevenZip != null && codec != ArchiveCodec.Zstd)
            {
                // Stock 7-Zip reads xz and bzip2, not zstd. A dummy password keeps it from ever asking for one.
                return new ArchiveStreamSource(codec, tools.SevenZip, path => new[] { "x", "-so", "-y", "-bd", "-pByteExplorer", path }, runner);
            }

            return null;
        }

        /// <summary>A source reading through the given program (tests feed it recorded output).</summary>
        internal static ArchiveStreamSource ForProgram(ArchiveCodec codec, string program, Func<string, string[]> arguments, IArchiveToolRunner runner) =>
            new ArchiveStreamSource(codec, program, arguments, runner);

        public Stream Open(string path, CancellationToken cancellationToken)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 256 * 1024, FileOptions.SequentialScan);
            switch (Codec)
            {
                case ArchiveCodec.None:
                    return file;
                case ArchiveCodec.Gzip:
                    return new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
            }

            file.Dispose();
            return _runner.OpenOutput(_program, _arguments(path), DecompressTimeout, cancellationToken);
        }
    }

    /// <summary>
    /// TAR archives, plain or compressed (.tar, .tar.gz/.tgz built in; .tar.xz, .tar.bz2, .tar.zst through a
    /// decompressor), read with System.Formats.Tar (ustar, pax and GNU long names). A TAR stream has no index, so
    /// listing reads it once from start to end, and extracting reads it again up to the last wanted entry. Symbolic
    /// and hard links, devices and FIFOs are listed nowhere and never created: following them is how crafted archives
    /// write outside the extraction folder.
    /// </summary>
    internal sealed class TarArchiveProvider : ArchiveProvider
    {
        private readonly ArchiveStreamSource _source;

        public TarArchiveProvider(ArchiveStreamSource source)
        {
            _source = source;
        }

        public override string FormatName => "TAR archive";

        public override IReadOnlyList<ArchiveEntryInfo> ReadEntries(string archivePath, int maxEntries, CancellationToken cancellationToken)
        {
            var entries = new List<ArchiveEntryInfo>();
            using Stream stream = _source.Open(archivePath, cancellationToken);
            using var reader = new TarReader(stream, leaveOpen: false);
            int index = -1;
            while (entries.Count < maxEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TarEntry entry = reader.GetNextEntry(copyData: false);
                if (entry == null)
                {
                    break;
                }

                index++;
                bool? isFolder = Classify(entry);
                if (isFolder != null)
                {
                    entries.Add(new ArchiveEntryInfo(entry.Name, isFolder.Value, isFolder.Value ? 0 : entry.Length, entry.ModificationTime.UtcDateTime, index));
                }
            }

            return entries;
        }

        public override void Extract(string archivePath, IReadOnlyList<ArchiveExtraction> items, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (items.Count == 0)
            {
                return;
            }

            var wanted = new Dictionary<int, ArchiveExtraction>(items.Count);
            foreach (ArchiveExtraction item in items)
            {
                wanted.TryAdd(item.Entry.Index, item);
            }

            using Stream stream = _source.Open(archivePath, cancellationToken);
            using var reader = new TarReader(stream, leaveOpen: false);
            long lastReport = 0;
            int index = -1;
            int done = 0;
            while (done < wanted.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TarEntry entry = reader.GetNextEntry(copyData: false) ?? throw new IOException("The archive changed while it was being read. Try again.");
                index++;
                if (!wanted.TryGetValue(index, out ArchiveExtraction item))
                {
                    continue;
                }

                if (!string.Equals(entry.Name, item.Entry.RawName, StringComparison.Ordinal))
                {
                    throw new IOException("The archive changed while it was being read. Try again.");
                }

                done++;
                Report(progress, ref lastReport, done, wanted.Count, PathUtil.GetName(item.Target));
                WriteFile(entry.DataStream, item.Target, item.Entry.LastWriteUtc);
            }
        }

        /// <summary>One pass over the stream (a compressed TAR is decompressed once, not once to list and once to extract).</summary>
        public override void ExtractAll(string archivePath, string destination, IProgress<string> progress, CancellationToken cancellationToken)
        {
            using Stream stream = _source.Open(archivePath, cancellationToken);
            using var reader = new TarReader(stream, leaveOpen: false);
            long lastReport = 0;
            int done = 0;
            TarEntry entry;
            while ((entry = reader.GetNextEntry(copyData: false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool? isFolder = Classify(entry);
                string name = ArchiveBrowser.NormalizeInner(entry.Name);
                if (isFolder == null || name.Length == 0)
                {
                    if (isFolder == null)
                    {
                        Log.Logger.Debug("Not extracted from {Archive}: {Entry} ({Type})", archivePath, entry.Name, entry.EntryType);
                    }

                    continue;
                }

                if (ArchiveBrowser.IsUnsafeName(entry.Name))
                {
                    throw new InvalidDataException($"The archive contains an unsafe path: {entry.Name}");
                }

                string target = ArchiveBrowser.SafeTarget(destination, name);
                if (isFolder.Value)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target))
                {
                    continue; // a name stored twice (an appended update): the first copy stays, as when browsing
                }

                done++;
                Report(progress, ref lastReport, done, int.MaxValue, PathUtil.GetName(target));
                WriteFile(entry.DataStream, target, entry.ModificationTime.UtcDateTime);
            }
        }

        /// <summary>True for a folder, false for a file with data, null for what is never listed (links, devices, metadata).</summary>
        private static bool? Classify(TarEntry entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    return true;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    return false;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// A single compressed file (.gz, .xz, .bz2, .zst that isn't a TAR): browsed as a folder holding the one file it
    /// decompresses to, named after it without the extension. The file's own date stands in for the entry's; the size
    /// is known up front only for gzip (its trailer stores it, modulo 4 GiB).
    /// </summary>
    internal sealed class CompressedFileProvider : ArchiveProvider
    {
        private readonly ArchiveStreamSource _source;

        public CompressedFileProvider(ArchiveStreamSource source)
        {
            _source = source;
        }

        public override string FormatName => _source.Codec switch
        {
            ArchiveCodec.Gzip => "gzip file",
            ArchiveCodec.Xz => "xz file",
            ArchiveCodec.Bzip2 => "bzip2 file",
            _ => "zstd file",
        };

        public override IReadOnlyList<ArchiveEntryInfo> ReadEntries(string archivePath, int maxEntries, CancellationToken cancellationToken)
        {
            long size = 0;
            using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Span<byte> head = stackalloc byte[6];
                int read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (!HasMagic(head.Slice(0, read)))
                {
                    throw new InvalidDataException($"This isn't a {FormatName}, or it is damaged.");
                }

                if (_source.Codec == ArchiveCodec.Gzip && file.Length >= 18)
                {
                    Span<byte> trailer = stackalloc byte[4];
                    file.Seek(-4, SeekOrigin.End);
                    file.ReadExactly(trailer);
                    size = BitConverter.ToUInt32(trailer);
                }
            }

            DateTime modified = File.GetLastWriteTimeUtc(archivePath);
            return new[] { new ArchiveEntryInfo(InnerName(archivePath), false, size, modified, 0) };
        }

        public override void Extract(string archivePath, IReadOnlyList<ArchiveExtraction> items, IProgress<string> progress, CancellationToken cancellationToken)
        {
            foreach (ArchiveExtraction item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(PathUtil.GetName(item.Target));
                using Stream data = _source.Open(archivePath, cancellationToken);
                WriteFile(data, item.Target, item.Entry.LastWriteUtc);
            }
        }

        /// <summary>"notes.txt.gz" holds "notes.txt".</summary>
        internal static string InnerName(string archivePath)
        {
            string stem = ArchiveFormats.StemOf(archivePath);
            return string.IsNullOrWhiteSpace(stem) || stem == "." || stem == ".." ? "content" : stem;
        }

        private bool HasMagic(ReadOnlySpan<byte> head) => _source.Codec switch
        {
            ArchiveCodec.Gzip => head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B,
            ArchiveCodec.Xz => head.Length >= 6 && head[0] == 0xFD && head[1] == (byte)'7' && head[2] == (byte)'z' && head[3] == (byte)'X' && head[4] == (byte)'Z' && head[5] == 0,
            ArchiveCodec.Bzip2 => head.Length >= 3 && head[0] == (byte)'B' && head[1] == (byte)'Z' && head[2] == (byte)'h',
            ArchiveCodec.Zstd => head.Length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD,
            _ => false,
        };
    }
}
