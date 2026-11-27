// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using ByteExplorer.Infrastructure.DefaultApp;
using Serilog;

namespace ByteExplorer.Infrastructure.Launch
{
    /// <summary>The default-file-manager operations a maintenance command needs (a seam: tests pass a fake).</summary>
    public interface IDefaultFileManagerControl
    {
        bool IsSupported { get; }

        /// <summary>There is a registration to undo (see <see cref="DefaultFileManager.HasRegistration"/>).</summary>
        bool HasRegistration { get; }

        void SetDefault(bool on);
    }

    /// <summary>
    /// Command-line switches that do one job without a window and exit, run by <c>Program.Main</c> before the single-instance
    /// check and before Avalonia starts:
    /// <list type="bullet">
    /// <item><c>--unregister-default-file-manager</c>: gives folders back to the system's file manager when ByteExplorer was
    /// made the default (Settings › Folder options). The Windows uninstaller runs it, so folders open in File Explorer again
    /// once ByteExplorer is gone, instead of failing to start a deleted program.</item>
    /// <item><c>--register-default-file-manager</c>: makes ByteExplorer the default (for scripted setups).</item>
    /// </list>
    /// Exit codes: 0 done (or nothing to do), 1 failed (logged). <see cref="LaunchRequest"/> ignores these switches like any
    /// other "--" switch, and a start with one never hands anything to a running ByteExplorer.
    /// </summary>
    public static class MaintenanceCommand
    {
        public const string UnregisterDefaultFileManagerSwitch = "--unregister-default-file-manager";
        public const string RegisterDefaultFileManagerSwitch = "--register-default-file-manager";

        /// <summary>
        /// Runs the maintenance switch on the command line and returns the process's exit code, or null when the command line
        /// has none (a normal start). The first maintenance switch wins.
        /// </summary>
        public static int? TryRun(IReadOnlyList<string> args, IDefaultFileManagerControl control)
        {
            if (args == null)
            {
                return null;
            }

            foreach (string raw in args)
            {
                string arg = (raw ?? string.Empty).Trim();
                if (string.Equals(arg, UnregisterDefaultFileManagerSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    return Unregister(control ?? throw new ArgumentNullException(nameof(control)));
                }

                if (string.Equals(arg, RegisterDefaultFileManagerSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    return Register(control ?? throw new ArgumentNullException(nameof(control)));
                }
            }

            return null;
        }

        /// <summary>Whether <paramref name="arg"/> is one of these switches.</summary>
        public static bool IsMaintenanceSwitch(string arg)
        {
            string text = (arg ?? string.Empty).Trim();
            return string.Equals(text, UnregisterDefaultFileManagerSwitch, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, RegisterDefaultFileManagerSwitch, StringComparison.OrdinalIgnoreCase);
        }

        private static int Unregister(IDefaultFileManagerControl control)
        {
            try
            {
                if (!control.IsSupported || !control.HasRegistration)
                {
                    Log.Logger.Information("{Switch}: ByteExplorer is not registered as the file manager; nothing to do", UnregisterDefaultFileManagerSwitch);
                    return 0;
                }

                control.SetDefault(false);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "{Switch} failed", UnregisterDefaultFileManagerSwitch);
                return 1;
            }
        }

        private static int Register(IDefaultFileManagerControl control)
        {
            try
            {
                if (!control.IsSupported)
                {
                    Log.Logger.Warning("{Switch}: not possible on this system", RegisterDefaultFileManagerSwitch);
                    return 1;
                }

                control.SetDefault(true);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "{Switch} failed", RegisterDefaultFileManagerSwitch);
                return 1;
            }
        }

        /// <summary>The real one: <see cref="DefaultFileManager"/>.</summary>
        public static IDefaultFileManagerControl System { get; } = new SystemDefaultFileManager();

        private sealed class SystemDefaultFileManager : IDefaultFileManagerControl
        {
            public bool IsSupported => DefaultFileManager.IsSupported;

            public bool HasRegistration => DefaultFileManager.HasRegistration;

            public void SetDefault(bool on) => DefaultFileManager.SetDefault(on);
        }
    }
}
