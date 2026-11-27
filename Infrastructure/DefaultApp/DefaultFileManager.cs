// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using Microsoft.Win32;
using Serilog;

namespace ByteExplorer.Infrastructure.DefaultApp
{
    /// <summary>
    /// "Make ByteExplorer the default file explorer" (Settings › Folder options): opens folders, drives and Win+E with
    /// ByteExplorer on Windows (<see cref="WindowsFileManagerRegistration"/>), and folders on Linux
    /// (<see cref="LinuxFileManagerRegistration"/>). macOS can't replace Finder, so it is unavailable there. The state
    /// is the system's, read each time, never a copy in the settings: what the switch shows is what the system does.
    /// </summary>
    public static class DefaultFileManager
    {
        /// <summary>Whether this system lets ByteExplorer become its file manager.</summary>
        public static bool IsSupported => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && ShellLauncher.FindOnPath("xdg-mime") != null);

        /// <summary>Why it can't (shown under the switch), or null.</summary>
        public static string UnsupportedReason =>
            IsSupported ? null
            : OperatingSystem.IsMacOS() ? "macOS always opens folders in Finder; no app can replace it"
            : "Needs xdg-mime (the xdg-utils package), which this system doesn't have";

        /// <summary>ByteExplorer opens folders now.</summary>
        public static bool IsDefault
        {
            get
            {
                try
                {
                    return OperatingSystem.IsWindows() ? Windows().IsRegistered
                        : IsSupported && Linux().IsRegistered;
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Could not read the default file manager");
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether there is a registration to undo: ByteExplorer is the default, or (Windows) its verb or backup is still in
        /// the registry although another program took over since. What <c>--unregister-default-file-manager</c> checks.
        /// </summary>
        public static bool HasRegistration
        {
            get
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        WindowsFileManagerRegistration registration = Windows();
                        return registration.IsRegistered || registration.HasTraces;
                    }

                    return IsSupported && Linux().IsRegistered;
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Could not read the default file manager");
                    return false;
                }
            }
        }

        /// <summary>Makes ByteExplorer the default (true) or gives folders back to the system's file manager (false). Throws on failure.</summary>
        public static void SetDefault(bool on)
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException(UnsupportedReason);
            }

            IReadOnlyList<string> program = ProgramCommand();
            if (OperatingSystem.IsWindows())
            {
                WindowsFileManagerRegistration registration = Windows();
                if (on)
                {
                    registration.Register(
                        WindowsFileManagerRegistration.BuildCommand(program, Launch.LaunchRequest.FromShellSwitch, "%1"),
                        WindowsFileManagerRegistration.BuildCommand(program, Launch.LaunchRequest.FromShellSwitch),
                        Environment.ProcessPath + ",0");
                    WriteRestoreScript(registration.BuildRestoreScript());
                }
                else
                {
                    registration.Unregister();
                    DeleteRestoreScript();
                }

                NotifyAssociationsChanged();
            }
            else if (on)
            {
                Linux().Register(program, LinuxIconPath());
            }
            else
            {
                Linux().Unregister();
            }

            Log.Logger.Information("ByteExplorer is {State} the default file manager", on ? "now" : "no longer");
        }

        /// <summary>
        /// At start: when ByteExplorer is the default but the program registered is gone (the app was moved or updated to
        /// another folder), the registration is pointed at this copy, so folders keep opening. A registration of another
        /// copy that still exists is left to it.
        /// </summary>
        public static void RepairIfMoved()
        {
            try
            {
                if (!IsSupported || !IsDefault)
                {
                    return;
                }

                string registered = OperatingSystem.IsWindows()
                    ? WindowsFileManagerRegistration.ProgramOf(Windows().RegisteredCommand)
                    : WindowsFileManagerRegistration.ProgramOf(Linux().RegisteredExec?.Replace("\\\\", "\\"));
                IReadOnlyList<string> program = ProgramCommand();
                if (string.IsNullOrEmpty(registered) || File.Exists(registered) || PathUtil.AreSame(registered, program[0]))
                {
                    return;
                }

                Log.Logger.Information("The default file manager pointed at {Old}, which is gone; now {New}", registered, program[0]);
                SetDefault(true);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not update the default file manager registration");
            }
        }

        /// <summary>
        /// Where the Windows undo file is kept: %LOCALAPPDATA%\ByteExplorer, outside the app's own (portable) folder, so it
        /// is still there if that folder is deleted.
        /// </summary>
        public static string RestoreScriptPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteExplorer", "restore-file-explorer.reg");

        /// <summary>The program and its first arguments that start this copy: the app itself, or "dotnet ByteExplorer.dll".</summary>
        internal static IReadOnlyList<string> ProgramCommand()
        {
            string processPath = Environment.ProcessPath ?? string.Empty;
            bool dotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            if (dotnetHost)
            {
                string assembly = typeof(DefaultFileManager).Assembly.Location;
                return new[] { processPath, assembly };
            }

            return new[] { processPath };
        }

        [SupportedOSPlatform("windows")]
        private static WindowsFileManagerRegistration Windows() => new WindowsFileManagerRegistration(new CurrentUserRegistry());

        private static LinuxFileManagerRegistration Linux()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return new LinuxFileManagerRegistration(Run,
                Path.Combine(string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "applications"),
                string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome,
                AppPaths.Combine("default-file-manager.json"));
        }

        /// <summary>The app's icon as a file the desktop can show (the PNGs are embedded resources): written to the data folder.</summary>
        private static string LinuxIconPath()
        {
            try
            {
                string path = AppPaths.Combine("byteexplorer-icon.png");
                if (!File.Exists(path))
                {
                    using Stream icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ByteExplorer/Assets/Icon/ByteExplorer-64.png"));
                    using FileStream file = File.Create(path);
                    icon.CopyTo(file);
                }

                return path;
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "No icon file for the desktop entry");
                return null;
            }
        }

        /// <summary>Runs a program from PATH (no shell) and returns its exit code and output; a missing program is exit code 127.</summary>
        private static (int ExitCode, string Output) Run(string program, string[] arguments)
        {
            string path = ShellLauncher.FindOnPath(program);
            if (path == null)
            {
                return (127, string.Empty);
            }

            var info = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info);
            if (process == null)
            {
                return (127, string.Empty);
            }

            var errors = new StringBuilder();
            process.ErrorDataReceived += (_, e) => errors.Append(e.Data);
            process.BeginErrorReadLine();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // It exited just now.
                }

                return (124, output);
            }

            if (process.ExitCode != 0)
            {
                Log.Logger.Debug("{Program} exited with {Code}: {Errors}", program, process.ExitCode, errors.ToString());
            }

            return (process.ExitCode, output);
        }

        private static void WriteRestoreScript(string script)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RestoreScriptPath)!);
                File.WriteAllText(RestoreScriptPath, script, Encoding.Unicode); // Registry Editor 5.00 files are UTF-16 with a BOM
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not write {Path}", RestoreScriptPath);
            }
        }

        private static void DeleteRestoreScript()
        {
            try
            {
                if (File.Exists(RestoreScriptPath))
                {
                    File.Delete(RestoreScriptPath);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not delete {Path}", RestoreScriptPath);
            }
        }

        /// <summary>Tells Explorer the associations changed (SHCNE_ASSOCCHANGED), so a folder opened next uses the new verb.</summary>
        [SupportedOSPlatform("windows")]
        private static void NotifyAssociationsChanged()
        {
            try
            {
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "SHChangeNotify failed");
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>HKEY_CURRENT_USER through Microsoft.Win32.Registry.</summary>
        [SupportedOSPlatform("windows")]
        private sealed class CurrentUserRegistry : IRegistryStore
        {
            public string GetValue(string key, string name)
            {
                using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key);
                return opened?.GetValue(name ?? string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }

            public bool KeyExists(string key)
            {
                using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key);
                return opened != null;
            }

            public bool IsEmpty(string key)
            {
                using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key);
                return opened == null || (opened.ValueCount == 0 && opened.SubKeyCount == 0);
            }

            public void SetValue(string key, string name, string value)
            {
                using RegistryKey opened = Registry.CurrentUser.CreateSubKey(key, writable: true);
                opened.SetValue(name ?? string.Empty, value ?? string.Empty, RegistryValueKind.String);
            }

            public void DeleteValue(string key, string name)
            {
                using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key, writable: true);
                opened?.DeleteValue(name ?? string.Empty, throwOnMissingValue: false);
            }

            public void DeleteKeyTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }
}
