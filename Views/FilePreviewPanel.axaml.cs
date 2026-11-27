// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ByteExplorer.ViewModels;
using LibVLCSharp.Shared;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Code-behind of the preview panel: owns the media playback for the Media state and the Windows preview handler host
    /// for the Document state (<see cref="PreviewHandlerHost"/>). Everything else is bindings.
    /// <para>
    /// Lifetime. The engine (LibVLC + one MediaPlayer, see <see cref="MediaPlaybackEngine"/>) is created the first time a
    /// media file is previewed and torn down when the panel leaves the visual tree; the VideoView the first time a video
    /// needs it. Selecting another file never creates a new player: the engine swaps the media on its worker, skipping
    /// loads that a newer selection has already replaced, so racing through a folder of videos leaves no native players,
    /// threads or open files behind and never blocks the UI on a LibVLC call.
    /// </para>
    /// <para>
    /// Source of truth. The panel follows <see cref="FilePreviewViewModel.MediaPath"/> of the window's active file list. The
    /// active list changes with pane focus in split view, so the panel also follows
    /// <see cref="MainViewModel.FileListViewModel"/> (the WPF panel subscribed once, to the first pane's preview only, and
    /// kept playing the other pane's file after a focus change).
    /// </para>
    /// <para>
    /// Hidden panel. While a file plays, the panel's refresh timer also pauses playback when the panel is no longer visible
    /// (details pane hidden), so audio does not keep playing from a pane nobody can see; it is not resumed automatically.
    /// </para>
    /// </summary>
    public partial class FilePreviewPanel : UserControl
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

        private readonly DispatcherTimer _refreshTimer;
        private MainViewModel _mainViewModel;
        private FilePreviewViewModel _preview;
        private MediaPlaybackEngine _engine;
        private VideoView _videoView;
        private FullScreenMediaWindow _fullScreenWindow;
        private PreviewHandlerHost _documentHost;
        private string _currentMediaPath;
        private int _mediaRequest;
        private int _volume = 100;
        private bool _isAttached;

        public FilePreviewPanel()
        {
            InitializeComponent();
            _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, OnRefreshTick);
            MediaUnavailableHint.Text = UnavailableHint();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;
            HookMainViewModel(DataContext as MainViewModel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            HookMainViewModel(null);
            TearDownMedia();
            TearDownDocument();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_isAttached)
            {
                HookMainViewModel(DataContext as MainViewModel);
            }
        }

        // ------------------------------------------------------------------ view model tracking

        private void HookMainViewModel(MainViewModel mainViewModel)
        {
            if (!ReferenceEquals(_mainViewModel, mainViewModel))
            {
                if (_mainViewModel != null)
                {
                    _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
                }

                _mainViewModel = mainViewModel;
                if (_mainViewModel != null)
                {
                    _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
                }
            }

            HookPreview(_mainViewModel?.FileListViewModel?.Preview);
        }

        private void OnMainViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.FileListViewModel))
            {
                HookPreview(_mainViewModel?.FileListViewModel?.Preview);
            }
        }

        private void HookPreview(FilePreviewViewModel preview)
        {
            if (!ReferenceEquals(_preview, preview))
            {
                if (_preview != null)
                {
                    _preview.PropertyChanged -= OnPreviewPropertyChanged;
                }

                _preview = preview;
                if (_preview != null)
                {
                    _preview.PropertyChanged += OnPreviewPropertyChanged;
                }
            }

            ApplyMediaPath(_preview?.MediaPath);
            ApplyDocument();
        }

        private void OnPreviewPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // MediaPath is set only once a media file is confirmed (and after IsMediaAudio), and cleared on every new
            // selection, so it alone decides what the player holds.
            if (e.PropertyName == nameof(FilePreviewViewModel.MediaPath))
            {
                ApplyMediaPath(_preview?.MediaPath);
            }
            else if (e.PropertyName == nameof(FilePreviewViewModel.DocumentPath))
            {
                ApplyDocument();
            }
        }

        // ------------------------------------------------------------------ documents (Windows preview handlers)

        /// <summary>
        /// Follows <see cref="FilePreviewViewModel.DocumentPath"/>: shows it in the preview handler host (created here on
        /// first use, Windows only) or unloads the handler when the selection left the document, so no handler, file handle
        /// or prevhost.exe work outlives what the pane shows.
        /// </summary>
        private void ApplyDocument()
        {
            string path = _preview?.DocumentPath;
            if (string.IsNullOrEmpty(path) || !_isAttached || !OperatingSystem.IsWindows())
            {
                _documentHost?.Clear();
                return;
            }

            if (_documentHost == null)
            {
                _documentHost = new PreviewHandlerHost();
                _documentHost.Failed += OnDocumentPreviewFailed;
                DocumentHost.Child = _documentHost;
            }

            _documentHost.Show(path, _preview.DocumentHandler);
        }

        private void OnDocumentPreviewFailed(object sender, string path) => _preview?.DocumentPreviewFailed(path);

        private void TearDownDocument()
        {
            if (_documentHost != null)
            {
                _documentHost.Clear();
                _documentHost.Failed -= OnDocumentPreviewFailed;
                DocumentHost.Child = null; // destroys the native window (and with it anything the handler still holds)
                _documentHost = null;
            }
        }

        // ------------------------------------------------------------------ playback

        private void ApplyMediaPath(string path)
        {
            if (string.Equals(path, _currentMediaPath, StringComparison.Ordinal))
            {
                return;
            }

            _currentMediaPath = path;
            int request = ++_mediaRequest;

            // A full-screen window must never keep showing a file the selection has left.
            CloseFullScreen();

            if (string.IsNullOrEmpty(path))
            {
                _engine?.Unload();
                _refreshTimer.Stop();
                TransportBar.Refresh();
                FullScreenButton.IsEnabled = false;
                return;
            }

            if (!MediaEngine.IsAvailable)
            {
                ShowMediaUnavailable(true);
                return;
            }

            LoadMedia(path, _preview?.IsMediaAudio == true, request);
        }

        private async void LoadMedia(string path, bool isAudio, int request)
        {
            try
            {
                _engine ??= new MediaPlaybackEngine(_volume);
                MediaPlaybackEngine engine = _engine;
                MediaPlayer player = await engine.EnsurePlayerAsync();

                // Superseded while LibVLC was starting (another selection, or the panel went away).
                if (request != _mediaRequest || !_isAttached || !ReferenceEquals(engine, _engine))
                {
                    return;
                }

                if (player == null)
                {
                    ShowMediaUnavailable(true);
                    return;
                }

                ShowMediaUnavailable(false);
                if (!isAudio)
                {
                    // LibVLC must have its target surface before it starts, or it opens a video window of its own.
                    AttachVideoView(player);
                }

                TransportBar.Engine = engine;
                engine.Load(path, isAudio, AutoplayCheckBox.IsChecked == true);
                _refreshTimer.Start();
                OnRefreshTick(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Failed to prepare media preview for {Path}", path);
            }
        }

        /// <summary>
        /// Creates the VideoView on first use and binds the player to it. (VideoView also hands its window to the player
        /// again whenever the native window is recreated, so moving it to full screen needs no re-binding; the null-then-
        /// player assignment below is kept as a cheap guarantee that the current handle is passed.)
        /// </summary>
        private void AttachVideoView(MediaPlayer player)
        {
            if (_videoView == null)
            {
                _videoView = new VideoView();
                VideoHost.Child = _videoView;
            }

            _videoView.MediaPlayer = null;
            _videoView.MediaPlayer = player;
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            MediaPlaybackEngine engine = _engine;
            if (engine == null)
            {
                _refreshTimer.Stop();
                return;
            }

            if (engine.IsPlaying && _fullScreenWindow == null && !IsEffectivelyVisible)
            {
                engine.Pause();
            }

            TransportBar.Refresh();
            FullScreenButton.IsEnabled = _videoView != null && _preview?.IsMediaAudio == false && engine.IsActive;
        }

        private void ShowMediaUnavailable(bool unavailable)
        {
            MediaUnavailablePanel.IsVisible = unavailable;
            MediaPlayerArea.IsVisible = !unavailable;
        }

        private static string UnavailableHint()
        {
            if (OperatingSystem.IsWindows())
            {
                return "The VLC libraries shipped with ByteExplorer could not be loaded.";
            }

            return OperatingSystem.IsMacOS()
                ? "Install VLC in Applications to preview audio and video."
                : "Install VLC (libvlc) to preview audio and video.";
        }

        /// <summary>
        /// Releases every native media object: closes full screen, detaches the player from the VideoView on the UI thread
        /// (the VideoView calls into the player when detached, so this must precede the player's disposal), then disposes the
        /// engine, which stops and frees LibVLC on its worker.
        /// </summary>
        private void TearDownMedia()
        {
            _mediaRequest++;
            _currentMediaPath = null;
            CloseFullScreen();
            _refreshTimer.Stop();
            TransportBar.Engine = null;

            if (_videoView != null)
            {
                _videoView.MediaPlayer = null;
                VideoHost.Child = null;
                _videoView = null;
            }

            if (_engine != null)
            {
                _volume = _engine.Volume;
                _engine.Dispose();
                _engine = null;
            }

            FullScreenButton.IsEnabled = false;
        }

        // ------------------------------------------------------------------ full screen

        /// <summary>
        /// Moves the VideoView into a full-screen window. The removal and the insertion happen in the same dispatcher turn,
        /// which is what keeps the native surface (and the picture LibVLC is drawing into it) alive across the move; see
        /// <see cref="FullScreenMediaWindow"/>.
        /// </summary>
        private void OnFullScreenClick(object sender, RoutedEventArgs e)
        {
            if (_fullScreenWindow != null || _videoView == null || _engine == null || !_engine.IsActive)
            {
                return;
            }

            VideoView video = _videoView;
            var window = new FullScreenMediaWindow();
            try
            {
                VideoHost.Child = null;
                _fullScreenWindow = window;
                window.Closed += OnFullScreenWindowClosed;
                window.Present(TopLevel.GetTopLevel(this) as Window, _engine, video, OnVideoReturned);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to enter full-screen preview playback");
                window.Closed -= OnFullScreenWindowClosed;
                _fullScreenWindow = null;
                try
                {
                    window.Close();
                }
                catch (Exception closeEx)
                {
                    Log.Logger.Debug(closeEx, "Failed to close a half-opened full-screen window");
                }

                // Close hands the surface back through OnVideoReturned; if the window never got it, put it back here.
                if (video.Parent == null && ReferenceEquals(video, _videoView))
                {
                    VideoHost.Child = video;
                }
            }
        }

        /// <summary>
        /// The full-screen window gives the surface back while closing. An intact surface goes straight back into the panel
        /// (same dispatcher turn, same native window, playback uninterrupted). A lost surface (the window was destroyed
        /// first, or the panel is going away) cannot carry the picture any more, and LibVLC only picks up a new target when
        /// it next starts its video output: playback is stopped and, if the panel is still showing the video, a fresh
        /// VideoView is bound so the next Play draws into the panel instead of a window LibVLC would open on its own.
        /// </summary>
        private void OnVideoReturned(Control video, bool surfaceIntact)
        {
            if (!ReferenceEquals(video, _videoView))
            {
                return; // the panel tore its media down meanwhile; the surface is simply dropped
            }

            if (surfaceIntact && _isAttached)
            {
                VideoHost.Child = video;
                return;
            }

            _videoView.MediaPlayer = null;
            _videoView = null;
            _engine?.Stop();
            Dispatcher.UIThread.Post(RebindAfterLostSurface, DispatcherPriority.Background);
        }

        private async void RebindAfterLostSurface()
        {
            try
            {
                MediaPlaybackEngine engine = _engine;
                if (!_isAttached || engine == null || _videoView != null || _preview?.IsMediaAudio != false || string.IsNullOrEmpty(_currentMediaPath))
                {
                    return;
                }

                MediaPlayer player = await engine.EnsurePlayerAsync();
                if (player != null && _isAttached && ReferenceEquals(engine, _engine) && _videoView == null)
                {
                    AttachVideoView(player);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Failed to rebuild the preview video surface after full screen");
            }
        }

        private void OnFullScreenWindowClosed(object sender, EventArgs e)
        {
            if (sender is FullScreenMediaWindow window)
            {
                window.Closed -= OnFullScreenWindowClosed;
                if (ReferenceEquals(window, _fullScreenWindow))
                {
                    _fullScreenWindow = null;
                }
            }
        }

        private void CloseFullScreen()
        {
            FullScreenMediaWindow window = _fullScreenWindow;
            if (window == null)
            {
                return;
            }

            try
            {
                window.Close(); // hands the surface back (OnVideoReturned) and clears _fullScreenWindow (Closed)
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Failed to close the full-screen preview window cleanly");
                _fullScreenWindow = null;
            }
        }
    }
}
