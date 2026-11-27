// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Short system sound cues — the greeting/goodbye toasts, file
    /// operation outcomes (copy/move/delete success, or any error), and
    /// everyday navigation (opening a folder or file, expanding a tree
    /// node). Every method is fire-and-forget, never throws, and
    /// respects its own relevant GreetingSettings toggle internally
    /// (EnableSoundFeedback, EnableActionSounds, or
    /// EnableNavigationSounds) — callers never need to check settings
    /// themselves before calling.
    /// </summary>
    public interface ISoundFeedbackService
    {
        void PlayGreeting();
        void PlayGoodbye();

        /// <summary>A batch copy completed fully successfully.</summary>
        void PlayCopySuccess();

        /// <summary>A batch move completed fully successfully.</summary>
        void PlayMoveSuccess();

        /// <summary>A batch delete completed fully successfully.</summary>
        void PlayDeleteSuccess();

        /// <summary>Any file operation ended in failure or partial failure (not a full clean success, and not a user-initiated cancel).</summary>
        void PlayError();

        /// <summary>A folder was opened — in the file list, or by expanding a node in the left tree. Throttled (see GreetingSettings.NavigationSoundThrottleMs) so fast browsing doesn't fire overlapping sounds.</summary>
        void PlayNavigate();

        /// <summary>A file was opened (launched via the OS). Throttled the same way as PlayNavigate.</summary>
        void PlayFileOpened();
    }
}
