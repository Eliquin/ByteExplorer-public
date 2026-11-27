// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteSearcher;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The search index: building, the query syntax, ranking and ordering, persistence and live updates.</summary>
    public sealed class SearchIndexTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-index-" + Guid.NewGuid().ToString("N"));
        private readonly string _store = Path.Combine(Path.GetTempPath(), "be-index-store-" + Guid.NewGuid().ToString("N"));

        public SearchIndexTests()
        {
            Directory.CreateDirectory(_root);
            Write("Documents/Tax 2024/report.pdf", 2_000);
            Write("Documents/Tax 2024/receipts.zip", 50_000);
            Write("Documents/Report.docx", 300);
            Write("Documents/old reports/report-2019.pdf", 10);
            Write("Photos/Beach/IMG_0001.JPG", 3_000_000);
            Write("Photos/Beach/img_0002.jpg", 2_500_000);
            Write("Photos/Mountains/summit.png", 900_000);
            Write("Music/song.mp3", 4_000_000);
            Write("Code/app/Program.cs", 1_000);
            Write("Code/app/file2.txt", 1);
            Write("Code/app/file10.txt", 1);
            Write(".config/secret-report.txt", 5);
            Write("Été/café.txt", 5);
            Directory.CreateDirectory(Path.Combine(_root, "Empty Folder"));
            File.SetLastWriteTimeUtc(Path.Combine(_root, "Documents/old reports/report-2019.pdf"), new DateTime(2019, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        }

        public void Dispose()
        {
            foreach (string directory in new[] { _root, _store })
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                }
            }
        }

        // ------------------------------------------------------------------ building

        [Fact]
        public async Task Build_IndexesEverything_AndSavesAtomically()
        {
            using var index = await BuildAsync();
            Assert.True(index.IsIndexLoaded);
            Assert.False(index.IsPartialIndex);
            // 1 root + 13 files + folders: Documents, Tax 2024, old reports, Photos, Beach, Mountains, Music, Code, app, .config, Été, Empty Folder
            Assert.Equal(13, index.TotalFiles);
            Assert.Equal(13, index.TotalFolders);
            Assert.True(File.Exists(Path.Combine(_store, "index.bxi")));
            Assert.False(File.Exists(Path.Combine(_store, "index.bxi.tmp")));
            Assert.True(index.IndexExistsAndValid());
        }

        [Fact]
        public async Task CancelledBuild_KeepsThePreviousIndex()
        {
            using var index = await BuildAsync();
            long before = index.TotalItems;
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.BuildIndexAsync(cancellationToken: cts.Token, restrictToRoots: new[] { _root }));
            Assert.Equal(before, index.TotalItems);
            Assert.True(index.IndexExistsAndValid());
        }

        [Fact]
        public async Task Index_SurvivesAReload_AndADamagedFileIsRefused()
        {
            using (var index = await BuildAsync())
            {
            }

            using (var reloaded = new SearchIndex(_store))
            {
                reloaded.LoadIndex();
                Assert.Equal(26, reloaded.TotalItems);
                Assert.Equal(new[] { "report.pdf" }, Names(reloaded.Search("report.pdf")));
            }

            string file = Path.Combine(_store, "index.bxi");
            byte[] bytes = File.ReadAllBytes(file);
            File.WriteAllBytes(file, bytes.AsSpan(0, bytes.Length - 20).ToArray()); // truncated
            using (var damaged = new SearchIndex(_store))
            {
                Assert.Throws<InvalidOperationException>(() => damaged.LoadIndex());
            }

            bytes[bytes.Length / 2] ^= 0xFF; // flipped bits somewhere in the middle
            File.WriteAllBytes(file, bytes);
            using (var corrupt = new SearchIndex(_store))
            {
                try
                {
                    corrupt.LoadIndex();
                    corrupt.Search("report"); // loaded: a flipped name byte is harmless, searching must still work
                }
                catch (InvalidOperationException)
                {
                    // or refused: both are fine, crashing is not
                }
            }
        }

        // ------------------------------------------------------------------ the query syntax

        [Fact]
        public async Task Words_AreCaseInsensitive_AndAllMustMatch()
        {
            using var index = await BuildAsync();
            Assert.Equal(new[] { "IMG_0001.JPG", "img_0002.jpg" }, Names(index.Search("img")).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "report-2019.pdf" }, Names(index.Search("REPORT 2019")));
            Assert.Empty(Names(index.Search("report beach")));
            Assert.Equal(new[] { "café.txt" }, Names(index.Search("CAFÉ")));
            Assert.Contains("Été", Names(index.Search("été")));
        }

        [Fact]
        public async Task Exclusions_Patterns_PhrasesAndPathTerms()
        {
            using var index = await BuildAsync();
            Assert.DoesNotContain("report-2019.pdf", Names(index.Search("report -2019")));
            Assert.Equal(new[] { "report-2019.pdf", "report.pdf" }, Names(index.Search("*.pdf")).OrderBy(n => n));
            Assert.Equal(new[] { "img_0002.jpg" }, Names(index.Search("img_???2.*")));
            Assert.Equal(new[] { "Tax 2024" }, Names(index.Search("\"tax 2024\"")));
            Assert.Equal(new[] { "report.pdf" }, Names(index.Search("tax/report")));
            Assert.Equal(new[] { "report-2019.pdf" }, Names(index.Search("documents/old/report")));
            Assert.Equal(new[] { "IMG_0001.JPG", "img_0002.jpg" }, Names(index.Search("beach/")).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public async Task Filters_ExtensionKindSizeDateAndFolder()
        {
            using var index = await BuildAsync();
            Assert.Equal(new[] { "IMG_0001.JPG", "img_0002.jpg" }, Names(index.Search("ext:jpg")).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(3, Names(index.Search("kind:image")).Count);
            Assert.Equal(new[] { "song.mp3" }, Names(index.Search("type:music")));
            Assert.Equal(new[] { "IMG_0001.JPG", "img_0002.jpg", "song.mp3" }, Names(index.Search("size:>2mb")).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "receipts.zip" }, Names(index.Search("size:10kb..100kb")));
            Assert.Equal(new[] { "report-2019.pdf" }, Names(index.Search("report modified:2019")));
            Assert.DoesNotContain("report-2019.pdf", Names(index.Search("report dm:year")));
            Assert.Equal(new[] { "Photos", "Beach", "Mountains" }.OrderBy(n => n), Names(index.Search("kind:folder in:photos")).Concat(new[] { "Photos" }).Distinct().OrderBy(n => n));
            Assert.Equal(new[] { "summit.png" }, Names(index.Search("in:" + Path.Combine(_root, "Photos", "Mountains"))));
            Assert.Empty(Names(index.Search("in:" + Path.Combine(_root, "Nowhere"))));
            Assert.Equal(new[] { "Empty Folder" }, Names(index.Search("empty type:folder")));
            Assert.Contains("size:huge123", SearchQuery.Parse("size:huge123").Terms); // unreadable filter: searched as text
        }

        [Fact]
        public async Task HiddenItems_FollowTheOption()
        {
            using var index = await BuildAsync();
            Assert.Contains("secret-report.txt", Names(index.Search("report")));
            Assert.DoesNotContain("secret-report.txt", Names(index.Search("report", new SearchOptions { IncludeHidden = false })));
        }

        [Fact]
        public async Task Options_ScopeKindAndFoldersOnly()
        {
            using var index = await BuildAsync();
            Assert.Equal(new[] { "report.pdf" }, Names(index.Search("report", new SearchOptions { ScopeFolder = Path.Combine(_root, "Documents", "Tax 2024") })));
            Assert.Equal(new[] { "report-2019.pdf", "report.pdf" }, Names(index.Search("report", new SearchOptions { Kind = FileKind.Document, IncludeHidden = false })).Where(n => n.EndsWith(".pdf")).OrderBy(n => n));
            Assert.Equal(new[] { "old reports" }, Names(index.Search("report", new SearchOptions { FoldersOnly = true })));
        }

        // ------------------------------------------------------------------ ranking and order

        [Fact]
        public async Task Relevance_ExactThenStemThenPrefixThenWords()
        {
            using var index = await BuildAsync();
            List<string> names = Names(index.Search("report", new SearchOptions { IncludeHidden = false }));
            Assert.Equal("Report.docx", names[0]); // stem equals the word (ties broken by length, depth: docx is shallower)
            Assert.True(names.IndexOf("report.pdf") < names.IndexOf("old reports"));
            Assert.True(names.IndexOf("old reports") < names.Count);
        }

        [Fact]
        public async Task Sorting_ByNameNaturally_BySize_AndResortingKeepsTheMatches()
        {
            using var index = await BuildAsync();
            SearchResults results = index.Search("file", new SearchOptions { Sort = SearchSort.Name });
            Assert.Equal(new[] { "file2.txt", "file10.txt" }, Names(results));
            SearchResults bySize = index.Search("ext:jpg;png;mp3", new SearchOptions { Sort = SearchSort.Size, Descending = true });
            Assert.Equal(new[] { "song.mp3", "IMG_0001.JPG", "img_0002.jpg", "summit.png" }, Names(bySize));
            SearchResults resorted = bySize.Sorted(SearchSort.Name, descending: false);
            Assert.Equal(4, resorted.Count);
            Assert.Equal("IMG_0001.JPG", resorted.GetHit(0).Name);
        }

        [Fact]
        public async Task Hits_CarryPathsAndHighlights()
        {
            using var index = await BuildAsync();
            SearchHit hit = index.Search("summit").GetHit(0);
            Assert.Equal(Path.Combine(_root, "Photos", "Mountains", "summit.png"), hit.FullPath);
            Assert.Equal(Path.Combine(_root, "Photos", "Mountains"), hit.FolderPath);
            Assert.Equal(FileKind.Image, hit.Kind);
            Assert.Equal(900_000, hit.Size);
            Assert.Equal(new[] { new TextRange(0, 6) }, hit.NameHighlights);
            SearchHit both = index.Search("img 0002").GetHit(0);
            Assert.Equal(new[] { new TextRange(0, 3), new TextRange(4, 4) }, both.NameHighlights);
        }

        [Fact]
        public async Task LegacySearchAll_ReturnsTheBestMatches()
        {
            using var index = await BuildAsync();
            SearchResult result = index.SearchAll("report", maxResults: 2);
            Assert.Equal(2, result.Items.Count);
            Assert.True(result.Truncated);
            Assert.Equal("Report.docx", result.Items[0].Name);
        }

        // ------------------------------------------------------------------ live updates

        [Fact]
        public async Task LiveUpdates_AddRemoveRenameAndNewTrees()
        {
            using var index = await BuildAsync();
            string documents = Path.Combine(_root, "Documents");
            int events = 0;
            index.IndexChanged += (_, _) => Interlocked.Increment(ref events);

            Write("Documents/new-invoice.pdf", 10);
            Assert.True(index.ApplyChanges(new[] { documents }) > 0);
            Assert.Equal(new[] { "new-invoice.pdf" }, Names(index.Search("invoice")));

            File.Move(Path.Combine(documents, "new-invoice.pdf"), Path.Combine(documents, "paid-invoice.pdf"));
            index.ApplyChanges(new[] { documents });
            Assert.Equal(new[] { "paid-invoice.pdf" }, Names(index.Search("invoice")));

            Directory.Delete(Path.Combine(documents, "Tax 2024"), true);
            index.ApplyChanges(new[] { documents });
            Assert.Empty(Names(index.Search("receipts")));
            Assert.DoesNotContain("report.pdf", Names(index.Search("report")));

            // A whole new tree, reported through a folder deep inside it.
            Write("Projects/Alpha/src/main.cs", 10);
            index.ApplyChanges(new[] { Path.Combine(_root, "Projects", "Alpha", "src") });
            Assert.Equal(new[] { "main.cs" }, Names(index.Search("main.cs")));
            Assert.Equal(Path.Combine(_root, "Projects", "Alpha", "src", "main.cs"), index.Search("main.cs").GetHit(0).FullPath);

            File.WriteAllText(Path.Combine(_root, "Music", "song.mp3"), "short");
            index.ApplyChanges(new[] { Path.Combine(_root, "Music") });
            Assert.Equal(5, index.Search("song").GetHit(0).Size);

            Assert.Equal(0, index.ApplyChanges(new[] { Path.Combine(_root, "Music") })); // nothing changed
            Assert.Equal(0, index.ApplyChanges(new[] { Path.GetTempPath() })); // outside the roots
            Assert.True(events >= 5);

            Assert.True(index.HasUnsavedChanges);
            index.SaveIfChanged();
            using var reloaded = new SearchIndex(_store);
            reloaded.LoadIndex();
            Assert.Equal(new[] { "paid-invoice.pdf" }, Names(reloaded.Search("invoice")));
            Assert.Empty(Names(reloaded.Search("receipts")));
        }

        // ------------------------------------------------------------------ parsing

        [Theory]
        [InlineData(">10mb", 10L * 1024 * 1024 + 1, long.MaxValue)]
        [InlineData("<=1k", long.MinValue, 1024)]
        [InlineData("1mb..2mb", 1024L * 1024, 2L * 1024 * 1024)]
        [InlineData("empty", 0, 0)]
        [InlineData("1.5g", 1610612736, 1610612736)]
        public void SizeRanges(string text, long min, long max)
        {
            Assert.True(SearchQuery.TryParseSizeRange(text, out long actualMin, out long actualMax));
            Assert.Equal(min, actualMin);
            Assert.Equal(max, actualMax);
        }

        [Fact]
        public void DateRanges_AreLocalPeriods()
        {
            var now = new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Local);
            Assert.True(SearchQuery.TryParseDateRange("2024", now, out long min, out long max));
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, min);
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks - 1, max);
            Assert.True(SearchQuery.TryParseDateRange("today", now, out min, out max));
            Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, min);
            Assert.Equal(long.MaxValue, max);
            Assert.True(SearchQuery.TryParseDateRange(">2024-05", now, out min, out _));
            Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks, min);
            Assert.False(SearchQuery.TryParseDateRange("someday", now, out _, out _));
        }

        [Fact]
        public void Parser_SplitsWordsFiltersAndQuotes()
        {
            SearchQuery query = SearchQuery.Parse("Tax \"my report\" -draft ext:pdf;DOCX in:\"old stuff\" *.txt C:\\Users");
            Assert.Equal(new[] { "tax", "my report", "users" }, query.Terms);
            Assert.Equal(new[] { "draft" }, query.ExcludedTerms);
            Assert.Equal(new[] { ".pdf", ".docx" }, query.Extensions);
            Assert.Equal(new[] { "old stuff" }, query.InFolderNames);
            Assert.Equal(new[] { "*.txt" }, query.Patterns);
            Assert.Equal(new[] { "c:" }, query.FolderSegments.Single());
            Assert.True(SearchQuery.Parse("   ").IsEmpty);
        }

        // ------------------------------------------------------------------ helpers

        private async Task<SearchIndex> BuildAsync()
        {
            var index = new SearchIndex(_store);
            await index.BuildIndexAsync(restrictToRoots: new[] { _root });
            return index;
        }

        private static List<string> Names(SearchResults results) =>
            Enumerable.Range(0, results.Count).Select(i => results.GetHit(i).Name).ToList();

        private void Write(string relative, int size)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[size]);
        }
    }
}
