// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ByteSearcher;
using Xunit;
using Xunit.Abstractions;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Timing probe of the search index on a synthetic index (BYTEEXPLORER_PERF_ITEMS entries, default 3,000,000): building
    /// the in-memory index, saving and loading it, and searches of every kind. Runs only when BYTEEXPLORER_PERF=1, best in
    /// Release: dotnet test -c Release --filter SearchPerformanceProbe --logger "console;verbosity=detailed".
    /// </summary>
    public sealed class SearchPerformanceProbe
    {
        private static readonly string[] Words =
        {
            "report", "invoice", "photo", "holiday", "project", "budget", "draft", "final", "meeting", "notes", "backup",
            "summary", "design", "contract", "image", "video", "music", "scan", "letter", "plan", "beach", "family", "build",
            "release", "config", "readme", "data", "export", "archive", "screenshot"
        };

        private static readonly string[] Extensions = { ".pdf", ".docx", ".jpg", ".png", ".mp4", ".mp3", ".txt", ".cs", ".zip", ".xlsx", "" };

        private readonly ITestOutputHelper _output;

        public SearchPerformanceProbe(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe()
        {
            if (Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF") != "1")
            {
                return;
            }

            int count = int.TryParse(Environment.GetEnvironmentVariable("BYTEEXPLORER_PERF_ITEMS"), out int n) ? n : 3_000_000;
            var random = new Random(42);
            long memoryBefore = GC.GetTotalMemory(true);
            var stopwatch = Stopwatch.StartNew();
            var builder = new IndexBuilder();
            var batch = new EntryBatch();
            batch.Add("/data", -1, EntryFlags.Directory, 0, 0, DateTime.UtcNow.Ticks);
            builder.Append(batch);
            batch.Clear();
            int folders = 1;
            var folderIds = new System.Collections.Generic.List<int> { 0 };
            while (builder.Count < count)
            {
                int parent = folderIds[random.Next(folderIds.Count)];
                for (int i = 0; i < EntryBatch.MaxCount && builder.Count + batch.Count < count; i++)
                {
                    bool folder = random.Next(12) == 0;
                    string name = Words[random.Next(Words.Length)] + (random.Next(3) == 0 ? " " : "_") + Words[random.Next(Words.Length)] + "-" +
                                  random.Next(100_000) + (folder ? string.Empty : Extensions[random.Next(Extensions.Length)]);
                    if (random.Next(4) == 0)
                    {
                        name = char.ToUpperInvariant(name[0]) + name.Substring(1);
                    }

                    batch.Add(name, parent, folder ? EntryFlags.Directory : EntryFlags.None, (byte)random.Next(1, 12), folder ? 0 : random.Next(1, 50_000_000),
                        DateTime.UtcNow.AddDays(-random.Next(2000)).Ticks);
                }

                int first = builder.Append(batch);
                for (int i = 0; i < batch.Count; i++)
                {
                    if ((batch.Flags[i] & (byte)EntryFlags.Directory) != 0)
                    {
                        folderIds.Add(first + i);
                        folders++;
                    }
                }

                batch.Clear();
            }

            IndexData data = builder.Finish();
            long buildMs = stopwatch.ElapsedMilliseconds;
            folderIds = null;
            long memory = GC.GetTotalMemory(true) - memoryBefore;
            _output.WriteLine($"{count:N0} entries ({folders:N0} folders): built in memory in {buildMs} ms, {memory / 1048576.0:F0} MB ({(double)memory / count:F0} bytes per entry)");

            string file = Path.Combine(Path.GetTempPath(), "be-search-probe.bxi");
            stopwatch.Restart();
            IndexStorage.Save(data, file);
            long saveMs = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            IndexData loaded = IndexStorage.Load(file);
            _output.WriteLine($"saved in {saveMs} ms ({new FileInfo(file).Length / 1048576.0:F0} MB), loaded and validated in {stopwatch.ElapsedMilliseconds} ms");
            File.Delete(file);

            foreach (string query in new[] { "report", "r", "invoice 2024", "\"budget plan\"", "*.pdf", "ext:jpg size:>10mb", "kind:video modified:year", "zzzz", "report -draft -final", "photo/beach" })
            {
                SearchEngine.Run(loaded, SearchQuery.Parse(query), new SearchOptions(), CancellationToken.None); // warm up
                long best = long.MaxValue;
                SearchResults results = null;
                for (int i = 0; i < 5; i++)
                {
                    stopwatch.Restart();
                    results = SearchEngine.Run(loaded, SearchQuery.Parse(query), new SearchOptions(), CancellationToken.None);
                    best = Math.Min(best, stopwatch.ElapsedTicks);
                }

                long scanOnly = long.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    stopwatch.Restart();
                    SearchEngine.Run(loaded, SearchQuery.Parse(query), new SearchOptions { MaxResults = 1 }, CancellationToken.None);
                    scanOnly = Math.Min(scanOnly, stopwatch.ElapsedTicks);
                }

                stopwatch.Restart();
                SearchResults byName = results.Sorted(SearchSort.Name, false);
                long sortMs = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();
                for (int i = 0; i < Math.Min(100, results.Count); i++)
                {
                    results.GetHit(i);
                }

                _output.WriteLine($"{query,-28} {results.TotalMatches,10:N0} matches  search+rank {best * 1000.0 / Stopwatch.Frequency,8:F1} ms (scan {scanOnly * 1000.0 / Stopwatch.Frequency,6:F1})   re-sort by name {sortMs,5} ms   first 100 rows {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }
}
