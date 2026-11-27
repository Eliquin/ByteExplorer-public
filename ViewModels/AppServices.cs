// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Search;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// The services there must be one of per process, shared by every main window: the settings (one file, one writer),
    /// the search index (built and watched once), the tags, the usage analytics, the sound feedback, the directory index
    /// cache and the file operation queue (so undo and the operations panel are app-wide, as Explorer's copy dialogs are).
    /// Each window keeps its own tabs, panes, navigation and layout in its <see cref="MainViewModel"/>.
    /// <para>
    /// The main windows share one instance (<see cref="CreateForWindows"/>, then handed from the open windows to each new
    /// one by MainWindowBase): it is created with the first window and disposed when the last one closes, so closing a window never stops the index or cancels another window's operations. A view
    /// model made on its own (tests, without a window) gets a private instance that lives and dies with it, which keeps
    /// such view models independent of each other as they always were.
    /// </para>
    /// <para>UI thread only: windows are created, activated and closed there.</para>
    /// </summary>
    public sealed class AppServices
    {
        private readonly IUiDispatcher _dispatcher;
        private readonly ILogger _logger;
        private readonly bool _isShared;

        /// <summary>The view models using this instance, least recently active first (the active window is last).</summary>
        private readonly List<MainViewModel> _windows = new List<MainViewModel>();

        private Task _settingsReady;
        private Task _startupFinished;
        private Task _tagsLoaded;
        private CancellationTokenSource _indexCts;
        private bool _disposed;

        private AppServices(IUiDispatcher dispatcher, bool isShared)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _isShared = isShared;
            _logger = Log.Logger.ForContext<AppServices>();

            Settings = new SettingsService(Log.Logger);

            // Local-only usage analytics backing the Home dashboard: constructed early so a session can start
            // immediately; IsEnabled reads the settings' Current, which is usable before LoadAsync completes.
            Analytics = new UserAnalyticsService(Settings, Log.Logger);
            Analytics.StartSession();
            SoundFeedback = new SoundFeedbackService(Settings, Log.Logger);

            DirectoryIndex = new DirectoryIndexService(Settings, Log.Logger);
            OperationService = new FileOperationService(Settings, Log.Logger) { JournalFolder = MainViewModel.OperationJournalFolder };
            BackupService = new FileBackupService(Settings, Log.Logger);
            // Dialogs of a running operation (name clashes, errors) appear over the window used last: the one that started
            // it may have been closed since, and the operation goes on.
            Operations = new FileOperations.FileOperationQueue(OperationService, _dispatcher, () => ActiveDialogService);
            // Subscribed before any window's own handler, so caches are invalidated before a window reloads its panes.
            Operations.FoldersChanged += OnOperationFoldersChanged;
            Operations.Recorded += OnOperationRecorded;
            Operations.Undone += OnOperationUndone;
            Operations.OperationFinished += OnOperationFinished;

            Search = new GlobalSearchService(Settings);
            Tags = new Core.Tags.TagStore(Core.AppPaths.Combine("tags.json"));
        }

        /// <summary>The last window using these services has closed: they are stopped.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>Shared by the main windows (false: a view model's own, see <see cref="CreatePrivate"/>).</summary>
        public bool IsShared => _isShared;

        /// <summary>A new instance for the first main window; the windows opened while it is open get the same one.</summary>
        public static AppServices CreateForWindows(IUiDispatcher dispatcher) => new AppServices(dispatcher, isShared: true);

        /// <summary>An instance of its own for a view model made without a window (it is disposed with that view model).</summary>
        public static AppServices CreatePrivate(IUiDispatcher dispatcher) => new AppServices(dispatcher, isShared: false);

        public SettingsService Settings { get; }

        public IUserAnalyticsService Analytics { get; }

        public ISoundFeedbackService SoundFeedback { get; }

        public IDirectoryIndexService DirectoryIndex { get; }

        public IFileOperationService OperationService { get; }

        public IFileBackupService BackupService { get; }

        /// <summary>The app's file operations: the queue every window's operations panel shows, and the undo history.</summary>
        public FileOperations.FileOperationQueue Operations { get; }

        public GlobalSearchService Search { get; }

        /// <summary>The tags of files and folders (tags.json in the data folder); also <see cref="Core.Tags.TagStore.Default"/>.</summary>
        public Core.Tags.TagStore Tags { get; }

        /// <summary>The window view models using these services, least recently active first.</summary>
        public IReadOnlyList<MainViewModel> Windows => _windows;

        /// <summary>The window used last (null when none is left).</summary>
        public MainViewModel ActiveWindow => _windows.Count == 0 ? null : _windows[_windows.Count - 1];

        /// <summary>The dialogs of the window used last that has them (a view model without a window has none).</summary>
        private IDialogService ActiveDialogService
        {
            get
            {
                for (int i = _windows.Count - 1; i >= 0; i--)
                {
                    if (_windows[i].DialogService != null)
                    {
                        return _windows[i].DialogService;
                    }
                }

                return null;
            }
        }

        internal void Attach(MainViewModel window)
        {
            if (!_windows.Contains(window))
            {
                _windows.Add(window);
            }
        }

        /// <summary>A window was activated: it becomes the one dialogs of shared work appear over.</summary>
        internal void MarkActive(MainViewModel window)
        {
            if (_windows.Remove(window))
            {
                _windows.Add(window);
            }
        }

        /// <summary>A window's view model is disposed: the services stop with the last one.</summary>
        internal void Detach(MainViewModel window)
        {
            _windows.Remove(window);
            if (_windows.Count == 0)
            {
                Dispose();
            }
        }

        // ------------------------------------------------------------------ startup

        /// <summary>
        /// The settings loaded and interrupted operations put right, once for the process: the second window waits for the
        /// first's start instead of reading the settings again (which would replace the object every window holds).
        /// </summary>
        public Task EnsureSettingsReadyAsync() => _settingsReady ??= LoadSettingsAsync();

        /// <summary>The tags loaded and the background index check started, once (after a window's own start).</summary>
        public Task FinishStartupAsync() => _startupFinished ??= FinishStartupCoreAsync();

        /// <summary>Whether the settings are loaded (windows made later apply the saved view to new panes at once).</summary>
        public bool IsSettingsLoaded { get; private set; }

        private async Task LoadSettingsAsync()
        {
            Core.Tags.TagStore.Default = Tags;
            Task recovery = RecoverInterruptedOperationsAsync();
            _tagsLoaded = Tags.LoadAsync();
            await Settings.LoadAsync().ConfigureAwait(true);
            await recovery.ConfigureAwait(true); // before anything can start a new operation
            IsSettingsLoaded = true;
            DetailsColumnsState.Attach(Settings); // the saved Details columns, in every pane
        }

        private async Task FinishStartupCoreAsync()
        {
            try
            {
                await (_tagsLoaded ?? Task.CompletedTask).ConfigureAwait(true);
                Tags.PruneMissingAsync().Forget("prune the tags of missing items");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not load the tags");
            }

            if (_disposed)
            {
                return;
            }

            // Kick off index ensure on a background thread. Status updates flow back through each window's
            // StatusChanged handler.
            _indexCts = new CancellationTokenSource();
            CancellationToken token = _indexCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Search.EnsureIndexAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.Information("Background search index initialization cancelled");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Background search index initialization failed");
                }
            }, token).Forget("search index initialization");
        }

        /// <summary>Rebuilds the search index now (the settings window's "Rebuild now", the status bar).</summary>
        public async Task RefreshIndexAsync()
        {
            _indexCts?.Cancel();
            _indexCts = new CancellationTokenSource();
            CancellationToken token = _indexCts.Token;
            try
            {
                await Search.RefreshIndexAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Index refresh cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Index refresh failed");
            }
        }

        public void StopIndexing()
        {
            try
            {
                _indexCts?.Cancel();
                Search.StopIndexing();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Stop indexing failed");
            }
        }

        /// <summary>
        /// Puts right what file operations interrupted by a crash, a killed process or a power cut left (OperationRecovery),
        /// before anything else can start one, and tells the person once what was interrupted. Never throws.
        /// </summary>
        private async Task RecoverInterruptedOperationsAsync()
        {
            IReadOnlyList<Core.Services.FileOperations.RecoveredOperation> recovered;
            try
            {
                recovered = await Task.Run(() => Core.Services.FileOperations.OperationRecovery.Recover(MainViewModel.OperationJournalFolder)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Recovering interrupted file operations failed");
                return;
            }

            IDialogService dialogs = ActiveDialogService;
            if (recovered.Count == 0 || dialogs == null)
            {
                return;
            }

            var message = new System.Text.StringBuilder();
            message.Append(recovered.Count == 1
                ? "ByteExplorer was closed while a file operation was running:"
                : $"ByteExplorer was closed while {recovered.Count} file operations were running:");
            message.Append("\n");
            foreach (Core.Services.FileOperations.RecoveredOperation operation in recovered)
            {
                message.Append("\n•  ").Append(operation.Description);
                if (operation.StartedUtc > DateTime.MinValue)
                {
                    message.Append(" (started ").Append(operation.StartedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)).Append(')');
                }
            }

            int removed = recovered.Sum(r => r.TemporaryFilesRemoved);
            int completed = recovered.Sum(r => r.ReplacesCompleted);
            int rolledBack = recovered.Sum(r => r.ReplacesRolledBack);
            var problems = recovered.SelectMany(r => r.Problems).ToList();
            message.Append("\n\nNo file was lost: originals are only ever removed after their copy is complete. ");
            var fixes = new List<string>();
            if (removed > 0)
            {
                fixes.Add($"{removed:N0} unfinished temporary file{(removed == 1 ? " was" : "s were")} removed");
            }

            if (completed > 0)
            {
                fixes.Add($"{completed:N0} replaced file{(completed == 1 ? " was" : "s were")} put in place");
            }

            if (rolledBack > 0)
            {
                fixes.Add($"{rolledBack:N0} file{(rolledBack == 1 ? " that was being replaced was" : "s that were being replaced were")} put back");
            }

            if (fixes.Count > 0)
            {
                message.Append(char.ToUpper(fixes[0][0], System.Globalization.CultureInfo.CurrentCulture)).Append(string.Join(", ", fixes)[1..]).Append(". ");
            }

            message.Append("Run the operation again to finish it; items already done are offered as name clashes.");
            if (problems.Count > 0)
            {
                message.Append("\n\nCould not be put right:\n").Append(string.Join("\n", problems.Select(p => "•  " + p)));
            }

            // Shown without holding up the start; the recovery itself is done by now.
            dialogs.ShowMessageAsync("An operation was interrupted", message.ToString()).Forget("interrupted operation message");
        }

        // ------------------------------------------------------------------ what operations change, once for every window

        /// <summary>
        /// An operation or an undo changed these folders: the caches and the index follow, once (each window then reloads
        /// its own panes that show one of them).
        /// </summary>
        private void OnOperationFoldersChanged(object sender, IReadOnlyList<string> folders)
        {
            try
            {
                foreach (string folder in folders)
                {
                    DirectoryIndex?.InvalidateCache(folder);
                    FolderSizes.Invalidate(folder); // its counted size, and every folder's above it, include the change
                }

                Search.NotifyFoldersChanged(folders); // the search index follows the app's own changes
                Tags.ReconcileFolders(folders); // a deleted item's tags are set aside (and come back if it does)
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Updating the caches after a file operation failed");
            }
        }

        private void OnOperationFinished(object sender, Core.Models.FileOperationResult result)
        {
            Analytics.TrackFileOperation(result.Kind.ToString(), result.SuccessCount, result.Failures.Count, result.WasCancelled);
        }

        /// <summary>Renames and moves done in the app (and their undo) take the items' tags along.</summary>
        private void OnOperationRecorded(object sender, Core.Services.FileOperations.FileOperationRecord record)
        {
            foreach (var action in record.Actions)
            {
                if (action is Core.Services.FileOperations.MovedAction moved)
                {
                    Tags.MovePath(moved.From, moved.To);
                    Analytics.TrackPathMoved(moved.From, moved.To);
                }
            }
        }

        private void OnOperationUndone(object sender, Core.Services.FileOperations.FileOperationRecord record)
        {
            for (int i = record.Actions.Count - 1; i >= 0; i--)
            {
                if (record.Actions[i] is Core.Services.FileOperations.MovedAction moved)
                {
                    Tags.MovePath(moved.To, moved.From);
                    Analytics.TrackPathMoved(moved.To, moved.From);
                }
            }
        }

        /// <summary>The last window closed: running operations are cancelled, the index stops, the session is flushed.</summary>
        private void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Operations.FoldersChanged -= OnOperationFoldersChanged;
            Operations.Recorded -= OnOperationRecorded;
            Operations.Undone -= OnOperationUndone;
            Operations.OperationFinished -= OnOperationFinished;
            Operations.CancelAll();
            _indexCts?.Cancel();

            try
            {
                // Ends and synchronously flushes the current session: without this the most recent activity, and the
                // session's true end time, would be lost on every clean shutdown.
                Analytics.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the analytics service cleanly");
            }

            try
            {
                Search.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to dispose the search service cleanly");
            }
        }
    }
}
