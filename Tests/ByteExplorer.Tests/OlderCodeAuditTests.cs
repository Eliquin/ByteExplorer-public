// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Core.Analytics;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.FileOperations;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The findings of the audit of the files untouched since the first port, where older code met newer features: the
    /// copy engine's temporary files in listings, analytics following renames and counting every operation, path keys,
    /// network mounts on Linux, extended paths and view preset parsing.
    /// </summary>
    public sealed class OlderCodeAuditTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-olderaudit-" + Guid.NewGuid().ToString("N"));

        public OlderCodeAuditTests() => Directory.CreateDirectory(_root);

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
        public void ACopyEngineTemporaryFile_IsHiddenInListings_EvenWithoutTheHiddenAttribute()
        {
            // As FindFirstFile reports it on Windows: no Hidden attribute, only the leading dot.
            var temporary = new DirectoryIndexEntry(".movie.mkv.byteexplorer-0123456789abcdef0123456789abcdef.partial", false, 10, DateTime.UtcNow, FileAttributes.Normal);
            var ordinary = new DirectoryIndexEntry("movie.mkv", false, 10, DateTime.UtcNow, FileAttributes.Normal);
            var folder = new DirectoryIndexEntry(".x.byteexplorer-1.partial", true, 0, DateTime.UtcNow, FileAttributes.Directory);

            Assert.True(temporary.IsHidden);
            Assert.False(ordinary.IsHidden);
            Assert.False(folder.IsHidden); // the engine never makes temporary folders
            Assert.False(OperationTemporaries.IsTemporaryName("report.partial"));
            Assert.False(OperationTemporaries.IsTemporaryName(null));
        }

        [Fact]
        public void TheAnalytics_FollowARenamedFolder_AndTheFilesInIt()
        {
            string docs = Directory.CreateDirectory(Path.Combine(_root, "Docs")).FullName;
            string sub = Directory.CreateDirectory(Path.Combine(docs, "Taxes")).FullName;
            string file = Path.Combine(sub, "2026.pdf");
            File.WriteAllText(file, "x");
            using var analytics = new UserAnalyticsService(Path.Combine(_root, "analytics.json"), null);

            analytics.TrackFolderVisited(docs);
            analytics.TrackFolderVisited(sub);
            analytics.TrackFileOpened(file);

            string renamed = Path.Combine(_root, "Papers");
            analytics.TrackPathMoved(docs, renamed);

            AnalyticsSummary summary = analytics.GetSummary();
            string[] folders = summary.RecentlyVisitedFolders.Select(f => f.FullPath).ToArray();
            Assert.Contains(renamed, folders);
            Assert.Contains(Path.Combine(renamed, "Taxes"), folders);
            Assert.DoesNotContain(docs, folders);
            RecentFileInsight recent = Assert.Single(summary.RecentFileOpens);
            Assert.Equal(Path.Combine(renamed, "Taxes", "2026.pdf"), recent.FullPath);
            Assert.Equal("2026.pdf", recent.Name);

            // Renaming the file itself updates its shown name too.
            analytics.TrackPathMoved(recent.FullPath, Path.Combine(renamed, "Taxes", "2026-final.pdf"));
            Assert.Equal("2026-final.pdf", Assert.Single(analytics.GetSummary().RecentFileOpens).Name);
        }

        [Fact]
        public void FolderStatistics_UseTheSystemsPathRules()
        {
            using var analytics = new UserAnalyticsService(Path.Combine(_root, "analytics.json"), null);
            string upper = Path.Combine(_root, "Music");
            string lower = Path.Combine(_root, "music");
            analytics.TrackFolderVisited(upper);
            analytics.TrackFolderVisited(lower);

            int expected = PathUtil.Comparison == StringComparison.OrdinalIgnoreCase ? 1 : 2; // two folders on Linux
            Assert.Equal(expected, analytics.GetSummary().RecentlyVisitedFolders.Count);
        }

        [Fact]
        public async Task WhatIsTrackedWhileTheSavedHistoryLoads_IsAddedToIt_NotDropped()
        {
            string file = Path.Combine(_root, "analytics.json");
            string folder = Path.Combine(_root, "Projects");
            var saved = new UserAnalyticsData { TotalFolderVisits = 5 };
            saved.Folders[folder] = new FolderUsageStat { Path = folder, VisitCount = 5, FirstVisitedUtc = DateTime.UtcNow.AddDays(-3), LastVisitedUtc = DateTime.UtcNow.AddDays(-1) };
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(saved));

            using var analytics = new UserAnalyticsService(file, null);
            analytics.TrackFolderVisited(folder); // the first navigation of a start, racing the background load

            for (int i = 0; i < 200 && analytics.GetSummary().TotalFolderVisits < 6; i++)
            {
                await Task.Delay(10);
            }

            AnalyticsSummary summary = analytics.GetSummary();
            Assert.Equal(6, summary.TotalFolderVisits);
            Assert.Equal(6, Assert.Single(summary.RecentlyVisitedFolders).VisitCount);
        }

        [AvaloniaFact]
        public async Task EveryCopyMoveOrDelete_IsReportedOnce_IncludingRedo()
        {
            string src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
            string dst = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
            string file = Path.Combine(src, "a.txt");
            File.WriteAllText(file, "a");
            var queue = new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null);
            int reported = 0;
            queue.OperationFinished += (_, result) =>
            {
                Assert.Equal(FileOperationKind.Copy, result.Kind);
                reported++;
            };

            await queue.TransferAsync(FileOperationKind.Copy, new[] { file }, dst);
            Assert.Equal(1, reported);

            await queue.UndoLastAsync(); // an undo is not a new operation
            Assert.Equal(1, reported);

            await queue.RedoLastAsync(); // a redo is
            Assert.Equal(2, reported);
        }

        [Fact]
        public void TheLinuxMountTable_ClassifiesNetworkFileSystems_WithoutTouchingThem()
        {
            const string table =
                "/dev/nvme0n1p2 / ext4 rw,relatime 0 0\n" +
                "nas:/export/media /mnt/nas nfs4 rw,relatime 0 0\n" +
                "//server/Shared\\040Files /mnt/shared\\040files cifs rw 0 0\n" +
                "gvfsd-fuse /run/user/1000/gvfs fuse.gvfsd-fuse rw 0 0\n" +
                "/dev/sdb1 /mnt/nas/usb ext4 rw 0 0\n";
            (string MountPoint, string Type)[] parsed = MountPoints.ParseMountTable(table);

            Assert.Contains(parsed, m => m.MountPoint == "/mnt/shared files" && m.Type == "cifs");
            Assert.False(MountPoints.IsOnNetworkFileSystem("/home/me/Documents", parsed));
            Assert.True(MountPoints.IsOnNetworkFileSystem("/mnt/nas/Movies", parsed));
            Assert.True(MountPoints.IsOnNetworkFileSystem("/mnt/shared files/report.pdf", parsed));
            Assert.True(MountPoints.IsOnNetworkFileSystem("/run/user/1000/gvfs/smb-share:server=nas,share=photos", parsed));
            Assert.False(MountPoints.IsOnNetworkFileSystem("/mnt/nas/usb/backup", parsed)); // a local disk mounted inside the share
            Assert.False(MountPoints.IsOnNetworkFileSystem("/mnt/nasty", parsed)); // a prefix of the name is not inside it
        }

        [Fact]
        public void ExtendedPaths_AcceptForwardSlashes_AndKeepDriveRoots()
        {
            Assert.Equal(@"\\?\C:\Users\me\file.txt", ExtendedPath.Prefix("C:/Users/me/file.txt"));
            Assert.Equal(@"\\?\C:\", ExtendedPath.Prefix(@"C:\"));
            Assert.Equal(@"\\?\C:\Data", ExtendedPath.Prefix(@"C:\Data\"));
            Assert.Equal(@"\\?\UNC\server\share\file.txt", ExtendedPath.Prefix("//server/share/file.txt"));
            Assert.Equal(@"\\?\D:\x", ExtendedPath.Prefix(@"\\?\D:\x"));
        }

        [Theory]
        [InlineData("LargeIcons", FileListViewPreset.LargeIcons)]
        [InlineData("columns", FileListViewPreset.Columns)]
        [InlineData("3", FileListViewPreset.Details)]
        [InlineData("42", FileListViewPreset.Details)]
        [InlineData("-1", FileListViewPreset.Details)]
        [InlineData("", FileListViewPreset.Details)]
        [InlineData(null, FileListViewPreset.Details)]
        [InlineData("Gallery", FileListViewPreset.Details)]
        public void AViewPresetFromTheSettingsFile_IsAlwaysARealPreset(string value, FileListViewPreset expected)
        {
            Assert.Equal(expected, FileListViewPresets.FromSettingsString(value));
        }
    }
}
