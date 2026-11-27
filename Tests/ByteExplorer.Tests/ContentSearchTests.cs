// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Services;
using ByteSearcher;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Search inside files: the tokenizer, each format's extractor (documents built here, in memory), the on-disk content
    /// index (build, query, incremental update, deletion, merging, reload), the content: syntax and how content matches
    /// join the name search.
    /// </summary>
    public sealed class ContentSearchTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-content-" + Guid.NewGuid().ToString("N"));
        private readonly string _store = Path.Combine(Path.GetTempPath(), "be-content-store-" + Guid.NewGuid().ToString("N"));

        public ContentSearchTests()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_store);
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

        // ------------------------------------------------------------------ tokenizer

        [Fact]
        public void Tokenizer_FoldsCaseAndDiacritics_SplitsOnNonLetters_DropsSingleLetters()
        {
            Assert.Equal(new[] { "cafe", "mail", "2024", "grusse" }, ContentTokenizer.TermsOf("Café e-mail_2024 X Grüsse"));
            // Decomposed text (e + combining acute) folds the same as composed.
            Assert.Equal(new[] { "cafe" }, ContentTokenizer.TermsOf("Café"));
            // A very long word is cut to the maximum, on both sides alike.
            string longWord = new string('a', 80);
            Assert.Equal(ContentTokenizer.MaxLength, ContentTokenizer.TermsOf(longWord).Single().Length);
        }

        [Fact]
        public void Tokenizer_GivesChineseAndJapaneseAsCharactersAndPairs()
        {
            var collector = new List<string>();
            var tokenizer = new ContentTokenizer();
            var receiver = new Collector(collector);
            tokenizer.Feed("東京都", receiver);
            tokenizer.Finish(receiver);
            Assert.Equal(new[] { "東", "京", "東京", "都", "京都" }, collector);
            // A query keeps only the pairs (far more selective).
            Assert.Equal(new[] { "東京", "京都" }, ContentTokenizer.TermsOf("東京都"));
        }

        [Fact]
        public void Tokenizer_JoinsAWordSplitAcrossPieces()
        {
            var collector = new List<string>();
            var tokenizer = new ContentTokenizer();
            var receiver = new Collector(collector);
            tokenizer.Feed("inv", receiver);
            tokenizer.Feed("oice 42", receiver);
            tokenizer.Finish(receiver);
            Assert.Equal(new[] { "invoice", "42" }, collector);
        }

        // ------------------------------------------------------------------ extractors

        [Fact]
        public void Text_DetectsTheEncoding_AndSkipsBinary()
        {
            Assert.Contains("Grüße aus Köln", Extract("utf16bom.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Grüße aus Köln")).ToArray()));
            Assert.Contains("Grüße aus Köln", Extract("utf16.txt", Encoding.Unicode.GetBytes("Grüße aus Köln, ohne Markierung")));
            Assert.Contains("naïve café", Extract("utf8.md", Encoding.UTF8.GetBytes("# naïve café")));
            Assert.Contains("déjà", Extract("latin1.txt", Encoding.Latin1.GetBytes("déjà vu")));
            Assert.Null(Extract("binary.log", new byte[] { 1, 0, 2, 0, 0, 0, 3, 4, 0, 0, 7, 0, 0, 0, 0, 9 }));
        }

        [Fact]
        public void Html_IsReadWithoutTags()
        {
            string text = Extract("page.html", Encoding.UTF8.GetBytes("<html><body><p class=\"x\">Hello <b>bold</b> world</p></body></html>"));
            Assert.Contains("Hello", text);
            Assert.Contains("bold", text);
            Assert.DoesNotContain("class", text);
        }

        [Fact]
        public void Docx_GivesTheTextRuns_InParagraphs()
        {
            byte[] docx = Zip(("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t xml:space=\"preserve\"> wor</w:t></w:r><w:r><w:t>ld</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Invoice</w:t></w:r><w:r><w:instrText>HYPERLINK secret</w:instrText></w:r></w:p></w:body></w:document>"),
                ("word/footer1.xml", "<w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:r><w:t>Page footer</w:t></w:r></w:p></w:ftr>"));
            string text = Extract("letter.docx", docx);
            Assert.Contains("Hello world", text);
            Assert.Contains("Invoice", text);
            Assert.Contains("Page footer", text);
            Assert.DoesNotContain("secret", text); // field codes are not text
            Assert.DoesNotContain("worldinvoice", ContentTokenizer.TermsOf(text)); // paragraphs separate words
        }

        [Fact]
        public void Xlsx_GivesSharedStrings_InlineStringsAndNumbers()
        {
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            byte[] xlsx = Zip(
                ("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>Customer</t></si><si><r><t>Acme </t></r><r><t>Corp</t></r></si></sst>"),
                ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>4711</v></c>" +
                                              "<c r=\"C1\" t=\"inlineStr\"><is><t>Overdue</t></is></c></row></sheetData></worksheet>"));
            string text = Extract("book.xlsx", xlsx);
            Assert.Contains("Customer", text);
            Assert.Contains("Acme Corp", text);
            Assert.Contains("4711", text);
            Assert.Contains("Overdue", text);
            Assert.DoesNotContain("\n0\n", text); // the shared string index of A1 is not text
        }

        [Fact]
        public void Pptx_GivesSlidesInNumberOrder_ThenNotes()
        {
            const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            string Slide(string text) => $"<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"{a}\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
            byte[] pptx = Zip(("ppt/slides/slide10.xml", Slide("Tenth")), ("ppt/slides/slide2.xml", Slide("Second")),
                ("ppt/notesSlides/notesSlide1.xml", Slide("Speaker notes")));
            string text = Extract("deck.pptx", pptx);
            Assert.True(text.IndexOf("Second", StringComparison.Ordinal) < text.IndexOf("Tenth", StringComparison.Ordinal));
            Assert.Contains("Speaker notes", text);
        }

        [Fact]
        public void OpenDocument_GivesContentXmlText()
        {
            byte[] odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"),
                ("content.xml", "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">" +
                                "<office:body><office:text><text:h>Minutes</text:h><text:p>Budget<text:s/>approved</text:p></office:text></office:body></office:document-content>"));
            string text = Extract("notes.odt", odt);
            Assert.Contains("Minutes", text);
            Assert.Equal(new[] { "minutes", "budget", "approved" }, ContentTokenizer.TermsOf(text));
        }

        [Fact]
        public void Rtf_DropsControlWordsAndTables_DecodesEscapes()
        {
            const string rtf = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Arial;}}{\\colortbl;\\red255\\green0\\blue0;}" +
                               "{\\*\\generator Riched20;}{\\info{\\title Secret title}}\\f0\\fs24 Hello \\b world\\b0\\par Caf\\'e9 costs \\u8364?100\\par\n" +
                               "Braces \\{ and \\} stay}";
            string text = Extract("memo.rtf", Encoding.ASCII.GetBytes(rtf));
            Assert.Contains("Hello world", text);
            Assert.Contains("Café", text);
            Assert.Contains("€100", text);
            Assert.Contains("{ and }", text);
            Assert.DoesNotContain("Arial", text);
            Assert.DoesNotContain("Riched", text);
            Assert.DoesNotContain("Secret", text);
            Assert.DoesNotContain("fs24", text);
        }

        [Fact]
        public void Pdf_InflatesContentStreams_AndReadsTjTjArraysAndHexStrings()
        {
            byte[] pdf = BuildPdf(
                "BT /F1 12 Tf 72 712 Td (Hello PDF \\(draft\\)) Tj 0 -14 Td [(Wor) -20 (ld) -400 (again)] TJ T* <496E766F696365> Tj (\\101\\102C) ' ET",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            string text = Extract("report.pdf", pdf);
            Assert.Contains("Hello PDF (draft)", text);
            Assert.Contains("World again", text);
            Assert.Contains("Invoice", text);
            Assert.Contains("ABC", text);
        }

        [Fact]
        public void Pdf_UsesToUnicodeMaps_AndSkipsCompositeFontsWithoutOne()
        {
            const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                                "3 beginbfchar <0001> <0048> <0002> <0069> <0003> <0020> endbfchar 1 beginbfrange <0010> <0012> <0061> endbfrange endcmap end end";
            byte[] mapped = BuildPdf("BT /F1 12 Tf <00010002> Tj <0003> Tj <001000110012> Tj ET",
                "<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H /ToUnicode 6 0 R >>", cmap);
            Assert.Contains("Hi abc", Extract("mapped.pdf", mapped));

            byte[] unmapped = BuildPdf("BT /F1 12 Tf <00010002> Tj ET", "<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H >>");
            Assert.Equal(string.Empty, Extract("unmapped.pdf", unmapped).Trim());
        }

        [Fact]
        public void Damaged_And_Unknown_Files_GiveNothing_WithoutThrowing()
        {
            Assert.Null(Extract("broken.docx", new byte[] { 1, 2, 3 }));
            Assert.Null(Extract("broken.pdf", Encoding.ASCII.GetBytes("not a pdf at all")));
            Assert.Null(Extract("photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF }));
            Assert.Equal(ContentFormat.Pdf, ContentExtractor.FormatOf("A.PDF"));
            Assert.Equal(ContentFormat.Pdf, ContentExtractor.FormatOfUtf8("a.pdf"u8));
            Assert.Equal(ContentFormat.None, ContentExtractor.FormatOf("noextension"));
        }

        // ------------------------------------------------------------------ query syntax

        [Fact]
        public void Query_ParsesContentFilters()
        {
            SearchQuery query = SearchQuery.Parse("content:invoice report");
            Assert.Equal(new[] { "invoice" }, query.ContentWords);
            Assert.Equal(new[] { "report" }, query.Terms);
            Assert.True(query.HasContentFilter);

            query = SearchQuery.Parse("content:\"exact phrase\" ext:pdf");
            Assert.Equal(new[] { "exact phrase" }, query.ContentPhrases);
            Assert.Empty(query.ContentWords);
            Assert.Equal(new[] { ".pdf" }, query.Extensions);

            query = SearchQuery.Parse("contains:budget");
            Assert.Equal(new[] { "budget" }, query.ContentWords);
            Assert.False(query.IsEmpty);
            Assert.Empty(query.Terms);

            Assert.True(SearchQuery.Parse("content:\"single\"").ContentWords.SequenceEqual(new[] { "single" }));
            Assert.False(SearchQuery.Parse("plain").HasContentFilter);
        }

        // ------------------------------------------------------------------ the index

        [Fact]
        public async Task ContentIndex_Builds_Finds_UpdatesIncrementally_AndRemovesDeletedFiles()
        {
            WriteText("notes/quick.txt", "The quick brown fox sends an invoice.");
            File.WriteAllBytes(Path.Combine(_root, "notes", "plan.docx"), Zip(("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Quarterly invoice summary</w:t></w:r></w:p></w:body></w:document>")));
            WriteText("notes/other.md", "Nothing to see here.");
            WriteText("invoice-template.md", "blank form");
            WriteText(".hidden/secret.txt", "invoice hidden away");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(Path.Combine(_root, ".hidden"), FileAttributes.Directory | FileAttributes.Hidden);
            }

            WriteText("node_modules/pkg/readme.md", "invoice in a dependency");

            using var names = new SearchIndex(_store);
            await names.BuildIndexAsync(restrictToRoots: new[] { _root });
            using var content = new ContentIndex(Path.Combine(_store, "Content"));
            Assert.True(content.TryLock());
            content.Load();
            names.Content = content;
            using var indexer = NewIndexer(names, content);
            indexer.RunOnce();

            Assert.Equal(4, content.DocumentCount); // not the hidden file, not the dependency
            Assert.Equal(new[] { "plan.docx", "quick.txt" }, Names(names.Search("content:invoice")));
            Assert.Equal(new[] { "plan.docx", "quick.txt" }, Names(names.Search("content:INVOIC"))); // case, prefix
            Assert.Equal(new[] { "plan.docx" }, Names(names.Search("content:invoice ext:docx")));
            Assert.Equal(new[] { "quick.txt" }, Names(names.Search("content:\"brown fox\"")));
            Assert.Empty(Names(names.Search("content:\"fox brown\""))); // both words are there, not in this order
            Assert.Empty(Names(names.Search("content:zebra")));
            Assert.True(names.Search("content:invoice").GetHit(0).IsContentMatch);

            // The words also match contents when asked: name matches first, then content matches (marked).
            SearchResults both = names.Search("invoice", new SearchOptions { SearchContents = true });
            Assert.Equal(3, both.Count);
            Assert.Equal("invoice-template.md", both.GetHit(0).Name);
            Assert.False(both.GetHit(0).IsContentMatch);
            Assert.Equal(new[] { "plan.docx", "quick.txt" }, new[] { both.GetHit(1).Name, both.GetHit(2).Name }.OrderBy(n => n, StringComparer.Ordinal));
            Assert.True(both.GetHit(1).IsContentMatch && both.GetHit(2).IsContentMatch);
            Assert.Contains("invoice", both.ContentWords);
            Assert.Equal(new[] { "invoice-template.md" }, Names(names.Search("invoice"))); // off: names only
            // Other filters still apply to content matches.
            Assert.Equal(new[] { "plan.docx" }, Names(names.Search("invoice ext:docx", new SearchOptions { SearchContents = true })));
            // A file matching by name is listed once, as a name match.
            SearchResults quick = names.Search("quick", new SearchOptions { SearchContents = true });
            Assert.Equal(new[] { "quick.txt" }, Names(quick));
            Assert.False(quick.GetHit(0).IsContentMatch);

            // Incremental: a changed file is read again, a deleted one leaves.
            string other = Path.Combine(_root, "notes", "other.md");
            File.WriteAllText(other, "Now it mentions the invoice too, at greater length.");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(1));
            File.Delete(Path.Combine(_root, "notes", "quick.txt"));
            names.ApplyChanges(new[] { Path.Combine(_root, "notes") });
            indexer.RunOnce();
            Assert.Equal(new[] { "other.md", "plan.docx" }, Names(names.Search("content:invoice")));
            Assert.Equal(3, content.DocumentCount);

            // Saved: a fresh instance finds the same; while the first holds the lock it can only read.
            using var reloaded = new ContentIndex(Path.Combine(_store, "Content"));
            Assert.False(reloaded.TryLock());
            Assert.True(reloaded.Load());
            Assert.Equal(3, reloaded.DocumentCount);
            Assert.Equal(2, reloaded.Search(ContentMatcher.TermsForWords(new[] { "invoice" })).Count);
        }

        [Fact]
        public void ContentIndex_MergesSegments_DropsReplacedPostings_AndSurvivesDamage()
        {
            string directory = Path.Combine(_store, "Content");
            using (var index = new ContentIndex(directory))
            {
                Assert.True(index.TryLock());
                index.Load();
                for (int i = 0; i < 20; i++)
                {
                    index.AddDocument($"/docs/file{i}.txt", 10, i, new[] { "common", "word" + i });
                    index.Commit();
                }

                Assert.True(index.SegmentCount <= 8);
                Assert.Equal(20, index.Search(new[] { new ContentTerm("common", false) }).Count);
                Assert.Equal(new[] { "/docs/file7.txt" }, index.Search(new[] { new ContentTerm("word7", false) }));
                Assert.Equal(11, index.Search(new[] { new ContentTerm("word1", true) }).Count); // word1, word10..word19

                // Replaced: the old terms no longer find it.
                index.AddDocument("/docs/file7.txt", 11, 99, new[] { "changed" });
                index.RemoveDocument("/docs/file8.txt");
                index.Commit();
                Assert.Empty(index.Search(new[] { new ContentTerm("word7", false) }));
                Assert.Empty(index.Search(new[] { new ContentTerm("word8", false) }));
                Assert.Equal(new[] { "/docs/file7.txt" }, index.Search(new[] { new ContentTerm("changed", false) }));
                Assert.Equal(18, index.Search(new[] { new ContentTerm("common", false) }).Count);
                Assert.True(index.IsCurrent("/docs/file7.txt", 11, 99));
                Assert.False(index.IsCurrent("/docs/file7.txt", 10, 7));
            }

            // A damaged document table: the index starts over instead of failing.
            File.WriteAllBytes(Path.Combine(directory, ContentIndex.DocumentsFileName), new byte[] { 1, 2, 3, 4 });
            using (var damaged = new ContentIndex(directory))
            {
                Assert.True(damaged.TryLock());
                Assert.False(damaged.Load());
                Assert.Equal(0, damaged.DocumentCount);
                Assert.Empty(Directory.GetFiles(directory, "seg-*"));
            }
        }

        [Fact]
        public async Task ContentFilter_WithoutAContentIndex_FindsNothing_AndNamesStillWork()
        {
            WriteText("invoice.txt", "invoice");
            using var names = new SearchIndex(_store);
            await names.BuildIndexAsync(restrictToRoots: new[] { _root });
            Assert.Empty(Names(names.Search("content:invoice")));
            Assert.Equal(new[] { "invoice.txt" }, Names(names.Search("invoice", new SearchOptions { SearchContents = true })));
        }

        [Fact]
        public async Task SearchService_IndexesContents_SearchesThem_AndDeletesTheIndexWhenTurnedOff()
        {
            // Not under the temporary folder: on Linux and macOS it is one of the system folders contents are never read from.
            string root = Path.Combine(AppContext.BaseDirectory, "be-content-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "minutes.txt"), "The committee approved the budget.");
            File.WriteAllText(Path.Combine(root, "docs", "budget.csv"), "item;amount");
            try
            {
                await SearchServiceScenario(root);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private async Task SearchServiceScenario(string root)
        {
            string settingsPath = Path.Combine(_store, "settings.json");
            var settings = new SettingsService(settingsPath);
            await settings.LoadAsync();
            settings.Current.Index.Locations.Add(root);
            settings.Current.Index.EnableAutoRebuild = false;
            settings.Current.Index.WatchForChanges = false;
            string indexFolder = Path.Combine(_store, "Index");
            using var service = new ByteExplorer.Infrastructure.Search.GlobalSearchService(settings, indexFolder);
            await service.EnsureIndexAsync();
            ContentIndexer indexer = service.ContentIndexer;
            Assert.NotNull(indexer);
            for (int i = 0; i < 200 && (!indexer.IsIdle || indexer.Status.StartsWith("Loading", StringComparison.Ordinal)); i++)
            {
                await Task.Delay(25); // the worker loads the (empty) saved index first
            }

            indexer.RunOnce();
            SearchResults results = await service.SearchAsync("content:committee", new SearchOptions(), CancellationToken.None);
            Assert.Equal(new[] { "minutes.txt" }, Names(results));
            results = await service.SearchAsync("budget", new SearchOptions { SearchContents = true }, CancellationToken.None);
            Assert.Equal(new[] { "budget.csv", "minutes.txt" }, Names(results));
            Assert.Equal("budget.csv", results.GetHit(0).Name); // the name match first

            settings.Current.Index.IndexFileContents = false;
            service.ApplyContentSettings();
            Assert.Null(service.ContentIndexer);
            Assert.StartsWith("Off", service.ContentStatus);
            Assert.False(File.Exists(Path.Combine(indexFolder, "Content", ContentIndex.DocumentsFileName)));
            results = await service.SearchAsync("content:committee", new SearchOptions(), CancellationToken.None);
            Assert.Empty(Names(results));
        }

        [Avalonia.Headless.XUnit.AvaloniaFact]
        public async Task ResultRow_OfAContentMatch_ShowsContainsWithASnippet_DrawnAfterTheName()
        {
            WriteText("mail/letter.txt", "Dear team, the quarterly invoice is attached.");
            using var names = new SearchIndex(_store);
            await names.BuildIndexAsync(restrictToRoots: new[] { _root });
            using var content = new ContentIndex(Path.Combine(_store, "Content"));
            Assert.True(content.TryLock());
            content.Load();
            names.Content = content;
            using var indexer = NewIndexer(names, content);
            indexer.RunOnce();

            var list = new ByteExplorer.ViewModels.Search.SearchResultList(names.Search("invoice", new SearchOptions { SearchContents = true }));
            ByteExplorer.ViewModels.Search.SearchResultRow row = list[0];
            Assert.True(row.IsContentMatch);
            Assert.Null(row.ContainsText); // until the snippet is read

            var window = new Avalonia.Controls.Window { Width = 900, Height = 60 };
            var view = new ByteExplorer.Views.Search.SearchResultRowView { DataContext = row };
            window.Content = view;
            window.Show();
            try
            {
                for (int i = 0; i < 250 && row.ContainsText == null; i++)
                {
                    await Task.Delay(20);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // rendering asks for the snippet; it arrives on the UI thread
                }

                Assert.NotNull(row.ContainsText);
                Assert.StartsWith("Contains: ", row.ContainsText);
                Assert.Contains("quarterly invoice is attached", row.ContainsText);
                Assert.Contains("Contains: ", row.ToolTip);
            }
            finally
            {
                window.Close();
            }
        }

        [Fact]
        public void Snippet_ShowsTheTextAroundTheFirstMatch()
        {
            string path = WriteText("letter.txt", "Dear customer,\n\nplease find attached the   invoice for March. Payment is due within thirty days of receipt.");
            string snippet = ContentMatcher.Snippet(path, new[] { "invoice" }, 60);
            Assert.NotNull(snippet);
            Assert.Contains("invoice for March", snippet);
            Assert.DoesNotContain("  ", snippet);
            Assert.True(snippet.Length <= 80, snippet);
            Assert.Null(ContentMatcher.Snippet(path, new[] { "zebra" }));
        }

        [Fact]
        public async Task Settings_AreWrittenWithTheirComments_AndRoundTrip()
        {
            string path = Path.Combine(_store, "settings.json");
            var service = new SettingsService(path);
            await service.LoadAsync();
            Assert.True(service.Current.Index.IndexFileContents);
            Assert.True(service.Current.Index.SearchFileContents);
            Assert.Equal(20, service.Current.Index.ContentMaxFileSizeMB);
            service.Current.Index.IndexFileContents = false;
            service.Current.Index.ContentMaxFileSizeMB = 5;
            await service.SaveAsync();
            string json = File.ReadAllText(path);
            Assert.Contains("\"IndexFileContents\": false", json);
            Assert.Contains("\"ContentMaxFileSizeMB\": 5", json);
            Assert.Contains("content index", json);
            var again = new SettingsService(path);
            await again.LoadAsync();
            Assert.False(again.Current.Index.IndexFileContents);
            Assert.Equal(5, again.Current.Index.ContentMaxFileSizeMB);
        }

        // ------------------------------------------------------------------ helpers

        private ContentIndexer NewIndexer(SearchIndex names, ContentIndex content) =>
            new ContentIndexer(content, () => names.CurrentData, () => false, new ContentIndexerOptions { ExcludedFolders = new[] { _store } });

        private static List<string> Names(SearchResults results) =>
            Enumerable.Range(0, results.Count).Select(i => results.GetHit(i).Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        private string WriteText(string relative, string text)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>The text of a file with this name and content, or null when the extractor read nothing.</summary>
        private string Extract(string name, byte[] bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes);
            var sink = new TextSink();
            return ContentExtractor.Extract(path, sink) ? sink.ToString() : null;
        }

        private static byte[] Zip(params (string Name, string Text)[] entries)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, string text) in entries)
                {
                    using Stream stream = archive.CreateEntry(name).Open();
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            return memory.ToArray();
        }

        /// <summary>A one-page PDF whose content stream (FlateDecode) is <paramref name="content"/>; object 5 is the font.</summary>
        private static byte[] BuildPdf(string content, string font, string cmap = null)
        {
            var output = new MemoryStream();
            var offsets = new List<long>();
            void Write(string text)
            {
                byte[] bytes = Encoding.Latin1.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
            }

            void Stream(int number, string dictionary, string data)
            {
                byte[] compressed = Deflate(Encoding.Latin1.GetBytes(data));
                offsets.Add(output.Position);
                Write($"{number} 0 obj\n<< {dictionary} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
                output.Write(compressed, 0, compressed.Length);
                Write("\nendstream\nendobj\n");
            }

            Write("%PDF-1.4\n%âãÏÓ\n");
            offsets.Add(output.Position);
            Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets.Add(output.Position);
            Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets.Add(output.Position);
            Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
            Stream(4, string.Empty, content);
            offsets.Add(output.Position);
            Write($"5 0 obj\n{font}\nendobj\n");
            if (cmap != null)
            {
                Stream(6, string.Empty, cmap);
            }

            long xref = output.Position;
            Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets)
            {
                Write($"{offset:D10} 00000 n \n");
            }

            Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }

        private static byte[] Deflate(byte[] data)
        {
            using var memory = new MemoryStream();
            using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data, 0, data.Length);
            }

            return memory.ToArray();
        }

        private sealed class Collector : ITokenReceiver
        {
            private readonly List<string> _terms;

            public Collector(List<string> terms) => _terms = terms;

            public bool OnToken(ReadOnlySpan<char> term, long start, long end)
            {
                _terms.Add(term.ToString());
                return true;
            }
        }
    }
}
