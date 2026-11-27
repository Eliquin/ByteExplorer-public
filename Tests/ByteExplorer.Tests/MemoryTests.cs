// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>What the app keeps in memory: thumbnail waiters held weakly, hidden lists without container pools, and
    /// folder listings dropped once they expire.</summary>
    public sealed class MemoryTests
    {
        private sealed class Observer : IThumbnailObserver
        {
            public void OnThumbnailReady(ThumbnailCache.Key key)
            {
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference WaitForAThumbnail(ThumbnailCache.Key key)
        {
            var observer = new Observer();
            ThumbnailCache.Acquire(key, observer);
            return new WeakReference(observer);
        }

        // The cache's UI-thread state belongs to the headless UI thread (see AsyncSafetyTests).
        [AvaloniaFact]
        public void ThumbnailWaiter_IsNotKeptAliveByTheCache()
        {
            ThumbnailCache.Key key = ThumbnailCache.KeyFor("/nonexistent/" + Guid.NewGuid().ToString("N") + ".jpg", DateTime.UtcNow, 128);
            WeakReference observer = WaitForAThumbnail(key); // never released, like a view in a window that was closed
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(observer.IsAlive);
            ThumbnailCache.Release(key);
        }

        [Fact]
        public void IndexCache_DropsExpiredListings()
        {
            var cache = new DirectoryIndexCache();
            cache.Set("/a", DirectoryIndex.Empty("/a"), capacity: 8, ttlMs: 20);
            System.Threading.Thread.Sleep(60);
            cache.Set("/b", DirectoryIndex.Empty("/b"), capacity: 8, ttlMs: 20);
            Assert.False(cache.TryGet("/a", 60_000, out _)); // gone, even when asked with a long time to live
            Assert.True(cache.TryGet("/b", 60_000, out _));
        }

        [AvaloniaFact]
        public async Task HiddenList_ReleasesItsContainerPool()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-pool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            for (int i = 0; i < 300; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"file {i:D3}.txt"), "x");
            }

            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel vm = window.ViewModel.FileListViewModel;
                vm.SelectedPreset = FileListViewPreset.LargeIcons;
                await vm.LoadFolderAsync(folder);
                await Pump(40); // realize, then warm the pool on idle time

                VirtualizingWrapPanel panel = window.GetVisualDescendants().OfType<VirtualizingWrapPanel>()
                    .First(p => p.IsEffectivelyVisible && p.Children.Count > 0);
                Assert.True(panel.Children.Count > 0);

                vm.SelectedPreset = FileListViewPreset.Details; // the icon list is hidden and emptied
                await Pump(10);
                Assert.Equal(0, panel.PooledContainerCount);
                Assert.Empty(panel.Children);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
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
