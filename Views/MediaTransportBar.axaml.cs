// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ByteExplorer.Icons;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Transport controls bound to a <see cref="MediaPlaybackEngine"/> (see the XAML for why values are pushed, not bound).
    /// <para>
    /// Seeking: while the pointer holds the seek bar (a press on the track jumps there and drags), <see cref="Refresh"/>
    /// leaves the slider alone, so playback does not yank the thumb back every tick; the seek is applied once, on release,
    /// because seeking on every intermediate value stutters with most codecs. Keyboard changes (arrow keys, Home/End) seek
    /// immediately. Changes made by <see cref="Refresh"/> itself are ignored by the value handlers.
    /// </para>
    /// </summary>
    public partial class MediaTransportBar : UserControl
    {
        private MediaPlaybackEngine _engine;
        private bool _isSeekDragging;
        private bool _updatingFromEngine;
        private bool _lastIsPlaying;
        private long _lastTime = -1;
        private long _lastLength = -1;
        private int _lastVolume = -1;

        public MediaTransportBar()
        {
            InitializeComponent();

            SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            SeekSlider.AddHandler(PointerCaptureLostEvent, OnSeekPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            SeekSlider.ValueChanged += OnSeekValueChanged;
            VolumeSlider.ValueChanged += OnVolumeValueChanged;
        }

        /// <summary>The engine these controls drive; null disables them.</summary>
        public MediaPlaybackEngine Engine
        {
            get => _engine;
            set
            {
                if (ReferenceEquals(_engine, value))
                {
                    return;
                }

                _engine = value;
                _isSeekDragging = false;
                _lastTime = _lastLength = -1;
                _lastVolume = -1;
                Refresh();
            }
        }

        /// <summary>
        /// Brings the controls up to date with the engine. Called by the owner's timer; cheap when nothing changed (every
        /// control is written only when its value differs).
        /// </summary>
        public void Refresh()
        {
            MediaPlaybackEngine engine = _engine;
            bool hasMedia = engine != null && engine.HasMedia;
            bool isPlaying = hasMedia && engine.IsPlaying;
            long length = hasMedia ? engine.Length : 0;
            long time = hasMedia ? Math.Min(engine.Time, length > 0 ? length : long.MaxValue) : 0;

            _updatingFromEngine = true;
            try
            {
                PlayPauseButton.IsEnabled = hasMedia;
                StopButton.IsEnabled = hasMedia && engine.State != LibVLCSharp.Shared.VLCState.Stopped && engine.State != LibVLCSharp.Shared.VLCState.NothingSpecial;
                SeekSlider.IsEnabled = hasMedia && length > 0 && engine.IsActive;

                if (isPlaying != _lastIsPlaying)
                {
                    _lastIsPlaying = isPlaying;
                    PlayPauseIcon.Symbol = isPlaying ? Symbol.Pause : Symbol.Play;
                    ToolTip.SetTip(PlayPauseButton, isPlaying ? "Pause" : "Play");
                }

                if (length != _lastLength)
                {
                    _lastLength = length;
                    SeekSlider.Maximum = Math.Max(1, length);
                    TotalText.Text = FormatTime(length);
                }

                if (!_isSeekDragging && time != _lastTime)
                {
                    _lastTime = time;
                    SeekSlider.Value = time;
                    ElapsedText.Text = FormatTime(time);
                }

                int volume = engine?.Volume ?? (int)VolumeSlider.Value;
                if (volume != _lastVolume)
                {
                    _lastVolume = volume;
                    VolumeSlider.Value = volume;
                    VolumeIcon.Symbol = volume == 0 ? Symbol.SpeakerMute : Symbol.Speaker2;
                }
            }
            finally
            {
                _updatingFromEngine = false;
            }
        }

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_engine == null)
            {
                return;
            }

            if (_engine.IsPlaying)
            {
                _engine.Pause();
            }
            else
            {
                _engine.Play();
            }
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            _engine?.Stop();
            _updatingFromEngine = true;
            SeekSlider.Value = 0;
            ElapsedText.Text = FormatTime(0);
            _lastTime = 0;
            _updatingFromEngine = false;
        }

        private void OnSeekPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (SeekSlider.IsEnabled && e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed)
            {
                _isSeekDragging = true;
            }
        }

        private void OnSeekPointerReleased(object sender, PointerReleasedEventArgs e) => CommitSeekDrag();

        private void OnSeekPointerCaptureLost(object sender, PointerCaptureLostEventArgs e) => CommitSeekDrag();

        private void CommitSeekDrag()
        {
            if (!_isSeekDragging)
            {
                return;
            }

            _isSeekDragging = false;
            _engine?.Seek((long)SeekSlider.Value);
        }

        private void OnSeekValueChanged(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingFromEngine)
            {
                return;
            }

            ElapsedText.Text = FormatTime((long)e.NewValue);
            if (!_isSeekDragging)
            {
                _engine?.Seek((long)e.NewValue);
            }
        }

        private void OnVolumeValueChanged(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingFromEngine)
            {
                return;
            }

            int volume = (int)Math.Round(e.NewValue);
            _lastVolume = volume;
            VolumeIcon.Symbol = volume == 0 ? Symbol.SpeakerMute : Symbol.Speaker2;
            _engine?.SetVolume(volume);
        }

        /// <summary>"m:ss", or "h:mm:ss" from one hour on.</summary>
        internal static string FormatTime(long milliseconds)
        {
            TimeSpan span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
                : $"{span.Minutes}:{span.Seconds:D2}";
        }
    }
}
