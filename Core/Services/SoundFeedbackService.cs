// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Plays short, built-in system notification sounds (Windows'
    /// SystemSounds, macOS's /System/Library/Sounds, the Linux desktop's
    /// sound theme; see Platform.SystemSoundPlayer — no audio files to
    /// bundle or manage) for the
    /// greeting/goodbye toasts, file operation outcomes, and everyday
    /// navigation. SystemSound.Play() is itself non-blocking (it hands
    /// off to the OS's own async sound-playback mechanism), so these
    /// calls are safe on the UI thread and can never contribute to the
    /// "zero lag" concern the rest of this app is built around.
    ///
    /// Three independent settings gate three independent groups of
    /// sounds — EnableSoundFeedback (greeting/goodbye), EnableActionSounds
    /// (copy/move/delete/error), and EnableNavigationSounds (folder/file
    /// open) — any one can be turned off without affecting the others.
    /// </summary>
    public sealed class SoundFeedbackService : ISoundFeedbackService
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;
        private readonly object _throttleLock = new object();
        private readonly Dictionary<string, DateTime> _lastPlayUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public SoundFeedbackService(ISettingsService settingsService, ILogger logger = null)
        {
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<SoundFeedbackService>();
        }

        private bool IsGreetingSoundEnabled => _settingsService?.Current?.Greeting?.EnableSoundFeedback ?? true;

        private bool IsActionSoundEnabled => _settingsService?.Current?.Greeting?.EnableActionSounds ?? true;

        private bool IsNavigationSoundEnabled => _settingsService?.Current?.Greeting?.EnableNavigationSounds ?? false;

        public void PlayGreeting() => PlaySafely(FeedbackSound.Asterisk, IsGreetingSoundEnabled);

        public void PlayGoodbye() => PlaySafely(FeedbackSound.Asterisk, IsGreetingSoundEnabled);

        // Deliberately distinct built-in sounds per outcome, all from
        // System.Media.SystemSounds (the fixed set Windows itself
        // ships — no custom audio assets involved): Asterisk (a soft,
        // neutral "info" tone) for copy, Beep (the plain default beep)
        // for move to distinguish it from copy at a glance/listen,
        // Exclamation (a mild "heads up" tone) for delete since it's
        // the one destructive operation of the three, and Hand (the
        // classic Windows "error"/stop tone) for anything that didn't
        // fully succeed.
        public void PlayCopySuccess() => PlaySafely(FeedbackSound.Asterisk, IsActionSoundEnabled);

        public void PlayMoveSuccess() => PlaySafely(FeedbackSound.Beep, IsActionSoundEnabled);

        public void PlayDeleteSuccess() => PlaySafely(FeedbackSound.Exclamation, IsActionSoundEnabled);

        public void PlayError() => PlaySafely(FeedbackSound.Hand, IsActionSoundEnabled);

        // Folder-open (file list or tree) and file-open deliberately
        // share PlayNavigate/PlayFileOpened's own distinct sound rather
        // than reusing any of the action sounds above — navigation is a
        // completely different category of feedback (confirming "you
        // moved somewhere", not "an operation finished"), and giving it
        // its own pair keeps it unambiguous even though both groups can
        // in principle be enabled at once. Both are throttled — see
        // TryClaimThrottleSlot — since this is by far the highest-
        // frequency trigger of any sound this service plays.
        public void PlayNavigate()
        {
            if (!IsNavigationSoundEnabled || !TryClaimThrottleSlot("navigate"))
            {
                return;
            }

            PlaySafely(FeedbackSound.Beep, true);
        }

        public void PlayFileOpened()
        {
            if (!IsNavigationSoundEnabled || !TryClaimThrottleSlot("file-opened"))
            {
                return;
            }

            PlaySafely(FeedbackSound.Asterisk, true);
        }

        /// <summary>
        /// Returns true (and reserves the slot) only if at least
        /// NavigationSoundThrottleMs has passed since the last
        /// successful claim under this same key — false otherwise,
        /// meaning the caller should skip playing anything this time.
        /// A shared, keyed throttle (rather than one fixed field per
        /// sound) specifically so a single user gesture that happens to
        /// trigger both PlayNavigate call sites in quick succession (a
        /// tree node's own expand, and the center pane opening the same
        /// folder a moment later) coalesces into one audible beep
        /// instead of two — both calls contend for the SAME "navigate"
        /// key, so only the first one through actually plays.
        /// </summary>
        private bool TryClaimThrottleSlot(string key)
        {
            int throttleMs = _settingsService?.Current?.Greeting?.NavigationSoundThrottleMs ?? 150;
            if (throttleMs <= 0)
            {
                return true;
            }

            lock (_throttleLock)
            {
                DateTime now = DateTime.UtcNow;
                if (_lastPlayUtc.TryGetValue(key, out DateTime last) && (now - last).TotalMilliseconds < throttleMs)
                {
                    return false;
                }

                _lastPlayUtc[key] = now;
                return true;
            }
        }

        private void PlaySafely(FeedbackSound sound, bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            try
            {
                SystemSoundPlayer.Play(sound);
            }
            catch (Exception ex)
            {
                // A missing/misconfigured system sound scheme, audio
                // hardware issues, whatever — this is pure cosmetic
                // feedback and must never be able to disrupt anything
                // else, especially not a file operation or navigation
                // that already succeeded or failed on its own terms.
                _logger.Debug(ex, "Failed to play a feedback sound");
            }
        }
    }
}
