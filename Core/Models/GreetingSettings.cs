// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Controls the startup greeting toast, the shutdown goodbye toast,
    /// and short sound cues — the toasts' own, file operation outcomes
    /// (copy/move/delete success, or any error), and general navigation
    /// (opening a folder or file, expanding a tree node). The section
    /// is still named "Greeting" for settings.json backward
    /// compatibility with earlier versions of this app that only had
    /// the toast feature — it's grown to cover general sound feedback
    /// since, but splitting a handful of closely-related, purely
    /// cosmetic toggles into further sections wasn't worth the churn
    /// each time. Every toggle here is independent and has no effect
    /// on anything else in the app when turned off.
    /// </summary>
    public sealed class GreetingSettings
    {
        /// <summary>Whether a time-of-day-appropriate greeting toast shows once, briefly, when the app starts. Default: false.</summary>
        public bool EnableGreeting { get; set; }

        /// <summary>
        /// Whether a time-of-day-appropriate goodbye toast shows once,
        /// briefly, when the app is closing. When true, actually
        /// closing the window is delayed by GoodbyeDwellMs so there's
        /// time to see it — see MainWindow's OnWindowClosing. Default: false.
        /// </summary>
        public bool EnableGoodbye { get; set; }

        /// <summary>Whether a short system sound plays alongside the greeting/goodbye toasts specifically. Independent of the other sound toggles below. Default: true.</summary>
        public bool EnableSoundFeedback { get; set; } = true;

        /// <summary>
        /// Whether a short system sound plays for file operation
        /// outcomes — a distinct sound each for a fully successful
        /// copy, move, or delete, and a separate one for any operation
        /// that didn't fully succeed (a failure, partial failure; not a
        /// user-initiated cancel, which stays silent). Independent of
        /// the other sound toggles here. Default: true.
        /// </summary>
        public bool EnableActionSounds { get; set; } = true;

        /// <summary>
        /// Whether a short system sound plays for everyday navigation —
        /// opening a folder (in the file list or by expanding a node in
        /// the left tree) and opening a file. Independent of the other
        /// sound toggles here. Off by default, unlike the others: this
        /// is by far the most frequent trigger of the three sound
        /// features (every click while browsing, not just occasional
        /// operations or once-per-session toasts), so it's opt-in
        /// rather than opt-out — turn it on if you want it, rather than
        /// everyone hearing a beep on their very first click after
        /// upgrading. Default: false.
        /// </summary>
        public bool EnableNavigationSounds { get; set; } = false;

        /// <summary>
        /// Minimum time (ms) that must pass since the last navigation
        /// sound before another one is allowed to play — without this,
        /// fast browsing (holding an arrow key through a long tree,
        /// rapid double-clicks) would fire far more overlapping sounds
        /// than are actually useful as feedback, closer to noise than
        /// confirmation. A single click that happens to trigger both
        /// the tree's own expand sound and the center pane's folder-
        /// open sound in quick succession (they share the same sound,
        /// deliberately, for exactly this reason) is coalesced into
        /// what's heard as one beep by this same mechanism. Default:
        /// 150. Range: 0-2000 (0 disables throttling entirely).
        /// </summary>
        public int NavigationSoundThrottleMs { get; set; } = 150;

        /// <summary>
        /// How long (ms) the goodbye toast stays visible before the
        /// window actually closes. Only relevant when EnableGoodbye is
        /// true — the window closes immediately, as it always did
        /// before this feature existed, when it's false. Default: 1800.
        /// Range: 500-6000.
        /// </summary>
        public int GoodbyeDwellMs { get; set; } = 1800;

        public void Sanitize()
        {
            if (GoodbyeDwellMs < 500)
            {
                GoodbyeDwellMs = 500;
            }
            else if (GoodbyeDwellMs > 6000)
            {
                GoodbyeDwellMs = 6000;
            }

            if (NavigationSoundThrottleMs < 0)
            {
                NavigationSoundThrottleMs = 0;
            }
            else if (NavigationSoundThrottleMs > 2000)
            {
                NavigationSoundThrottleMs = 2000;
            }
        }
    }
}
