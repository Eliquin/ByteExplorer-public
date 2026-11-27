// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The full-screen video window (see the XAML for the layout and the airspace constraint).
    /// <para>
    /// The video surface is the preview pane's own VideoView, moved into <c>VideoHost</c> by <see cref="Present"/> and
    /// handed back when the window closes. Pointing the player at a second VideoView does not work during playback:
    /// LibVLC reads the target window only when it creates its video output, so the picture would stay in the old surface
    /// while audio continued. Moving the control instead keeps the same native window: Avalonia re-parents a native
    /// control's window when the control is detached and re-attached within one dispatcher turn, instead of destroying it.
    /// The hand-back therefore happens in <see cref="OnClosing"/>, while this window's native handle still exists; once
    /// the window is destroyed its native children are gone with it, so a surface only recovered in
    /// <see cref="OnClosed"/> (an unusual close path) is reported as lost and the pane builds a fresh one.
    /// </para>
    /// <para>Nothing here starts or stops playback except the user's own clicks; the engine belongs to the pane.</para>
    /// </summary>
    public partial class FullScreenMediaWindow : Window
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(3);

        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _idleTimer;
        private MediaPlaybackEngine _engine;
        private Control _video;
        private Action<Control, bool> _returnVideo;

        public FullScreenMediaWindow()
        {
            InitializeComponent();

            _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => TransportBar.Refresh());
            _idleTimer = new DispatcherTimer(IdleHideDelay, DispatcherPriority.Background, OnIdleTimerTick);

            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            ControlBar.PointerMoved += (_, _) => ShowControlBar();
            ControlBar.PointerPressed += (_, _) => ShowControlBar();
            HotEdge.PointerEntered += (_, _) => ShowControlBar();
        }

        /// <summary>
        /// Takes over <paramref name="video"/> (already removed from its previous parent by the caller, in the same
        /// dispatcher turn) and shows the window full screen on <paramref name="owner"/>'s monitor.
        /// <paramref name="returnVideo"/> receives the surface back when the window closes, with true when the native
        /// surface is intact and can be re-inserted as is.
        /// </summary>
        public void Present(Window owner, MediaPlaybackEngine engine, Control video, Action<Control, bool> returnVideo)
        {
            _engine = engine;
            _video = video;
            _returnVideo = returnVideo;
            TransportBar.Engine = engine;

            // Full screen applies to the monitor the window is on, so start it there.
            Screen screen = owner?.Screens.ScreenFromWindow(owner) ?? Screens.Primary;
            if (screen != null)
            {
                Position = screen.Bounds.Position;
                Width = screen.Bounds.Width / screen.Scaling;
                Height = screen.Bounds.Height / screen.Scaling;
            }

            WindowState = WindowState.FullScreen;
            VideoHost.Child = video;

            if (owner != null)
            {
                Show(owner);
            }
            else
            {
                Show();
            }

            Activate();
            _refreshTimer.Start();
            ShowControlBar();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (!e.Cancel)
            {
                ReturnVideo(surfaceIntact: true);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            _idleTimer.Stop();
            TransportBar.Engine = null;
            ReturnVideo(surfaceIntact: false);
            _engine = null;
            base.OnClosed(e);
        }

        private void ReturnVideo(bool surfaceIntact)
        {
            Control video = _video;
            Action<Control, bool> returnVideo = _returnVideo;
            if (video == null)
            {
                return;
            }

            _video = null;
            _returnVideo = null;
            VideoHost.Child = null;
            returnVideo?.Invoke(video, surfaceIntact);
        }

        private void OnExitClick(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// Tunnelling, so Esc/F11 work wherever focus is (a slider or a button in the bar included). Space toggles playback
        /// unless a button has focus, where Space means "click it". Any key brings the hidden bar back.
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ShowControlBar();
            switch (e.Key)
            {
                case Key.Escape:
                case Key.F11:
                    e.Handled = true;
                    Close();
                    break;
                case Key.Space when e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not Button:
                    e.Handled = true;
                    if (_engine?.IsPlaying == true)
                    {
                        _engine.Pause();
                    }
                    else
                    {
                        _engine?.Play();
                    }

                    break;
            }
        }

        private void ShowControlBar()
        {
            if (!ControlBar.IsVisible)
            {
                ControlBar.IsVisible = true;
                HotEdge.IsVisible = false;
            }

            _idleTimer.Stop();
            _idleTimer.Start();
        }

        /// <summary>Hides the bar after a while without use, but only during playback and never under the pointer.</summary>
        private void OnIdleTimerTick(object sender, EventArgs e)
        {
            if (ControlBar.IsPointerOver || _engine?.IsPlaying != true)
            {
                return; // keep waiting; the timer keeps running
            }

            _idleTimer.Stop();
            ControlBar.IsVisible = false;
            HotEdge.IsVisible = true;
        }
    }
}
