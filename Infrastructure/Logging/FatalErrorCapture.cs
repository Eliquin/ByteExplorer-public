// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ByteExplorer.Infrastructure.Logging
{
    /// <summary>
    /// Keeps what the .NET runtime prints when the process dies in a way no exception handler sees: a native access
    /// violation ("Fatal error. 0xC0000005" with the managed stack), a stack overflow ("Stack overflow." with the repeating
    /// frames), Environment.FailFast. The runtime writes those to the standard error stream, which a windowed app throws
    /// away; here it goes to logs/crash-yyyyMMdd.log instead, so such a crash leaves its cause behind. Normal runs write
    /// one line per start there.
    /// </summary>
    public static class FatalErrorCapture
    {
        private const int StdErrorHandle = -12;
        private static FileStream s_file; // kept open for the life of the process: the runtime writes to its handle

        public static string FilePath { get; private set; }

        public static void Start()
        {
            try
            {
                string folder = ByteExplorer.Core.AppPaths.Combine("logs");
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, "crash-" + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log");
                s_file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.WriteThrough);
                byte[] header = Encoding.UTF8.GetBytes(Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                    $" ByteExplorer started (process {Environment.ProcessId}); a fatal error, if any, follows" + Environment.NewLine);
                s_file.Write(header, 0, header.Length);
                s_file.Flush(true);

                var writer = new StreamWriter(s_file, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetError(writer); // managed writes to the error stream
                if (OperatingSystem.IsWindows())
                {
                    SetStdHandle(StdErrorHandle, s_file.SafeFileHandle.DangerousGetHandle()); // the runtime's own (native) writes
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Couldn't set up the fatal error capture");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int stdHandle, IntPtr handle);
    }
}
