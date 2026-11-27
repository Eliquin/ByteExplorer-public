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
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>Browsing inside ZIP files: paths, listing, extraction (and its safety), and the pane's read-only behaviour.</summary>
    public sealed class ArchiveBrowsingTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-zip-" + Guid.NewGuid().ToString("N"));
        private readonly string _zip;

        public ArchiveBrowsingTests()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "be-zip-trash"));
            Directory.CreateDirectory(_root);
            _zip = Path.Combine(_root, "photos.zip");
            using ZipArchive zip = ZipFile.Open(_zip, ZipArchiveMode.Create);
            Add(zip, "readme.txt", "hello");
            Add(zip, "2024/beach.jpg", "jpeg bytes");
            Add(zip, "2024/summer/sea.jpg", "more bytes");
            zip.CreateEntry("empty/");
            Add(zip, "../evil.txt", "should never be listed or extracted");
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

        [Fact]
        public void Paths_ResolveIntoTheArchive()
        {
            Assert.True(ArchiveBrowser.TryResolve(_zip, out string archive, out string inner));
            Assert.Equal(_zip, archive);
            Assert.Equal(string.Empty, inner);

            Assert.True(ArchiveBrowser.TryResolve(Path.Combine(_zip, "2024", "summer"), out _, out inner));
            Assert.Equal("2024/summer", inner);

            Assert.False(ArchiveBrowser.TryResolve(_root, out _, out _));
            Assert.False(ArchiveBrowser.TryResolve(Path.Combine(_root, "missing.zip", "x"), out _, out _));
        }

        [Fact]
        public void Listing_ShowsFoldersImpliedByFilePaths_AndHidesUnsafeNames()
        {
            DirectoryIndex root = ArchiveBrowser.List(_zip, string.Empty, _zip);
            Assert.False(root.HasError);
            Assert.Equal(new[] { "2024", "empty", "readme.txt" }, root.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.True(root.Entries.Single(e => e.Name == "2024").IsDirectory); // implied: no "2024/" entry exists

            DirectoryIndex year = ArchiveBrowser.List(_zip, "2024", Path.Combine(_zip, "2024"));
            Assert.Equal(new[] { "beach.jpg", "summer" }, year.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(10, year.Entries.Single(e => e.Name == "beach.jpg").SizeBytes);

            Assert.True(ArchiveBrowser.List(_zip, "nope", "x").HasError);
        }

        [Fact]
        public void DamagedArchive_IsAnErrorListing_NotAnException()
        {
            string bad = Path.Combine(_root, "bad.zip");
            File.WriteAllText(bad, "not a zip");
            DirectoryIndex index = ArchiveBrowser.List(bad, string.Empty, bad);
            Assert.True(index.HasError);
        }

        [Fact]
        public void Extract_FileAndFolder_ToTemp_WithoutUnsafeEntries()
        {
            IReadOnlyList<string> extracted = ArchiveBrowser.ExtractToTemp(
                new[] { Path.Combine(_zip, "readme.txt"), Path.Combine(_zip, "2024") }, null, CancellationToken.None);
            try
            {
                Assert.Equal("hello", File.ReadAllText(extracted[0]));
                Assert.Equal("more bytes", File.ReadAllText(Path.Combine(extracted[1], "summer", "sea.jpg")));
                Assert.StartsWith(ArchiveBrowser.TempRoot, extracted[0]);

                // Extracting the whole archive skips "../evil.txt": nothing lands beside or above the extraction.
                IReadOnlyList<string> all = ArchiveBrowser.ExtractToTemp(new[] { _zip }, null, CancellationToken.None);
                string folder = Path.GetDirectoryName(all[0]);
                Assert.False(File.Exists(Path.Combine(folder, "evil.txt")));
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder), "evil.txt")));
                Assert.True(File.Exists(Path.Combine(all[0], "2024", "beach.jpg")));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(extracted[0]), true);
            }
        }

        [Theory]
        [InlineData("../x", true)]
        [InlineData("a/../../x", true)]
        [InlineData("/etc/passwd", true)]
        [InlineData(@"C:\Windows\x", true)]
        [InlineData("a/b..c/d", false)]
        [InlineData("dir/file.txt", false)]
        public void UnsafeNames(string name, bool unsafeName)
        {
            Assert.Equal(unsafeName, ArchiveBrowser.IsUnsafeName(name));
        }

        [AvaloniaFact]
        public async Task Pane_OpensTheZipLikeAFolder_ReadOnly_AndExtractCanBeUndone()
        {
            var window = new ModernMainWindow { Width = 1300, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(_root);
                await Pump(5);

                FileListItemViewModel zipItem = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "photos.zip");
                await pane.OpenItemAsync(zipItem);
                await Pump(5);
                Assert.Equal(_zip, pane.CurrentPath);
                Assert.True(pane.IsInsideArchive);
                Assert.Contains(pane.Items.OfType<FileListItemViewModel>(), i => i.Name == "2024" && i.IsDirectory);

                FileListItemViewModel year = pane.Items.OfType<FileListItemViewModel>().Single(i => i.Name == "2024");
                await pane.OpenItemAsync(year);
                await Pump(5);
                Assert.Equal(Path.Combine(_zip, "2024"), pane.CurrentPath);

                // Read-only: nothing that writes is available.
                pane.UpdateSelection(pane.Items.OfType<FileListItemViewModel>().Where(i => i.Name == "beach.jpg"));
                Assert.False(pane.DeleteCommand.CanExecute(null));
                Assert.False(pane.RenameCommand.CanExecute(null));
                Assert.False(pane.CutCommand.CanExecute(null));
                Assert.False(pane.NewFolderCommand.CanExecute(null));
                Assert.True(pane.CopyCommand.CanExecute(null));

                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "inside-zip.png"));
                }

                // Extract beside the archive, then undo it.
                await pane.ExtractFromArchiveAsync(new[] { Path.Combine(_zip, "2024", "beach.jpg") });
                Assert.Equal("jpeg bytes", File.ReadAllText(Path.Combine(_root, "beach.jpg")));
                await window.ViewModel.Operations.UndoLastAsync();
                Assert.False(File.Exists(Path.Combine(_root, "beach.jpg")));

                // Up leaves the archive.
                pane.NavigateUpCommand.Execute(null);
                for (int i = 0; i < 50 && pane.CurrentPath != _zip; i++)
                {
                    await Pump(1);
                }

                pane.NavigateUpCommand.Execute(null);
                for (int i = 0; i < 50 && pane.CurrentPath != _root; i++)
                {
                    await Pump(1);
                }

                Assert.Equal(_root, pane.CurrentPath);
                Assert.False(pane.IsInsideArchive);
            }
            finally
            {
                window.Close();
            }
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
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
}
