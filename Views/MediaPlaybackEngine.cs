// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The preview pane's media player: one LibVLC instance and one MediaPlayer, driven from the UI thread but executed
    /// on a private serial worker.
    /// <para>
    /// Why a worker: every LibVLC call that changes playback state can block. Creating LibVLC loads (and on first run
    /// scans) the plugin cache, Stop joins the decoder and output threads, and both can take from hundreds of milliseconds
    /// to seconds on a slow or network file. The WPF panel made those calls on the UI thread, so the window stalled each
    /// time the selection moved off a playing file. Here the UI thread only enqueues; the worker runs one operation at a
    /// time, in order, so LibVLC never sees two concurrent state changes, and a burst of selections (arrow keys through a
    /// folder of videos) collapses: a load that has been superseded by a newer one is skipped before it opens anything.
    /// </para>
    /// <para>
    /// Why events instead of polling the player: the UI reads <see cref="Time"/>, <see cref="Length"/> and
    /// <see cref="State"/>, which are plain fields written by LibVLC's event callbacks. The UI thread therefore never
    /// calls into native code to refresh its display, and cannot race a disposal happening on the worker. The callbacks
    /// only store values; calling back into LibVLC from its own event thread can deadlock it.
    /// </para>
    /// <para>
    /// Disposal is deterministic: <see cref="Dispose"/> enqueues the teardown (stop, release the media, dispose the
    /// player and the LibVLC instance) behind whatever is pending, and nothing can be enqueued after it. The caller must
    /// detach the player from its VideoView on the UI thread first (VideoView talks to the player when detached).
    /// </para>
    /// </summary>
    public sealed class MediaPlaybackEngine : IDisposable
    {
        private const int ParseTimeoutMs = 3000;

        private readonly object _queueLock = new object();
        private readonly Queue<Action> _queue = new Queue<Action>();
        private readonly TaskCompletionSource<MediaPlayer> _playerReady = new TaskCompletionSource<MediaPlayer>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pumpRunning;
        private bool _closed;
        private bool _creationQueued;
        private int _loadGeneration;
        private int _volume;

        // Worker-only state.
        private LibVLC _libVlc;
        private MediaPlayer _player;
        private Media _media;

        // Published by LibVLC's event thread, read by the UI thread.
        private long _time;
        private long _length;
        private volatile int _state = (int)VLCState.NothingSpecial;
        private volatile bool _hasMedia;
        private volatile Media _parsingMedia;

        public MediaPlaybackEngine(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
        }

        /// <summary>Playback position in milliseconds.</summary>
        public long Time => Interlocked.Read(ref _time);

        /// <summary>Length of the loaded media in milliseconds, 0 while unknown.</summary>
        public long Length => Interlocked.Read(ref _length);

        public VLCState State => (VLCState)_state;

        /// <summary>True while a media is loaded (playing, paused, stopped or ended).</summary>
        public bool HasMedia => _hasMedia;

        /// <summary>Playing, or about to (opening/buffering): the state in which the play button shows "pause".</summary>
        public bool IsPlaying
        {
            get
            {
                VLCState state = State;
                return state == VLCState.Playing || state == VLCState.Opening || state == VLCState.Buffering;
            }
        }

        /// <summary>Playing or paused: a video frame is on screen and the position can be changed.</summary>
        public bool IsActive => IsPlaying || State == VLCState.Paused;

        /// <summary>The volume (0-100) applied to the player, kept across media and shared by every transport bar.</summary>
        public int Volume => Volatile.Read(ref _volume);

        /// <summary>
        /// Creates LibVLC and the player on the worker the first time it is called; later calls return the same task. The
        /// result is null when LibVLC cannot be created (libvlc missing or broken): the failure is logged once and media
        /// playback is simply unavailable. The caller attaches the player to its VideoView before the first
        /// <see cref="Load"/>, so LibVLC never opens a video window of its own.
        /// </summary>
        public Task<MediaPlayer> EnsurePlayerAsync()
        {
            lock (_queueLock)
            {
                if (_closed)
                {
                    return Task.FromResult<MediaPlayer>(null);
                }

                if (_creationQueued)
                {
                    return _playerReady.Task;
                }

                _creationQueued = true;
            }

            Enqueue(CreatePlayer);
            return _playerReady.Task;
        }

        /// <summary>
        /// Replaces the loaded media with <paramref name="path"/>, starting it when <paramref name="autoplay"/> is true.
        /// Audio files get ":no-video" so cover art or a stray video stream never opens a video output.
        /// </summary>
        public void Load(string path, bool isAudio, bool autoplay)
        {
            int generation = Interlocked.Increment(ref _loadGeneration);
            ResetPublishedState();
            Enqueue(() => LoadCore(generation, path, isAudio, autoplay));
        }

        /// <summary>Stops and releases the loaded media (the file is closed).</summary>
        public void Unload()
        {
            Interlocked.Increment(ref _loadGeneration);
            ResetPublishedState();
            Enqueue(ReleaseMedia);
        }

        public void Play() => Enqueue(() =>
        {
            if (_player == null || _media == null)
            {
                return;
            }

            // A player that reached the end (or failed) must be stopped before it can start again.
            VLCState state = _player.State;
            if (state == VLCState.Ended || state == VLCState.Error)
            {
                _player.Stop();
            }

            _player.Play();
        });

        public void Pause() => Enqueue(() => _player?.SetPause(true));

        /// <summary>Stops playback and rewinds; the media stays loaded so Play starts it again from the beginning.</summary>
        public void Stop() => Enqueue(() =>
        {
            _player?.Stop();
            Interlocked.Exchange(ref _time, 0);
        });

        public void Seek(long milliseconds) => Enqueue(() =>
        {
            if (_player != null && _player.IsSeekable)
            {
                _player.Time = Math.Max(0, milliseconds);
            }
        });

        public void SetVolume(int volume)
        {
            int clamped = Math.Clamp(volume, 0, 100);
            if (Interlocked.Exchange(ref _volume, clamped) == clamped)
            {
                return;
            }

            Enqueue(() =>
            {
                if (_player != null)
                {
                    _player.Volume = Volatile.Read(ref _volume);
                }
            });
        }

        public void Dispose()
        {
            lock (_queueLock)
            {
                if (_closed)
                {
                    return;
                }

                _queue.Enqueue(Teardown);
                _closed = true;
                StartPumpIfIdle();
            }

            Interlocked.Increment(ref _loadGeneration);
            _playerReady.TrySetResult(null);
        }

        private void Enqueue(Action work)
        {
            lock (_queueLock)
            {
                if (_closed)
                {
                    return;
                }

                _queue.Enqueue(work);
                StartPumpIfIdle();
            }
        }

        /// <summary>Must be called under _queueLock. The pump is a long-running task because LibVLC calls block.</summary>
        private void StartPumpIfIdle()
        {
            if (_pumpRunning)
            {
                return;
            }

            _pumpRunning = true;
            Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void Pump()
        {
            while (true)
            {
                Action work;
                lock (_queueLock)
                {
                    if (_queue.Count == 0)
                    {
                        _pumpRunning = false;
                        return;
                    }

                    work = _queue.Dequeue();
                }

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // A broken file or a LibVLC hiccup affects that one operation only.
                    Log.Logger.Debug(ex, "Preview media operation failed");
                }
            }
        }

        private void CreatePlayer()
        {
            try
            {
                // No file name drawn over the first seconds of every video; on Linux no Xlib-based outputs, since the
                // UI toolkit owns the Xlib connection (LibVLC's xcb outputs are thread-safe and stay available).
                _libVlc = MediaEngine.Shared ?? throw new InvalidOperationException("LibVLC is unavailable");
                _player = new MediaPlayer(_libVlc)
                {
                    // The app owns the keyboard and mouse: LibVLC's own input handling would swallow shortcuts while the
                    // video surface has focus.
                    EnableKeyInput = false,
                    EnableMouseInput = false,
                    Volume = Volatile.Read(ref _volume)
                };
                _player.TimeChanged += OnTimeChanged;
                _player.LengthChanged += OnLengthChanged;
                _player.Opening += OnOpening;
                _player.Buffering += OnBuffering;
                _player.Playing += OnPlaying;
                _player.Paused += OnPaused;
                _player.Stopped += OnStopped;
                _player.EndReached += OnEndReached;
                _player.EncounteredError += OnError;
                _playerReady.TrySetResult(_player);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "LibVLC failed to initialize — media preview playback is unavailable this session");
                DisposePlayerObjects();
                _playerReady.TrySetResult(null);
            }
        }

        private void LoadCore(int generation, string path, bool isAudio, bool autoplay)
        {
            if (_player == null || generation != Volatile.Read(ref _loadGeneration))
            {
                return; // superseded before it started: never open a file nobody is looking at any more
            }

            ReleaseMedia();
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            var media = new Media(_libVlc, path, FromType.FromPath);
            if (isAudio)
            {
                media.AddOption(":no-video");
            }

            _media = media;
            _player.Media = media;
            _hasMedia = true;
            _state = (int)VLCState.NothingSpecial;

            // The length is otherwise unknown until playback starts; a local parse fills the seek bar's total right away.
            media.DurationChanged += OnDurationChanged;
            _parsingMedia = media;
            Task<MediaParsedStatus> parse = media.Parse(MediaParseOptions.ParseLocal, ParseTimeoutMs);
            parse.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            if (autoplay && generation == Volatile.Read(ref _loadGeneration))
            {
                _player.Play();
            }
        }

        /// <summary>Worker: stops playback and disposes the current media, stopping a pending parse first.</summary>
        private void ReleaseMedia()
        {
            if (_player != null)
            {
                _player.Stop();
                _player.Media = null;
            }

            Media media = _media;
            _media = null;
            _hasMedia = false;
            if (media != null)
            {
                _parsingMedia = null;
                media.DurationChanged -= OnDurationChanged;
                media.ParseStop();
                media.Dispose();
            }

            Interlocked.Exchange(ref _time, 0);
            Interlocked.Exchange(ref _length, 0);
            _state = (int)VLCState.NothingSpecial;
        }

        private void Teardown()
        {
            try
            {
                ReleaseMedia();
            }
            finally
            {
                DisposePlayerObjects();
            }
        }

        private void DisposePlayerObjects()
        {
            if (_player != null)
            {
                _player.TimeChanged -= OnTimeChanged;
                _player.LengthChanged -= OnLengthChanged;
                _player.Opening -= OnOpening;
                _player.Buffering -= OnBuffering;
                _player.Playing -= OnPlaying;
                _player.Paused -= OnPaused;
                _player.Stopped -= OnStopped;
                _player.EndReached -= OnEndReached;
                _player.EncounteredError -= OnError;
                _player.Dispose();
                _player = null;
            }

            _libVlc = null; // the shared instance (MediaEngine.Shared) lives for the session

        }

        private void ResetPublishedState()
        {
            Interlocked.Exchange(ref _time, 0);
            Interlocked.Exchange(ref _length, 0);
            _state = (int)VLCState.NothingSpecial;
        }

        // ----- LibVLC event thread: store values only, never call back into LibVLC. -----

        private void OnTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e) => Interlocked.Exchange(ref _time, e.Time);

        private void OnLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            if (e.Length > 0)
            {
                Interlocked.Exchange(ref _length, e.Length);
            }
        }

        private void OnDurationChanged(object sender, MediaDurationChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _parsingMedia) && e.Duration > 0 && Interlocked.Read(ref _length) <= 0)
            {
                Interlocked.Exchange(ref _length, e.Duration);
            }
        }

        private void OnBuffering(object sender, MediaPlayerBufferingEventArgs e)
        {
            // Buffering events keep arriving during normal playback; only report it before the first frame.
            if (_state != (int)VLCState.Playing && _state != (int)VLCState.Paused)
            {
                _state = (int)VLCState.Buffering;
            }
        }

        // Each event maps to its state directly: querying the player's state from inside a LibVLC callback is a native
        // call on LibVLC's own event thread, which is exactly what must not happen there.
        private void OnOpening(object sender, EventArgs e) => _state = (int)VLCState.Opening;

        private void OnPlaying(object sender, EventArgs e) => _state = (int)VLCState.Playing;

        private void OnPaused(object sender, EventArgs e) => _state = (int)VLCState.Paused;

        private void OnStopped(object sender, EventArgs e) => _state = (int)VLCState.Stopped;

        private void OnEndReached(object sender, EventArgs e) => _state = (int)VLCState.Ended;

        private void OnError(object sender, EventArgs e) => _state = (int)VLCState.Error;
    }
}
