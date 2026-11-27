// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using Xunit;
using Xunit.Abstractions;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Timing probe for ordering 100,000 entries (natural names, the display sorter in each order). Runs only when
    /// BYTEEXPLORER_PERF=1, best in Release: dotnet test -c Release --filter SortPerformanceProbe --logger "console;verbosity=detailed".
    /// </summary>
    public sealed class SortPerformanceProbe
    {
        private readonly ITestOutputHelper _o;
        public SortPerformanceProbe(ITestOutputHelper o) => _o = o;

        [Fact]
        public void Probe()
        {
            if (Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF") != "1") return;
            var rnd = new Random(1);
            string[] ext = { ".txt", ".jpg", ".pdf", ".mp3" };
            var entries = Enumerable.Range(0, 100_000).Select(i => new DirectoryIndexEntry($"item {i:D6}{ext[i % 4]}", i % 20 == 0, rnd.Next(), DateTime.UtcNow.AddSeconds(-rnd.Next()), FileAttributes.Normal)).OrderBy(_ => rnd.Next()).ToArray();
            string[] names = entries.Select(e => e.Name).ToArray();
            for (int rep = 0; rep < 5; rep++)
            {
                var sw = Stopwatch.StartNew();
                var a = (string[])names.Clone(); Array.Sort(a, StringComparer.OrdinalIgnoreCase); long oic = sw.ElapsedMilliseconds; sw.Restart();
                a = (string[])names.Clone(); Array.Sort(a, (x, y) => NaturalStringComparer.CompareManaged(x, y)); long nat = sw.ElapsedMilliseconds; sw.Restart();
                var e1 = (DirectoryIndexEntry[])entries.Clone(); Array.Sort(e1, DirectoryEntrySorter.ComparerFor(EntryOrder.Name, true)); long serialEntries = sw.ElapsedMilliseconds; sw.Restart();
                var s = DirectoryEntrySorter.Sort(entries, EntryOrder.Name, true); long full = sw.ElapsedMilliseconds; sw.Restart();
                var s2 = DirectoryEntrySorter.Sort(entries, EntryOrder.Size, true); long size = sw.ElapsedMilliseconds; sw.Restart();
                var s3 = DirectoryEntrySorter.Sort(entries, EntryOrder.Type, true); long type = sw.ElapsedMilliseconds;
                long c = 0; sw.Restart(); for (int i = 0; i < 1_000_000; i++) c += NaturalStringComparer.CompareManaged(names[i % 1000], names[(i * 7) % 1000]); long per = sw.ElapsedMilliseconds;
                _o.WriteLine($"ordinal-ignore-case {oic} ms | natural {nat} ms | entries, one thread {serialEntries} ms | sorter: name {full} ms, size {size} ms, type {type} ms | 1M natural compares {per} ms");
            }
        }
    }
}
