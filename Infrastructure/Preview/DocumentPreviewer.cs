// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>How a document (Office, PDF, OpenDocument...) is previewed in the details pane.</summary>
    public enum DocumentPreviewMethod
    {
        /// <summary>Not a document ByteExplorer can preview here: the usual text sniffing / "no preview" applies.</summary>
        None,

        /// <summary>Windows: the system's preview handler for the type (the one File Explorer shows), hosted in the pane.</summary>
        PreviewHandler,

        /// <summary>A PDF's first page rendered to a picture by poppler's pdftoppm (Linux, macOS; any OS that has it).</summary>
        PdfFirstPage,

        /// <summary>The text of a .docx/.odt read from its ZIP package, shown as a scrollable text preview.</summary>
        DocumentText,
    }

    /// <summary>What <see cref="DocumentPreviewer.Plan"/> decided for one file type.</summary>
    public readonly struct DocumentPreviewPlan
    {
        public DocumentPreviewPlan(DocumentPreviewMethod method, Guid handler)
        {
            Method = method;
            Handler = handler;
        }

        public DocumentPreviewMethod Method { get; }

        /// <summary>The preview handler's CLSID (only for <see cref="DocumentPreviewMethod.PreviewHandler"/>).</summary>
        public Guid Handler { get; }

        public static DocumentPreviewPlan None => default;
    }

    /// <summary>
    /// Decides how the details pane previews a document, from its extension. Kept apart from the view model, with every
    /// system dependency injected (the OS, the registry lookup, the pdftoppm renderer), so the choice is testable on any
    /// OS: on Windows the system's preview handlers come first, because they show the real document (Office, PDF through
    /// Edge or Acrobat, fonts, anything a program registered) exactly as File Explorer does; without one (or on Linux and
    /// macOS) a PDF's first page is rendered by pdftoppm when it is installed, and .docx/.odt fall back to their text.
    /// Called for files that are not pictures, video, audio or known text: those keep their own previews.
    /// </summary>
    public sealed class DocumentPreviewer
    {
        /// <summary>The extensions whose text <see cref="OfficeTextExtractor"/> reads.</summary>
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".docm", ".dotx", ".dotm", ".odt", ".ott",
        };

        private static readonly Lazy<DocumentPreviewer> SystemInstance = new Lazy<DocumentPreviewer>(CreateSystem);

        private readonly bool _isWindows;
        private readonly Func<string, Guid?> _findPreviewHandler;

        public DocumentPreviewer(bool isWindows, Func<string, Guid?> findPreviewHandler, PdfPageRenderer pdfRenderer)
        {
            _isWindows = isWindows;
            _findPreviewHandler = findPreviewHandler ?? (_ => null);
            PdfRenderer = pdfRenderer;
        }

        /// <summary>This system's previewer: the registry's preview handlers on Windows, pdftoppm everywhere.</summary>
        public static DocumentPreviewer System => SystemInstance.Value;

        /// <summary>Renders PDF pages; null when PDFs are not rendered.</summary>
        public PdfPageRenderer PdfRenderer { get; }

        /// <summary>Whether <see cref="OfficeTextExtractor"/> reads this extension.</summary>
        public static bool IsTextExtractable(string extension) => !string.IsNullOrEmpty(extension) && TextExtensions.Contains(extension);

        /// <summary>
        /// How to preview a file with this extension (".pdf"). May read the registry and look for pdftoppm on PATH, so it
        /// is called off the UI thread.
        /// </summary>
        public DocumentPreviewPlan Plan(string extension)
        {
            if (string.IsNullOrEmpty(extension) || extension[0] != '.')
            {
                return DocumentPreviewPlan.None;
            }

            if (_isWindows && _findPreviewHandler(extension) is Guid handler && handler != Guid.Empty)
            {
                return new DocumentPreviewPlan(DocumentPreviewMethod.PreviewHandler, handler);
            }

            if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) && PdfRenderer != null && PdfRenderer.IsAvailable)
            {
                return new DocumentPreviewPlan(DocumentPreviewMethod.PdfFirstPage, Guid.Empty);
            }

            return IsTextExtractable(extension)
                ? new DocumentPreviewPlan(DocumentPreviewMethod.DocumentText, Guid.Empty)
                : DocumentPreviewPlan.None;
        }

        /// <summary>Forgets cached lookups (a program installed or removed a preview handler, pdftoppm was installed).</summary>
        public void Refresh()
        {
            if (_isWindows && OperatingSystem.IsWindows())
            {
                PreviewHandlerRegistry.System.ClearCache();
            }

            PdfRenderer?.ResetToolLookup();
        }

        private static DocumentPreviewer CreateSystem()
        {
            Func<string, Guid?> lookup = null;
            if (OperatingSystem.IsWindows())
            {
                lookup = PreviewHandlerRegistry.System.FindHandler;
            }

            return new DocumentPreviewer(OperatingSystem.IsWindows(), lookup, PdfPageRenderer.CreateDefault());
        }
    }
}
