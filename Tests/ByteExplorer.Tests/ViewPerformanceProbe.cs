// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Timing probe for every view on a large folder (BYTEEXPLORER_PERF_ITEMS items, default 100,000). Runs only when
    /// BYTEEXPLORER_PERF=1; prints a table (dotnet test --logger "console;verbosity=detailed"). Headless rendering with
    /// Skia: absolute numbers differ from a GPU desktop, but relative costs and regressions show.
    /// </summary>
    public sealed class ViewPerformanceProbe
    {
        private readonly ITestOutputHelper _output;

        public ViewPerformanceProbe(ITestOutputHelper output) => _output = output;

        [AvaloniaFact]
        public async Task Probe()
        {
            if (Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF") != "1")
            {
                return;
            }

            int count = int.TryParse(Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF_ITEMS"), out int n) ? n : 100_000;
            string root = Path.Combine(Path.GetTempPath(), $"be-perf-{count}");
            if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Count() < count)
            {
                Directory.CreateDirectory(root);
                for (int i = 0; i < count / 20; i++)
                {
                    Directory.CreateDirectory(Path.Combine(root, $"Folder {i:D6}"));
                }

                string[] extensions = { ".txt", ".jpg", ".pdf", ".mp3", ".mp4", ".cs", ".zip", ".png", ".docx", ".xlsx" };
                for (int i = 0; i < count - count / 20; i++)
                {
                    File.WriteAllBytes(Path.Combine(root, $"item {i:D6}{extensions[i % extensions.Length]}"), Array.Empty<byte>());
                }
            }

            var window = new ModernMainWindow { Width = 1600, Height = 1000 };
            window.Show();
            await Pump(30);
            var vm = ((MainViewModel)window.DataContext).FileListViewModel;
            var report = new StringBuilder();
            report.AppendLine($"{"view",-16}{"load",8}{"page(avg)",11}{"page(max)",11}{"small(avg)",12}{"zoom",8}{"selAll",9}{"realized",10}{"idleFrame",11}");

            foreach (FileListViewPreset preset in Enum.GetValues<FileListViewPreset>())
            {
                vm.SelectedPreset = preset;
                await Pump(5);
                var sw = Stopwatch.StartNew();
                await vm.LoadFolderAsync(root, bypassCache: true);
                Settle(window);
                long load = sw.ElapsedMilliseconds;

                ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .Where(s => s.IsEffectivelyVisible && s.Extent.Height > s.Viewport.Height + 1)
                    .OrderByDescending(s => s.Extent.Height).FirstOrDefault();

                double idle = 0;
                for (int i = 0; i < 10; i++)
                {
                    sw.Restart();
                    window.InvalidateVisual();
                    Settle(window);
                    idle += sw.Elapsed.TotalMilliseconds / 10;
                }

                double pageAvg = 0, pageMax = 0, smallAvg = 0;
                if (scroller != null)
                {
                    const int pages = 30;
                    double pageLayoutAvg = 0, pageLayoutMax = 0;
                    for (int i = 1; i <= pages; i++)
                    {
                        sw.Restart();
                        scroller.Offset = new Vector(scroller.Offset.X, Math.Min(scroller.Extent.Height, scroller.Offset.Y + scroller.Viewport.Height));
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        double layout = sw.Elapsed.TotalMilliseconds;
                        pageLayoutAvg += layout / pages;
                        pageLayoutMax = Math.Max(pageLayoutMax, layout);
                        Settle(window);
                        double t = sw.Elapsed.TotalMilliseconds;
                        pageAvg += t / pages;
                        pageMax = Math.Max(pageMax, t);
                    }

                    System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "be-perf-steps.txt"), $"{preset}: page layout avg {pageLayoutAvg:F2} max {pageLayoutMax:F2} ms{Environment.NewLine}");

                    // Jump into the middle, then 60 wheel-sized steps.
                    scroller.Offset = new Vector(scroller.Offset.X, scroller.Extent.Height / 2);
                    Settle(window);
                    double layoutAvg = 0, renderAvg = 0;
                    for (int i = 0; i < 60; i++)
                    {
                        sw.Restart();
                        scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y + 50);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        double layout = sw.Elapsed.TotalMilliseconds;
                        window.CaptureRenderedFrame();
                        Dispatcher.UIThread.RunJobs();
                        double total = sw.Elapsed.TotalMilliseconds;
                        smallAvg += total / 60;
                        layoutAvg += layout / 60;
                        renderAvg += (total - layout) / 60;
                    }

                    _output.WriteLine($"{preset}: small step layout {layoutAvg:F1} ms, render {renderAvg:F1} ms");
                    System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "be-perf-steps.txt"), $"{preset}: small step layout {layoutAvg:F2} ms, render {renderAvg:F2} ms{Environment.NewLine}");
                }

                sw.Restart();
                vm.IconSize = vm.IconSize + 32;
                Settle(window);
                vm.IconSize = vm.IconSize - 32;
                Settle(window);
                double zoom = sw.Elapsed.TotalMilliseconds / 2;

                sw.Restart();
                if (vm.SelectAllCommand.CanExecute(null))
                {
                    vm.SelectAllCommand.Execute(null);
                }

                Settle(window);
                double selectAll = sw.Elapsed.TotalMilliseconds;
                int realized = window.GetVisualDescendants().OfType<ListBoxItem>().Count(c => c.IsEffectivelyVisible);
                report.AppendLine($"{preset,-16}{load,8}{pageAvg,11:F1}{pageMax,11:F1}{smallAvg,12:F1}{zoom,8:F1}{selectAll,9:F0}{realized,10}{idle,11:F1}");
            }

            window.Close();
            _output.WriteLine(report.ToString());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "be-perf-report.txt"), report.ToString());
        }

        private static void Settle(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
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
