// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ByteExplorer.Infrastructure.Logging
{
    /// <summary>
    /// Builds the app's Serilog logger. Kept out of App.xaml.cs so the
    /// startup class stays thin, and so logging configuration can be
    /// changed without touching startup wiring.
    ///
    /// Log files land in a "logs" folder next to the executable
    /// (portable, matches settings.json's location), rolling daily
    /// with a 14-day retention so the folder can't grow unbounded.
    /// </summary>
    public static class AppLogging
    {
        /// <summary>
        /// Debug builds log everything; Release builds log Information and above, because Debug-level lines are written
        /// on hot paths (every folder load, drag, search) and cost disk I/O and allocations for no benefit to a user.
        /// BYTEEXPLORER_LOG_LEVEL (Verbose, Debug, Information, Warning, Error) overrides this for troubleshooting.
        /// </summary>
        private static LogEventLevel MinimumLevel
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("BYTEEXPLORER_LOG_LEVEL");
                if (!string.IsNullOrWhiteSpace(configured) && Enum.TryParse(configured, ignoreCase: true, out LogEventLevel level))
                {
                    return level;
                }
#if DEBUG
                return LogEventLevel.Debug;
#else
                return LogEventLevel.Information;
#endif
            }
        }

        public static Logger CreateLogger()
        {
            string logDirectory = ByteExplorer.Core.AppPaths.Combine("logs");

            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception)
            {
                // Can't create the log directory (locked-down install
                // location, read-only media, etc.) — fall back to a
                // debug-output-only logger rather than failing startup
                // over logging infrastructure.
                return new LoggerConfiguration()
                    .MinimumLevel.Is(MinimumLevel)
                    .WriteTo.Debug()
                    .CreateLogger();
            }

            string logFilePath = Path.Combine(logDirectory, "byteexplorer-.log");

            return new LoggerConfiguration()
                .MinimumLevel.Is(MinimumLevel)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.Debug()
                .CreateLogger();
        }
    }
}
