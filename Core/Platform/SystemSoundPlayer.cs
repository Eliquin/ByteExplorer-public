// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.IO;

namespace ByteExplorer.Core.Platform
{
    /// <summary>The four system sounds the app uses for feedback (the names of Windows' SystemSounds).</summary>
    public enum FeedbackSound
    {
        Asterisk,
        Beep,
        Exclamation,
        Hand
    }

    /// <summary>
    /// Plays a system feedback sound on each OS without bundling audio:
    /// <list type="bullet">
    /// <item>Windows: System.Media.SystemSounds, as before (the user's sound scheme).</item>
    /// <item>macOS: afplay with a sound from /System/Library/Sounds (Glass, Pop, Funk, Basso).</item>
    /// <item>Linux: canberra-gtk-play with the freedesktop sound-theme event ids (dialog-information, bell,
    /// dialog-warning, dialog-error), which plays the desktop's own sound theme; paplay with the freedesktop theme's
    /// files as a fallback. If neither tool exists the sound is skipped, as a missing Windows scheme was.</item>
    /// </list>
    /// The player process is started and not waited for: feedback must never delay the operation it reports on.
    /// </summary>
    public static class SystemSoundPlayer
    {
        private static readonly string[] CanberraPaths = { "/usr/bin/canberra-gtk-play", "/usr/local/bin/canberra-gtk-play" };
        private static readonly string[] PaplayPaths = { "/usr/bin/paplay", "/usr/local/bin/paplay" };

        public static void Play(FeedbackSound sound)
        {
            if (OperatingSystem.IsWindows())
            {
                PlayWindows(sound);
            }
            else if (OperatingSystem.IsMacOS())
            {
                string name = sound switch
                {
                    FeedbackSound.Beep => "Pop",
                    FeedbackSound.Exclamation => "Funk",
                    FeedbackSound.Hand => "Basso",
                    _ => "Glass"
                };
                Start("/usr/bin/afplay", "/System/Library/Sounds/" + name + ".aiff");
            }
            else
            {
                string eventId = sound switch
                {
                    FeedbackSound.Beep => "bell",
                    FeedbackSound.Exclamation => "dialog-warning",
                    FeedbackSound.Hand => "dialog-error",
                    _ => "dialog-information"
                };

                string canberra = FirstExisting(CanberraPaths);
                if (canberra != null)
                {
                    Start(canberra, "-i", eventId);
                    return;
                }

                string paplay = FirstExisting(PaplayPaths);
                string file = "/usr/share/sounds/freedesktop/stereo/" + eventId + ".oga";
                if (paplay != null && File.Exists(file))
                {
                    Start(paplay, file);
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void PlayWindows(FeedbackSound sound)
        {
            System.Media.SystemSound systemSound = sound switch
            {
                FeedbackSound.Beep => System.Media.SystemSounds.Beep,
                FeedbackSound.Exclamation => System.Media.SystemSounds.Exclamation,
                FeedbackSound.Hand => System.Media.SystemSounds.Hand,
                _ => System.Media.SystemSounds.Asterisk
            };
            systemSound.Play();
        }

        private static string FirstExisting(string[] paths)
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static void Start(string program, params string[] arguments)
        {
            if (!File.Exists(program))
            {
                return;
            }

            var startInfo = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using (Process.Start(startInfo))
            {
                // Not waited for; disposing the handle does not stop the player.
            }
        }
    }
}
