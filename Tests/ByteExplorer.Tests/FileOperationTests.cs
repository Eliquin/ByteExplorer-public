// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>File operations on a real temporary folder, on whatever OS the tests run on.</summary>
    public sealed class FileOperationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-tests-" + Guid.NewGuid().ToString("N"));

        public FileOperationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Enumerate_ReturnsEntriesWithMetadata()
        {
            File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[1234]);
            Directory.CreateDirectory(Path.Combine(_root, "sub"));
            File.WriteAllText(Path.Combine(_root, ".hidden"), "x");

            var entries = NativeFileEnumerator.Enumerate(_root, CancellationToken.None).ToDictionary(e => e.Name);
            Assert.Equal(1234, entries["a.bin"].SizeBytes);
            Assert.False(entries["a.bin"].IsDirectory);
            Assert.True(entries["sub"].IsDirectory);
            Assert.True(entries["a.bin"].LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5));
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(entries[".hidden"].IsHidden, "dotfiles are hidden on Unix");
            }
        }

        [Fact]
        public void Enumerate_MissingFolderThrowsDirectoryNotFound()
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                NativeFileEnumerator.Enumerate(Path.Combine(_root, "missing"), CancellationToken.None).ToList());
        }

        [Fact]
        public void CopyFile_NeverOverwritesAndReportsProgress()
        {
            string source = Path.Combine(_root, "source.bin");
            string destination = Path.Combine(_root, "dest.bin");
            var data = new byte[3 * 1024 * 1024 + 17];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(source, data);

            long last = -1;
            NativeFileOperations.CopyFile(source, destination, (done, total) => last = done, CancellationToken.None);
            Assert.Equal(data.Length, last);
            Assert.Equal(data, File.ReadAllBytes(destination));

            File.WriteAllText(destination, "keep me");
            Assert.ThrowsAny<Exception>(() => NativeFileOperations.CopyFile(source, destination, null, CancellationToken.None));
            Assert.Equal("keep me", File.ReadAllText(destination));
        }

        [Fact]
        public void CopyFile_CancelledCopyLeavesNoPartialFile()
        {
            if (OperatingSystem.IsWindows())
            {
                return; // the Win32 path is covered by CopyFileEx itself
            }

            string source = Path.Combine(_root, "big.bin");
            string destination = Path.Combine(_root, "big-copy.bin");
            File.WriteAllBytes(source, new byte[8 * 1024 * 1024]);
            using var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                NativeFileOperations.CopyFile(source, destination, (done, _) =>
                {
                    if (done > 0)
                    {
                        cts.Cancel();
                    }
                }, cts.Token));
            Assert.False(File.Exists(destination));
        }

        [Fact]
        public void DeleteTree_RemovesLinksWithoutFollowingThem()
        {
            if (OperatingSystem.IsWindows())
            {
                return; // symlink creation needs a privilege on Windows
            }

            string outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not delete");

            string tree = Path.Combine(_root, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "nested"));
            File.WriteAllText(Path.Combine(tree, "nested", "file.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(tree, "link-to-outside"), outside);

            NativeFileOperations.DeleteDirectoryTreePermanently(tree, CancellationToken.None);
            Assert.False(Directory.Exists(tree));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        }

        [Fact]
        public void Trash_FreedesktopLayoutAndUniqueNames()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            string dataHome = Path.Combine(_root, "xdg-data");
            string previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    string victim = Path.Combine(_root, "victim file.txt");
                    File.WriteAllText(victim, "content " + i);
                    Trash.MoveToTrash(victim);
                    Assert.False(File.Exists(victim));
                }

                string files = Path.Combine(dataHome, "Trash", "files");
                string info = Path.Combine(dataHome, "Trash", "info");
                Assert.True(File.Exists(Path.Combine(files, "victim file.txt")));
                Assert.True(File.Exists(Path.Combine(files, "victim file.2.txt")));
                string trashInfo = File.ReadAllText(Path.Combine(info, "victim file.txt.trashinfo"));
                Assert.StartsWith("[Trash Info]\nPath=", trashInfo);
                Assert.Contains("victim%20file.txt", trashInfo);
                Assert.Contains("DeletionDate=", trashInfo);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            }
        }

        [Fact]
        public async Task Archive_RoundTripsATree()
        {
            string folder = Path.Combine(_root, "project");
            Directory.CreateDirectory(Path.Combine(folder, "src", "empty"));
            File.WriteAllText(Path.Combine(folder, "src", "main.cs"), "class A {}");
            File.WriteAllText(Path.Combine(folder, "readme.md"), "# hi");

            var service = new ArchiveService();
            string zip = await service.CompressAsync(new[] { folder }, Path.Combine(_root, "project.zip"), null, CancellationToken.None);
            Assert.True(File.Exists(zip));
            Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));

            string extracted = await service.ExtractAsync(zip, null, CancellationToken.None);
            Assert.Equal("class A {}", File.ReadAllText(Path.Combine(extracted, "project", "src", "main.cs")));
            Assert.True(Directory.Exists(Path.Combine(extracted, "project", "src", "empty")));
            // "project" is taken by the source folder, so the extraction gets the next free name.
            Assert.Equal(Path.Combine(_root, "project (2)"), extracted);
        }

        [Fact]
        public async Task Archive_RejectsZipSlip()
        {
            string zip = Path.Combine(_root, "evil.zip");
            using (var stream = File.Create(zip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../../escaped.txt").Open());
                writer.Write("pwned");
            }

            var service = new ArchiveService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(zip, null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "escaped.txt")));
            Assert.Empty(Directory.GetDirectories(_root)); // the staging folder was removed
        }

        [Theory]
        [InlineData("gedit %U", new[] { "gedit", "\0path\0" })]
        [InlineData("\"/opt/My App/app\" --new-window %f", new[] { "/opt/My App/app", "--new-window", "\0path\0" })]
        [InlineData("vlc --started-from-file %U %i", new[] { "vlc", "--started-from-file", "\0path\0" })]
        [InlineData("echo 100%%", new[] { "echo", "100%" })]
        [InlineData("app \"quoted \\\"inner\\\"\" %F %F", new[] { "app", "quoted \"inner\"", "\0path\0" })]
        public void DesktopExec_IsParsedPerSpec(string exec, string[] expected)
        {
            List<string> argv = OpenWithApplications.ParseDesktopExec(exec);
            Assert.Equal(expected, argv);
        }
    }
}
