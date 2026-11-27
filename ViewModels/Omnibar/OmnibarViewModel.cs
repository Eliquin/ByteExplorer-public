// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ByteExplorer.Core;
using ByteExplorer.Core.Analytics;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Omnibar;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels.Omnibar
{
    /// <summary>
    /// The omnibar: the address bar of the active pane, turned into one place to go anywhere and do anything.
    /// <para>
    /// <b>Showing where you are</b>: breadcrumbs as people read locations (<see cref="BreadcrumbBuilder"/>: "Home",
    /// volume labels, "photos on nas", archives), each with a dropdown of its subfolders; a thin animated line while the
    /// folder loads (only after a moment, so quick folders don't flicker); a pin for Quick access.
    /// </para>
    /// <para>
    /// <b>Typing</b>: suggestions as you type (<see cref="OmnibarSuggestionEngine"/>) — path completion with an inline
    /// faded completion (Tab accepts), recent folders, places, pinned folders, the current folder's items, commands ("&gt;"),
    /// search ("?"), web and network addresses. Each keystroke cancels the previous query; a short pause (30 ms) merges
    /// fast typing; folder listings come from the shared index cache, so typing inside one folder does no I/O.
    /// </para>
    /// <para>
    /// <b>Enter</b> runs the highlighted suggestion when one was picked with the arrow keys; otherwise it resolves the
    /// text: a folder or ZIP opens, a file opens its folder with the file selected, a network or web address is opened,
    /// words go to the top suggestion (search, at worst). Text that leads nowhere is refused with a shake.
    /// </para>
    /// All members are used on the UI thread.
    /// </summary>
    public sealed class OmnibarViewModel : ViewModelBase, IOmnibarContext, IDisposable
    {
        private const int DebounceMs = 30;
        private const int LoadingIndicatorDelayMs = 250;
        private const int MaxSessionHistory = 40;
        private static readonly TimeSpan ExistenceTimeout = TimeSpan.FromSeconds(3);

        private readonly MainViewModel _main;
        private readonly IDirectoryIndexService _index;
        private readonly IUserAnalyticsService _analytics;
        private readonly ISettingsService _settings;
        private readonly OmnibarSuggestionEngine _engine;
        private readonly List<string> _sessionHistory = new List<string>();
        private readonly List<OmnibarCommand> _commands = new List<OmnibarCommand>();
        private readonly RelayCommand _togglePinCommand;
        private readonly RelayCommand _copyPathCommand;

        private FileListViewModel _pane;
        private IReadOnlyList<Crumb> _crumbs = Array.Empty<Crumb>();
        private IReadOnlyList<CrumbVolume> _volumes = Array.Empty<CrumbVolume>();
        private IReadOnlyList<OmnibarPlace> _places = Array.Empty<OmnibarPlace>();
        private IReadOnlyList<string> _recent = Array.Empty<string>();
        private CancellationTokenSource _queryCts;
        private int _queryVersion;
        private int _loadingVersion;
        private bool _isEditing;
        private string _text = string.Empty;
        private int _selectedIndex = -1;
        private string _ghost;
        private string _hintText;
        private bool _isLoadingVisible;
        private bool _isPinned;
        private bool _suppressQuery;

        public OmnibarViewModel(MainViewModel main, IDirectoryIndexService index, IUserAnalyticsService analytics, ISettingsService settings)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));
            _index = index;
            _analytics = analytics;
            _settings = settings;
            _engine = new OmnibarSuggestionEngine(this);
            _togglePinCommand = new RelayCommand(_ => _main.TogglePinCommand.Execute(null), _ => CanPin);
            _copyPathCommand = new RelayCommand(_ => CopyPath(), _ => !string.IsNullOrEmpty(_pane?.CurrentPath));

            _main.PropertyChanged += OnMainPropertyChanged;
            _main.Pins.Items.CollectionChanged += OnPinsChanged;
            BuildCommands();
            AttachPane(_main.FileListViewModel);
        }

        // ================================================================== state

        /// <summary>The breadcrumb segments of the active pane's location.</summary>
        public IReadOnlyList<Crumb> Crumbs
        {
            get => _crumbs;
            private set => SetProperty(ref _crumbs, value);
        }

        /// <summary>The icon at the start of the bar: what kind of place this is.</summary>
        public Symbol LocationSymbol => _crumbs.Count == 0
            ? Symbol.Folder
            : _crumbs[0].Kind switch
            {
                CrumbKind.Home => Symbol.Home,
                CrumbKind.Volume => Symbol.UsbStick,
                CrumbKind.Phone => Symbol.Phone,
                CrumbKind.Network => Symbol.GlobeDesktop,
                CrumbKind.Place => _pane?.IsThisPcView == true ? Symbol.Desktop : _pane?.IsTrashView == true ? Symbol.Delete : Symbol.Home,
                CrumbKind.Lens => Symbol.Sparkle,
                CrumbKind.Root => Symbol.HardDrive,
                _ => Symbol.Folder
            };

        public bool IsEditing
        {
            get => _isEditing;
            private set
            {
                if (SetProperty(ref _isEditing, value))
                {
                    RaisePropertyChanged(nameof(IsPopupOpen));
                }
            }
        }

        /// <summary>The text being edited. Every change starts a new (debounced, cancellable) query.</summary>
        public string Text
        {
            get => _text;
            set
            {
                if (SetProperty(ref _text, value ?? string.Empty))
                {
                    HintText = null;
                    if (_isEditing && !_suppressQuery)
                    {
                        QueryAsync(_text, debounce: true).Forget("omnibar query");
                    }
                }
            }
        }

        public BulkObservableCollection<OmnibarSuggestion> Suggestions { get; } = new BulkObservableCollection<OmnibarSuggestion>();

        /// <summary>The row picked with the arrow keys; -1 when none (Enter then resolves the typed text).</summary>
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                int clamped = value < -1 || value >= Suggestions.Count ? -1 : value;
                if (SetProperty(ref _selectedIndex, clamped))
                {
                    UpdateGhost();
                }
            }
        }

        /// <summary>Suggestions, or a hint about why Enter did nothing, are showing under the bar.</summary>
        public bool IsPopupOpen => _isEditing && (Suggestions.Count > 0 || HasHint);

        /// <summary>The faded completion after the caret (the top suggestion's rest); null for none.</summary>
        public string Ghost
        {
            get => _ghost;
            private set => SetProperty(ref _ghost, value);
        }

        /// <summary>Why Enter did nothing ("There's nothing at …"); shown under the bar until the text changes.</summary>
        public string HintText
        {
            get => _hintText;
            private set
            {
                if (SetProperty(ref _hintText, value))
                {
                    RaisePropertyChanged(nameof(HasHint));
                    RaisePropertyChanged(nameof(IsPopupOpen));
                }
            }
        }

        public bool HasHint => !string.IsNullOrEmpty(_hintText);

        /// <summary>The loading line: shown once a folder has taken longer than a moment.</summary>
        public bool IsLoadingVisible
        {
            get => _isLoadingVisible;
            private set => SetProperty(ref _isLoadingVisible, value);
        }

        public bool IsPinned
        {
            get => _isPinned;
            private set => SetProperty(ref _isPinned, value);
        }

        public bool CanPin => !string.IsNullOrEmpty(_pane?.CurrentPath) && !_main.IsLensActive;

        /// <summary>The full location, for the tooltip and "copy path".</summary>
        public string CurrentPathText => _pane?.CurrentPath ??
            (_pane?.IsThisPcView == true ? "This PC" : _pane?.IsTrashView == true ? Core.Platform.Trash.DisplayName : "Home");

        public ICommand TogglePinCommand => _togglePinCommand;

        public ICommand CopyPathCommand => _copyPathCommand;

        /// <summary>Text the person typed led nowhere: the view shakes the bar.</summary>
        public event EventHandler InputRejected;

        /// <summary>Editing ended; true when it ended by going somewhere (the view returns focus to the file list).</summary>
        public event EventHandler<bool> EditEnded;

        /// <summary>The active pane (the omnibar follows focus between split panes).</summary>
        public FileListViewModel Pane => _pane;

        // ================================================================== editing

        /// <summary>
        /// Starts editing with the full path (selected by the view, so typing replaces it), or with
        /// <paramref name="initialText"/> (typing "/" or "~" in the file list starts the omnibar with it).
        /// </summary>
        public void BeginEdit(string initialText = null)
        {
            RefreshSnapshots();
            _suppressQuery = true;
            Text = initialText ?? _pane?.CurrentPath ?? string.Empty;
            _suppressQuery = false;
            HintText = null;
            IsEditing = true;
            QueryAsync(_text, debounce: false).Forget("omnibar query");
        }

        public void CancelEdit() => EndEdit(committed: false);

        private void EndEdit(bool committed)
        {
            if (!_isEditing)
            {
                return;
            }

            _queryCts?.Cancel();
            IsEditing = false;
            Suggestions.Clear();
            SelectedIndex = -1;
            Ghost = null;
            HintText = null;
            RaisePropertyChanged(nameof(IsPopupOpen));
            EditEnded?.Invoke(this, committed);
        }

        /// <summary>Arrow keys: moves the highlight, wrapping, skipping rows that do nothing.</summary>
        public void MoveSelection(int delta)
        {
            int count = Suggestions.Count;
            if (count == 0)
            {
                return;
            }

            int index = _selectedIndex;
            for (int step = 0; step < count; step++)
            {
                index = index < 0 ? (delta > 0 ? 0 : count - 1) : (index + delta + count) % count;
                if (Suggestions[index].IsActionable)
                {
                    SelectedIndex = index;
                    return;
                }
            }
        }

        /// <summary>
        /// Tab: puts the highlighted (or top) suggestion's completion in the box. False when there is nothing to complete
        /// (Tab then moves focus as usual).
        /// </summary>
        public bool AcceptCompletion()
        {
            OmnibarSuggestion suggestion = _selectedIndex >= 0 ? Suggestions[_selectedIndex] : (Suggestions.Count > 0 ? Suggestions[0] : null);
            string completion = suggestion?.CompletionText;
            if (string.IsNullOrEmpty(completion) || string.Equals(completion, _text, StringComparison.Ordinal))
            {
                return false;
            }

            Text = completion;
            return true;
        }

        /// <summary>Enter.</summary>
        public async Task CommitAsync()
        {
            if (_selectedIndex >= 0 && _selectedIndex < Suggestions.Count)
            {
                await ExecuteAsync(Suggestions[_selectedIndex]).ConfigureAwait(true);
                return;
            }

            string text = _text?.Trim() ?? string.Empty;
            if (text.Length == 0 || (_pane?.CurrentPath != null && string.Equals(text, _pane.CurrentPath, PathUtil.Comparison)))
            {
                EndEdit(committed: false);
                return;
            }

            OmnibarInput input = OmnibarInput.Parse(text, _pane?.CurrentPath, HomeFolder);
            switch (input.Kind)
            {
                case OmnibarInputKind.Path:
                    await GoToPathAsync(input.FullPath).ConfigureAwait(true);
                    return;
                case OmnibarInputKind.WebAddress:
                    OpenWebAddress(input.Query);
                    return;
                case OmnibarInputKind.NetworkAddress:
                    EndEdit(committed: true);
                    await _main.OpenNetworkLocationAsync(input.Query).ConfigureAwait(true);
                    return;
            }

            // Words, commands and searches: the best suggestion, computed now if the list is stale.
            IReadOnlyList<OmnibarSuggestion> current = await _engine.GetSuggestionsAsync(text, CancellationToken.None).ConfigureAwait(true);
            foreach (OmnibarSuggestion suggestion in current)
            {
                if (suggestion.IsActionable)
                {
                    await ExecuteAsync(suggestion).ConfigureAwait(true);
                    return;
                }
            }

            Reject($"Nothing matches \"{text}\".");
        }

        /// <summary>Runs a suggestion (a click, or Enter on the highlighted row).</summary>
        public async Task ExecuteAsync(OmnibarSuggestion suggestion)
        {
            if (suggestion == null || !suggestion.IsActionable || _pane == null)
            {
                return;
            }

            try
            {
                switch (suggestion.Action)
                {
                    case OmnibarActionKind.Navigate:
                        EndEdit(committed: true);
                        await _pane.LoadFolderAsync(suggestion.Target).ConfigureAwait(true);
                        break;
                    case OmnibarActionKind.Reveal:
                        EndEdit(committed: true);
                        await _pane.RevealAsync(suggestion.Target).ConfigureAwait(true);
                        break;
                    case OmnibarActionKind.OpenNetwork:
                        EndEdit(committed: true);
                        await _main.OpenNetworkLocationAsync(suggestion.Target).ConfigureAwait(true);
                        break;
                    case OmnibarActionKind.OpenWebAddress:
                        OpenWebAddress(suggestion.Target);
                        break;
                    case OmnibarActionKind.RunCommand:
                        EndEdit(committed: true);
                        suggestion.Command?.Run();
                        break;
                    case OmnibarActionKind.FilterHere:
                        EndEdit(committed: true);
                        _pane.FilterText = suggestion.Target;
                        break;
                    case OmnibarActionKind.SearchEverywhere:
                        EndEdit(committed: true);
                        _main.SearchBoxDisplayText = suggestion.Target;
                        break;
                    case OmnibarActionKind.ThisPc:
                        EndEdit(committed: true);
                        _main.ClearSearch();
                        await _pane.LoadThisPcAsync().ConfigureAwait(true);
                        break;
                    case OmnibarActionKind.Dashboard:
                        EndEdit(committed: true);
                        _main.ClearSearch();
                        await _pane.LoadHomeAsync().ConfigureAwait(true);
                        break;
                    case OmnibarActionKind.Trash:
                        EndEdit(committed: true);
                        _main.ClearSearch();
                        await _pane.LoadTrashAsync().ConfigureAwait(true);
                        break;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Logger.Warning(ex, "Omnibar action failed: {Action} {Target}", suggestion.Action, suggestion.Target);
            }
        }

        /// <summary>
        /// A typed path: a folder (or ZIP) opens, a file is shown selected in its folder. Existence is checked off the UI
        /// thread with a timeout, so an unreachable network path can't freeze the window; when the check times out the
        /// folder is opened anyway and the pane reports what happens.
        /// </summary>
        private async Task GoToPathAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                Reject("That isn't a valid path.");
                return;
            }

            (bool done, bool isFolder, bool isFile) = await ProbeAsync(path).ConfigureAwait(true);
            if (!done || isFolder || ArchiveBrowser.IsInsideArchive(path))
            {
                EndEdit(committed: true);
                _main.ClearSearch();
                await _pane.LoadFolderAsync(path).ConfigureAwait(true);
            }
            else if (isFile)
            {
                EndEdit(committed: true);
                _main.ClearSearch();
                await _pane.RevealAsync(path).ConfigureAwait(true);
            }
            else
            {
                Reject($"There's nothing at {path}.");
            }
        }

        private static async Task<(bool Done, bool IsFolder, bool IsFile)> ProbeAsync(string path)
        {
            Task<(bool, bool)> probe = Task.Run(() =>
            {
                if (Core.Platform.Phones.PhonePaths.IsPhonePath(path))
                {
                    // Asking the phone takes long: a path on it is opened, and the folder listing says if it isn't there.
                    return (true, false);
                }

                return (Directory.Exists(path), File.Exists(path));
            });
            Task finished = await Task.WhenAny(probe, Task.Delay(ExistenceTimeout)).ConfigureAwait(true);
            if (finished != probe)
            {
                probe.Forget("path probe");
                return (false, false, false);
            }

            (bool isFolder, bool isFile) = await probe.ConfigureAwait(true);
            return (true, isFolder, isFile);
        }

        private void OpenWebAddress(string address)
        {
            try
            {
                ShellLauncher.Open(address);
                EndEdit(committed: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Logger.Warning(ex, "Couldn't open {Address}", address);
                Reject("Couldn't open the address in a browser.");
            }
        }

        private void Reject(string hint)
        {
            HintText = hint;
            InputRejected?.Invoke(this, EventArgs.Empty);
        }

        private async Task QueryAsync(string text, bool debounce)
        {
            _queryCts?.Cancel();
            var cts = new CancellationTokenSource();
            _queryCts = cts;
            int version = ++_queryVersion;
            try
            {
                if (debounce)
                {
                    await Task.Delay(DebounceMs, cts.Token).ConfigureAwait(true);
                }

                IReadOnlyList<OmnibarSuggestion> results = await Task.Run(() => _engine.GetSuggestionsAsync(text, cts.Token), cts.Token).ConfigureAwait(true);
                if (version != _queryVersion || !_isEditing)
                {
                    return;
                }

                Suggestions.ReplaceAll(results);
                _selectedIndex = -1;
                RaisePropertyChanged(nameof(SelectedIndex));
                RaisePropertyChanged(nameof(IsPopupOpen));
                UpdateGhost();
            }
            catch (OperationCanceledException)
            {
                // A newer keystroke took over.
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Omnibar suggestions failed for {Text}", text);
            }
            finally
            {
                if (ReferenceEquals(_queryCts, cts))
                {
                    _queryCts = null;
                }

                cts.Dispose();
            }
        }

        private void UpdateGhost()
        {
            Ghost = _selectedIndex < 0 && Suggestions.Count > 0 ? Suggestions[0].GhostSuffix : null;
        }

        // ================================================================== breadcrumbs

        /// <summary>A breadcrumb was clicked.</summary>
        /// <summary>The lenses, for the address bar's "Lens ›" menu (switch lens without the left panel).</summary>
        public System.Collections.Generic.IReadOnlyList<ViewModels.Lens.LensEntryViewModel> Lenses => _main.Lens?.Lenses;

        /// <summary>Shows another lens (the address bar's "Lens ›" menu).</summary>
        public Task OpenLensAsync(ViewModels.Lens.LensEntryViewModel lens) => _main.OpenLensAsync(lens);

        public async Task NavigateToCrumbAsync(Crumb crumb)
        {
            if (crumb == null || _pane == null)
            {
                return;
            }

            try
            {
                if (crumb.Kind == CrumbKind.Lens)
                {
                    await _main.Lens.RefreshAsync().ConfigureAwait(true); // the lens is already shown: run it again
                    return;
                }

                _main.ClearSearch();
                if (crumb.Kind == CrumbKind.Place)
                {
                    if (_pane.IsThisPcView)
                    {
                        await _pane.LoadThisPcAsync().ConfigureAwait(true);
                    }
                    else if (_pane.IsTrashView)
                    {
                        await _pane.ReloadTrashAsync().ConfigureAwait(true);
                    }
                    else
                    {
                        await _pane.LoadHomeAsync().ConfigureAwait(true);
                    }
                }
                else if (!string.IsNullOrEmpty(crumb.Path))
                {
                    await _pane.LoadFolderAsync(crumb.Path).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Logger.Warning(ex, "Breadcrumb navigation failed for {Path}", crumb.Path);
            }
        }

        /// <summary>A folder's subfolders, for the dropdown after its breadcrumb (sorted naturally, hidden ones per setting).</summary>
        public async Task<IReadOnlyList<string>> GetSubfoldersAsync(string folder)
        {
            IReadOnlyList<DirectoryIndexEntry> entries = await ListAsync(folder, CancellationToken.None).ConfigureAwait(true);
            var names = new List<string>();
            if (entries == null)
            {
                return names;
            }

            bool hidden = ShowHidden;
            foreach (DirectoryIndexEntry entry in entries)
            {
                if ((entry.IsDirectory || ArchiveBrowser.IsArchiveFile(entry.Name)) && (hidden || !entry.IsHidden))
                {
                    names.Add(entry.Name);
                }
            }

            names.Sort(Core.Native.NaturalStringComparer.Instance);
            return names;
        }

        /// <summary>Files dropped on a breadcrumb go into that folder (copy or move, as the drag decided).</summary>
        public Task DropAsync(IReadOnlyList<string> paths, string folder, bool move) =>
            _pane == null ? Task.CompletedTask : _pane.ExecuteDropAsync(paths, folder, move);

        /// <summary>The mounted volumes the breadcrumbs name (the tabs name places the same way).</summary>
        internal IReadOnlyList<CrumbVolume> Volumes => _volumes;

        /// <summary>Mounted volumes, for naming breadcrumbs by volume label. Read off the UI thread (it can block on a mount).</summary>
        public async Task RefreshVolumesAsync()
        {
            try
            {
                IReadOnlyList<MountedVolume> volumes = await Task.Run(MountedVolumes.GetDisplayVolumes).ConfigureAwait(true);
                var crumbVolumes = new List<CrumbVolume>(volumes.Count);
                foreach (MountedVolume volume in volumes)
                {
                    crumbVolumes.Add(new CrumbVolume(volume.RootPath, volume.Label, volume.Kind == VolumeKind.Network));
                }

                _volumes = crumbVolumes;
                RebuildCrumbs();
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Mounted volumes unavailable for the breadcrumbs");
            }
        }

        // ================================================================== the active pane

        private void OnMainPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.FileListViewModel))
            {
                AttachPane(_main.FileListViewModel);
            }
            else if (e.PropertyName == nameof(MainViewModel.IsLensActive))
            {
                OnLocationChanged(); // "Lens › Photos" while a lens is shown, the folder again after
            }
            else if (e.PropertyName == nameof(MainViewModel.LensTitle) && _main.IsLensActive)
            {
                RebuildCrumbs(); // another lens chosen while one is shown
            }
        }

        private void AttachPane(FileListViewModel pane)
        {
            if (ReferenceEquals(pane, _pane))
            {
                return;
            }

            if (_pane != null)
            {
                _pane.PropertyChanged -= OnPanePropertyChanged;
            }

            _pane = pane;
            if (_pane != null)
            {
                _pane.PropertyChanged += OnPanePropertyChanged;
            }

            RaisePropertyChanged(nameof(Pane));
            OnLocationChanged();
            OnLoadingChanged();
        }

        private void OnPanePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(FileListViewModel.CurrentPath):
                case nameof(FileListViewModel.IsHomeView):
                case nameof(FileListViewModel.IsThisPcView):
                case nameof(FileListViewModel.IsTrashView):
                    OnLocationChanged();
                    break;
                case nameof(FileListViewModel.IsLoading):
                    OnLoadingChanged();
                    break;
            }
        }

        private void OnLocationChanged()
        {
            string path = _pane?.CurrentPath;
            if (!string.IsNullOrEmpty(path))
            {
                _sessionHistory.RemoveAll(p => PathUtil.AreSame(p, path));
                _sessionHistory.Insert(0, path);
                if (_sessionHistory.Count > MaxSessionHistory)
                {
                    _sessionHistory.RemoveAt(_sessionHistory.Count - 1);
                }
            }

            RebuildCrumbs();
            UpdatePinState();
            RaisePropertyChanged(nameof(CurrentPathText));
            RaisePropertyChanged(nameof(CanPin));
            _copyPathCommand.RaiseCanExecuteChanged();
            _togglePinCommand.RaiseCanExecuteChanged();
            if (_isEditing)
            {
                EndEdit(committed: false); // the location changed under the edit (Back, a sidebar click)
            }
        }

        private void RebuildCrumbs()
        {
            if (_pane == null)
            {
                Crumbs = Array.Empty<Crumb>();
            }
            else if (_main.IsLensActive)
            {
                // A lens is a view of every drive, not a folder: the bar says which one.
                Crumbs = new[] { new Crumb("Lens", null, CrumbKind.Lens), new Crumb(_main.Lens.Title, null, CrumbKind.Lens) };
            }
            else if (_pane.IsThisPcView)
            {
                Crumbs = new[] { new Crumb("This PC", null, CrumbKind.Place) };
            }
            else if (_pane.IsTrashView)
            {
                Crumbs = new[] { new Crumb(Core.Platform.Trash.DisplayName, null, CrumbKind.Place) };
            }
            else if (_pane.IsHomeView || string.IsNullOrEmpty(_pane.CurrentPath))
            {
                Crumbs = new[] { new Crumb("Home", null, CrumbKind.Place) };
            }
            else
            {
                Crumbs = BreadcrumbBuilder.Build(_pane.CurrentPath, HomeFolder, _volumes);
            }

            RaisePropertyChanged(nameof(LocationSymbol));
        }

        /// <summary>The loading line appears only for loads longer than a moment.</summary>
        private async void OnLoadingChanged()
        {
            try
            {
                int version = ++_loadingVersion;
                if (_pane?.IsLoading != true)
                {
                    IsLoadingVisible = false;
                    return;
                }

                await Task.Delay(LoadingIndicatorDelayMs).ConfigureAwait(true);
                if (version == _loadingVersion && _pane?.IsLoading == true)
                {
                    IsLoadingVisible = true;
                }
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Debug(ex, "Loading indicator update failed");
            }
        }

        private void OnPinsChanged(object sender, NotifyCollectionChangedEventArgs e) => UpdatePinState();

        private void UpdatePinState() =>
            IsPinned = !string.IsNullOrEmpty(_pane?.CurrentPath) && _main.Pins.Contains(_pane.CurrentPath);

        private void CopyPath()
        {
            string path = _pane?.CurrentPath;
            if (!string.IsNullOrEmpty(path))
            {
                ClipboardFileService.SetText(path);
                _pane.ShowStatus("Path copied");
            }
        }

        // ================================================================== IOmnibarContext

        public string CurrentFolder => _pane?.CurrentPath;

        public string HomeFolder => SpecialFolders.Home;

        public bool ShowHidden => _pane?.ShowHiddenItems ?? false;

        public IReadOnlyList<string> RecentFolders => _recent;

        public IReadOnlyList<OmnibarPlace> Places => _places;

        public IReadOnlyList<OmnibarCommand> Commands => _commands;

        public async Task<IReadOnlyList<DirectoryIndexEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(folder) || _index == null)
            {
                return null;
            }

            int timeout = _settings?.Current?.FileList?.NetworkEnumerationTimeoutMs ?? 8000;
            DirectoryIndex index = await _index.BuildIndexAsync(folder, timeout, bypassCache: false, cancellationToken).ConfigureAwait(false);
            return index == null || index.HasError ? null : index.Entries;
        }

        /// <summary>
        /// Recent folders (this session's first, then the most visited ever) and places, snapshotted when editing starts,
        /// so the worker thread reads immutable lists while the person types.
        /// </summary>
        private void RefreshSnapshots()
        {
            var recent = new List<string>(_sessionHistory);
            try
            {
                foreach (FavoriteFolderInsight insight in _analytics?.GetTopFolders(30) ?? Array.Empty<FavoriteFolderInsight>())
                {
                    if (!recent.Exists(p => PathUtil.AreSame(p, insight.FullPath)))
                    {
                        recent.Add(insight.FullPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Frequent folders unavailable for the omnibar");
            }

            _recent = recent;

            var places = new List<OmnibarPlace>
            {
                new OmnibarPlace("Home folder", SpecialFolders.Home, Symbol.Folder, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Desktop", SpecialFolders.Desktop, Symbol.Desktop, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Documents", SpecialFolders.Documents, Symbol.Document, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Downloads", SpecialFolders.Downloads, Symbol.ArrowDownload, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Pictures", SpecialFolders.Pictures, Symbol.Image, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Music", SpecialFolders.Music, Symbol.MusicNote2, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("Videos", SpecialFolders.Videos, Symbol.Video, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate),
                new OmnibarPlace("This PC", null, Symbol.Desktop, OmnibarPlaceKind.Place, OmnibarActionKind.ThisPc, "Drives and recent files"),
                new OmnibarPlace("Home", null, Symbol.Home, OmnibarPlaceKind.Place, OmnibarActionKind.Dashboard, "Your activity, recent and frequent folders")
            };

            // Special folders that don't exist on this machine (no ~/Music on a server) are not places to go.
            places.RemoveAll(place => place.Kind == OmnibarPlaceKind.Place && place.Target != null && !SafeDirectoryExists(place.Target));

            // The trash view, on every system (on Windows the Recycle Bin is no folder, so it used to be missing here).
            places.Add(new OmnibarPlace(Core.Platform.Trash.DisplayName, null, Symbol.Delete, OmnibarPlaceKind.Place, OmnibarActionKind.Trash,
                "Deleted items: restore them or delete them permanently"));

            foreach (CrumbVolume volume in _volumes)
            {
                places.Add(new OmnibarPlace(string.IsNullOrEmpty(volume.Label) ? volume.Root : volume.Label, volume.Root,
                    volume.IsNetwork ? Symbol.GlobeDesktop : Symbol.HardDrive, OmnibarPlaceKind.Drive, OmnibarActionKind.Navigate));
            }

            foreach (Sidebar.PinnedItemViewModel pin in _main.Pins.Items)
            {
                if (!pin.IsTrash && !places.Exists(p => p.Target != null && PathUtil.AreSame(p.Target, pin.Path)))
                {
                    places.Add(new OmnibarPlace(pin.Name, pin.Path, Symbol.Pin, OmnibarPlaceKind.Pinned, OmnibarActionKind.Navigate));
                }
            }

            foreach (SavedLocation location in _main.NetworkLocations.Items)
            {
                places.Add(new OmnibarPlace(location.Name, location.Path, Symbol.GlobeDesktop, OmnibarPlaceKind.Network, OmnibarActionKind.OpenNetwork, location.Path));
            }

            _places = places;
        }

        private static bool SafeDirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ================================================================== commands

        /// <summary>Adds a command only the window can run (show/hide its panels).</summary>
        public void RegisterCommand(OmnibarCommand command)
        {
            if (command != null)
            {
                _commands.Add(command);
            }
        }

        private void BuildCommands()
        {
            string mod = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
            void Add(string title, string keywords, Symbol icon, string gesture, Func<ICommand> command, object parameter = null) =>
                _commands.Add(new OmnibarCommand(title, keywords, icon, gesture,
                    () => command()?.CanExecute(parameter) == true,
                    () => command()?.Execute(parameter)));
            void AddAction(string title, string keywords, Symbol icon, string gesture, Func<bool> canRun, Action run) =>
                _commands.Add(new OmnibarCommand(title, keywords, icon, gesture, canRun, run));

            Add("New folder", "mkdir create directory", Symbol.FolderAdd, $"{mod}+Shift+N", () => _pane?.NewFolderCommand);
            Add("New text document", "create file touch txt", Symbol.DocumentAdd, null, () => _pane?.NewTextFileCommand);
            Add("Paste", "clipboard", Symbol.ClipboardPaste, $"{mod}+V", () => _pane?.PasteCommand);
            Add("Undo", "revert back", Symbol.ArrowUndo, $"{mod}+Z", () => _main.Operations.UndoCommand);
            Add("Redo", "do again", Symbol.ArrowRedo, $"{mod}+Y", () => _main.Operations.RedoCommand);
            Add("Select all", "everything", Symbol.SelectAllOn, $"{mod}+A", () => _pane?.SelectAllCommand);
            Add("Invert selection", "select others", Symbol.SelectAllOn, null, () => _pane?.InvertSelectionCommand);
            Add("Refresh", "reload update", Symbol.ArrowClockwise, "F5", () => _pane?.RefreshCommand);
            Add("Go up", "parent", Symbol.ArrowUp, "Alt+Up", () => _pane?.NavigateUpCommand);
            Add("Go back", "previous history", Symbol.ArrowLeft, "Alt+Left", () => _pane?.NavigateBackCommand);
            Add("Go forward", "next history", Symbol.ArrowRight, "Alt+Right", () => _pane?.NavigateForwardCommand);
            Add("Copy folder path", "clipboard location", Symbol.Link, null, () => _copyPathCommand);
            Add("Open in terminal", "shell console command prompt bash", Symbol.WindowConsole, null, () => _pane?.OpenTerminalCommand);
            Add("Properties", "info details size", Symbol.Info, "Alt+Enter", () => _pane?.PropertiesCommand);
            Add("Pin to Quick access", "favorite bookmark star unpin", Symbol.Pin, $"{mod}+D", () => _main.TogglePinCommand);
            Add("Show or hide hidden items", "dotfiles toggle", Symbol.Filter, $"{mod}+H", () => _pane?.ToggleShowHiddenItemsCommand);
            Add("Show or hide system items", "toggle", Symbol.Filter, null, () => _pane?.ToggleShowSystemItemsCommand);
            Add("Split view", "two panes side dual", Symbol.SplitVertical, null, () => _main.ToggleSplitViewCommand);
            Add("Add network location", "share smb server connect", Symbol.GlobeDesktop, null, () => _main.AddNetworkLocationCommand);

            foreach ((FileListViewPreset preset, string name, Symbol icon) in new[]
                     {
                         (FileListViewPreset.Details, "Details", Symbol.Table),
                         (FileListViewPreset.List, "List", Symbol.AppsList),
                         (FileListViewPreset.Tiles, "Tiles", Symbol.Apps),
                         (FileListViewPreset.SmallIcons, "Small icons", Symbol.Grid),
                         (FileListViewPreset.MediumIcons, "Medium icons", Symbol.Grid),
                         (FileListViewPreset.LargeIcons, "Large icons", Symbol.Grid),
                         (FileListViewPreset.ExtraLargeIcons, "Extra large icons", Symbol.Grid),
                         (FileListViewPreset.Timeline, "Timeline", Symbol.History),
                         (FileListViewPreset.Columns, "Columns", Symbol.PanelLeft),
                         (FileListViewPreset.Nexus, "Nexus", Symbol.Apps)
                     })
            {
                FileListViewPreset captured = preset;
                AddAction("View: " + name, "layout mode display", icon, null, () => _pane != null, () => _pane.SelectedPreset = captured);
            }

            foreach ((FileListSortColumn column, string name) in new[]
                     {
                         (FileListSortColumn.Name, "name"), (FileListSortColumn.DateModified, "date modified"),
                         (FileListSortColumn.Size, "size"), (FileListSortColumn.Type, "type"), (FileListSortColumn.Tags, "tags"),
                         (FileListSortColumn.DateCreated, "date created"), (FileListSortColumn.DateAccessed, "date accessed")
                     })
            {
                FileListSortColumn captured = column;
                AddAction("Sort by " + name, "order arrange", Symbol.ArrowSort, null, () => _pane != null,
                    () => _pane.SetSort(captured, _pane.SortColumn == captured ? !_pane.SortAscending : captured == FileListSortColumn.Name));
            }

            AddAction("Zoom in", "bigger larger icons", Symbol.Add, $"{mod}++", () => _pane != null, () => _pane.AdjustZoom(16));
            AddAction("Zoom out", "smaller icons", Symbol.Subtract, $"{mod}+-", () => _pane != null, () => _pane.AdjustZoom(-16));
            foreach (string theme in new[] { "System", "Light", "Dark" })
            {
                string captured = theme;
                AddAction("Theme: " + theme, "appearance colors mode", Symbol.Apps, null,
                    () => !string.Equals(_main.Theme, captured, StringComparison.OrdinalIgnoreCase),
                    () => _main.SetThemeCommand.Execute(captured));
            }
        }

        public void Dispose()
        {
            _queryCts?.Cancel();
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Pins.Items.CollectionChanged -= OnPinsChanged;
            if (_pane != null)
            {
                _pane.PropertyChanged -= OnPanePropertyChanged;
            }
        }
    }
}
