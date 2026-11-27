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
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core.Platform;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Preview;
using ByteExplorer.ViewModels;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Document previews in the details pane: which method a type gets (preview handler, pdftoppm, text), the Windows
    /// preview handler lookup on a registry in memory, the pdftoppm renderer with a fake and (when installed) the real
    /// tool, the .docx/.odt text extraction, and the preview view model's Document/Image/Text states. The COM hosting itself
    /// (PreviewHandlerSession, PreviewHandlerHost) runs only on Windows and is not covered here.
    /// </summary>
    public sealed class DocumentPreviewTests : IDisposable
    {
        private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        private static readonly Guid PdfHandler = new Guid("{3A84F9C2-6164-485C-A7D9-4B27F8AC009E}");
        private static readonly Guid OfficeHandler = new Guid("{84F66100-FF7C-4fb4-B0C0-02CD7FB668FE}");

        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-docpreview-" + Guid.NewGuid().ToString("N"));

        public DocumentPreviewTests()
        {
            Directory.CreateDirectory(_root);
        }

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

        // ------------------------------------------------------------------ choosing the method

        [Fact]
        public void Plan_Windows_UsesThePreviewHandlerFirst_ThenTheFallbacks()
        {
            var handlers = new Dictionary<string, Guid> { [".pdf"] = PdfHandler, [".xlsx"] = OfficeHandler };
            var renderer = FakeRenderer(toolFound: true, (_, _) => 0);
            var previewer = new DocumentPreviewer(true, ext => handlers.TryGetValue(ext, out Guid g) ? g : null, renderer);

            DocumentPreviewPlan pdf = previewer.Plan(".pdf");
            Assert.Equal(DocumentPreviewMethod.PreviewHandler, pdf.Method);
            Assert.Equal(PdfHandler, pdf.Handler);
            Assert.Equal(DocumentPreviewMethod.PreviewHandler, previewer.Plan(".xlsx").Method);

            // No handler for .docx on this machine: its text instead; .xls has neither.
            Assert.Equal(DocumentPreviewMethod.DocumentText, previewer.Plan(".docx").Method);
            Assert.Equal(DocumentPreviewMethod.None, previewer.Plan(".xls").Method);
            Assert.Equal(DocumentPreviewMethod.None, previewer.Plan("pdf").Method);
            Assert.Equal(DocumentPreviewMethod.None, previewer.Plan(string.Empty).Method);
        }

        [Fact]
        public void Plan_LinuxAndMac_RenderPdfsWithPdftoppm_AndReadDocumentText()
        {
            // The handler lookup is never consulted off Windows, even if it would answer.
            var previewer = new DocumentPreviewer(false, _ => PdfHandler, FakeRenderer(toolFound: true, (_, _) => 0));
            Assert.Equal(DocumentPreviewMethod.PdfFirstPage, previewer.Plan(".pdf").Method);
            Assert.Equal(DocumentPreviewMethod.PdfFirstPage, previewer.Plan(".PDF").Method);
            Assert.Equal(DocumentPreviewMethod.DocumentText, previewer.Plan(".docx").Method);
            Assert.Equal(DocumentPreviewMethod.DocumentText, previewer.Plan(".ODT").Method);
            Assert.Equal(DocumentPreviewMethod.None, previewer.Plan(".pptx").Method);
            Assert.Equal(DocumentPreviewMethod.None, previewer.Plan(".xlsx").Method);

            // Without pdftoppm a PDF has no document preview (the pane falls back to "no preview").
            var withoutTool = new DocumentPreviewer(false, null, FakeRenderer(toolFound: false, (_, _) => 0));
            Assert.Equal(DocumentPreviewMethod.None, withoutTool.Plan(".pdf").Method);
            Assert.Equal(DocumentPreviewMethod.DocumentText, withoutTool.Plan(".docx").Method);
        }

        // ------------------------------------------------------------------ the Windows registry lookup

        [Fact]
        public void Registry_FindsTheHandlerWhereTheShellLooks()
        {
            var registry = new MemoryClassesRoot();
            registry.Set(@".pdf\" + PreviewHandlerRegistry.PreviewHandlerShellEx, PdfHandler.ToString("B"));
            registry.Set(@".docx", "Word.Document.12");
            registry.Set(@"Word.Document.12\" + PreviewHandlerRegistry.PreviewHandlerShellEx, OfficeHandler.ToString("B"));
            registry.Set(@"SystemFileAssociations\.ttf\" + PreviewHandlerRegistry.PreviewHandlerShellEx, "{11111111-1111-1111-1111-111111111111}");
            registry.Set(@".log", "PerceivedType", "text");
            registry.Set(@"SystemFileAssociations\text\" + PreviewHandlerRegistry.PreviewHandlerShellEx, "{22222222-2222-2222-2222-222222222222}");
            registry.Set(@".gone\" + PreviewHandlerRegistry.PreviewHandlerShellEx, "{33333333-3333-3333-3333-333333333333}"); // CLSID not registered
            registry.Set(@".bad\" + PreviewHandlerRegistry.PreviewHandlerShellEx, "not a guid");
            foreach (string clsid in new[] { PdfHandler.ToString("B"), OfficeHandler.ToString("B"), "{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}" })
            {
                registry.Set(@"CLSID\" + clsid, "A preview handler");
            }

            var lookup = new PreviewHandlerRegistry(registry);
            Assert.Equal(PdfHandler, lookup.FindHandler(".pdf"));                // the extension's own key
            Assert.Equal(OfficeHandler, lookup.FindHandler(".DOCX"));            // through its ProgID
            Assert.Equal(new Guid("11111111-1111-1111-1111-111111111111"), lookup.FindHandler(".ttf"));  // SystemFileAssociations
            Assert.Equal(new Guid("22222222-2222-2222-2222-222222222222"), lookup.FindHandler(".log"));  // perceived type
            Assert.Null(lookup.FindHandler(".gone"));
            Assert.Null(lookup.FindHandler(".bad"));
            Assert.Null(lookup.FindHandler(".zip"));
            Assert.Null(lookup.FindHandler(@".x\..\CLSID"));
            Assert.Null(lookup.FindHandler("pdf"));

            // Cached per extension until cleared (a handler installed later is found after ClearCache).
            registry.Set(@".zip\" + PreviewHandlerRegistry.PreviewHandlerShellEx, PdfHandler.ToString("B"));
            Assert.Null(lookup.FindHandler(".zip"));
            lookup.ClearCache();
            Assert.Equal(PdfHandler, lookup.FindHandler(".zip"));
        }

        [Fact]
        public void Registry_AReadThatFails_MeansNoHandler()
        {
            var lookup = new PreviewHandlerRegistry(new ThrowingClassesRoot());
            Assert.Null(lookup.FindHandler(".pdf"));
        }

        // ------------------------------------------------------------------ pdftoppm

        [Fact]
        public async Task PdfRenderer_RunsPdftoppmOnce_ThenUsesTheCache()
        {
            string pdf = Path.Combine(_root, "-report.pdf"); // a name that looks like an option
            File.WriteAllText(pdf, "%PDF-1.4 not really");
            var calls = new List<(string Program, IReadOnlyList<string> Arguments)>();
            var renderer = new PdfPageRenderer(() => "/usr/bin/pdftoppm", (program, arguments, timeout, token) =>
            {
                calls.Add((program, arguments));
                File.WriteAllBytes(arguments[^1] + ".png", Convert.FromBase64String(OnePixelPng));
                return Task.FromResult(0);
            }, Path.Combine(_root, "cache"));

            string png = await renderer.RenderFirstPageAsync(pdf, 800, CancellationToken.None);
            Assert.NotNull(png);
            Assert.True(File.Exists(png));
            Assert.StartsWith(Path.Combine(_root, "cache"), png);
            Assert.Single(calls);
            Assert.Equal("/usr/bin/pdftoppm", calls[0].Program);
            Assert.Equal(new[] { "-png", "-f", "1", "-l", "1", "-singlefile", "-scale-to", "800" }, calls[0].Arguments.Take(8));
            Assert.Equal(Path.GetFullPath(pdf), calls[0].Arguments[8]); // absolute: never read as an option
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache"), "render-*"));

            Assert.Equal(png, await renderer.RenderFirstPageAsync(pdf, 800, CancellationToken.None));
            Assert.Single(calls); // cached

            // Another size or a changed file renders again.
            Assert.NotEqual(png, await renderer.RenderFirstPageAsync(pdf, 400, CancellationToken.None));
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddMinutes(-5));
            await renderer.RenderFirstPageAsync(pdf, 800, CancellationToken.None);
            Assert.Equal(3, calls.Count);
        }

        [Fact]
        public async Task PdfRenderer_Failures_GiveNoPicture()
        {
            string pdf = Path.Combine(_root, "locked.pdf");
            File.WriteAllText(pdf, "%PDF-1.4");
            string cache = Path.Combine(_root, "cache");

            var failing = new PdfPageRenderer(() => "pdftoppm", (_, _, _, _) => Task.FromResult(1), cache); // encrypted, broken
            Assert.Null(await failing.RenderFirstPageAsync(pdf, 800, CancellationToken.None));

            var slow = new PdfPageRenderer(() => "pdftoppm", (_, _, _, _) => Task.FromResult(PdfPageRenderer.TimedOut), cache);
            Assert.Null(await slow.RenderFirstPageAsync(pdf, 800, CancellationToken.None));

            int runs = 0;
            var missing = new PdfPageRenderer(() => null, (_, _, _, _) => Task.FromResult(runs++), cache);
            Assert.False(missing.IsAvailable);
            Assert.Null(await missing.RenderFirstPageAsync(pdf, 800, CancellationToken.None));
            Assert.Equal(0, runs);

            var ok = new PdfPageRenderer(() => "pdftoppm", (_, _, _, _) => Task.FromResult(0), cache); // exit 0 but no file
            Assert.Null(await ok.RenderFirstPageAsync(pdf, 800, CancellationToken.None));
            Assert.Null(await ok.RenderFirstPageAsync(Path.Combine(_root, "absent.pdf"), 800, CancellationToken.None));
        }

        [Fact]
        public async Task PdfRenderer_WithTheRealPdftoppm_RendersTheFirstPage()
        {
            if (ShellLauncher.FindOnPath(PdfPageRenderer.ToolName) == null)
            {
                return; // poppler-utils not installed here: covered by the fake above
            }

            string pdf = Path.Combine(_root, "two pages.pdf");
            File.WriteAllBytes(pdf, MinimalPdf());
            var renderer = new PdfPageRenderer(() => ShellLauncher.FindOnPath(PdfPageRenderer.ToolName), PdfPageRenderer.RunProcessAsync,
                Path.Combine(_root, "cache"));
            string png = await renderer.RenderFirstPageAsync(pdf, 300, CancellationToken.None);
            Assert.NotNull(png);
            byte[] header = new byte[8];
            using (FileStream file = File.OpenRead(png))
            {
                Assert.Equal(8, file.Read(header, 0, 8));
            }

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);

            string notPdf = Path.Combine(_root, "fake.pdf");
            File.WriteAllText(notPdf, "this is not a PDF at all");
            Assert.Null(await renderer.RenderFirstPageAsync(notPdf, 300, CancellationToken.None));
        }

        // ------------------------------------------------------------------ .docx / .odt text

        [Fact]
        public void DocxText_KeepsParagraphsTabsAndBreaks_AndLeavesOutDeletedText()
        {
            string docx = Path.Combine(_root, "letter.docx");
            WriteZip(docx, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>Dear </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">Ada </w:t></w:r><w:r><w:t>Lovelace,</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Name</w:t><w:tab/><w:t>Value</w:t><w:br/><w:t>second line</w:t></w:r></w:p>" +
                "<w:p><w:del><w:r><w:delText>removed words</w:delText></w:r></w:del><w:r><w:instrText>PAGE</w:instrText><w:t>Kept &amp; shown</w:t></w:r></w:p>" +
                "<w:p/><w:p><w:r><w:t>Ünïcödé €</w:t></w:r></w:p>" +
                "</w:body></w:document>");

            DocumentText text = OfficeTextExtractor.Extract(docx, 10_000, CancellationToken.None);
            Assert.NotNull(text);
            Assert.False(text.Truncated);
            Assert.Equal("Dear Ada Lovelace,\nName\tValue\nsecond line\nKept & shown\n\nÜnïcödé €", text.Text);
        }

        [Fact]
        public void OdtText_KeepsHeadingsSpacesTabsAndSpans()
        {
            string odt = Path.Combine(_root, "notes.odt");
            WriteZip(odt, "content.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\">" +
                "<office:automatic-styles><style:style style:name=\"P1\">ignored</style:style></office:automatic-styles>" +
                "<office:body><office:text>" +
                "<text:h text:outline-level=\"1\">Title</text:h>" +
                "<text:p>a<text:s text:c=\"3\"/>b<text:tab/>c<text:line-break/>d <text:span>in a span</text:span></text:p>" +
                "<text:list><text:list-item><text:p>item</text:p></text:list-item></text:list>" +
                "</office:text></office:body></office:document-content>");

            DocumentText text = OfficeTextExtractor.Extract(odt, 10_000, CancellationToken.None);
            Assert.NotNull(text);
            Assert.Equal("Title\na   b\tc\nd in a span\nitem", text.Text);
        }

        [Fact]
        public void DocumentText_IsCappedAndRejectsWhatIsNotAPackage()
        {
            var body = new StringBuilder();
            for (int i = 0; i < 500; i++)
            {
                body.Append("<w:p><w:r><w:t>Paragraph number ").Append(i).Append("</w:t></w:r></w:p>");
            }

            string big = Path.Combine(_root, "big.docx");
            WriteZip(big, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
            DocumentText capped = OfficeTextExtractor.Extract(big, 100, CancellationToken.None);
            Assert.True(capped.Truncated);
            Assert.True(capped.Text.Length <= 100);
            Assert.StartsWith("Paragraph number 0\nParagraph number 1", capped.Text);

            string notZip = Path.Combine(_root, "encrypted.docx"); // an encrypted .docx is an OLE file, not a ZIP
            File.WriteAllBytes(notZip, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0 });
            Assert.Null(OfficeTextExtractor.Extract(notZip, 1000, CancellationToken.None));

            string noBody = Path.Combine(_root, "other.docx");
            WriteZip(noBody, "something/else.xml", "<x/>");
            Assert.Null(OfficeTextExtractor.Extract(noBody, 1000, CancellationToken.None));

            string dtd = Path.Combine(_root, "entities.odt"); // no DTD processing: no entity expansion
            WriteZip(dtd, "content.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaa\">]><x>&a;</x>");
            Assert.Null(OfficeTextExtractor.Extract(dtd, 1000, CancellationToken.None));

            Assert.Null(OfficeTextExtractor.Extract(Path.Combine(_root, "missing.docx"), 1000, CancellationToken.None));
            Assert.Null(OfficeTextExtractor.Extract(big.Replace(".docx", ".xlsx"), 1000, CancellationToken.None));
        }

        // ------------------------------------------------------------------ the preview view model

        [AvaloniaFact]
        public async Task Preview_Docx_ShowsItsTextAsADocument()
        {
            string docx = Path.Combine(_root, "report.docx");
            WriteZip(docx, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                "<w:p><w:r><w:t>Quarterly report</w:t></w:r></w:p></w:body></w:document>");
            using var preview = NewPreview(new DocumentPreviewer(false, null, null));

            preview.LoadFor(docx, isDirectory: false);
            await WaitFor(() => preview.Kind != FilePreviewKind.None && !preview.IsLoading);
            Assert.Equal(FilePreviewKind.Text, preview.Kind);
            Assert.True(preview.IsDocumentText);
            Assert.Equal("Quarterly report", preview.TextContent);

            // A text file afterwards is a text file again.
            string txt = Path.Combine(_root, "plain.txt");
            File.WriteAllText(txt, "hello");
            preview.LoadFor(txt, isDirectory: false);
            await WaitFor(() => preview.Kind == FilePreviewKind.Text && !preview.IsLoading && preview.TextContent == "hello");
            Assert.False(preview.IsDocumentText);
        }

        [AvaloniaFact]
        public async Task Preview_Pdf_ShowsTheRenderedPageAsAPicture()
        {
            string pdf = Path.Combine(_root, "paper.pdf");
            File.WriteAllText(pdf, "%PDF-1.4");
            var renderer = new PdfPageRenderer(() => "pdftoppm", (_, arguments, _, _) =>
            {
                File.WriteAllBytes(arguments[^1] + ".png", Convert.FromBase64String(OnePixelPng));
                return Task.FromResult(0);
            }, Path.Combine(_root, "cache"));
            using var preview = NewPreview(new DocumentPreviewer(false, null, renderer));

            preview.LoadFor(pdf, isDirectory: false);
            await WaitFor(() => preview.Kind != FilePreviewKind.None && !preview.IsLoading);
            Assert.Equal(FilePreviewKind.Image, preview.Kind);
            Assert.NotNull(preview.ImageSource);
        }

        [AvaloniaFact]
        public async Task Preview_WindowsHandler_IsHandedToTheView_AndAFailureShowsNoPreview()
        {
            string xlsx = Path.Combine(_root, "budget.xlsx");
            File.WriteAllBytes(xlsx, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 });
            using var preview = NewPreview(new DocumentPreviewer(true, ext => ext == ".xlsx" ? OfficeHandler : null, null));

            preview.LoadFor(xlsx, isDirectory: false);
            await WaitFor(() => preview.Kind != FilePreviewKind.None && !preview.IsLoading);
            Assert.Equal(FilePreviewKind.Document, preview.Kind);
            Assert.Equal(xlsx, preview.DocumentPath);
            Assert.Equal(OfficeHandler, preview.DocumentHandler);

            preview.DocumentPreviewFailed(Path.Combine(_root, "another.xlsx")); // an old report: ignored
            Assert.Equal(FilePreviewKind.Document, preview.Kind);
            preview.DocumentPreviewFailed(xlsx);
            Assert.Equal(FilePreviewKind.Unsupported, preview.Kind);
            Assert.Null(preview.DocumentPath);

            // Another selection lets go of the document at once.
            preview.LoadFor(xlsx, isDirectory: false);
            await WaitFor(() => preview.Kind == FilePreviewKind.Document);
            preview.ShowNothing();
            Assert.Null(preview.DocumentPath);
        }

        [AvaloniaFact]
        public async Task Preview_DocumentPreviewsOff_FallsBackToNoPreview()
        {
            string docx = Path.Combine(_root, "off.docx");
            WriteZip(docx, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>x</w:t></w:r></w:p></w:body></w:document>");
            bool enabled = false;
            using var preview = new FilePreviewViewModel(new UiDispatcher(Dispatcher.UIThread), null, () => enabled,
                new DocumentPreviewer(true, _ => OfficeHandler, null)) { IsActive = true };

            preview.LoadFor(docx, isDirectory: false);
            await WaitFor(() => preview.Kind != FilePreviewKind.None && !preview.IsLoading);
            Assert.Equal(FilePreviewKind.Unsupported, preview.Kind); // a ZIP: binary
            Assert.Null(preview.DocumentPath);

            enabled = true;
            preview.Reload();
            await WaitFor(() => preview.Kind == FilePreviewKind.Document && !preview.IsLoading);
        }

        // ------------------------------------------------------------------ helpers

        private static FilePreviewViewModel NewPreview(DocumentPreviewer previewer) =>
            new FilePreviewViewModel(new UiDispatcher(Dispatcher.UIThread), null, () => true, previewer) { IsActive = true };

        private PdfPageRenderer FakeRenderer(bool toolFound, Func<string, IReadOnlyList<string>, int> run) =>
            new PdfPageRenderer(() => toolFound ? "pdftoppm" : null, (program, arguments, _, _) => Task.FromResult(run(program, arguments)),
                Path.Combine(_root, "cache"));

        private static void WriteZip(string path, string entryName, string content)
        {
            using FileStream file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        /// <summary>A valid two-page PDF (correct cross-reference offsets), each page with a line of text.</summary>
        private static byte[] MinimalPdf()
        {
            string[] objects =
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
                Stream("BT /F1 24 Tf 20 100 Td (Page one) Tj ET"),
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
                Stream("BT /F1 24 Tf 20 100 Td (Page two) Tj ET"),
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            };

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Length; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
                pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
            }

            int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
            pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
            foreach (int offset in offsets)
            {
                pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
            }

            pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());

            static string Stream(string content) => "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";
        }

        private static async Task WaitFor(Func<bool> condition, int rounds = 250)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(condition());
        }

        private sealed class MemoryClassesRoot : IClassesRootReader
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void Set(string key, string value) => Set(key, string.Empty, value);

            public void Set(string key, string name, string value) => _values[key + "|" + name] = value;

            public string GetValue(string key, string name) => _values.TryGetValue(key + "|" + (name ?? string.Empty), out string value) ? value : null;

            public bool KeyExists(string key)
            {
                foreach (string entry in _values.Keys)
                {
                    if (entry.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase) || entry.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private sealed class ThrowingClassesRoot : IClassesRootReader
        {
            public string GetValue(string key, string name) => throw new UnauthorizedAccessException();

            public bool KeyExists(string key) => throw new UnauthorizedAccessException();
        }
    }
}
