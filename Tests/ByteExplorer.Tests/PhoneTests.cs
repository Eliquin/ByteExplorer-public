// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Omnibar;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Services;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.FileOperations;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Phones: paths, the phone copy route (both directions, conflicts, failures, cancel, move, delete), listing, the
    /// address bar and breadcrumbs, drag data, the Linux GVfs parsers and the Windows interop's method order. Runs against
    /// <see cref="FakePhoneProvider"/>; every test is an AvaloniaFact so none overlaps a window test (the fake is global).
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class PhoneTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-phone-" + Guid.NewGuid().ToString("N"));
        private readonly FakePhoneProvider _phone = new FakePhoneProvider();
        private readonly IDisposable _registration;

        public PhoneTests()
        {
            Directory.CreateDirectory(_root);
            _registration = Phones.UseProviderForTests(_phone);
        }

        public void Dispose()
        {
            _registration.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------------ paths

        [AvaloniaFact]
        public void PhonePaths_BehaveLikePaths_WithoutTouchingTheDisk()
        {
            string camera = "phone://Pixel 7/Internal shared storage/DCIM/Camera";
            Assert.True(Phones.IsPhonePath(camera));
            Assert.False(Phones.IsPhonePath(_root));
            Assert.Equal("Camera", PathUtil.GetName(camera));
            Assert.Equal("phone://Pixel 7/Internal shared storage/DCIM", PathUtil.GetParent(camera));
            Assert.Null(PathUtil.GetParent("phone://Pixel 7"));
            Assert.Equal(camera + "/IMG_1.jpg", PathUtil.Combine(camera, "IMG_1.jpg"));
            // Path.Combine on Windows adds '\': still the same path.
            Assert.True(PathUtil.AreSame(camera + "\\IMG_1.jpg", camera + "/img_1.JPG"));
            Assert.Equal(camera, PathUtil.Normalize(camera + "//"));
            Assert.True(PathUtil.IsInside(camera + "/IMG_1.jpg", "phone://Pixel 7"));
            Assert.False(PathUtil.IsInside("phone://Pixel 7 Pro/x", "phone://Pixel 7"));
            Assert.Equal("phone://Pixel 7/", PathUtil.Split(camera).Root);
            Assert.Equal(new[] { "Internal shared storage", "DCIM", "Camera" }, PathUtil.Split(camera).Segments);
            Assert.Equal("a_b_c", PhonePaths.SanitizeName("a/b:c"));
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pixel 7" };
            Assert.Equal("Pixel 7 (2)", PhonePaths.UniqueDeviceName("Pixel 7", taken));
        }

        [AvaloniaFact]
        public void BreadcrumbsAndTheAddressBar_ShowAndAcceptPhonePaths()
        {
            IReadOnlyList<Crumb> crumbs = BreadcrumbBuilder.Build("phone://Pixel 7/Internal shared storage/DCIM", null, null);
            Assert.Equal(new[] { "Pixel 7", "Internal shared storage", "DCIM" }, crumbs.Select(c => c.Name));
            Assert.Equal(CrumbKind.Phone, crumbs[0].Kind);
            Assert.Equal("phone://Pixel 7/Internal shared storage", crumbs[1].Path);

            OmnibarInput input = OmnibarInput.Parse("phone://Pixel 7/Internal shared storage/DC", "/tmp", "/home/me");
            Assert.Equal(OmnibarInputKind.Path, input.Kind);
            Assert.Equal("phone://Pixel 7/Internal shared storage/DC", input.FullPath);
            Assert.Equal("phone://Pixel 7/Internal shared storage", input.Folder);
            Assert.Equal("DC", input.Partial);
        }

        [AvaloniaFact]
        public async Task APhoneFolder_IsListedThroughItsProvider_FoldersFirst()
        {
            _phone.AddFile("b.txt", "b");
            _phone.AddFolder("DCIM");
            _phone.AddFile("a.txt", "a");
            var service = new DirectoryIndexService(null);

            DirectoryIndex index = await service.BuildIndexAsync(_phone.Storage, 8000);
            Assert.False(index.HasError);
            Assert.Equal(new[] { "DCIM", "a.txt", "b.txt" }, index.Entries.Select(e => e.Name));
            Assert.True(index.IsSortedByName);

            DirectoryIndex root = await service.BuildIndexAsync(_phone.Root, 8000);
            Assert.Equal("Internal shared storage", Assert.Single(root.Entries).Name);

            DirectoryIndex missing = await service.BuildIndexAsync(_phone.Storage + "/Nope", 8000);
            Assert.True(missing.HasError);
        }

        [AvaloniaFact]
        public async Task TheTree_ListsAPhonesSubfolders()
        {
            _phone.AddFolder("DCIM");
            _phone.AddFolder("Music");
            _phone.AddFile("notes.txt", "x");
            IReadOnlyList<DirectoryNodeInfo> folders = await new FileSystemNavigationService(null).GetSubdirectoriesAsync(_phone.Storage, includeHiddenAndSystem: false);
            Assert.Equal(new[] { "DCIM", "Music" }, folders.Select(f => f.Name));
            Assert.Equal(_phone.Storage + "/DCIM", folders[0].FullPath);
        }

        // ------------------------------------------------------------------ copies into the PC

        [AvaloniaFact]
        public async Task CopyFromThePhone_ArrivesWhole_IsUndoable_AndKeepsTheDate()
        {
            DateTime taken = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            string photo = _phone.AddFile("DCIM/IMG_1.jpg", "pixels", taken);
            _phone.AddFile("DCIM/Sub/IMG_2.jpg", "more");
            string dcim = PathUtil.GetParent(photo);
            var engine = new FileOperationService(null);

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Copy, new[] { dcim }, _root, new FileOperationContext());
            Assert.True(outcome.Result.IsFullSuccess, Describe(outcome));
            Assert.Equal("pixels", File.ReadAllText(Path.Combine(_root, "DCIM", "IMG_1.jpg")));
            Assert.Equal("more", File.ReadAllText(Path.Combine(_root, "DCIM", "Sub", "IMG_2.jpg")));
            Assert.Equal(taken, File.GetLastWriteTimeUtc(Path.Combine(_root, "DCIM", "IMG_1.jpg")));
            Assert.True(_phone.Exists(photo)); // a copy leaves the phone alone
            Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));

            Assert.NotNull(outcome.Record); // what arrived on the PC can be undone
            await engine.UndoAsync(outcome.Record, new FileOperationContext());
            Assert.False(Directory.Exists(Path.Combine(_root, "DCIM")));
            Assert.True(_phone.Exists(photo));
        }

        [AvaloniaFact]
        public async Task MoveFromThePhone_RemovesFromThePhoneOnlyAfterTheCopyArrived_AndIsNotUndoable()
        {
            string a = _phone.AddFile("a.txt", "alpha");
            string b = _phone.AddFile("b.txt", "beta");
            var engine = new FileOperationService(null);
            _phone.FailNextOpens = 1; // b fails once: skipped (nobody to ask), and must stay on the phone

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Move, new[] { b, a }, _root, new FileOperationContext());
            Assert.Single(outcome.Result.Failures);
            Assert.True(_phone.Exists(b));
            Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
            Assert.False(_phone.Exists(a));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "a.txt")));
            Assert.Null(outcome.Record); // the phone's copy is gone: undo couldn't put it back
        }

        // ------------------------------------------------------------------ copies onto the phone

        [AvaloniaFact]
        public async Task CopyToThePhone_FoldersAndFiles_NotUndoable()
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, "Trip")).FullName;
            File.WriteAllText(Path.Combine(folder, "day1.txt"), "sun");
            Directory.CreateDirectory(Path.Combine(folder, "Night"));
            File.WriteAllText(Path.Combine(folder, "Night", "moon.txt"), "moon");
            var engine = new FileOperationService(null);
            var progress = new List<FileOperationProgress>();

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Copy, new[] { folder }, _phone.Storage,
                new FileOperationContext { Progress = progress.Add });
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal("sun", _phone.ReadText(_phone.Storage + "/Trip/day1.txt"));
            Assert.Equal("moon", _phone.ReadText(_phone.Storage + "/Trip/Night/moon.txt"));
            Assert.True(File.Exists(Path.Combine(folder, "day1.txt")));
            Assert.Null(outcome.Record);
            Assert.Equal(7, progress.Last().TotalBytes);
            Assert.Equal(7, progress.Last().CompletedBytes);
        }

        [AvaloniaFact]
        public async Task AnUploadThatBreaksOff_LeavesNothingOnThePhone_AndRetrySucceeds()
        {
            string file = Path.Combine(_root, "video.bin");
            File.WriteAllBytes(file, new byte[600_000]);
            _phone.FailUploadAfterBytes = 300_000;
            var engine = new FileOperationService(null);
            var errors = new ScriptedErrors(ErrorChoice.Retry);

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Copy, new[] { file }, _phone.Storage,
                new FileOperationContext { ErrorResolver = errors });
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.Equal(1, errors.Asked);
            Assert.Equal(2, _phone.UploadsStarted);
            Assert.Single(_phone.AllPaths(), p => PhonePaths.GetName(p) == "video.bin");
        }

        [AvaloniaFact]
        public async Task ReplaceOnThePhone_KeepsTheOldFileUntilTheNewOneIsComplete()
        {
            string old = _phone.AddFile("song.mp3", "old version");
            string incoming = Path.Combine(_root, "song.mp3");
            File.WriteAllText(incoming, "new version, longer");
            var engine = new FileOperationService(null);

            // The upload breaks off: Skip. The old file must still be there, and no temporary file left.
            _phone.FailUploadAfterBytes = 5;
            FileOperationOutcome failed = await engine.TransferAsync(FileOperationKind.Copy, new[] { incoming }, _phone.Storage,
                new FileOperationContext { ConflictResolver = new FixedConflict(ConflictChoice.Replace), ErrorResolver = new ScriptedErrors(ErrorChoice.Skip) });
            Assert.Single(failed.Result.Failures);
            Assert.Equal("old version", _phone.ReadText(old));
            Assert.Single(_phone.AllPaths(), p => PhonePaths.GetParent(p) is string parent && PathUtil.AreSame(parent, _phone.Storage));

            FileOperationOutcome replaced = await engine.TransferAsync(FileOperationKind.Copy, new[] { incoming }, _phone.Storage,
                new FileOperationContext { ConflictResolver = new FixedConflict(ConflictChoice.Replace) });
            Assert.True(replaced.Result.IsFullSuccess);
            Assert.Equal("new version, longer", _phone.ReadText(old));
            Assert.DoesNotContain(_phone.AllPaths(), p => p.EndsWith(".partial", StringComparison.Ordinal));
        }

        [AvaloniaFact]
        public async Task KeepBothAndReplaceIfNewer_WorkOnThePhone()
        {
            _phone.AddFile("doc.txt", "phone copy", DateTime.UtcNow.AddDays(1)); // newer than the incoming one
            string incoming = Path.Combine(_root, "doc.txt");
            File.WriteAllText(incoming, "pc copy");
            var engine = new FileOperationService(null);

            await engine.TransferAsync(FileOperationKind.Copy, new[] { incoming }, _phone.Storage,
                new FileOperationContext { ConflictResolver = new FixedConflict(ConflictChoice.ReplaceIfNewer) });
            Assert.Equal("phone copy", _phone.ReadText(_phone.Storage + "/doc.txt")); // older: skipped

            await engine.TransferAsync(FileOperationKind.Copy, new[] { incoming }, _phone.Storage,
                new FileOperationContext { ConflictResolver = new FixedConflict(ConflictChoice.KeepBoth) });
            Assert.Equal("pc copy", _phone.ReadText(_phone.Storage + "/doc (2).txt"));
        }

        [AvaloniaFact]
        public async Task ACancelledUpload_LeavesNothingOnThePhone()
        {
            string file = Path.Combine(_root, "big.bin");
            File.WriteAllBytes(file, new byte[4_000_000]);
            _phone.WriteDelayMs = 40; // about 16 buffers: long enough to cancel part-way
            using var cancel = new CancellationTokenSource();
            var engine = new FileOperationService(null);

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Copy, new[] { file }, _phone.Storage,
                new FileOperationContext
                {
                    CancellationToken = cancel.Token,
                    Progress = p =>
                    {
                        if (p.CompletedBytes > 0)
                        {
                            cancel.Cancel();
                        }
                    }
                });
            Assert.True(outcome.Result.WasCancelled, Describe(outcome));
            Assert.DoesNotContain(_phone.AllPaths(), p => PhonePaths.GetName(p) == "big.bin");
        }

        [AvaloniaFact]
        public async Task MoveToThePhone_DeletesTheSourceOnlyAfterTheUpload()
        {
            string file = Path.Combine(_root, "move.txt");
            File.WriteAllText(file, "moving");
            var engine = new FileOperationService(null);

            _phone.FailUploadAfterBytes = 2;
            await engine.TransferAsync(FileOperationKind.Move, new[] { file }, _phone.Storage, new FileOperationContext());
            Assert.True(File.Exists(file)); // the upload failed: the source stays

            FileOperationOutcome outcome = await engine.TransferAsync(FileOperationKind.Move, new[] { file }, _phone.Storage, new FileOperationContext());
            Assert.True(outcome.Result.IsFullSuccess);
            Assert.False(File.Exists(file));
            Assert.Equal("moving", _phone.ReadText(_phone.Storage + "/move.txt"));
        }

        [AvaloniaFact]
        public async Task PhoneToPhone_AndADuplicateInTheSameFolder()
        {
            string photo = _phone.AddFile("IMG.jpg", "pic");
            string backup = _phone.AddFolder("Backup");
            var engine = new FileOperationService(null);

            FileOperationOutcome first = await engine.TransferAsync(FileOperationKind.Copy, new[] { photo }, backup, new FileOperationContext());
            Assert.True(first.Result.IsFullSuccess, Describe(first));
            Assert.Equal("pic", _phone.ReadText(backup + "/IMG.jpg"));

            await engine.TransferAsync(FileOperationKind.Copy, new[] { photo }, _phone.Storage, new FileOperationContext());
            Assert.Equal("pic", _phone.ReadText(_phone.Storage + "/IMG - Copy.jpg"));
            Assert.Empty(Directory.Exists(PhoneTemp.Root) ? Directory.GetFiles(PhoneTemp.Root, "*", SearchOption.TopDirectoryOnly) : Array.Empty<string>());
        }

        [AvaloniaFact]
        public async Task DeletingOnThePhone_IsPermanent_AndNotRecordedForUndo()
        {
            string photo = _phone.AddFile("IMG.jpg", "pic");
            _phone.AddFile("Folder/inside.txt", "x");
            var engine = new FileOperationService(null);

            // Asked for the trash, but a phone has none: deleted for good, nothing to undo.
            FileOperationOutcome outcome = await engine.DeleteAsync(new[] { photo, _phone.Storage + "/Folder" }, permanent: false, new FileOperationContext());
            Assert.True(outcome.Result.SuccessCount == 2, Describe(outcome));
            Assert.False(_phone.Exists(photo));
            Assert.False(_phone.Exists(_phone.Storage + "/Folder/inside.txt"));
            Assert.Null(outcome.Record);
        }

        [AvaloniaFact]
        public async Task TheQueue_RetriesFailedPhoneItems()
        {
            string a = _phone.AddFile("a.txt", "a");
            var queue = new FileOperationQueue(new FileOperationService(null), new UiDispatcher(Dispatcher.UIThread), () => null);
            _phone.FailNextOpens = 1;

            await queue.TransferAsync(FileOperationKind.Copy, new[] { a }, _root);
            FileOperationJob job = queue.Jobs.Last();
            Assert.True(job.CanRetry);
            Assert.False(queue.CanUndo); // nothing arrived

            job.RetryCommand.Execute(null);
            for (int i = 0; i < 200 && !File.Exists(Path.Combine(_root, "a.txt")); i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "a.txt")));
        }

        [AvaloniaFact]
        public void OpeningAPhoneFile_CopiesItToTheTemporaryFolder()
        {
            string photo = _phone.AddFile("IMG.jpg", "pic", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string local = PhoneTemp.Download(photo, null, CancellationToken.None);
            try
            {
                Assert.True(PhoneTemp.IsTemporaryCopy(local));
                Assert.Equal("IMG.jpg", Path.GetFileName(local));
                Assert.Equal("pic", File.ReadAllText(local));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(local), true);
            }
        }

        // ------------------------------------------------------------------ drag, drop, clipboard data

        [AvaloniaFact]
        public async Task PhoneItems_TravelInByteExplorersOwnDragData_Only()
        {
            string[] paths = { _phone.Storage + "/a.jpg", _phone.Storage + "/b.jpg" };
            using Avalonia.Input.DataTransfer data = await ClipboardFileService.BuildTransferAsync(paths, isCut: false, storage: null);
            IDataTransfer transfer = data;
            Assert.True(transfer.Contains(ClipboardFileService.AppPathsFormat));
            Assert.False(transfer.Contains(DataFormat.File));
            Assert.True(FileDragDropHelper.TryGetDroppedPaths(transfer, out IReadOnlyList<string> dropped));
            Assert.Equal(paths, dropped);
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task ThePhone_AppearsInThisPcAndTheTree_AndCanBeBrowsedAndWritten()
        {
            _phone.AddFile("DCIM/IMG_1.jpg", "pixels");
            string local = Path.Combine(_root, "holiday.txt");
            File.WriteAllText(local, "postcard");
            var window = new ByteExplorer.ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(10);
                ByteExplorer.ViewModels.FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadThisPcAsync();

                // Phones are listed in the background a few seconds after they're plugged in (here: the window opens).
                await WaitUntil(() => pane.ThisPcDrives.Any(d => d.IsPhone), rounds: 900);
                ByteExplorer.ViewModels.ThisPcDriveItem tile = pane.ThisPcDrives.First(d => d.IsPhone);
                Assert.Equal("Pixel 7", tile.DisplayName);
                Assert.Equal("Google · Pixel 7", tile.TypeDisplay);
                Assert.Equal(ByteExplorer.Icons.Symbol.Phone, tile.Glyph);
                Assert.Contains("free of", tile.CapacityDisplay);
                Screenshot(window, "phone-this-pc.png");
                await WaitUntil(() => window.ViewModel.NavigationViewModel.ThisPcNode.Children.Any(n => n.NodeType == NavigationNodeType.Phone));

                await pane.OpenThisPcDriveAsync(tile);
                await WaitUntil(() => pane.Items?.Count == 1);
                Assert.Equal(_phone.Root, pane.CurrentPath);
                Assert.True(pane.IsOnWindowsPhone);

                await pane.LoadFolderAsync(_phone.Storage);
                await WaitUntil(() => pane.Items?.Count == 1);
                Assert.False(pane.PropertiesCommand.CanExecute(null)); // no file path behind a Windows phone's items

                // Paste (a drop) onto the phone, through the queue.
                Assert.True(await pane.ExecuteDropAsync(new[] { local }, pane.CurrentPath, isMove: false) || _phone.Exists(_phone.Storage + "/holiday.txt"));
                await WaitUntil(() => pane.Items?.Count == 2);
                Assert.Equal("postcard", _phone.ReadText(_phone.Storage + "/holiday.txt"));
                Assert.True(File.Exists(local));
                Assert.False(window.ViewModel.Operations.CanUndo); // nothing on a phone can be put back
                Screenshot(window, "phone-folder.png");

                // Rename and a new folder, on the phone.
                var item = pane.Items.OfType<ByteExplorer.ViewModels.FileListItemViewModel>().Single(i => i.Name == "holiday.txt");
                await pane.RenameItemAsync(item, "postcard.txt");
                Assert.True(_phone.Exists(_phone.Storage + "/postcard.txt"));
                pane.NewFolderCommand.Execute(null);
                await WaitUntil(() => _phone.Exists(_phone.Storage + "/New folder"));

                // The phone's own menu: delete is permanent, no Properties or compressing.
                var menu = ByteExplorer.Views.Menus.FileContextMenu.ForItems(window, pane, new[] { _phone.Storage + "/postcard.txt" }, pathsAreSelection: false);
                string[] headers = menu.ItemsSource.OfType<Avalonia.Controls.MenuItem>().Select(m => m.Header as string).ToArray();
                Assert.Contains("Delete from phone", headers);
                Assert.DoesNotContain("Properties", headers);
                Assert.DoesNotContain(headers, h => h != null && h.Contains("ZIP", StringComparison.Ordinal));

                // Pinning a phone folder isn't offered (it is there only while the phone is connected).
                Assert.False(window.ViewModel.TogglePinCommand.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        }

        private static void Screenshot(Avalonia.Controls.Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Dispatcher.UIThread.RunJobs();
                Directory.CreateDirectory(directory);
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(directory, name));
            }
        }

        private static async Task WaitUntil(Func<bool> condition, int rounds = 300)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition());
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }

        // ------------------------------------------------------------------ Linux GVfs

        [AvaloniaFact]
        public void GvfsNamesAndGioOutput_AreUnderstood()
        {
            Assert.True(MountedPhoneProvider.TryParseGvfsFolder("mtp:host=SAMSUNG_SAMSUNG_Android_R58M12345", out string scheme, out string host));
            Assert.Equal("mtp", scheme);
            Assert.Equal("SAMSUNG_SAMSUNG_Android_R58M12345", host);
            Assert.True(MountedPhoneProvider.TryParseGvfsFolder("gphoto2:host=%5Busb%3A001%2C005%5D", out scheme, out host));
            Assert.Equal("gphoto2", scheme);
            Assert.Equal("[usb:001,005]", host);
            Assert.False(MountedPhoneProvider.TryParseGvfsFolder("smb-share:server=nas,share=photos", out _, out _));
            Assert.Equal("SAMSUNG Android", MountedPhoneProvider.NameFromHost("SAMSUNG_SAMSUNG_Android_R58M12345"));

            const string gio =
                "Drive(0): Samsung SSD\n  Type: GProxyDrive\n" +
                "Volume(0): Galaxy S21\n  Type: GProxyVolume (GProxyVolumeMonitorMTP)\n  activation_root=mtp://SAMSUNG_SAMSUNG_Android_R58M12345/\n  can_mount=1\n" +
                "Volume(1): Pixel 7\n  Type: GProxyVolume (GProxyVolumeMonitorMTP)\n  activation_root=mtp://Google_Pixel_7_29031FDH2000/\n" +
                "Mount(0): Pixel 7 -> mtp://Google_Pixel_7_29031FDH2000/\n  Type: GProxyShadowMount\n" +
                "Volume(2): USB stick\n  activation_root=file:///media/usb\n";
            IReadOnlyList<MountedPhoneProvider.GioVolume> volumes = MountedPhoneProvider.ParseGioMountList(gio);
            Assert.Equal(2, volumes.Count);
            Assert.Equal("Galaxy S21", volumes[0].Name);
            Assert.False(volumes[0].IsMounted);
            Assert.Equal("SAMSUNG_SAMSUNG_Android_R58M12345", volumes[0].Host);
            Assert.True(volumes[1].IsMounted);

            if (OperatingSystem.IsLinux())
            {
                const string phoneFolder = "/run/user/1000/gvfs/mtp:host=Google_Pixel_7/Internal shared storage/DCIM";
                Assert.True(Phones.IsPhonePath(phoneFolder));
                Assert.Equal("/run/user/1000/gvfs/mtp:host=Google_Pixel_7", MountedPhoneProvider.DeviceRootOf(phoneFolder));
                Assert.True(Phones.AreOnSamePhone(phoneFolder, "/run/user/1000/gvfs/mtp:host=Google_Pixel_7/x"));
                Assert.False(Phones.AreOnSamePhone(phoneFolder, "/run/user/1000/gvfs/mtp:host=Other/x"));
                IReadOnlyList<Crumb> crumbs = BreadcrumbBuilder.Build(phoneFolder, "/home/me", null);
                Assert.Equal(new[] { "Google Pixel 7", "Internal shared storage", "DCIM" }, crumbs.Select(c => c.Name));
            }
        }

        [AvaloniaFact]
        public async Task ALinuxPhoneMountedByGvfs_IsWrittenThePhoneWay()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            // A folder named like GVfs's mount of a phone: the Linux provider's real file access runs against it.
            string storage = Directory.CreateDirectory(Path.Combine(_root, "gvfs", "mtp:host=Fake_Phone_123", "Internal shared storage")).FullName;
            Assert.True(Phones.IsPhonePath(storage));
            Assert.IsType<MountedPhoneProvider>(Phones.For(storage));
            string local = Path.Combine(_root, "song.mp3");
            File.WriteAllBytes(local, new byte[300_000]);
            var engine = new FileOperationService(null);

            FileOperationOutcome copy = await engine.TransferAsync(FileOperationKind.Copy, new[] { local }, storage, new FileOperationContext());
            Assert.True(copy.Result.IsFullSuccess, Describe(copy));
            Assert.Equal(300_000, new FileInfo(Path.Combine(storage, "song.mp3")).Length);
            Assert.Empty(Directory.GetFiles(storage, "*.partial"));
            Assert.Null(copy.Record);

            // A write that fails part-way leaves no partial file on the phone (MountedWriteStream deletes it).
            IPhoneProvider phone = Phones.For(storage);
            using (PhoneWriteStream stream = phone.CreateFile(storage, "half.bin", 10, default))
            {
                stream.Write(new byte[4], 0, 4);
            }

            Assert.False(File.Exists(Path.Combine(storage, "half.bin")));

            string renamed = phone.Rename(Path.Combine(storage, "song.mp3"), "tune.mp3");
            Assert.True(File.Exists(renamed));
            FileOperationOutcome delete = await engine.DeleteAsync(new[] { renamed }, permanent: false, new FileOperationContext());
            Assert.Equal(1, delete.Result.SuccessCount);
            Assert.False(File.Exists(renamed)); // deleted for good: a phone has no trash
            Assert.Null(delete.Record);
        }

        // ------------------------------------------------------------------ Windows interop layout

        /// <summary>
        /// The Windows Portable Devices interfaces must list their methods in the SDK's vtable order (a slot out of place
        /// calls the wrong method). The expected order is MinGW-w64's portabledeviceapi.h / portabledevicetypes.h.
        /// </summary>
        [AvaloniaFact]
        public void TheWindowsInteropInterfaces_FollowTheSdkVtableOrder()
        {
            var expected = new Dictionary<string, string[]>
            {
                ["IPortableDeviceManager"] = new[] { "GetDevices", "RefreshDeviceList", "GetDeviceFriendlyName", "GetDeviceDescription", "GetDeviceManufacturer", "GetDeviceProperty", "GetPrivateDevices" },
                ["IPortableDevice"] = new[] { "Open", "SendCommand", "Content", "Capabilities", "Cancel", "Close", "Advise", "Unadvise", "GetPnPDeviceID" },
                ["IPortableDeviceContent"] = new[] { "EnumObjects", "Properties", "Transfer", "CreateObjectWithPropertiesOnly", "CreateObjectWithPropertiesAndData", "Delete", "GetObjectIDsFromPersistentUniqueIDs", "Cancel", "Move", "Copy" },
                ["IPortableDeviceProperties"] = new[] { "GetSupportedProperties", "GetPropertyAttributes", "GetValues", "SetValues", "Delete", "Cancel" },
                ["IPortableDeviceResources"] = new[] { "GetSupportedResources", "GetResourceAttributes", "GetStream", "Delete", "Cancel", "CreateResource" },
                ["IEnumPortableDeviceObjectIDs"] = new[] { "Next", "Skip", "Reset", "Clone", "Cancel" },
                ["IPortableDeviceDataStream"] = new[] { "Read", "Write", "Seek", "SetSize", "CopyTo", "Commit", "Revert", "LockRegion", "UnlockRegion", "Stat", "Clone", "GetObjectID", "Cancel" },
                ["IPortableDeviceKeyCollection"] = new[] { "GetCount", "GetAt", "Add", "Clear", "RemoveAt" },
                ["IPortableDevicePropVariantCollection"] = new[] { "GetCount", "GetAt", "Add", "GetType", "ChangeType", "Clear", "RemoveAt" },
                ["IPortableDeviceValues"] = new[]
                {
                    "GetCount", "GetAt", "SetValue", "GetValue", "SetStringValue", "GetStringValue", "SetUnsignedIntegerValue", "GetUnsignedIntegerValue",
                    "SetSignedIntegerValue", "GetSignedIntegerValue", "SetUnsignedLargeIntegerValue", "GetUnsignedLargeIntegerValue",
                    "SetSignedLargeIntegerValue", "GetSignedLargeIntegerValue", "SetFloatValue", "GetFloatValue", "SetErrorValue", "GetErrorValue",
                    "SetKeyValue", "GetKeyValue", "SetBoolValue", "GetBoolValue", "SetIUnknownValue", "GetIUnknownValue", "SetGuidValue", "GetGuidValue",
                    "SetBufferValue", "GetBufferValue", "SetIPortableDeviceValuesValue", "GetIPortableDeviceValuesValue",
                    "SetIPortableDevicePropVariantCollectionValue", "GetIPortableDevicePropVariantCollectionValue",
                    "SetIPortableDeviceKeyCollectionValue", "GetIPortableDeviceKeyCollectionValue",
                    "SetIPortableDeviceValuesCollectionValue", "GetIPortableDeviceValuesCollectionValue",
                    "RemoveValue", "CopyValuesFromPropertyStore", "CopyValuesToPropertyStore", "Clear"
                }
            };

            Assembly app = typeof(PhonePaths).Assembly;
            foreach ((string name, string[] methods) in expected)
            {
                Type type = app.GetType("ByteExplorer.Core.Platform.Phones.Wpd." + name, throwOnError: true);
                string[] declared = type.GetMethods().OrderBy(m => m.MetadataToken).Select(m => m.Name).ToArray();
                Assert.Equal(methods, declared);
            }

            // PROPVARIANT: 16 bytes on 32-bit Windows, 24 on 64-bit.
            Type variant = app.GetType("ByteExplorer.Core.Platform.Phones.Wpd.PropVariant", throwOnError: true);
            Assert.Equal(IntPtr.Size == 8 ? 24 : 16, System.Runtime.InteropServices.Marshal.SizeOf(variant));
        }

        private string Describe(FileOperationOutcome outcome) =>
            $"ok={outcome.Result.SuccessCount} cancelled={outcome.Result.WasCancelled} failures: " +
            string.Join(" | ", outcome.Result.Failures.Select(f => f.SourcePath + ": " + f.Message)) + " phone: " + string.Join(", ", _phone.AllPaths());

        private sealed class FixedConflict : IConflictResolver
        {
            private readonly ConflictChoice _choice;

            public FixedConflict(ConflictChoice choice) => _choice = choice;

            public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken) =>
                Task.FromResult(new ConflictDecision(_choice, applyToAll: true));
        }

        private sealed class ScriptedErrors : IErrorResolver
        {
            private readonly ErrorChoice _choice;

            public ScriptedErrors(ErrorChoice choice) => _choice = choice;

            public int Asked { get; private set; }

            public Task<ErrorChoice> ResolveAsync(ErrorInfo error, CancellationToken cancellationToken)
            {
                Asked++;
                return Task.FromResult(_choice);
            }
        }
    }
}
