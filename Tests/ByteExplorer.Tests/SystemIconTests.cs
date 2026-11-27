// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Infrastructure.SystemIcons;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Windows icons (Folder options › "Show Windows icons"): how items share icons, the size buckets, the background
    /// lookups and the cache, and the file views drawing them. A fake source stands in for the Windows shell (its
    /// interop runs only on Windows; this tests everything around it).
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class SystemIconTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-icons-" + Guid.NewGuid().ToString("N"));

        public SystemIconTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            SystemIconCache.UseSourceForTests(null);
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void Items_ShareIcons_TheWayTheShellDoes()
        {
            string folder = Path.Combine(_root, "Work");
            Assert.Equal(".docx", SystemIconCache.KeyFor(Path.Combine(folder, "a.docx"), "a.docx", false, false));
            Assert.Equal(".docx", SystemIconCache.KeyFor(Path.Combine(folder, "B.DOCX"), "B.DOCX", false, false));
            Assert.Equal("file:", SystemIconCache.KeyFor(Path.Combine(folder, "Makefile"), "Makefile", false, false));
            // Folders keep ByteExplorer's own icon (no Windows icon), however special.
            Assert.Null(SystemIconCache.KeyFor(folder, "Work", true, false));
            Assert.Null(SystemIconCache.KeyFor(folder, "Work", true, isSystem: true));

            // Their own icon: programs, shortcuts, icon files, drives.
            string exe = Path.Combine(folder, "setup.exe");
            Assert.Equal("path:" + exe, SystemIconCache.KeyFor(exe, "setup.exe", false, false));
            string lnk = Path.Combine(folder, "Game.lnk");
            Assert.Equal("path:" + lnk, SystemIconCache.KeyFor(lnk, "Game.lnk", false, false));
            string drive = Path.GetPathRoot(_root);
            Assert.StartsWith("path:", SystemIconCache.KeyFor(drive, drive, true, false));

            // Where the shell can't see the item, only its type counts.
            string zip = Path.Combine(_root, "tools.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("tool.exe");
            }

            Assert.Equal(".exe", SystemIconCache.KeyFor(Path.Combine(zip, "tool.exe"), "tool.exe", false, false));
        }

        [Theory]
        [InlineData(12, 16)]
        [InlineData(16, 16)]
        [InlineData(20, 32)]
        [InlineData(40, 48)]
        [InlineData(64, 64)]
        [InlineData(80, 96)]
        [InlineData(200, 256)]
        [InlineData(512, 256)]
        public void DrawnSizes_UseTheNextBucketUp(double pixels, int bucket)
        {
            Assert.Equal(bucket, SystemIconCache.BucketFor(pixels));
        }

        [AvaloniaFact]
        public async Task Icons_AreLookedUpOncePerType_InTheBackground_AndViewsAreTold()
        {
            var source = new FakeSource();
            SystemIconCache.UseSourceForTests(source);
            var observer = new Observer();

            Assert.Null(SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 16, observer));
            await WaitUntil(() => observer.Calls > 0);
            Bitmap first = SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 16, observer);
            Assert.NotNull(first);
            Assert.Equal(new PixelSize(16, 16), first.PixelSize);

            // Every other .txt file: the same bitmap, no new lookup.
            for (int i = 2; i < 200; i++)
            {
                Assert.Same(first, SystemIconCache.TryGet(Path.Combine(_root, i + ".txt"), i + ".txt", false, false, 16, observer));
            }

            Assert.Equal(1, SystemIconCache.LookupCount);

            // A bigger size of a known type: rendered (from the 256 px image, scaled to the bucket), not looked up again.
            Assert.Null(SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 90, observer));
            await WaitUntil(() => SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 90, null) != null);
            Assert.Equal(new PixelSize(96, 96), SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 90, null).PixelSize);
            Assert.Equal(1, SystemIconCache.LookupCount);
            Assert.Contains(256, source.RenderedSizes);

            // No icon from the shell: asked once, then the glyph stays.
            source.Unknown.Add(".weird");
            Assert.Null(SystemIconCache.TryGet(Path.Combine(_root, "a.weird"), "a.weird", false, false, 16, observer));
            await WaitUntil(() => SystemIconCache.LookupCount == 2);
            await Pump(3);
            Assert.Null(SystemIconCache.TryGet(Path.Combine(_root, "b.weird"), "b.weird", false, false, 16, observer));
            await Pump(3);
            Assert.Equal(2, SystemIconCache.LookupCount);
        }

        [AvaloniaFact]
        public void TheSetting_TurnsTheIconsOff()
        {
            SystemIconCache.UseSourceForTests(new FakeSource());
            try
            {
                SystemIconCache.AttachSetting(() => false);
                Assert.False(SystemIconCache.IsActive);
                Assert.Null(SystemIconCache.TryGet(Path.Combine(_root, "1.txt"), "1.txt", false, false, 16, null));
                SystemIconCache.AttachSetting(() => true);
                Assert.True(SystemIconCache.IsActive);
            }
            finally
            {
                SystemIconCache.AttachSetting(null);
            }
        }

        [AvaloniaFact]
        public async Task TheFileViews_DrawTheWindowsIcon()
        {
            File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
            Directory.CreateDirectory(Path.Combine(_root, "Projects"));
            SystemIconCache.UseSourceForTests(new FakeSource());
            var window = new ModernMainWindow { Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                pane.SelectedPreset = Infrastructure.FileListViewPreset.LargeIcons;
                await pane.LoadFolderAsync(_root);
                await WaitUntil(() => pane.Items?.Count == 2);
                Assert.True(pane.ShowSystemIcons);

                FileItemView view = null;
                await WaitUntil(() => (view = VisibleView(window, "notes.txt")) != null);
                FileItemView folderView = VisibleView(window, "Projects");
                Assert.NotNull(folderView);
                await WaitUntil(() => SystemIconCache.LookupCount >= 1);
                await Pump(10);

                // The fake icon is pure red: the icon's middle shows it once drawn.
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using WriteableBitmap frame = window.CaptureRenderedFrame() as WriteableBitmap ?? ToWriteable(window.CaptureRenderedFrame());
                string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    frame.Save(Path.Combine(directory, "windows-icons.png"));
                }
                Point topLeft = view.TranslatePoint(new Point(0, 0), window) ?? default;
                // The folder keeps ByteExplorer's own folder icon, never the Windows one.
                Point folderTopLeft = folderView.TranslatePoint(new Point(0, 0), window) ?? default;
                Assert.Equal(0, CountRed(frame, new PixelRect((int)folderTopLeft.X, (int)folderTopLeft.Y, (int)folderView.Bounds.Width, (int)folderView.Bounds.Height)));

                int red = CountRed(frame, new PixelRect((int)topLeft.X, (int)topLeft.Y, (int)view.Bounds.Width, (int)view.Bounds.Height));
                Assert.True(red > 500, $"the item doesn't show the Windows icon's red ({red} red pixels at {topLeft}, {view.Bounds.Size}; whole frame {CountRed(frame, new PixelRect(frame.PixelSize))})");
            }
            finally
            {
                window.Close();
            }
        }

        private static FileItemView VisibleView(Avalonia.Controls.Window window, string name) =>
            window.GetVisualDescendants().OfType<FileItemView>()
                .FirstOrDefault(v => v.IsEffectivelyVisible && v.Bounds.Width > 0 && (v.DataContext as FileListItemViewModel)?.Name == name);

        private static WriteableBitmap ToWriteable(Bitmap bitmap)
        {
            var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (ILockedFramebuffer buffer = copy.Lock())
            {
                bitmap.CopyPixels(buffer, Avalonia.Platform.AlphaFormat.Premul);
            }

            return copy;
        }

        private static unsafe int CountRed(WriteableBitmap frame, PixelRect area)
        {
            using ILockedFramebuffer buffer = frame.Lock();
            int count = 0;
            for (int y = Math.Max(0, area.Y); y < Math.Min(buffer.Size.Height, area.Bottom); y++)
            {
                for (int x = Math.Max(0, area.X); x < Math.Min(buffer.Size.Width, area.Right); x++)
                {
                    byte* p = (byte*)buffer.Address + y * buffer.RowBytes + x * 4;
                    // Red in BGRA or RGBA (the headless frame's channel order isn't promised).
                    if (p[1] < 60 && ((p[2] > 200 && p[0] < 60) || (p[0] > 200 && p[2] < 60)))
                    {
                        count++;
                    }
                }
            }

            return count;
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

        private sealed class Observer : ISystemIconObserver
        {
            public int Calls;

            public void OnSystemIconReady() => Calls++;
        }

        /// <summary>One slot per extension; every icon is solid red at the size asked (16, 32, 48 or 256).</summary>
        private sealed class FakeSource : ISystemIconSource
        {
            private readonly Dictionary<string, int> _slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> Unknown { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public List<int> RenderedSizes { get; } = new List<int>();

            public bool IsAvailable => true;

            public int IndexOf(string path, bool isDirectory, bool byPath)
            {
                string kind = isDirectory ? "<dir>" : Path.GetExtension(path);
                if (Unknown.Contains(kind))
                {
                    return -1;
                }

                lock (_slots)
                {
                    if (!_slots.TryGetValue(kind, out int slot))
                    {
                        _slots[kind] = slot = _slots.Count;
                    }

                    return slot;
                }
            }

            public IconPixels Render(int index, int size)
            {
                int native = size <= 16 ? 16 : size <= 32 ? 32 : size <= 48 ? 48 : 256;
                lock (RenderedSizes)
                {
                    RenderedSizes.Add(native);
                }

                byte[] data = new byte[native * native * 4];
                for (int i = 0; i < data.Length; i += 4)
                {
                    data[i + 2] = 255; // red
                    data[i + 3] = 255;
                }

                return new IconPixels(native, native, data);
            }
        }
    }
}
