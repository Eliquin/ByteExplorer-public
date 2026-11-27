// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>Which external archiver an <see cref="ExternalArchiveProvider"/> drives.</summary>
    internal enum ArchiveTool
    {
        Bsdtar,
        SevenZip,
    }

    /// <summary>
    /// 7z, RAR, ISO, CAB (and compressed TAR when no plain decompressor is installed) through bsdtar or 7-Zip:
    /// <list type="bullet">
    /// <item>Listing: <c>bsdtar -tvf archive</c> (<see cref="BsdtarListing"/>) or <c>7z l -slt archive</c>
    /// (<see cref="SevenZipListing"/>).</item>
    /// <item>Extracting some entries: the tool extracts them by name (a list file: <c>bsdtar -x -T list</c>,
    /// <c>7z x @list</c>) into a private staging folder, and each file is then moved to its checked target. Names the
    /// tool would read as wildcards make it extract everything into the staging folder instead.</item>
    /// <item>Extracting all: the tool extracts straight into the destination after the listing was checked for names
    /// that leave it. Both tools refuse such names themselves too (bsdtar without -P, 7-Zip since 9.x), and bsdtar
    /// refuses to extract through symbolic links.</item>
    /// </list>
    /// Encrypted archives aren't opened: 7-Zip's listing marks encrypted entries; bsdtar says "encrypted" when it fails.
    /// A dummy password is given to 7-Zip so it never waits for one (standard input is closed as well).
    /// </summary>
    internal sealed class ExternalArchiveProvider : ArchiveProvider
    {
        private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ExtractTimeout = TimeSpan.FromHours(4);
        private const string DummyPassword = "-pByteExplorer";

        private readonly ArchiveTool _tool;
        private readonly string _program;
        private readonly IArchiveToolRunner _runner;
        private readonly string _formatName;

        public ExternalArchiveProvider(ArchiveTool tool, string program, IArchiveToolRunner runner, string formatName)
        {
            _tool = tool;
            _program = program;
            _runner = runner;
            _formatName = formatName;
        }

        public override string FormatName => _formatName;

        public ArchiveTool Tool => _tool;

        public override IReadOnlyList<ArchiveEntryInfo> ReadEntries(string archivePath, int maxEntries, CancellationToken cancellationToken)
        {
            IReadOnlyList<ArchiveEntryInfo> entries = Array.Empty<ArchiveEntryInfo>();
            ToolRunResult result;
            if (_tool == ArchiveTool.Bsdtar)
            {
                DateTime now = DateTime.Now;
                result = _runner.Run(_program, new[] { "-t", "-v", "-f", archivePath }, ListTimeout, cancellationToken,
                    reader => entries = BsdtarListing.Parse(reader, maxEntries, now));
            }
            else
            {
                result = _runner.Run(_program, new[] { "l", "-slt", "-sccUTF-8", DummyPassword, archivePath }, ListTimeout, cancellationToken,
                    reader => entries = SevenZipListing.Parse(reader, maxEntries), new UTF8Encoding(false));
            }

            if (!result.StoppedEarly && !Succeeded(result))
            {
                ThrowFailure(result, entries.Count > 0 ? null : $"This {_formatName} is damaged, or {ToolName} can't read it");
            }

            return entries;
        }

        public override void Extract(string archivePath, IReadOnlyList<ArchiveExtraction> items, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (items.Count == 0)
            {
                return;
            }

            foreach (ArchiveExtraction item in items)
            {
                if (item.Entry.IsEncrypted)
                {
                    throw new ArchivePasswordException();
                }
            }

            string staging = Path.Combine(ArchiveBrowser.TempRoot, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string listFile = staging + ".list";
            try
            {
                var names = new List<string>(items.Count);
                bool everything = false;
                foreach (ArchiveExtraction item in items)
                {
                    string pattern = ToPattern(item.Entry.RawName);
                    if (pattern == null)
                    {
                        everything = true;
                        break;
                    }

                    names.Add(pattern);
                }

                var arguments = new List<string>();
                Encoding listEncoding = _tool == ArchiveTool.Bsdtar ? ProcessToolRunner.DefaultEncoding : new UTF8Encoding(false);
                if (!everything)
                {
                    File.WriteAllLines(listFile, names, listEncoding);
                }

                if (_tool == ArchiveTool.Bsdtar)
                {
                    arguments.AddRange(new[] { "-x", "-f", archivePath, "-C", staging });
                    if (!everything)
                    {
                        arguments.AddRange(new[] { "-T", listFile });
                    }
                }
                else
                {
                    arguments.AddRange(new[] { "x", "-y", "-bd", "-sccUTF-8", "-scsUTF-8", DummyPassword, "-o" + staging, archivePath });
                    if (!everything)
                    {
                        arguments.Add("@" + listFile);
                    }
                }

                progress?.Report(items.Count == 1 ? PathUtil.GetName(items[0].Target) : $"Extracting {items.Count:N0} items with {ToolName}");
                ToolRunResult result = _runner.Run(_program, arguments, ExtractTimeout, cancellationToken, null, listEncoding);
                if (!Succeeded(result))
                {
                    // Even a failed entry can leave a file behind (bsdtar writes an empty one for encrypted data): use none.
                    ThrowFailure(result, items.Count == 1 ? $"\"{PathUtil.GetName(items[0].Target)}\" couldn't be extracted" : "The items couldn't be extracted");
                }

                long lastReport = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArchiveExtraction item = items[i];
                    string source = ArchiveBrowser.SafeTarget(staging, ArchiveBrowser.NormalizeInner(item.Entry.RawName));
                    if (!File.Exists(source) || new FileInfo(source).LinkTarget != null)
                    {
                        throw new IOException($"{ToolName} didn't extract \"{item.Entry.RawName}\".");
                    }

                    Report(progress, ref lastReport, i + 1, items.Count, PathUtil.GetName(item.Target));
                    MoveOut(source, item.Target);
                    SetTime(item.Target, item.Entry.LastWriteUtc);
                }
            }
            finally
            {
                TryDelete(staging, listFile);
            }
        }

        public override void ExtractAll(string archivePath, string destination, IProgress<string> progress, CancellationToken cancellationToken)
        {
            IReadOnlyList<ArchiveEntryInfo> entries = ReadEntries(archivePath, int.MaxValue, cancellationToken);
            foreach (ArchiveEntryInfo entry in entries)
            {
                if (ArchiveBrowser.NormalizeInner(entry.RawName).Length > 0 && ArchiveBrowser.IsUnsafeName(entry.RawName))
                {
                    throw new InvalidDataException($"The archive contains an unsafe path: {entry.RawName}");
                }

                if (entry.IsEncrypted)
                {
                    throw new ArchivePasswordException();
                }
            }

            progress?.Report($"Extracting {entries.Count:N0} items with {ToolName}");
            string[] arguments = _tool == ArchiveTool.Bsdtar
                ? new[] { "-x", "-f", archivePath, "-C", destination }
                : new[] { "x", "-y", "-bd", "-sccUTF-8", DummyPassword, "-o" + destination, archivePath };
            ToolRunResult result = _runner.Run(_program, arguments, ExtractTimeout, cancellationToken);
            if (!Succeeded(result))
            {
                ThrowFailure(result, $"The {_formatName} couldn't be extracted");
            }
        }

        private string ToolName => _tool == ArchiveTool.Bsdtar ? "tar" : "7-Zip";

        /// <summary>7-Zip's exit code 1 is a warning (the files were extracted); bsdtar has only 0 for success.</summary>
        private bool Succeeded(ToolRunResult result) => result.ExitCode == 0 || (_tool == ArchiveTool.SevenZip && result.ExitCode == 1);

        /// <summary>
        /// The name as the tool's list file needs it, or null when it can't be given exactly (extract everything then).
        /// bsdtar reads patterns: '*', '?' and '[' are escaped with a backslash (not on Windows, where the backslash is a
        /// separator; such names make it extract everything). 7-Zip's wildcards '*' and '?' can't be escaped.
        /// </summary>
        private string ToPattern(string rawName)
        {
            if (rawName.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                return null;
            }

            if (_tool == ArchiveTool.SevenZip)
            {
                return rawName.IndexOfAny(new[] { '*', '?' }) >= 0 ? null : rawName;
            }

            if (rawName.IndexOfAny(new[] { '*', '?', '[', '\\' }) < 0)
            {
                return rawName;
            }

            if (OperatingSystem.IsWindows())
            {
                return null;
            }

            var builder = new StringBuilder(rawName.Length + 4);
            foreach (char c in rawName)
            {
                if (c == '*' || c == '?' || c == '[' || c == '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private void ThrowFailure(ToolRunResult result, string lead)
        {
            string error = result.Error ?? string.Empty;
            if (error.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
                || error.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchivePasswordException();
            }

            string detail = ProcessToolRunner.FirstLine(error);
            Log.Logger.Warning("{Tool} failed with exit code {Code}: {Error}", _program, result.ExitCode, error.Trim());
            if (lead == null)
            {
                return; // a listing that ended with an error after some entries (a truncated archive): show what was read
            }

            throw new IOException(lead + (detail.Length > 0 ? $" ({detail})." : "."));
        }

        /// <summary>Moves an extracted file to its target; a read-only folder in the staging area (kept from the archive) means a copy.</summary>
        private static void MoveOut(string source, string target)
        {
            try
            {
                File.Move(source, target, overwrite: false);
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(source, target, overwrite: false);
            }
        }

        private static void TryDelete(string staging, string listFile)
        {
            try
            {
                File.Delete(listFile);
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Staging folder left for the next cleanup: {Folder}", staging);
            }
        }
    }
}
