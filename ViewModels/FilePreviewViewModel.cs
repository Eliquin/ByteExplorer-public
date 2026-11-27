// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using ByteExplorer.Core.Native;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Preview;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>What kind of preview content this file/folder currently shows.</summary>
    public enum FilePreviewKind
    {
        None,
        Folder,
        Image,
        Media,
        Text,

        /// <summary>A document shown by a Windows preview handler (see <see cref="FilePreviewViewModel.DocumentPath"/>).</summary>
        Document,
        Unsupported
    }

    /// <summary>One row in a folder's preview listing.</summary>
    public sealed class FilePreviewFolderEntryViewModel
    {
        public FilePreviewFolderEntryViewModel(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; }
        public bool IsDirectory { get; }
    }

    /// <summary>
    /// Drives the details panel's preview area (see FilePreviewPanel, its
    /// view). Deliberately its own view model, exposed as
    /// FileListViewModel.Preview, rather than a pile of extra properties
    /// bolted directly onto FileListViewModel — the preview's own state
    /// (kind, loading, folder rows, image/media path, text content,
    /// truncation) is a self-contained concern with its own lifecycle,
    /// the same reasoning HomeDashboard already follows as a separate
    /// AnalyticsDashboardViewModel.
    ///
    /// Owns no WPF media-player objects itself (LibVLCSharp's LibVLC/
    /// MediaPlayer, or a MediaElement) — those are UI-framework-specific
    /// types that belong in FilePreviewPanel's own code-behind, which
    /// reacts to this view model's MediaPath/IsMediaAudio changes. This
    /// view model only ever decides WHAT to preview and prepares the
    /// data for it; actually rendering it is the view's job.
    /// <para>
    /// What it shows (the subject): the selected item; several selected items (a count and total size, no preview); or,
    /// with nothing selected, the folder the pane shows. The header (icon, name, type, size, date) appears at once; the
    /// preview and the extra details (dimensions, length, folder contents...) load only while the details pane is visible
    /// (<see cref="IsActive"/>) and, during fast selection changes (arrow keys held down), only for the item the
    /// selection settles on. They used to load on every selection change, with the pane hidden too (its default at
    /// startup): every image decoded at up to 3840 pixels, every media file parsed by LibVLC.
    /// </para>
    /// </summary>
    public sealed class FilePreviewViewModel : ViewModelBase, IDisposable
    {
        // Extensions confident enough on their own to skip content-
        // sniffing entirely and go straight to a text preview — mostly
        // source code and structured config formats a developer-facing
        // file explorer's users are likely to actually have on disk.
        private static readonly HashSet<string> KnownTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml",
            ".ini", ".cfg", ".conf", ".yaml", ".yml", ".config", ".cs", ".py",
            ".js", ".jsx", ".ts", ".tsx", ".html", ".htm", ".css", ".scss", ".less",
            ".sql", ".sh", ".bat", ".cmd", ".ps1", ".java", ".c", ".cpp", ".h",
            ".hpp", ".php", ".rb", ".go", ".rs", ".swift", ".kt", ".gradle",
            ".toml", ".env", ".gitignore", ".gitattributes", ".editorconfig",
            ".props", ".targets", ".csproj", ".sln", ".resx", ".manifest", ".reg",
            ".ps1xml", ".psm1", ".psd1"
        };

        private const int FolderPreviewLimit = 50;
        private const int TextSniffByteCount = 8192;
        private const int TextReadByteCount = 65536;
        private const int TextPreviewCharLimit = 4000;

        /// <summary>A document's text (.docx, .odt) is prose, read for its content: more of it than of a text file.</summary>
        private const int DocumentTextCharLimit = 20000;
        private const int MaxPreviewImageSide = 2560;
        private const int MinPreviewImageSide = 384;

        /// <summary>Selection changes closer together than this are a burst: only the one that settles loads.</summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(110);

        private readonly IUiDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly Func<bool> _documentPreviewsEnabled;
        private readonly DocumentPreviewer _documentPreviewer;
        private CancellationTokenSource _loadCts;
        private string _currentPath;
        private bool _currentIsDirectory;
        private string _subjectKey;
        private bool _isActive;
        private bool _loadPending;
        private long _lastSubjectTicks = long.MinValue / 2;
        private int _maxImageSide = 1024;

        /// <param name="dispatcher">The UI thread.</param>
        /// <param name="logger">Null: the global logger.</param>
        /// <param name="documentPreviewsEnabled">
        /// Settings › Folder options › "Show previews of documents", read at every load (null: always on).
        /// </param>
        /// <param name="documentPreviewer">How documents are previewed (null: this system's; tests pass their own).</param>
        public FilePreviewViewModel(IUiDispatcher dispatcher, ILogger logger = null, Func<bool> documentPreviewsEnabled = null,
            DocumentPreviewer documentPreviewer = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = (logger ?? Log.Logger).ForContext<FilePreviewViewModel>();
            _documentPreviewsEnabled = documentPreviewsEnabled ?? (() => true);
            _documentPreviewer = documentPreviewer;

            PreviewAsTextCommand = new RelayCommand(
                _ => { var ignored = ForceTextPreviewAsync(); },
                _ => !string.IsNullOrEmpty(_currentPath) && !IsLoading);

            // Tags edited while an item is shown update its rows. A weak subscription: the static event must not keep a
            // closed window's preview alive.
            var weak = new WeakReference<FilePreviewViewModel>(this);
            EventHandler<ByteExplorer.Core.Tags.TagsChangedEventArgs> handler = null;
            handler = (sender, e) =>
            {
                if (weak.TryGetTarget(out FilePreviewViewModel preview))
                {
                    preview.OnTagsChanged(sender, e);
                }
                else
                {
                    ByteExplorer.Core.Tags.TagStore.AnyChanged -= handler;
                }
            };
            ByteExplorer.Core.Tags.TagStore.AnyChanged += handler;
        }

        private void OnTagsChanged(object sender, ByteExplorer.Core.Tags.TagsChangedEventArgs e)
        {
            string path = _currentPath;
            if (path == null || !ReferenceEquals(sender, ByteExplorer.Core.Tags.TagStore.Default) ||
                e.Paths != null && !System.Linq.Enumerable.Any(e.Paths, p => ByteExplorer.Core.PathUtil.AreSame(p, path)))
            {
                return;
            }

            _dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(path, _currentPath, StringComparison.Ordinal))
                {
                    return;
                }

                int oldCount = _basicRows.Count;
                _basicRows = WithTagRows(_basicRows, path);
                var all = new List<FileDetailRow>(_basicRows);
                all.AddRange(System.Linq.Enumerable.Skip(Details, Math.Min(oldCount, Details.Count)));
                Details.ReplaceAll(all);
            }).Forget("FilePreviewViewModel.OnTagsChanged");
        }

        /// <summary>The basic rows with the item's tags and comment at the end (replacing any shown before).</summary>
        private static List<FileDetailRow> WithTagRows(List<FileDetailRow> rows, string path)
        {
            var result = rows.FindAll(r => r.Label != TagsRowLabel && r.Label != CommentRowLabel);
            ByteExplorer.Core.Tags.TagStore store = ByteExplorer.Core.Tags.TagStore.Default;
            IReadOnlyList<ByteExplorer.Core.Tags.TagDefinition> tags = store.GetTags(path);
            if (tags.Count > 0)
            {
                result.Add(new FileDetailRow(TagsRowLabel, string.Join(", ", System.Linq.Enumerable.Select(tags, t => store.FullName(t).Replace("/", " › ")))));
            }

            string comment = store.GetComment(path);
            if (!string.IsNullOrEmpty(comment))
            {
                result.Add(new FileDetailRow(CommentRowLabel, comment));
            }

            return result;
        }

        private const string TagsRowLabel = "Tags";
        private const string CommentRowLabel = "Comment";

        private FilePreviewKind _kind = FilePreviewKind.None;
        public FilePreviewKind Kind
        {
            get => _kind;
            private set => SetProperty(ref _kind, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    (PreviewAsTextCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public BulkObservableCollection<FilePreviewFolderEntryViewModel> FolderEntries { get; } = new BulkObservableCollection<FilePreviewFolderEntryViewModel>();

        /// <summary>
        /// Extra facts about the selected item for the details panel (dimensions, length, date taken, folder contents…),
        /// read in the background with the preview; see <see cref="FileMetadataReader"/>.
        /// </summary>
        /// <summary>The subject's facts: type, size and date at once, then what FileMetadataReader finds.</summary>
        public BulkObservableCollection<FileDetailRow> Details { get; } = new BulkObservableCollection<FileDetailRow>();

        private bool _folderHasMore;
        public bool FolderHasMore
        {
            get => _folderHasMore;
            private set => SetProperty(ref _folderHasMore, value);
        }

        private bool _folderIsEmpty;
        public bool FolderIsEmpty
        {
            get => _folderIsEmpty;
            private set => SetProperty(ref _folderIsEmpty, value);
        }

        private Bitmap _imageSource;
        /// <summary>
        /// A fully decoded bitmap, not a path: decoded on a background
        /// thread (see LoadImagePreviewAsync) rather than by the Image
        /// control on the UI thread, where decoding a large photo would
        /// stall the window. A replaced bitmap is disposed once the UI
        /// has let go of it, so browsing photos does not keep tens of
        /// megabytes of native pixel memory waiting for the GC.
        /// </summary>
        public Bitmap ImageSource
        {
            get => _imageSource;
            private set
            {
                Bitmap previous = _imageSource;
                if (SetProperty(ref _imageSource, value) && previous != null)
                {
                    // After the render pass that stops drawing it.
                    ByteExplorer.Infrastructure.UiThread.Post(previous.Dispose, Avalonia.Threading.DispatcherPriority.Background);
                }
            }
        }

        private string _mediaPath;
        /// <summary>
        /// Set only once loading actually finishes, deliberately — the
        /// view's code-behind treats a change to this property as "load
        /// and prepare this new media source," so it must never be set
        /// to a path that hasn't been confirmed to exist and match a
        /// known media extension.
        /// </summary>
        public string MediaPath
        {
            get => _mediaPath;
            private set => SetProperty(ref _mediaPath, value);
        }

        private bool _isMediaAudio;
        public bool IsMediaAudio
        {
            get => _isMediaAudio;
            private set => SetProperty(ref _isMediaAudio, value);
        }

        private string _documentPath;
        /// <summary>
        /// The document a Windows preview handler shows (Kind Document), set together with <see cref="DocumentHandler"/>
        /// once the file is confirmed; null otherwise. Like <see cref="MediaPath"/>, the view's code-behind treats a change
        /// as "load this document into the native host" (FilePreviewPanel), so it never holds an unconfirmed path.
        /// </summary>
        public string DocumentPath
        {
            get => _documentPath;
            private set => SetProperty(ref _documentPath, value);
        }

        /// <summary>The CLSID of the preview handler for <see cref="DocumentPath"/> (set before it).</summary>
        public Guid DocumentHandler { get; private set; }

        private bool _isDocumentText;
        /// <summary>
        /// The text preview is a document's text (.docx, .odt), not a text file: shown in the UI font, with a note that the
        /// formatting is left out.
        /// </summary>
        public bool IsDocumentText
        {
            get => _isDocumentText;
            private set => SetProperty(ref _isDocumentText, value);
        }

        private string _textContent;
        public string TextContent
        {
            get => _textContent;
            private set => SetProperty(ref _textContent, value);
        }

        private bool _isTextTruncated;
        public bool IsTextTruncated
        {
            get => _isTextTruncated;
            private set => SetProperty(ref _isTextTruncated, value);
        }

        private bool _canOfferTextPreview;
        /// <summary>
        /// Drives the "Preview as text" button's visibility. True only
        /// when Kind is Unsupported for a plain file (not a folder,
        /// image, or media file — those already have their own real
        /// preview) — addresses files with an unrecognized or
        /// misleading extension that are actually plain text
        /// underneath, forcing a text read regardless of what content-
        /// sniffing decided.
        /// </summary>
        public bool CanOfferTextPreview
        {
            get => _canOfferTextPreview;
            private set => SetProperty(ref _canOfferTextPreview, value);
        }

        public ICommand PreviewAsTextCommand { get; }

        // ------------------------------------------------------------------ the subject

        private string _title;
        private ByteExplorer.Icons.Symbol _icon = ByteExplorer.Icons.Symbol.Document;
        private Avalonia.Media.IBrush _iconBrush;
        private bool _hasSubject;
        private bool _isMultiSelection;
        private bool _isFolderSubject;

        /// <summary>The header's title: the item's name, "3 items selected", or the folder's name.</summary>
        public string Title
        {
            get => _title;
            private set => SetProperty(ref _title, value);
        }

        public ByteExplorer.Icons.Symbol Icon
        {
            get => _icon;
            private set => SetProperty(ref _icon, value);
        }

        public Avalonia.Media.IBrush IconBrush
        {
            get => _iconBrush;
            private set => SetProperty(ref _iconBrush, value);
        }

        /// <summary>Something is shown (an item, a selection or the pane's folder); false shows the "select something" hint.</summary>
        public bool HasSubject
        {
            get => _hasSubject;
            private set => SetProperty(ref _hasSubject, value);
        }

        public bool IsMultiSelection
        {
            get => _isMultiSelection;
            private set => SetProperty(ref _isMultiSelection, value);
        }

        /// <summary>Nothing is selected: the header describes the folder the pane shows.</summary>
        public bool IsFolderSubject
        {
            get => _isFolderSubject;
            private set => SetProperty(ref _isFolderSubject, value);
        }

        /// <summary>
        /// Whether the details pane is visible (set by MainViewModel). While false nothing heavy loads; becoming true
        /// loads the current subject.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (SetProperty(ref _isActive, value))
                {
                    if (value && _loadPending)
                    {
                        StartLoad(settle: false);
                    }
                    else if (!value)
                    {
                        // Hidden: stop work in flight and let go of what the preview holds (a decoded bitmap, a playing
                        // video, a text buffer); what it showed is loaded again when the pane opens.
                        CancelLoad();
                        ClearPreviewState();
                        if (_currentPath != null)
                        {
                            _loadPending = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The largest side, in device pixels, an image preview is decoded at: the view sets it from the panel's width
        /// (a 300-pixel panel never needs a 4K bitmap of 33 MB). Clamped to 384-2560.
        /// </summary>
        public int MaxImageSide
        {
            get => _maxImageSide;
            set => _maxImageSide = Math.Clamp(value, MinPreviewImageSide, MaxPreviewImageSide);
        }

        /// <summary>One selected item.</summary>
        public void ShowItem(FileListItemViewModel item)
        {
            if (item == null)
            {
                ShowNothing();
                return;
            }

            string key = "item:" + item.FullPath;
            if (key == _subjectKey)
            {
                return;
            }

            _subjectKey = key;
            SetHeader(item.Name, item.IconGlyph, item.IconColorBrush, multi: false, folder: false);
            var rows = new List<FileDetailRow> { new FileDetailRow("Type", item.TypeDisplay) };
            if (!item.IsDirectory)
            {
                rows.Add(new FileDetailRow("Size", item.SizeDisplay));
            }
            else if (FolderSizeRow(item.FullPath) is FileDetailRow folderSize)
            {
                rows.Add(folderSize); // counted with Calculate folder sizes
            }

            if (!string.IsNullOrEmpty(item.DateModifiedDisplay))
            {
                rows.Add(new FileDetailRow("Modified", item.DateModifiedDisplay));
            }

            BeginSubject(item.FullPath, item.IsDirectory, rows);
        }

        /// <summary>A folder's counted size as a details row ("1.2 GB (1,234 files, 56 folders)"), or null when not counted.</summary>
        private static FileDetailRow FolderSizeRow(string folder) =>
            Core.Services.FolderSizes.TryGet(folder, out Core.Services.FolderSizeInfo info) && info.IsComplete
                ? new FileDetailRow("Size", $"{Core.Services.FolderSizes.Format(info)} ({info.Files:N0} files, {info.Folders:N0} folders)")
                : null;

        /// <summary>A folder's size was counted: shown at once if that folder is the one in the pane.</summary>
        public void UpdateFolderSize(string folder)
        {
            if (!_currentIsDirectory || _currentPath == null || !string.Equals(_currentPath, folder, Core.PathUtil.Comparison) ||
                FolderSizeRow(folder) is not FileDetailRow row)
            {
                return;
            }

            var rows = new List<FileDetailRow>(Details);
            int existing = rows.FindIndex(r => r.Label == "Size");
            if (existing >= 0)
            {
                rows[existing] = row;
            }
            else
            {
                rows.Insert(Math.Min(1, rows.Count), row); // after Type
            }

            Details.ReplaceAll(rows);
        }

        /// <summary>Several selected items: how many, of which kinds, and their total size (no preview).</summary>
        public void ShowSelection(IReadOnlyCollection<FileListItemViewModel> items)
        {
            int files = 0;
            int folders = 0;
            long bytes = 0;
            foreach (FileListItemViewModel item in items)
            {
                if (item.IsDirectory)
                {
                    folders++;
                }
                else
                {
                    files++;
                    bytes += item.SizeBytes;
                }
            }

            string key = $"multi:{items.Count}:{files}:{bytes}";
            if (key == _subjectKey)
            {
                return;
            }

            _subjectKey = key;
            SetHeader($"{items.Count:N0} items selected", ByteExplorer.Icons.Symbol.SquareMultiple,
                Infrastructure.Converters.HexToBrushConverter.BrushFor("#5B9BD5"), multi: true, folder: false);
            string Count(int n, string one, string many) => n.ToString("N0") + " " + (n == 1 ? one : many);
            var rows = new List<FileDetailRow> { new FileDetailRow("Contains", Count(folders, "folder", "folders") + ", " + Count(files, "file", "files")) };
            if (bytes > 0)
            {
                rows.Add(new FileDetailRow("Total size", FormatBytes(bytes) + (folders > 0 ? " (files only)" : string.Empty)));
            }

            CancelLoad();
            _currentPath = null;
            _loadPending = false;
            ClearPreviewState();
            Details.ReplaceAll(rows);
        }

        /// <summary>Nothing selected: the folder the pane shows (its contents preview and details).</summary>
        public void ShowFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                ShowNothing();
                return;
            }

            string key = "folder:" + path;
            if (key == _subjectKey)
            {
                return;
            }

            _subjectKey = key;
            SetHeader(ByteExplorer.Core.PathUtil.GetName(path), FileIconGlyphs.For(path, isDirectory: true),
                Infrastructure.Converters.HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(path, isDirectory: true)), multi: false, folder: true);
            BeginSubject(path, isDirectory: true, new List<FileDetailRow> { new FileDetailRow("Type", "File folder (open)") });
        }

        /// <summary>Nothing to show (Home, This PC).</summary>
        public void ShowNothing()
        {
            if (_subjectKey == null && !HasSubject)
            {
                return;
            }

            _subjectKey = null;
            _currentPath = null;
            _loadPending = false;
            CancelLoad();
            HasSubject = false;
            IsMultiSelection = false;
            IsFolderSubject = false;
            Title = null;
            ResetToEmpty();
        }

        /// <summary>Compatibility: shows one path (an item of the list, or the folder when <paramref name="fullPath"/> is null).</summary>
        public void LoadFor(string fullPath, bool isDirectory)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                ShowNothing();
                return;
            }

            _subjectKey = "path:" + fullPath;
            SetHeader(ByteExplorer.Core.PathUtil.GetName(fullPath), FileIconGlyphs.For(fullPath, isDirectory),
                Infrastructure.Converters.HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(fullPath, isDirectory)), multi: false, folder: false);
            BeginSubject(fullPath, isDirectory, new List<FileDetailRow>());
        }

        private void SetHeader(string title, ByteExplorer.Icons.Symbol icon, Avalonia.Media.IBrush brush, bool multi, bool folder)
        {
            Title = title;
            Icon = icon;
            IconBrush = brush;
            IsMultiSelection = multi;
            IsFolderSubject = folder;
            HasSubject = true;
        }

        /// <summary>Shows the basic rows now and loads the rest (now, after the selection settles, or when the pane opens).</summary>
        private void BeginSubject(string fullPath, bool isDirectory, List<FileDetailRow> basicRows)
        {
            CancelLoad();
            _currentPath = fullPath;
            _currentIsDirectory = isDirectory;
            _basicRows = WithTagRows(basicRows, fullPath);
            ClearPreviewState();
            Details.ReplaceAll(_basicRows);
            long now = Environment.TickCount64;
            bool burst = now - _lastSubjectTicks < SettleDelay.TotalMilliseconds;
            _lastSubjectTicks = now;
            if (!_isActive)
            {
                _loadPending = true;
                return;
            }

            StartLoad(settle: burst);
        }

        private List<FileDetailRow> _basicRows = new List<FileDetailRow>();

        private void StartLoad(bool settle)
        {
            _loadPending = false;
            string path = _currentPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            CancelLoad();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            Kind = FilePreviewKind.None;
            IsLoading = true;
            RunLoadAsync(path, _currentIsDirectory, settle, cts.Token).Forget("preview");
        }

        private async Task RunLoadAsync(string path, bool isDirectory, bool settle, CancellationToken token)
        {
            try
            {
                if (settle)
                {
                    await Task.Delay(SettleDelay, token).ConfigureAwait(false);
                }

                if (Core.Platform.Phones.Phones.IsPhonePath(path))
                {
                    // A preview would download the whole file from the phone (a photo, a video) at every selection:
                    // the pane shows the listing's details (name, type, size, date) only.
                    await SetUnsupportedAsync(token, offerTextButton: false).ConfigureAwait(false);
                    return;
                }

                Task preview = LoadCoreAsync(path, isDirectory, token);
                Task details = LoadDetailsAsync(path, isDirectory, token);
                await Task.WhenAll(preview, details).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A newer subject, or the pane closed.
            }
        }

        private void CancelLoad()
        {
            CancellationTokenSource cts = _loadCts;
            _loadCts = null;
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        private void ClearPreviewState()
        {
            Kind = FilePreviewKind.None;
            IsLoading = false;
            FolderEntries.Clear();
            FolderHasMore = false;
            FolderIsEmpty = false;
            ImageSource = null;
            MediaPath = null;
            DocumentPath = null;
            TextContent = null;
            IsTextTruncated = false;
            IsDocumentText = false;
            CanOfferTextPreview = false;
        }

        /// <summary>
        /// Loads the current subject's preview again (Settings › "Show previews of documents" changed, or a preview handler
        /// was installed): nothing when the pane is hidden or shows no single item.
        /// </summary>
        public void Reload()
        {
            (_documentPreviewer ?? DocumentPreviewer.System).Refresh();
            if (_isActive && !string.IsNullOrEmpty(_currentPath) && !IsMultiSelection)
            {
                StartLoad(settle: false);
            }
        }

        /// <summary>
        /// The view could not show <paramref name="path"/> with its preview handler (the handler failed to start, crashed or
        /// hung): the pane shows "no preview" instead of an empty area.
        /// </summary>
        public void DocumentPreviewFailed(string path)
        {
            if (Kind != FilePreviewKind.Document || !string.Equals(path, DocumentPath, StringComparison.Ordinal))
            {
                return;
            }

            DocumentPath = null;
            Kind = FilePreviewKind.Unsupported;
            CanOfferTextPreview = false;
            IsLoading = false;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} bytes" : $"{size.ToString("N1", System.Globalization.CultureInfo.CurrentCulture)} {units[unit]}";
        }

        private async Task LoadDetailsAsync(string fullPath, bool isDirectory, CancellationToken token)
        {
            try
            {
                IReadOnlyList<FileDetailRow> rows = await FileMetadataReader.ReadAsync(fullPath, isDirectory, token).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    var all = new List<FileDetailRow>(_basicRows.Count + rows.Count);
                    all.AddRange(_basicRows);
                    all.AddRange(rows);
                    Details.ReplaceAll(all); // one change, not one per row
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The selection moved on.
            }
        }

        private void ResetToEmpty()
        {
            CancelLoad();
            ClearPreviewState();
            Details.Clear();
        }

        private async Task LoadCoreAsync(string fullPath, bool isDirectory, CancellationToken token)
        {
            try
            {
                if (isDirectory)
                {
                    await LoadFolderPreviewAsync(fullPath, token).ConfigureAwait(false);
                    return;
                }

                string extension;
                try
                {
                    extension = Path.GetExtension(fullPath);
                }
                catch (ArgumentException)
                {
                    extension = null;
                }

                if (FileCategoryClassifier.Matches(fullPath, false, FileCategoryFilter.Images))
                {
                    await LoadImagePreviewAsync(fullPath, token).ConfigureAwait(false);
                    return;
                }

                if (FileCategoryClassifier.Matches(fullPath, false, FileCategoryFilter.Videos))
                {
                    await SetMediaPreviewAsync(fullPath, isAudio: false, token).ConfigureAwait(false);
                    return;
                }

                if (FileCategoryClassifier.Matches(fullPath, false, FileCategoryFilter.Music))
                {
                    await SetMediaPreviewAsync(fullPath, isAudio: true, token).ConfigureAwait(false);
                    return;
                }

                bool knownText = !string.IsNullOrEmpty(extension) && KnownTextExtensions.Contains(extension);
                if (!knownText && !string.IsNullOrEmpty(extension) && _documentPreviewsEnabled() &&
                    await TryLoadDocumentPreviewAsync(fullPath, extension, token).ConfigureAwait(false))
                {
                    return;
                }

                await LoadGenericFilePreviewAsync(fullPath, forceText: knownText, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected whenever the selection moves on before this
                // load finishes — not an error.
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Preview load failed for {Path}", fullPath);
                await SetUnsupportedAsync(token, offerTextButton: !isDirectory).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// NativeFileEnumerator.Enumerate returns a lazy IEnumerable
        /// backed by native FindFirstFile/FindNextFile — the foreach
        /// below breaks out (stopping native enumeration itself) the
        /// moment it has FolderPreviewLimit real entries plus one more
        /// to confirm there IS a next one, so a folder with a million
        /// files costs the same here as one with 50: this never pays to
        /// enumerate the whole folder just to preview it. That one
        /// extra entry is only ever used to set hasMore — it's never
        /// added to the displayed list.
        /// </summary>
        private async Task LoadFolderPreviewAsync(string path, CancellationToken token)
        {
            (List<FilePreviewFolderEntryViewModel> entries, bool hasMore) = await Task.Run(() =>
            {
                var result = new List<FilePreviewFolderEntryViewModel>(FolderPreviewLimit);
                int count = 0;
                bool foundMore = false;

                foreach (var entry in NativeFileEnumerator.Enumerate(path, token))
                {
                    token.ThrowIfCancellationRequested();

                    if (entry.IsHidden || entry.IsSystem)
                    {
                        continue;
                    }

                    if (count >= FolderPreviewLimit)
                    {
                        foundMore = true;
                        break;
                    }

                    result.Add(new FilePreviewFolderEntryViewModel(entry.Name, entry.IsDirectory));
                    count++;
                }

                return (result, foundMore);
            }, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                FolderEntries.ReplaceAll(entries);

                FolderHasMore = hasMore;
                FolderIsEmpty = entries.Count == 0;
                Kind = FilePreviewKind.Folder;
                IsLoading = false;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Decodes the image fully off the UI thread (an Avalonia Bitmap
        /// is immutable and may be created on any thread).
        /// Decoded at no more than PreviewImageMaxSide pixels on the long
        /// side: sharp in any panel width up to a 4K screen, without the
        /// ~190 MB a full decode of a 48 MP photo used to take.
        /// </summary>
        private async Task LoadImagePreviewAsync(string path, CancellationToken token)
        {
            int maxSide = _maxImageSide;
            Bitmap image = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    // Bounded decode (enough for a 4K screen) and EXIF orientation: see ImageLoader.
                    return ImageLoader.Load(path, maxSide);
                }
                catch (Exception)
                {
                    // Corrupt file, or a format Skia doesn't decode (RAW,
                    // PSD, SVG) — falls through
                    // to "no preview available" rather than crashing or
                    // showing a broken-image glyph.
                    return null;
                }
            }, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                image?.Dispose();
                token.ThrowIfCancellationRequested();
            }

            if (image == null)
            {
                await SetUnsupportedAsync(token, offerTextButton: false).ConfigureAwait(false);
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    image.Dispose();
                    return;
                }

                ImageSource = image;
                Kind = FilePreviewKind.Image;
                IsLoading = false;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Office, PDF and OpenDocument files (and, on Windows, any type with a preview handler): see
        /// <see cref="DocumentPreviewer"/> for which method applies. False when none does, or the chosen one could not
        /// read the file, so the caller falls back to the text sniffing and "no preview" as for any other file.
        /// </summary>
        private async Task<bool> TryLoadDocumentPreviewAsync(string path, string extension, CancellationToken token)
        {
            DocumentPreviewer previewer = _documentPreviewer ?? DocumentPreviewer.System;
            int maxSide = _maxImageSide;

            // Registry reads, a PATH lookup, a process, a ZIP: all off the UI thread.
            return await Task.Run(async () =>
            {
                DocumentPreviewPlan plan = previewer.Plan(extension);
                switch (plan.Method)
                {
                    case DocumentPreviewMethod.PreviewHandler:
                        if (!File.Exists(path))
                        {
                            return false;
                        }

                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                return;
                            }

                            DocumentHandler = plan.Handler;
                            DocumentPath = path;
                            Kind = FilePreviewKind.Document;
                            IsLoading = false;
                        }).ConfigureAwait(false);
                        return true;

                    case DocumentPreviewMethod.PdfFirstPage:
                        string png = await previewer.PdfRenderer.RenderFirstPageAsync(path, maxSide, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        Bitmap page = null;
                        try
                        {
                            page = png == null ? null : ImageLoader.Load(png, maxSide);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "The rendered PDF page {Png} could not be decoded", png);
                        }

                        if (page == null)
                        {
                            return false;
                        }

                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                page.Dispose();
                                return;
                            }

                            ImageSource = page;
                            Kind = FilePreviewKind.Image;
                            IsLoading = false;
                        }).ConfigureAwait(false);
                        return true;

                    case DocumentPreviewMethod.DocumentText:
                        DocumentText text = OfficeTextExtractor.Extract(path, DocumentTextCharLimit, token);
                        token.ThrowIfCancellationRequested();
                        if (text == null)
                        {
                            return false;
                        }

                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                return;
                            }

                            TextContent = text.Text.Length == 0 ? "(This document has no text.)" : text.Text;
                            IsTextTruncated = text.Truncated;
                            IsDocumentText = true;
                            CanOfferTextPreview = false;
                            Kind = FilePreviewKind.Text;
                            IsLoading = false;
                        }).ConfigureAwait(false);
                        return true;

                    default:
                        return false;
                }
            }, token).ConfigureAwait(false);
        }

        private async Task SetMediaPreviewAsync(string path, bool isAudio, CancellationToken token)
        {
            bool exists = await Task.Run(() => File.Exists(path), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (!exists)
            {
                await SetUnsupportedAsync(token, offerTextButton: false).ConfigureAwait(false);
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                IsMediaAudio = isAudio;
                MediaPath = path;
                Kind = FilePreviewKind.Media;
                IsLoading = false;
            }).ConfigureAwait(false);
        }

        private async Task LoadGenericFilePreviewAsync(string path, bool forceText, CancellationToken token)
        {
            (bool looksLikeText, string text, bool truncated) = await Task.Run(
                () => ReadTextPreview(path, forceText, token), token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (looksLikeText)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    TextContent = text;
                    IsTextTruncated = truncated;
                    IsDocumentText = false;
                    Kind = FilePreviewKind.Text;
                    CanOfferTextPreview = false;
                    IsLoading = false;
                }).ConfigureAwait(false);
            }
            else
            {
                await SetUnsupportedAsync(token, offerTextButton: true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// User pressed "Preview as text" on a file content-sniffing
        /// decided was not text — forces the read regardless, exactly
        /// the escape hatch the file-with-a-misleading-extension case
        /// needs. Re-runs the same cancellation dance as a normal
        /// selection change, since the user could in principle click
        /// this and then immediately select something else.
        /// </summary>
        private async Task ForceTextPreviewAsync()
        {
            string path = _currentPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            IsLoading = true;

            try
            {
                await LoadGenericFilePreviewAsync(path, forceText: true, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Forced text preview failed for {Path}", path);
                await SetUnsupportedAsync(cts.Token, offerTextButton: true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads up to TextReadByteCount raw bytes and decides whether
        /// the content looks like text. forceText skips the sniff
        /// entirely (known extension, or the user's explicit "Preview
        /// as text" override) and just reads it. Runs entirely off the
        /// UI thread (called via Task.Run) — file I/O and the sniff
        /// scan over up to 64 KB never touch the UI thread.
        /// </summary>
        private static (bool looksLikeText, string text, bool truncated) ReadTextPreview(string path, bool forceText, CancellationToken token)
        {
            byte[] buffer;
            int bytesRead;
            long fileLength;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fileLength = stream.Length;
                    buffer = new byte[Math.Min(TextReadByteCount, fileLength)];
                    bytesRead = 0;
                    while (bytesRead < buffer.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        int read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                        if (read == 0)
                        {
                            break;
                        }
                        bytesRead += read;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Locked, inaccessible, path too long, etc. — treat as
                // "can't preview," not a crash.
                return (false, null, false);
            }

            if (bytesRead == 0)
            {
                // A genuinely empty file is still valid text — nothing
                // to sniff against, but nothing contradicts it either.
                return (true, string.Empty, false);
            }

            if (!forceText)
            {
                int sniffLength = Math.Min(bytesRead, TextSniffByteCount);
                if (!LooksLikeText(buffer, sniffLength))
                {
                    return (false, null, false);
                }
            }

            string text;
            try
            {
                // StreamReader over the already-read buffer's bytes,
                // not the file again — detectEncodingFromByteOrderMarks
                // handles a UTF-8/UTF-16 BOM correctly; anything without
                // one is decoded as UTF-8, which also correctly decodes
                // plain ASCII (a strict superset), covering the
                // overwhelming majority of real text files.
                using (var memoryStream = new MemoryStream(buffer, 0, bytesRead))
                using (var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return (false, null, false);
            }

            bool truncated = fileLength > bytesRead || text.Length > TextPreviewCharLimit;
            if (text.Length > TextPreviewCharLimit)
            {
                text = text.Substring(0, TextPreviewCharLimit);
            }

            return (true, text, truncated);
        }

        /// <summary>
        /// Heuristic, not a guarantee: no NUL byte anywhere in the
        /// sniffed span (the single strongest and cheapest binary
        /// indicator — genuine text essentially never contains one),
        /// and at least 95% of bytes are printable ASCII, a common
        /// control character (tab/CR/LF), or a UTF-8 continuation/lead
        /// byte (0x80-0xFF, since real-world text is very often UTF-8
        /// with non-ASCII content). Good enough to separate "obviously
        /// binary" from "plausibly text" without pulling in a real
        /// encoding-detection library for what is, after all, just a
        /// preview.
        /// </summary>
        private static bool LooksLikeText(byte[] buffer, int length)
        {
            if (length == 0)
            {
                return true;
            }

            int suspicious = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[i];

                if (b == 0x00)
                {
                    return false;
                }

                bool printableAscii = b >= 0x20 && b < 0x7F;
                bool commonControl = b == 0x09 || b == 0x0A || b == 0x0D; // tab, LF, CR
                bool utf8Range = b >= 0x80;

                if (!printableAscii && !commonControl && !utf8Range)
                {
                    suspicious++;
                }
            }

            double suspiciousRatio = (double)suspicious / length;
            return suspiciousRatio < 0.05;
        }

        private Task SetUnsupportedAsync(CancellationToken token, bool offerTextButton)
        {
            return _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Kind = FilePreviewKind.Unsupported;
                CanOfferTextPreview = offerTextButton;
                IsLoading = false;
            });
        }

        public void Dispose()
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }
    }
}
