// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Avalonia;
using ByteExplorer.Infrastructure.Logging;
using Serilog;

namespace ByteExplorer
{
    internal static class Program
    {
        /// <summary>
        /// Process entry point. Logging and the non-UI exception hooks are set up before Avalonia starts, so a failure
        /// during startup (a missing native library, a broken display connection) is logged rather than lost.
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            Log.Logger = AppLogging.CreateLogger();
            FatalErrorCapture.Start(); // a crash no handler sees (native, stack overflow) leaves its cause in logs/crash-*.log
            Log.Information("ByteExplorer starting up on {OS} ({Architecture})",
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled AppDomain exception (IsTerminating={IsTerminating})", e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved exception on a background task");
                e.SetObserved();
            };

            // A maintenance switch (the uninstaller's --unregister-default-file-manager): do it and exit, with no window,
            // no single-instance pipe and no hand-over to a running ByteExplorer.
            int? maintenance = Infrastructure.Launch.MaintenanceCommand.TryRun(args, Infrastructure.Launch.MaintenanceCommand.System);
            if (maintenance.HasValue)
            {
                Log.CloseAndFlush();
                return maintenance.Value;
            }

            // A folder the system (or a program) asked to open: the running ByteExplorer takes it, in a new tab.
            var request = Infrastructure.Launch.LaunchRequest.Parse(args, Environment.CurrentDirectory);
            bool primary = Infrastructure.Launch.SingleInstance.TryBecomePrimary();
            if (!primary && request.ShouldForward &&
                Infrastructure.Launch.SingleInstance.TryForward(request, TimeSpan.FromSeconds(4)))
            {
                Log.Information("Handed {Folder} to the running ByteExplorer", request.Folder ?? "(new tab)");
                Log.CloseAndFlush();
                return 0;
            }

            App.StartupRequest = request;
            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "ByteExplorer failed to start");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>Also used by the XAML previewer and the headless tests.</summary>
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new X11PlatformOptions { EnableIme = true })
                .LogToTrace();
    }
}
