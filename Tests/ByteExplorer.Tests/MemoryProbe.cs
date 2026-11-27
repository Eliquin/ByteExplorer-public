// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Memory probe: a scripted session (a 100,000-item folder in four views, a folder of photos with thumbnails, sixty
    /// folders, Home, closing the window) reporting the managed heap, the GC's committed memory, the resident set and
    /// GC pauses at each step. Runs only when BYTEEXPLORER_PERF=1, best in Release:
    /// dotnet test -c Release --filter MemoryProbe --logger "console;verbosity=detailed".
    /// With BYTEEXPLORER_MEMDIR set it also writes the process id and, at each step, a "checkpoint" file, then waits
    /// (up to a minute) for a "done-&lt;step&gt;" file there: time for an outside tool (dotnet-gcdump, createdump) to take
    /// a dump of that moment.
    /// </summary>
    public sealed class MemoryProbe
    {
        private readonly ITestOutputHelper _o;
        public MemoryProbe(ITestOutputHelper o) => _o = o;
        private static readonly string Dir = Environment.GetEnvironmentVariable("BYTEEXPLORER_MEMDIR");

        [AvaloniaFact]
        public async Task Memory()
        {
            if (Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF") != "1")
            {
                return;
            }

            if (!string.IsNullOrEmpty(Dir))
            {
                File.WriteAllText(Path.Combine(Dir, "pid"), Environment.ProcessId.ToString());
            }

            string big = Path.Combine(Path.GetTempPath(), "be-perf-100000");
            string images = Path.Combine(Path.GetTempPath(), "be-mem-images");
            if (!Directory.Exists(images))
            {
                Directory.CreateDirectory(images);
                using var bmp = new SKBitmap(1600, 1200);
                using (var canvas = new SKCanvas(bmp)) { canvas.Clear(SKColors.CornflowerBlue); canvas.DrawCircle(800, 600, 400, new SKPaint { Color = SKColors.Orange }); }
                using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 85);
                byte[] bytes = data.ToArray();
                for (int i = 0; i < 400; i++) File.WriteAllBytes(Path.Combine(images, $"photo {i:D4}.jpg"), bytes);
            }

            var window = new ModernMainWindow { Width = 1600, Height = 1000 };
            window.Show();
            await Pump(50);
            var vm = window.ViewModel.FileListViewModel;
            await Checkpoint("1-startup");

            foreach (var preset in new[] { FileListViewPreset.Details, FileListViewPreset.LargeIcons, FileListViewPreset.Tiles, FileListViewPreset.List })
            {
                vm.SelectedPreset = preset;
                await vm.LoadFolderAsync(big);
                await Pump(20);
                await ScrollAll(window);
            }

            await Checkpoint("2-big-folder-all-views");

            vm.SelectedPreset = FileListViewPreset.ExtraLargeIcons;
            await vm.LoadFolderAsync(images);
            await Pump(20);
            await ScrollAll(window);
            await Pump(100);
            await Checkpoint("3-thumbnails");

            string[] folders = Directory.GetDirectories("/usr/share").Take(60).ToArray();
            vm.SelectedPreset = FileListViewPreset.Details;
            foreach (string f in folders)
            {
                await vm.LoadFolderAsync(f);
                await Pump(2);
            }

            await vm.LoadHomeAsync();
            await Pump(20);
            await Checkpoint("4-many-folders-then-home");
            window.Close();
            await Pump(20);
            await Checkpoint("5-closed");
        }

        private async Task Checkpoint(string name)
        {
            string pauses = $"GC pauses so far {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms, {GC.CollectionCount(2)} full collections, longest recent {MaxPause()} ms";
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            GC.Collect();
            var info = GC.GetGCMemoryInfo();
            var p = Process.GetCurrentProcess();
            string status = File.ReadAllText("/proc/self/status");
            string anon = status.Split('\n').First(l => l.StartsWith("RssAnon")).Trim();
            string line = $"{name}: {pauses}; after a full collection: managed {GC.GetTotalMemory(false) / 1048576.0:F1} MB, committed {info.TotalCommittedBytes / 1048576.0:F1} MB, RSS {p.WorkingSet64 / 1048576.0:F1} MB, {anon}";
            _o.WriteLine(line);
            if (string.IsNullOrEmpty(Dir))
            {
                return;
            }

            File.AppendAllText(Path.Combine(Dir, "log"), line + "\n");
            File.WriteAllText(Path.Combine(Dir, "checkpoint"), name);
            string done = Path.Combine(Dir, "done-" + name);
            for (int i = 0; i < 600 && !File.Exists(done); i++)
            {
                await Task.Delay(100);
            }
        }

        private static string MaxPause()
        {
            double max = 0;
            foreach (GCKind kind in new[] { GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background })
            {
                foreach (TimeSpan pause in GC.GetGCMemoryInfo(kind).PauseDurations)
                {
                    max = Math.Max(max, pause.TotalMilliseconds);
                }
            }

            return max.ToString("F1");
        }

        private static async Task ScrollAll(Window window)
        {
            ScrollViewer s = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(x => x.IsEffectivelyVisible && x.Extent.Height > x.Viewport.Height + 1)
                .OrderByDescending(x => x.Extent.Height).FirstOrDefault();
            if (s == null)
            {
                return;
            }

            double step = s.Viewport.Height;
            for (double y = 0; y < Math.Min(s.Extent.Height, step * 150); y += step)
            {
                s.Offset = new Vector(0, y);
                await Pump(1);
            }
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
