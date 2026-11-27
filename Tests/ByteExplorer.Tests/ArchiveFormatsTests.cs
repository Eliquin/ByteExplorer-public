// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.Archives;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Archive formats beyond ZIP: TAR and gzip built in (listing, navigation, open, copy-out, extract all, zip-slip),
    /// the bsdtar and 7-Zip listing parsers on recorded output, the external-tool provider with a fake runner, and
    /// integration with the real tools when they are installed (skipped otherwise).
    /// </summary>
    public sealed class ArchiveFormatsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-tar-" + Guid.NewGuid().ToString("N"));
        private readonly string _tar;
        private readonly string _tgz;
        private readonly string _cleanTgz;

        public ArchiveFormatsTests()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "be-zip-trash"));
            Directory.CreateDirectory(_root);
            _tar = Path.Combine(_root, "docs.tar");
            using (FileStream file = File.Create(_tar))
            {
                WriteSample(file, includeUnsafe: true);
            }

            _tgz = Path.Combine(_root, "docs.tar.gz");
            using (FileStream file = File.Create(_tgz))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                WriteSample(gzip, includeUnsafe: true);
            }

            _cleanTgz = Path.Combine(_root, "clean.tgz");
            using (FileStream file = File.Create(_cleanTgz))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                WriteSample(gzip, includeUnsafe: false);
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// "./readme.txt", "./docs/a.txt", "./docs/sub/b.txt" (no "docs/sub/" entry: implied), "./empty/", a symbolic link,
        /// and with <paramref name="includeUnsafe"/> "../evil.txt" and "/abs.txt".
        /// </summary>
        private static void WriteSample(Stream stream, bool includeUnsafe)
        {
            using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            AddFile(writer, "./readme.txt", "hello", new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./docs/"));
            AddFile(writer, "./docs/a.txt", "alpha", DateTime.UtcNow);
            AddFile(writer, "./docs/sub/b.txt", "bravo bravo", DateTime.UtcNow);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./empty/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "./link") { LinkName = "/etc/passwd" });
            if (includeUnsafe)
            {
                AddFile(writer, "../evil.txt", "should never be listed or extracted", DateTime.UtcNow);
                AddFile(writer, "/abs.txt", "nor this", DateTime.UtcNow);
            }
        }

        private static void AddFile(TarWriter writer, string name, string content, DateTime modifiedUtc)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                ModificationTime = new DateTimeOffset(modifiedUtc),
            };
            writer.WriteEntry(entry);
        }

        // ------------------------------------------------------------------ formats and providers

        [Theory]
        [InlineData("a.tar", true)]
        [InlineData("a.TAR.GZ", true)]
        [InlineData("a.tgz", true)]
        [InlineData("notes.txt.gz", true)]
        [InlineData("a.zip", true)]
        [InlineData("a.txt", false)]
        [InlineData("tar", false)]
        public void BuiltInFormats_AreBrowsableEverywhere(string name, bool browsable)
        {
            Assert.Equal(browsable, ArchiveFormats.IsBrowsable(name));
        }

        [Fact]
        public void Providers_FollowTheInstalledTools()
        {
            ArchiveToolset none = ArchiveToolset.None;
            Assert.IsType<ZipArchiveProvider>(ArchiveFormats.GetProvider("a.zip", none, null));
            Assert.IsType<TarArchiveProvider>(ArchiveFormats.GetProvider("a.tar", none, null));
            Assert.IsType<TarArchiveProvider>(ArchiveFormats.GetProvider("a.tgz", none, null));
            Assert.IsType<CompressedFileProvider>(ArchiveFormats.GetProvider("a.gz", none, null));
            Assert.Null(ArchiveFormats.GetProvider("a.7z", none, null));
            Assert.Null(ArchiveFormats.GetProvider("a.tar.xz", none, null));
            Assert.Null(ArchiveFormats.GetProvider("a.xz", none, null));
            Assert.Null(ArchiveFormats.GetProvider("a.txt", none, null));

            var bsdtarOnly = new ArchiveToolset { Bsdtar = "/x/bsdtar" };
            Assert.IsType<ExternalArchiveProvider>(ArchiveFormats.GetProvider("a.7z", bsdtarOnly, null));
            Assert.IsType<ExternalArchiveProvider>(ArchiveFormats.GetProvider("a.tar.xz", bsdtarOnly, null)); // bsdtar reads it whole
            Assert.Null(ArchiveFormats.GetProvider("a.xz", bsdtarOnly, null)); // bsdtar can't read a bare .xz

            var xzOnly = new ArchiveToolset { Xz = "/x/xz" };
            Assert.IsType<TarArchiveProvider>(ArchiveFormats.GetProvider("a.txz", xzOnly, null));
            Assert.IsType<CompressedFileProvider>(ArchiveFormats.GetProvider("a.xz", xzOnly, null));
            Assert.Null(ArchiveFormats.GetProvider("a.tar.zst", xzOnly, null));

            var both = new ArchiveToolset { Bsdtar = "/x/bsdtar", SevenZip = "/x/7z" };
            Assert.Equal(ArchiveTool.SevenZip, ((ExternalArchiveProvider)ArchiveFormats.GetProvider("a.7z", both, null)).Tool);
            Assert.Equal(ArchiveTool.Bsdtar, ((ExternalArchiveProvider)ArchiveFormats.GetProvider("a.rar", both, null)).Tool);
        }

        [Theory]
        [InlineData("/x/photos.tar.gz", "photos")]
        [InlineData("/x/photos.TGZ", "photos")]
        [InlineData("/x/backup.2024.tar.zst", "backup.2024")]
        [InlineData("/x/notes.txt.gz", "notes.txt")]
        [InlineData("/x/disc.iso", "disc")]
        [InlineData("/x/a.zip", "a")]
        public void StemOf_DropsTheWholeArchiveExtension(string path, string stem)
        {
            Assert.Equal(stem, ArchiveFormats.StemOf(path));
        }

        // ------------------------------------------------------------------ TAR, built in

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Tar_ListsFoldersAndFiles_WithoutLinksOrUnsafeNames(bool compressed)
        {
            string archive = compressed ? _tgz : _tar;
            Assert.True(ArchiveBrowser.TryResolve(Path.Combine(archive, "docs", "sub"), out string resolved, out string inner));
            Assert.Equal(archive, resolved);
            Assert.Equal("docs/sub", inner);

            DirectoryIndex root = ArchiveBrowser.List(archive, string.Empty, archive);
            Assert.False(root.HasError, root.ErrorMessage);
            Assert.Equal(new[] { "docs", "empty", "readme.txt" }, root.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            DirectoryIndexEntry readme = root.Entries.Single(e => e.Name == "readme.txt");
            Assert.Equal(5, readme.SizeBytes);
            Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), readme.LastWriteTimeUtc);

            DirectoryIndex docs = ArchiveBrowser.List(archive, "docs", Path.Combine(archive, "docs"));
            Assert.Equal(new[] { "a.txt", "sub" }, docs.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.True(docs.Entries.Single(e => e.Name == "sub").IsDirectory); // implied by "docs/sub/b.txt"
            Assert.True(ArchiveBrowser.List(archive, "nope", "x").HasError);
        }

        [Fact]
        public void Tar_OpenAndCopyOut_ExtractToTemp_WithoutUnsafeEntries()
        {
            IReadOnlyList<string> extracted = ArchiveBrowser.ExtractToTemp(
                new[] { Path.Combine(_tgz, "readme.txt"), Path.Combine(_tgz, "docs") }, null, CancellationToken.None);
            try
            {
                Assert.Equal("hello", File.ReadAllText(extracted[0]));
                Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(extracted[0]));
                Assert.Equal("bravo bravo", File.ReadAllText(Path.Combine(extracted[1], "sub", "b.txt")));
                Assert.StartsWith(ArchiveBrowser.TempRoot, extracted[0]);

                // The whole archive: a folder named after it, without "../evil.txt", "/abs.txt" or the link.
                IReadOnlyList<string> all = ArchiveBrowser.ExtractToTemp(new[] { _tar }, null, CancellationToken.None);
                Assert.Equal("docs", Path.GetFileName(all[0]));
                string folder = Path.GetDirectoryName(all[0]);
                Assert.True(File.Exists(Path.Combine(all[0], "docs", "a.txt")));
                Assert.True(Directory.Exists(Path.Combine(all[0], "empty")));
                Assert.False(File.Exists(Path.Combine(folder, "evil.txt")));
                Assert.False(File.Exists(Path.Combine(all[0], "abs.txt")));
                Assert.False(File.Exists(Path.Combine(all[0], "link")) || Directory.Exists(Path.Combine(all[0], "link")));
                Directory.Delete(folder, true);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(extracted[0]), true);
            }
        }

        [Fact]
        public async Task Tar_ExtractAll_MakesAFolderNamedAfterTheArchive()
        {
            var service = new ArchiveService();
            string folder = await service.ExtractAsync(_cleanTgz, null, CancellationToken.None);
            Assert.Equal(Path.Combine(_root, "clean"), folder);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(folder, "readme.txt")));
            Assert.Equal("bravo bravo", File.ReadAllText(Path.Combine(folder, "docs", "sub", "b.txt")));
            Assert.True(Directory.Exists(Path.Combine(folder, "empty")));
            Assert.False(File.Exists(Path.Combine(folder, "link")));

            // "Extract all…" into a chosen folder; the name is numbered when taken.
            string elsewhere = Path.Combine(_root, "chosen");
            Directory.CreateDirectory(Path.Combine(elsewhere, "clean"));
            string second = await service.ExtractAsync(_cleanTgz, elsewhere, null, CancellationToken.None);
            Assert.Equal(Path.Combine(elsewhere, "clean (2)"), second);
            Assert.True(File.Exists(Path.Combine(second, "docs", "a.txt")));
        }

        [Fact]
        public async Task Tar_ExtractAll_RefusesZipSlip_AndLeavesNothingBehind()
        {
            var service = new ArchiveService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(_tgz, null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "evil.txt")));
            Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
            Assert.Empty(Directory.GetDirectories(_root)); // no staging folder left either
        }

        [Fact]
        public void DamagedTar_IsAnErrorListing()
        {
            string bad = Path.Combine(_root, "bad.tar.gz");
            File.WriteAllText(bad, "not gzip at all");
            DirectoryIndex index = ArchiveBrowser.List(bad, string.Empty, bad);
            Assert.True(index.HasError);
            Assert.Contains("TAR archive", index.ErrorMessage);

            string notTar = Path.Combine(_root, "bad.tar");
            File.WriteAllBytes(notTar, Enumerable.Repeat((byte)'x', 2048).ToArray());
            Assert.True(ArchiveBrowser.List(notTar, string.Empty, notTar).HasError);
        }

        [Fact]
        public void SingleGzipFile_IsAFolderWithOneFile()
        {
            string gz = Path.Combine(_root, "notes.txt.gz");
            using (FileStream file = File.Create(gz))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                gzip.Write(Encoding.UTF8.GetBytes("some notes"));
            }

            DirectoryIndex index = ArchiveBrowser.List(gz, string.Empty, gz);
            Assert.False(index.HasError, index.ErrorMessage);
            DirectoryIndexEntry entry = Assert.Single(index.Entries);
            Assert.Equal("notes.txt", entry.Name);
            Assert.Equal(10, entry.SizeBytes); // from the gzip trailer

            IReadOnlyList<string> extracted = ArchiveBrowser.ExtractToTemp(new[] { Path.Combine(gz, "notes.txt") }, null, CancellationToken.None);
            try
            {
                Assert.Equal("some notes", File.ReadAllText(extracted[0]));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(extracted[0]), true);
            }

            string fake = Path.Combine(_root, "fake.gz");
            File.WriteAllText(fake, "plain text");
            Assert.True(ArchiveBrowser.List(fake, string.Empty, fake).HasError);
        }

        [Theory]
        [InlineData("./a/./b/", "a/b")]
        [InlineData(".", "")]
        [InlineData("./", "")]
        [InlineData(@"dir\sub/", "dir/sub")]
        [InlineData("a/.hidden", "a/.hidden")]
        public void NormalizeInner_DropsDotSegments(string raw, string expected)
        {
            Assert.Equal(expected, ArchiveBrowser.NormalizeInner(raw));
        }

        // ------------------------------------------------------------------ the pane

        [AvaloniaFact]
        public async Task Pane_OpensATarGzLikeAFolder_ReadOnly()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);

                // Right-click on the archive: both extract commands; inside it, writing commands are disabled with a reason.
                var menu = ByteExplorer.Views.Menus.FileContextMenu.ForItems(window, pane, new[] { _cleanTgz }, false);
                var headers = menu.ItemsSource.OfType<Avalonia.Controls.MenuItem>().Select(m => m.Header as string).ToList();
                Assert.Contains("Extract all…", headers);
                Assert.Contains("Extract here", headers);
                var inside = ByteExplorer.Views.Menus.FileContextMenu.ForItems(window, pane, new[] { Path.Combine(_cleanTgz, "readme.txt") }, false);
                Avalonia.Controls.MenuItem rename = inside.ItemsSource.OfType<Avalonia.Controls.MenuItem>().Single(m => (m.Header as string) == "Rename");
                Assert.False(rename.IsEnabled);
                Assert.Contains("read-only", Avalonia.Controls.ToolTip.GetTip(rename) as string);

                FileListItemViewModel item = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "clean.tgz");
                await pane.OpenItemAsync(item);
                await Pump(5);
                Assert.Equal(_cleanTgz, pane.CurrentPath);
                Assert.True(pane.IsInsideArchive);
                FileListItemViewModel docs = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "docs");
                Assert.True(docs.IsDirectory);

                await pane.OpenItemAsync(docs);
                await Pump(5);
                Assert.Equal(Path.Combine(_cleanTgz, "docs"), pane.CurrentPath);
                pane.UpdateSelection(pane.Items.OfType<FileListItemViewModel>().Where(i => i.Name == "a.txt"));
                Assert.False(pane.DeleteCommand.CanExecute(null));
                Assert.False(pane.RenameCommand.CanExecute(null));
                Assert.False(pane.CutCommand.CanExecute(null));
                Assert.False(pane.PasteCommand.CanExecute(null));
                Assert.True(pane.CopyCommand.CanExecute(null));

                // Extract beside the archive through the queue.
                await pane.ExtractFromArchiveAsync(new[] { Path.Combine(_cleanTgz, "docs", "a.txt") });
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "a.txt")));
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ listing parsers (recorded output)

        /// <summary>Recorded from bsdtar 3.7.2 on Linux (LC_ALL=C.UTF-8), for a TAR made with "-C src .".</summary>
        private const string BsdtarSample =
            "drwxr-xr-x  0 root   root        0 Nov 26 21:36 ./\n" +
            "drwxr-xr-x  0 root   root        0 Nov 26 21:21 ./dir/\n" +
            "drwxr-xr-x  0 root   root        0 Nov 26 21:36 ./empty/\n" +
            "-rw-r--r--  0 root   root        1 Nov 26 21:36 ./we\\\\ird.txt\n" +
            "lrwxrwxrwx  0 root   root        0 Nov 26 21:36 ./link -> /etc/passwd\n" +
            "-rw-r--r--  0 root   root        3 Mar  4  2020 ./a.txt\n" +
            "drwxr-xr-x  0 root   root        0 Nov 26 21:21 ./dir/sub/\n" +
            "-rw-r--r--  0 root   root        6 Nov 26 21:21 ./dir/sub/ü b.txt\n" +
            "hrw-r--r--  0 root   root        0 Nov 26 21:21 ./hard link to ./a.txt\n" +
            "-rw-r--r--  0 0      0          12 Nov 26 21:21 ./caf\\303\\251  two  spaces.txt\n";

        [Fact]
        public void BsdtarListing_ParsesNamesSizesDatesAndSkipsLinks()
        {
            var now = new DateTime(2026, 11, 26, 22, 0, 0, DateTimeKind.Local);
            IReadOnlyList<ArchiveEntryInfo> entries = BsdtarListing.Parse(BsdtarSample, now);
            Assert.Equal(new[] { "./", "./dir/", "./empty/", "./we\\ird.txt", "./a.txt", "./dir/sub/", "./dir/sub/ü b.txt", "./café  two  spaces.txt" },
                entries.Select(e => e.RawName));
            Assert.True(entries[1].IsFolder);
            Assert.False(entries[4].IsFolder);
            Assert.Equal(3, entries[4].Length);
            Assert.Equal(new DateTime(2020, 3, 4, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), entries[4].LastWriteUtc);
            Assert.Equal(new DateTime(2026, 11, 26, 21, 21, 0, DateTimeKind.Local).ToUniversalTime(), entries[1].LastWriteUtc);
            Assert.Equal(12, entries[7].Length);
        }

        [Fact]
        public void BsdtarListing_Dates_YearlessPicksTheClosestYear_DayFirstAndUnknownMonths()
        {
            var now = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Local);
            Assert.Equal(new DateTime(2026, 12, 30, 9, 15, 0, DateTimeKind.Local).ToUniversalTime(), BsdtarListing.ParseDate("Dec", "30", "09:15", now));
            Assert.Equal(new DateTime(2027, 3, 2, 9, 15, 0, DateTimeKind.Local).ToUniversalTime(), BsdtarListing.ParseDate("2", "Mar", "09:15", now));
            Assert.Equal(new DateTime(2019, 7, 4, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), BsdtarListing.ParseDate("04", "jul.", "2019", now));
            Assert.Equal(11, BsdtarListing.ParseMonth("11月"));
            Assert.Equal(default, BsdtarListing.ParseDate("Xyz", "3", "2019", now));

            // A line whose date can't be read still lists the entry, with an unknown date.
            IReadOnlyList<ArchiveEntryInfo> entries = BsdtarListing.Parse("-rw-r--r--  0 0 0 5 qqq 3 2019 file.txt\n", now);
            Assert.Equal("file.txt", Assert.Single(entries).RawName);
            Assert.Equal(default, entries[0].LastWriteUtc);
        }

        /// <summary>Recorded from 7-Zip 23.01 ("7z l -slt -sccUTF-8"), with an encrypted entry and Windows separators added.</summary>
        private const string SevenZipSample =
            "\n7-Zip 23.01 (x64) : Copyright (c) 1999-2023 Igor Pavlov : 2023-06-20\n\n" +
            "Listing archive: t.7z\n\n--\nPath = t.7z\nType = 7z\nPhysical Size = 201\nSolid = +\n\n" +
            "----------\n" +
            "Path = dir\nSize = 0\nPacked Size = 0\nModified = 2026-11-26 21:21:59.1665932\nAttributes = D drwxr-xr-x\nCRC = \nEncrypted = -\nMethod = \nBlock = \n\n" +
            "Path = a.txt\nSize = 3\nPacked Size = 707\nModified = 2020-03-04 05:06:07.0000000\nAttributes = A -rw-r--r--\nCRC = ED6F7A7A\nEncrypted = -\nMethod = LZMA2:12\nBlock = 0\n\n" +
            "Path = dir\\sub\\ü b.txt\nSize = 6\nPacked Size = \nModified = 2026-11-26 21:21:59\nAttributes = A\nEncrypted = +\nMethod = LZMA2:12 7zAES:19\n\n" +
            "Path = link\nSize = 11\nModified = 2026-11-26 21:21:59\nAttributes = A lrwxrwxrwx\nEncrypted = -\n\n" +
            "Path = folder2\nFolder = +\nSize = 0\nModified = 2026-11-26 21:21:59\n";

        [Fact]
        public void SevenZipListing_ParsesBlocks_SkipsTheArchiveBlockAndLinks()
        {
            IReadOnlyList<ArchiveEntryInfo> entries = SevenZipListing.Parse(SevenZipSample);
            Assert.Equal(new[] { "dir", "a.txt", "dir\\sub\\ü b.txt", "folder2" }, entries.Select(e => e.RawName));
            Assert.True(entries[0].IsFolder);
            Assert.True(entries[3].IsFolder);
            Assert.Equal(3, entries[1].Length);
            Assert.Equal(new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Local).ToUniversalTime(), entries[1].LastWriteUtc);
            Assert.True(entries[2].IsEncrypted);
            Assert.False(entries[1].IsEncrypted);
            Assert.Equal("dir/sub/ü b.txt", ArchiveBrowser.NormalizeInner(entries[2].RawName));
        }

        // ------------------------------------------------------------------ the external provider, with a fake runner

        [Fact]
        public void ExternalProvider_ListsThroughTheRunner_AndExtractsByName()
        {
            string archive = Path.Combine(_root, "fake.7z");
            File.WriteAllText(archive, "fake");
            var runner = new FakeRunner
            {
                ListOutput = BsdtarSample,
                OnExtract = (args, staging) =>
                {
                    Assert.Contains("-T", args);
                    string list = File.ReadAllText(args[args.IndexOf("-T") + 1]);
                    Assert.Contains("./dir/sub/ü b.txt", list);
                    Assert.Contains(@"./we\\ird.txt", list); // a backslash is escaped for bsdtar's pattern matching
                    Directory.CreateDirectory(Path.Combine(staging, "dir", "sub"));
                    File.WriteAllText(Path.Combine(staging, "dir", "sub", "ü b.txt"), "content");
                    return new ToolRunResult(0, string.Empty, false);
                },
            };
            var provider = new ExternalArchiveProvider(ArchiveTool.Bsdtar, "/fake/bsdtar", runner, "7z archive");
            IReadOnlyList<ArchiveEntryInfo> entries = provider.ReadEntries(archive, 100, CancellationToken.None);
            Assert.Equal(8, entries.Count);
            Assert.Equal(new[] { "-t", "-v", "-f", archive }, runner.Calls[0]);

            string target = Path.Combine(_root, "out", "ü b.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            ArchiveEntryInfo wanted = entries.Single(e => e.RawName == "./dir/sub/ü b.txt");
            ArchiveEntryInfo weird = entries.Single(e => e.RawName == "./we\\ird.txt");
            Assert.Throws<IOException>(() => provider.Extract(archive, new[] { new ArchiveExtraction(wanted, target), new ArchiveExtraction(weird, target + "2") }, null, CancellationToken.None));
            Assert.Equal("content", File.ReadAllText(target)); // moved out before the missing one was noticed
        }

        [Fact]
        public void ExternalProvider_ReportsEncryptedArchives()
        {
            string archive = Path.Combine(_root, "secret.rar");
            File.WriteAllText(archive, "fake");
            var runner = new FakeRunner
            {
                ListOutput = string.Empty,
                ListResult = new ToolRunResult(1, "bsdtar: The archive header is encrypted, but currently not supported\n", false),
            };
            var provider = new ExternalArchiveProvider(ArchiveTool.Bsdtar, "/fake/bsdtar", runner, "RAR archive");
            var error = Assert.Throws<ArchivePasswordException>(() => provider.ReadEntries(archive, 100, CancellationToken.None));
            Assert.Contains("password protected", error.Message);

            // 7-Zip marks encrypted entries: extracting one is refused before the tool runs.
            var sevenZip = new ExternalArchiveProvider(ArchiveTool.SevenZip, "/fake/7z", new FakeRunner { ListOutput = SevenZipSample }, "7z archive");
            ArchiveEntryInfo encrypted = sevenZip.ReadEntries(archive, 100, CancellationToken.None).Single(e => e.IsEncrypted);
            Assert.Throws<ArchivePasswordException>(() => sevenZip.Extract(archive, new[] { new ArchiveExtraction(encrypted, Path.Combine(_root, "x")) }, null, CancellationToken.None));
            Assert.Throws<ArchivePasswordException>(() => sevenZip.ExtractAll(archive, Path.Combine(_root, "all"), null, CancellationToken.None));
        }

        [Fact]
        public void ExternalProvider_ExtractAll_RefusesUnsafeNamesBeforeRunningTheTool()
        {
            string archive = Path.Combine(_root, "slip.7z");
            File.WriteAllText(archive, "fake");
            var runner = new FakeRunner { ListOutput = "-rw-r--r--  0 0 0 5 Mar  4  2020 ../../evil.txt\n" };
            var provider = new ExternalArchiveProvider(ArchiveTool.Bsdtar, "/fake/bsdtar", runner, "7z archive");
            Assert.Throws<InvalidDataException>(() => provider.ExtractAll(archive, Path.Combine(_root, "dest"), null, CancellationToken.None));
            Assert.Single(runner.Calls); // the listing only
        }

        private sealed class FakeRunner : IArchiveToolRunner
        {
            public string ListOutput { get; set; }
            public ToolRunResult ListResult { get; set; } = new ToolRunResult(0, string.Empty, false);
            public Func<List<string>, string, ToolRunResult> OnExtract { get; set; }
            public List<string[]> Calls { get; } = new List<string[]>();

            public ToolRunResult Run(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
                Action<TextReader> readOutput = null, Encoding outputEncoding = null)
            {
                var args = arguments.ToList();
                Calls.Add(args.ToArray());
                bool listing = args.Contains("-t") || args.FirstOrDefault() == "l";
                if (listing)
                {
                    readOutput?.Invoke(new StringReader(ListOutput));
                    return ListResult;
                }

                string staging = args.Contains("-C") ? args[args.IndexOf("-C") + 1] : args.First(a => a.StartsWith("-o", StringComparison.Ordinal)).Substring(2);
                return OnExtract?.Invoke(args, staging) ?? new ToolRunResult(0, string.Empty, false);
            }

            public Stream OpenOutput(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }

        // ------------------------------------------------------------------ the real tools (skipped when missing)

        private const string SampleDir = "sample";

        /// <summary>A folder with "a.txt", "dir/sub/ü b.txt" and "empty/" to pack with the real tools.</summary>
        private string MakeSourceFolder()
        {
            string source = Path.Combine(_root, SampleDir);
            Directory.CreateDirectory(Path.Combine(source, "dir", "sub"));
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(source, "dir", "sub", "ü b.txt"), "unicode");
            return source;
        }

        private static void RunTool(string program, string workingDirectory, params string[] arguments)
        {
            var info = new ProcessStartInfo(program) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["LC_ALL"] = "C.UTF-8";
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info);
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60_000));
            Assert.True(process.ExitCode == 0, error);
        }

        private void CheckBrowseAndExtract(ArchiveProvider provider, string archive)
        {
            IReadOnlyList<ArchiveEntryInfo> entries = provider.ReadEntries(archive, 1000, CancellationToken.None);
            var names = entries.Select(e => ArchiveBrowser.NormalizeInner(e.RawName)).Where(n => n.Length > 0).ToList();
            Assert.Contains("a.txt", names);
            Assert.Contains("dir/sub/ü b.txt", names);
            Assert.Contains("empty", names);

            string target = Path.Combine(_root, "one-" + Guid.NewGuid().ToString("N"), "picked.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            ArchiveEntryInfo unicode = entries.Single(e => ArchiveBrowser.NormalizeInner(e.RawName) == "dir/sub/ü b.txt");
            provider.Extract(archive, new[] { new ArchiveExtraction(unicode, target) }, null, CancellationToken.None);
            Assert.Equal("unicode", File.ReadAllText(target));

            string all = Path.Combine(_root, "all-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(all);
            provider.ExtractAll(archive, all, null, CancellationToken.None);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(all, "a.txt")));
            Assert.Equal("unicode", File.ReadAllText(Path.Combine(all, "dir", "sub", "ü b.txt")));
        }

        [ToolFact("bsdtar")]
        public void Bsdtar_Reads7z_TarXz_AndReportsDamagedOrEncrypted()
        {
            string bsdtar = ArchiveTools.FindOnPath("bsdtar");
            string source = MakeSourceFolder();
            var tools = new ArchiveToolset { Bsdtar = bsdtar };

            string txz = Path.Combine(_root, "sample.tar.xz");
            RunTool(bsdtar, source, "-c", "-J", "-f", txz, ".");
            ArchiveProvider tarProvider = ArchiveFormats.GetProvider(txz, tools, ArchiveTools.Runner);
            Assert.IsType<ExternalArchiveProvider>(tarProvider);
            CheckBrowseAndExtract(tarProvider, txz);

            string sevenZip = Path.Combine(_root, "sample.7z");
            RunTool(bsdtar, source, "-c", "--format", "7zip", "-f", sevenZip, "a.txt", "dir", "empty");
            CheckBrowseAndExtract(ArchiveFormats.GetProvider(sevenZip, tools, ArchiveTools.Runner), sevenZip);

            string damaged = Path.Combine(_root, "damaged.7z");
            File.WriteAllText(damaged, "not a 7z");
            Assert.ThrowsAny<IOException>(() => ArchiveFormats.GetProvider(damaged, tools, ArchiveTools.Runner).ReadEntries(damaged, 100, CancellationToken.None));

            // bsdtar can't write encrypted archives; 7-Zip (when installed too) makes them.
            string sevenZipProgram = ArchiveTools.FindOnPath("7z");
            if (sevenZipProgram != null)
            {
                ArchiveProvider provider = ArchiveFormats.GetProvider(sevenZip, tools, ArchiveTools.Runner);
                string headers = Path.Combine(_root, "headers.7z");
                RunTool(sevenZipProgram, source, "a", "-bd", "-pSecret", "-mhe=on", headers, "a.txt");
                Assert.Throws<ArchivePasswordException>(() => provider.ReadEntries(headers, 100, CancellationToken.None));

                string content = Path.Combine(_root, "content.7z");
                RunTool(sevenZipProgram, source, "a", "-bd", "-pSecret", content, "a.txt");
                ArchiveEntryInfo entry = provider.ReadEntries(content, 100, CancellationToken.None).Single(); // bsdtar lists it…
                Assert.Throws<ArchivePasswordException>(() => provider.Extract(content, new[] { new ArchiveExtraction(entry, Path.Combine(_root, "enc.txt")) }, null, CancellationToken.None));
                Assert.Throws<ArchivePasswordException>(() => provider.ExtractAll(content, Directory.CreateDirectory(Path.Combine(_root, "enc-all")).FullName, null, CancellationToken.None));
            }
        }

        [ToolFact("7z")]
        public void SevenZip_Reads7z_SingleXz_AndRefusesEncrypted()
        {
            string program = ArchiveTools.FindOnPath("7z");
            string source = MakeSourceFolder();
            var tools = new ArchiveToolset { SevenZip = program };

            string archive = Path.Combine(_root, "sample.7z");
            RunTool(program, source, "a", "-bd", archive, "a.txt", "dir", "empty");
            ArchiveProvider provider = ArchiveFormats.GetProvider(archive, tools, ArchiveTools.Runner);
            Assert.Equal(ArchiveTool.SevenZip, Assert.IsType<ExternalArchiveProvider>(provider).Tool);
            CheckBrowseAndExtract(provider, archive);

            // End to end through the browser, with whichever archiver this machine has.
            Assert.True(ArchiveBrowser.IsArchiveFile(archive));
            DirectoryIndex sub = ArchiveBrowser.List(archive, "dir/sub", Path.Combine(archive, "dir", "sub"));
            Assert.False(sub.HasError, sub.ErrorMessage);
            Assert.Equal("ü b.txt", Assert.Single(sub.Entries).Name);
            IReadOnlyList<string> copied = ArchiveBrowser.ExtractToTemp(new[] { Path.Combine(archive, "dir") }, null, CancellationToken.None);
            try
            {
                Assert.Equal("unicode", File.ReadAllText(Path.Combine(copied[0], "sub", "ü b.txt")));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(copied[0]), true);
            }

            string encrypted = Path.Combine(_root, "secret.7z");
            RunTool(program, source, "a", "-bd", "-pSecret", "-mhe=on", encrypted, "a.txt");
            Assert.Throws<ArchivePasswordException>(() => provider.ReadEntries(encrypted, 100, CancellationToken.None));
            string contentOnly = Path.Combine(_root, "secret2.7z");
            RunTool(program, source, "a", "-bd", "-pSecret", contentOnly, "a.txt");
            Assert.Throws<ArchivePasswordException>(() => provider.ExtractAll(contentOnly, Path.Combine(_root, "nope"), null, CancellationToken.None));

            // 7-Zip as the decompressor of a single .xz file.
            string xz = Path.Combine(_root, "a.txt.xz");
            RunTool(program, source, "a", "-bd", "-txz", xz, "a.txt");
            ArchiveProvider single = ArchiveFormats.GetProvider(xz, tools, ArchiveTools.Runner);
            Assert.IsType<CompressedFileProvider>(single);
            ArchiveEntryInfo entry = Assert.Single(single.ReadEntries(xz, 10, CancellationToken.None));
            Assert.Equal("a.txt", entry.RawName);
            string target = Path.Combine(_root, "from-xz.txt");
            single.Extract(xz, new[] { new ArchiveExtraction(entry, target) }, null, CancellationToken.None);
            Assert.Equal("alpha", File.ReadAllText(target));
        }

        [ToolFact("xz")]
        public void Xz_DecompressesATarXz_ForTheBuiltInTarReader()
        {
            string xz = ArchiveTools.FindOnPath("xz");
            string tarXz = Path.Combine(_root, "built.tar.xz");
            File.Copy(_tar, Path.Combine(_root, "built.tar"));
            RunTool(xz, _root, "-z", "-k", Path.Combine(_root, "built.tar"));
            ArchiveProvider provider = ArchiveFormats.GetProvider(tarXz, new ArchiveToolset { Xz = xz }, ArchiveTools.Runner);
            Assert.IsType<TarArchiveProvider>(provider);
            IReadOnlyList<ArchiveEntryInfo> entries = provider.ReadEntries(tarXz, 100, CancellationToken.None);
            Assert.Contains(entries, e => e.RawName == "./docs/sub/b.txt");
            ArchiveEntryInfo b = entries.Single(e => e.RawName == "./docs/sub/b.txt");
            string target = Path.Combine(_root, "b-from-xz.txt");
            provider.Extract(tarXz, new[] { new ArchiveExtraction(b, target) }, null, CancellationToken.None);
            Assert.Equal("bravo bravo", File.ReadAllText(target));

            // A damaged stream fails with the decompressor's message, not silently.
            string bad = Path.Combine(_root, "bad.tar.xz");
            File.WriteAllBytes(bad, new byte[] { 0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0, 1, 2, 3, 4, 5 });
            Assert.ThrowsAny<IOException>(() => provider.ReadEntries(bad, 100, CancellationToken.None));
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    /// <summary>A fact that is skipped (reported as such) when the named program isn't installed.</summary>
    public sealed class ToolFactAttribute : FactAttribute
    {
        public ToolFactAttribute(string program)
        {
            if (ArchiveTools.FindOnPath(program) == null)
            {
                Skip = $"{program} isn't installed";
            }
        }
    }
}
