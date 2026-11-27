// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using Serilog;

namespace ByteExplorer
{
    /// <summary>
    /// Application entry point. Owns process-wide concerns only: last-resort exception handling, the media engine,
    /// choosing the first main window and quitting after the last one. The services the main windows share are
    /// ViewModels.AppServices; everything else is composed by each window and its view models.
    /// </summary>
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Infrastructure.UiThread.Capture(); // before anything can start background work that posts back (see UiThread)
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>What the command line asked for (a folder to open); consumed by the first window.</summary>
        public static Infrastructure.Launch.LaunchRequest StartupRequest { get; set; } = Infrastructure.Launch.LaunchRequest.Empty;

        public override void OnFrameworkInitializationCompleted()
        {
            // A background-thread-heavy app must never let one fault kill the process or corrupt UI state: every hook
            // logs first, then decides.
            Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

            InitializeMediaEngine();

            // Files opened from ZIP archives were extracted to a temporary folder; last session's are removed here.
            System.Threading.Tasks.Task.Run(() => ArchiveBrowser.CleanupTemp(TimeSpan.FromDays(1)));
            // Which archivers (bsdtar, 7-Zip, xz…) are installed decides which archives open as folders: found off the UI thread.
            System.Threading.Tasks.Task.Run(Core.Services.Archives.ArchiveTools.WarmUp);
            // Likewise copies of phone files opened in an application, and phone-to-phone transfer leftovers.
            System.Threading.Tasks.Task.Run(() => Core.Platform.Phones.PhoneTemp.Cleanup(TimeSpan.FromDays(1)));
            // Files opened from password-protected items are never kept: a crash may have left some, removed now.
            System.Threading.Tasks.Task.Run(Core.Security.ProtectedItems.CleanupTemp);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Several main windows can be open (Ctrl+N): the app quits when the last of them closes, never because of
                // the settings window or a dialog left open, and not when the first window closes while others stay.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.MainWindow = CreateMainWindow();
                MainWindowBase.LastWindowClosed += (_, _) => desktop.Shutdown();

                // Later starts with a folder (from the system, once ByteExplorer is the default file manager) arrive here,
                // in the main window used last.
                Infrastructure.Launch.SingleInstance.StartListening(request =>
                    Dispatcher.UIThread.Post(() => (MainWindowBase.MostRecentlyActive ?? desktop.MainWindow as MainWindowBase)?.HandleLaunchRequest(request)));
                System.Threading.Tasks.Task.Run(Infrastructure.DefaultApp.DefaultFileManager.RepairIfMoved);
                desktop.Exit += (_, e) =>
                {
                    Log.Information("ByteExplorer shutting down (exit code {ExitCode})", e.ApplicationExitCode);
                    // Unlocked items lock (their keys zeroed) and the files opened from them are deleted.
                    Core.Security.ProtectedItems.LockAll();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Loads libvlc once for the process, before any preview creates a player. Optional: if libvlc is missing (not
        /// deployed on Windows, VLC not installed on Linux/macOS) media previews are unavailable and everything else
        /// works. On macOS libvlc lives inside VLC.app, which LibVLCSharp does not search on its own.
        /// </summary>
        private static void InitializeMediaEngine()
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    RegisterLinuxLibVlcResolver();
                }

                const string macVlcLib = "/Applications/VLC.app/Contents/MacOS/lib";
                if (OperatingSystem.IsMacOS() && Directory.Exists(macVlcLib))
                {
                    LibVLCSharp.Shared.Core.Initialize(macVlcLib);
                }
                else
                {
                    LibVLCSharp.Shared.Core.Initialize();
                }

                MediaEngine.IsAvailable = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "libvlc could not be loaded — media file preview playback is unavailable this session");
            }
        }

        /// <summary>
        /// LibVLCSharp imports "libvlc"/"libvlccore", which resolve to the unversioned libvlc.so that only the -dev
        /// package installs; a desktop with VLC installed has just the versioned sonames. Map the names to those
        /// (libvlc.so.5 / libvlccore.so.9, the libvlc 3.x ABI) when the default lookup fails.
        /// </summary>
        private static void RegisterLinuxLibVlcResolver()
        {
            try
            {
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(LibVLCSharp.Shared.Core).Assembly, (name, assembly, searchPath) =>
                {
                    string versioned = name switch
                    {
                        "libvlc" => "libvlc.so.5",
                        "libvlccore" => "libvlccore.so.9",
                        _ => null
                    };
                    if (versioned == null)
                    {
                        return IntPtr.Zero;
                    }

                    if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle) ||
                        System.Runtime.InteropServices.NativeLibrary.TryLoad(versioned, assembly, searchPath, out handle))
                    {
                        return handle;
                    }

                    return IntPtr.Zero;
                });
            }
            catch (InvalidOperationException)
            {
                // A resolver is already set for the assembly (only possible if this ran twice); keep it.
            }
        }

        /// <summary>
        /// The window for the persisted layout: "Trove" (or the older "Dori" and "Nemo") starts the Trove window, anything else the Modern window ("Modern",
        /// its old name "Dark", and "Default", whose WPF MainWindow is not ported).
        /// <para>
        /// The settings file is read synchronously here on purpose: which window to show is a one-time choice made
        /// before anything is visible, and LoadAsync never resumes on the UI thread (Task.Run + ConfigureAwait(false)),
        /// so blocking on it cannot deadlock.
        /// </para>
        /// </summary>
        private static Window CreateMainWindow()
        {
            string layout = AppearanceSettings.LayoutModern;
            try
            {
                var settingsService = new SettingsService(Log.Logger);
                settingsService.LoadAsync().GetAwaiter().GetResult();
                layout = settingsService.Current?.Appearance?.WindowLayout ?? AppearanceSettings.LayoutModern;

                // Before the window exists, so it opens in the right colors (no dark-then-light flash).
                ApplyTheme(settingsService.Current?.Appearance?.Theme);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read the window-layout setting — starting with the Modern window");
            }

            return CreateWindowForLayout(layout);
        }

        /// <summary>The main window of a layout name (case-insensitive; unknown names get the Modern window).</summary>
        public static MainWindowBase CreateWindowForLayout(string layout)
        {
            if (string.Equals(layout, AppearanceSettings.LayoutTrove, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layout, AppearanceSettings.LegacyLayoutDori, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layout, AppearanceSettings.LegacyLayoutNemo, StringComparison.OrdinalIgnoreCase))
            {
                return new NemoMainWindow();
            }

            if (string.Equals(layout, AppearanceSettings.LayoutFolio, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layout, AppearanceSettings.LegacyLayoutFinder, StringComparison.OrdinalIgnoreCase))
            {
                return new FinderMainWindow();
            }

            if (string.Equals(layout, AppearanceSettings.LayoutDefault, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("The Default window layout is not available in the cross-platform build — using Modern");
            }

            return new ModernMainWindow();
        }

        /// <summary>
        /// Sets the application's theme variant from the setting: Light, Dark, or Default (follow the operating system,
        /// including live changes). Every color in the app comes from the Palette.axaml theme dictionaries, so this
        /// re-colors open windows immediately.
        /// </summary>
        /// <summary>
        /// The folder color everywhere: the theme's FolderYellow and FolderYellowLid resources (tree, address bar, pins,
        /// previews) are overridden at the application level, above the palette; empty = the palette's own.
        /// </summary>
        public static void ApplyFolderColor(string hex)
        {
            if (Current?.Resources is not { } resources)
            {
                return;
            }

            string normalized = Core.Models.AreaAppearance.NormalizeColor(hex);
            if (normalized.Length == 0 || !Avalonia.Media.Color.TryParse(normalized, out Avalonia.Media.Color color))
            {
                resources.Remove("FolderYellow");
                resources.Remove("FolderYellowLid");
                return;
            }

            // The lid (the folder's front) is a lighter shade of the body, as in the palette's yellow.
            var lid = Avalonia.Media.Color.FromArgb(color.A, Lighten(color.R), Lighten(color.G), Lighten(color.B));
            resources["FolderYellow"] = new Avalonia.Media.SolidColorBrush(color);
            resources["FolderYellowLid"] = new Avalonia.Media.SolidColorBrush(lid);

            static byte Lighten(byte channel) => (byte)(channel + (255 - channel) * 0.3);
        }

        public static void ApplyTheme(string theme)
        {
            if (Current == null)
            {
                return;
            }

            Current.RequestedThemeVariant = theme switch
            {
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default
            };
        }

        private static void OnUiThreadUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled exception on the UI thread");

            // Handled only after it is logged, so one bad handler cannot tear down the window and nothing is swallowed
            // silently.
            e.Handled = true;
        }
    }

    /// <summary>Whether libvlc loaded at startup, and the one LibVLC instance the app uses.</summary>
    public static class MediaEngine
    {
        private static readonly object Gate = new object();
        private static LibVLCSharp.Shared.LibVLC _shared;
        private static bool _failed;

        public static bool IsAvailable { get; internal set; }

        /// <summary>
        /// The process's LibVLC instance, created on first use (the first media preview or media details) and kept for
        /// the session; null when libvlc is unavailable. Playback and metadata parsing share it: each instance loads
        /// VLC's whole plugin set (tens of MB on Windows), and there used to be two, the playback one created again each
        /// time a preview panel started playing. Options are the union of what both needed: no file name drawn over
        /// videos, no logging to the console, and on Linux no Xlib-based video outputs (the UI toolkit owns the Xlib
        /// connection; the xcb outputs stay available). Thread-safe; never disposed by its users.
        /// </summary>
        public static LibVLCSharp.Shared.LibVLC Shared
        {
            get
            {
                if (!IsAvailable || _failed)
                {
                    return null;
                }

                lock (Gate)
                {
                    if (_shared == null && !_failed)
                    {
                        try
                        {
                            _shared = OperatingSystem.IsLinux()
                                ? new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet", "--no-xlib")
                                : new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet");
                        }
                        catch (Exception ex)
                        {
                            _failed = true;
                            Log.Warning(ex, "LibVLC could not be created — media previews and media details are unavailable this session");
                        }
                    }

                    return _shared;
                }
            }
        }
    }
}
