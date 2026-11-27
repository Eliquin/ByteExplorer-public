// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Hands files, folders and commands to the operating system: open with the default application, open with a chosen
    /// application, open a terminal in a folder, show an item in the system file manager.
    /// <para>
    /// Arguments always go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined command string, so a file
    /// name containing spaces, quotes or shell metacharacters reaches the program as exactly one argument and is never
    /// interpreted by a shell. Every method returns once the program is started; nothing waits for it to exit.
    /// </para>
    /// </summary>
    public static class ShellLauncher
    {
        /// <summary>
        /// Opens a file or folder with its default application: ShellExecute on Windows (which also resolves .lnk
        /// shortcuts), "open" on macOS, xdg-open on Linux (the desktop's MIME associations).
        /// </summary>
        public static void Open(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A path is required.", nameof(path));
            }

            if (OperatingSystem.IsWindows())
            {
                StartDetached(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Run("/usr/bin/open", path);
            }
            else
            {
                Run(FindOnPath("xdg-open") ?? throw new FileNotFoundException("xdg-open was not found; install xdg-utils to open files."), path);
            }
        }

        /// <summary>Opens a file with a specific application (an <see cref="OpenWithApplication"/> from the list).</summary>
        public static void OpenWith(string path, OpenWithApplication application)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var startInfo = new ProcessStartInfo(application.Executable) { UseShellExecute = false };
            bool pathPlaced = false;
            foreach (string argument in application.Arguments)
            {
                if (argument == OpenWithApplication.PathPlaceholder)
                {
                    startInfo.ArgumentList.Add(path);
                    pathPlaced = true;
                }
                else
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            if (!pathPlaced)
            {
                startInfo.ArgumentList.Add(path);
            }

            StartDetached(startInfo);
        }

        /// <summary>
        /// Lets the person choose an application the system knows about: Windows' own "Open with" dialog; on macOS and
        /// Linux the caller shows the list from <see cref="OpenWithApplications.For"/> instead (there is no system dialog
        /// to call), so this returns false there.
        /// </summary>
        public static bool TryShowSystemOpenWithDialog(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string rundll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
            Run(rundll, "shell32.dll,OpenAs_RunDLL", path);
            return true;
        }

        /// <summary>
        /// Opens a terminal whose working directory is the folder. Windows: Windows Terminal if installed, else cmd.exe.
        /// macOS: Terminal.app. Linux: $TERMINAL, then the Debian/Ubuntu x-terminal-emulator alternative, then the
        /// common terminals with their own working-directory flags.
        /// </summary>
        public static void OpenTerminal(string folder)
        {
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException(folder);
            }

            if (OperatingSystem.IsWindows())
            {
                string windowsTerminal = FindOnPath("wt.exe");
                if (windowsTerminal != null)
                {
                    Run(windowsTerminal, "-d", folder);
                }
                else
                {
                    StartDetached(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true });
                }

                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Run("/usr/bin/open", "-a", "Terminal", folder);
                return;
            }

            string fromEnvironment = Environment.GetEnvironmentVariable("TERMINAL");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                string resolved = Path.IsPathRooted(fromEnvironment) ? fromEnvironment : FindOnPath(fromEnvironment);
                if (resolved != null && File.Exists(resolved))
                {
                    StartDetached(new ProcessStartInfo(resolved) { WorkingDirectory = folder, UseShellExecute = false });
                    return;
                }
            }

            // (program, arguments before the folder); a null flag means "set the working directory only".
            (string Program, string Flag)[] terminals =
            {
                ("x-terminal-emulator", null),
                ("gnome-terminal", "--working-directory="),
                ("kgx", "--working-directory="),
                ("ptyxis", "--working-directory="),
                ("konsole", "--workdir"),
                ("xfce4-terminal", "--working-directory="),
                ("mate-terminal", "--working-directory="),
                ("tilix", "--working-directory="),
                ("kitty", "--directory"),
                ("alacritty", "--working-directory"),
                ("wezterm", null),
                ("foot", null),
                ("lxterminal", "--working-directory="),
                ("xterm", null)
            };

            foreach ((string program, string flag) in terminals)
            {
                string executable = FindOnPath(program);
                if (executable == null)
                {
                    continue;
                }

                var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = folder, UseShellExecute = false };
                if (flag != null)
                {
                    if (flag.EndsWith("=", StringComparison.Ordinal))
                    {
                        startInfo.ArgumentList.Add(flag + folder);
                    }
                    else
                    {
                        startInfo.ArgumentList.Add(flag);
                        startInfo.ArgumentList.Add(folder);
                    }
                }

                StartDetached(startInfo);
                return;
            }

            throw new FileNotFoundException("No terminal emulator was found. Set the TERMINAL environment variable to your terminal's path.");
        }

        /// <summary>
        /// Shows the item selected in the system file manager (Explorer /select, Finder -R, or the containing folder
        /// through xdg-open on Linux, where there is no standard "select" call).
        /// </summary>
        public static void RevealInSystemFileManager(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select," + path);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Run("/usr/bin/open", "-R", path);
            }
            else
            {
                Open(Directory.Exists(path) ? path : PathUtil.GetParent(path) ?? path);
            }
        }

        /// <summary>The full path of a program found in PATH, or null.</summary>
        public static string FindOnPath(string program)
        {
            if (string.IsNullOrEmpty(program))
            {
                return null;
            }

            if (Path.IsPathRooted(program))
            {
                return File.Exists(program) ? program : null;
            }

            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return null;
            }

            foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), program);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry: skip it.
                }
            }

            return null;
        }

        private static void Run(string program, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            StartDetached(startInfo);
        }

        private static void StartDetached(ProcessStartInfo startInfo)
        {
            if (!startInfo.UseShellExecute)
            {
                startInfo.RedirectStandardInput = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
            }

            try
            {
                using (Process.Start(startInfo))
                {
                    // Handed off; the handle is released, the program keeps running.
                }
            }
            catch (Win32Exception ex)
            {
                // Translate "program not found / no association" into a message a person can act on.
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }
}
