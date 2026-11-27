// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Services.Archives
{
    /// <summary>
    /// The external archivers this machine has, found once by looking at fixed locations and the PATH (no program is
    /// started to find them, so the first archive check costs no process launch):
    /// <list type="bullet">
    /// <item><b>bsdtar</b> (libarchive): Windows 10 1803+ ships it as <c>%SystemRoot%\System32\tar.exe</c>, macOS as
    /// <c>/usr/bin/bsdtar</c>, Linux in "libarchive-tools". It reads 7z, RAR (v4 and v5), ISO, CAB and compressed TAR.</item>
    /// <item><b>7-Zip</b> (<c>7z</c>, <c>7zz</c>, <c>7za</c>): reads the same and reports encryption per entry.</item>
    /// <item>Plain decompressors for single compressed files and compressed TAR streams: <c>xz</c>, <c>bzip2</c>,
    /// <c>zstd</c> and libarchive's <c>bsdcat</c>.</item>
    /// </list>
    /// GNU tar is deliberately not used: it reads only TAR, which is built in.
    /// </summary>
    internal sealed class ArchiveToolset
    {
        public static readonly ArchiveToolset None = new ArchiveToolset();

        /// <summary>A libarchive tar (bsdtar), or null.</summary>
        public string Bsdtar { get; init; }

        /// <summary>A 7-Zip command-line program, or null.</summary>
        public string SevenZip { get; init; }

        /// <summary>libarchive's bsdcat (decompresses anything libarchive knows), or null.</summary>
        public string Bsdcat { get; init; }

        public string Xz { get; init; }
        public string Bzip2 { get; init; }
        public string Zstd { get; init; }

        /// <summary>Whether anything external can read archives at all (for the "what to install" hint).</summary>
        public bool HasArchiver => Bsdtar != null || SevenZip != null;
    }

    /// <summary>Finds the archivers (<see cref="ArchiveToolset"/>) once, and runs them.</summary>
    internal static class ArchiveTools
    {
        private static readonly Lazy<ArchiveToolset> Detected = new Lazy<ArchiveToolset>(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The tools found on this machine (detected on first use).</summary>
        public static ArchiveToolset Current => Detected.Value;

        /// <summary>How tools are started (a real process runner; tests build providers with their own).</summary>
        public static IArchiveToolRunner Runner { get; } = new ProcessToolRunner();

        /// <summary>Detects the tools in the background at startup so the first archive check on the UI thread is free.</summary>
        public static void WarmUp() => _ = Current;

        private static ArchiveToolset Detect()
        {
            try
            {
                var toolset = new ArchiveToolset
                {
                    Bsdtar = FindBsdtar(),
                    SevenZip = FindSevenZip(),
                    Bsdcat = FindOnPath("bsdcat"),
                    Xz = FindOnPath("xz"),
                    Bzip2 = FindOnPath("bzip2"),
                    Zstd = FindOnPath("zstd"),
                };
                Log.Logger.Information("Archivers: bsdtar {Bsdtar}, 7-Zip {SevenZip}, bsdcat {Bsdcat}, xz {Xz}, bzip2 {Bzip2}, zstd {Zstd}",
                    toolset.Bsdtar ?? "-", toolset.SevenZip ?? "-", toolset.Bsdcat ?? "-", toolset.Xz ?? "-", toolset.Bzip2 ?? "-", toolset.Zstd ?? "-");
                return toolset;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Log.Logger.Warning(ex, "Couldn't look for archivers");
                return ArchiveToolset.None;
            }
        }

        private static string FindBsdtar()
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows 10 1803+ ships libarchive's bsdtar as tar.exe; a GNU tar earlier on the PATH must not win.
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string tar = string.IsNullOrEmpty(system) ? null : Path.Combine(system, "tar.exe");
                return tar != null && File.Exists(tar) ? tar : FindOnPath("bsdtar");
            }

            // macOS ships /usr/bin/bsdtar (its /usr/bin/tar is the same program); Linux has it in libarchive-tools.
            return FindOnPath("bsdtar");
        }

        private static string FindSevenZip()
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string root = Environment.GetFolderPath(folder);
                    string candidate = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "7-Zip", "7z.exe");
                    if (candidate != null && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            // 7z (p7zip-full, or the full 7-Zip), 7zz (7-Zip for Linux/macOS), 7za (standalone: no RAR, but 7z/xz/bz2).
            return FindOnPath("7z") ?? FindOnPath("7zz") ?? FindOnPath("7za");
        }

        /// <summary>The program's full path from the PATH (plus the usual Homebrew/MacPorts folders on macOS), or null.</summary>
        internal static string FindOnPath(string name)
        {
            var folders = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            folders.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (OperatingSystem.IsMacOS())
            {
                // An app started from Finder gets a minimal PATH without Homebrew's folders.
                folders.AddRange(new[] { "/usr/bin", "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" });
            }
            else if (!OperatingSystem.IsWindows())
            {
                folders.AddRange(new[] { "/usr/bin", "/usr/local/bin", "/bin" });
            }

            string[] extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".com" } : new[] { string.Empty };
            foreach (string folder in folders)
            {
                if (!Path.IsPathFullyQualified(folder))
                {
                    continue; // a relative PATH entry would run whatever is in the current folder
                }

                foreach (string extension in extensions)
                {
                    string candidate = Path.Combine(folder, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>What a finished tool run said.</summary>
    internal sealed class ToolRunResult
    {
        public ToolRunResult(int exitCode, string error, bool stoppedEarly)
        {
            ExitCode = exitCode;
            Error = error ?? string.Empty;
            StoppedEarly = stoppedEarly;
        }

        public int ExitCode { get; }

        /// <summary>Standard error (capped), for messages and to recognise encrypted archives.</summary>
        public string Error { get; }

        /// <summary>The output reader stopped before the end (an entry limit): the program was stopped, its exit code means nothing.</summary>
        public bool StoppedEarly { get; }
    }

    /// <summary>
    /// Starts archivers. An interface so the listing and extraction logic can be tested with recorded tool output on a
    /// machine without the tools.
    /// </summary>
    internal interface IArchiveToolRunner
    {
        /// <summary>
        /// Runs the program to completion; <paramref name="readOutput"/> consumes standard output as text while it runs
        /// (it may stop early, which stops the program). Throws <see cref="IOException"/> when the program can't start,
        /// an <see cref="IOException"/> too after <paramref name="timeout"/>, and honours cancellation (the program is killed).
        /// </summary>
        /// <param name="outputEncoding">How standard output is decoded; null for the platform's tool encoding
        /// (<see cref="ProcessToolRunner.DefaultEncoding"/>).</param>
        ToolRunResult Run(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
            Action<TextReader> readOutput = null, Encoding outputEncoding = null);

        /// <summary>
        /// Starts the program and returns its standard output as a stream (a decompressor's output). Reading to the end
        /// throws <see cref="IOException"/> when the program failed; disposing it early stops the program.
        /// </summary>
        Stream OpenOutput(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runs tools as child processes: arguments as a list (never a shell string, so no name in an archive or path can
    /// become a command), no window, standard input closed (a program that would ask a question, such as 7-Zip asking
    /// for a password, gets end-of-input instead of hanging), a timeout, and the whole process tree killed on
    /// cancellation. On Linux and macOS the child runs with a UTF-8 locale so names arrive as UTF-8 and bsdtar's dates
    /// use English month names.
    /// </summary>
    internal sealed class ProcessToolRunner : IArchiveToolRunner
    {
        private const int MaxErrorChars = 64 * 1024;

        public ToolRunResult Run(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
            Action<TextReader> readOutput = null, Encoding outputEncoding = null)
        {
            using Process process = Start(program, arguments, outputEncoding);
            var error = new StringBuilder();
            DrainErrorAsync(process, error);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            using CancellationTokenRegistration registration = linked.Token.Register(() => Kill(process));

            bool stoppedEarly = false;
            try
            {
                if (readOutput != null)
                {
                    readOutput(process.StandardOutput);
                }

                if (!process.StandardOutput.EndOfStream)
                {
                    stoppedEarly = readOutput != null;
                    if (stoppedEarly)
                    {
                        Kill(process);
                    }
                    else
                    {
                        process.StandardOutput.ReadToEnd();
                    }
                }
            }
            catch (Exception ex) when ((ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) && linked.IsCancellationRequested)
            {
                // The pipe broke because the program was killed: reported below as cancellation or timeout.
            }

            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
            {
                throw new IOException($"{Path.GetFileName(program)} took longer than {timeout.TotalMinutes:0} minutes and was stopped.");
            }

            lock (error)
            {
                return new ToolRunResult(stoppedEarly ? -1 : process.ExitCode, error.ToString(), stoppedEarly);
            }
        }

        public Stream OpenOutput(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Process process = Start(program, arguments, null);
            var error = new StringBuilder();
            DrainErrorAsync(process, error);
            return new ProcessOutputStream(process, error, timeout, cancellationToken);
        }

        private static Process Start(string program, IReadOnlyList<string> arguments, Encoding outputEncoding)
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding ?? DefaultEncoding,
                StandardErrorEncoding = DefaultEncoding,
            };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            if (!OperatingSystem.IsWindows())
            {
                string locale = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
                info.Environment["LC_ALL"] = locale;
                info.Environment["LANG"] = locale;
            }

            try
            {
                var process = Process.Start(info) ?? throw new IOException($"{Path.GetFileName(program)} couldn't be started.");
                process.StandardInput.Close();
                return process;
            }
            catch (Win32Exception ex)
            {
                throw new IOException($"{Path.GetFileName(program)} couldn't be started: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The tools' text encoding: UTF-8 on Linux and macOS (the locale set above). Windows' tar.exe writes names in the
        /// ANSI code page of the user's locale (the C runtime's locale after setlocale(LC_ALL, "")), and reads its list
        /// files the same way. 7-Zip is told to use UTF-8 everywhere (-sccUTF-8, -scsUTF-8) and read as such.
        /// </summary>
        internal static Encoding DefaultEncoding { get; } = FindDefaultEncoding();

        private static Encoding FindDefaultEncoding()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new UTF8Encoding(false);
            }

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                return new UTF8Encoding(false);
            }
        }

        private static void DrainErrorAsync(Process process, StringBuilder error)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (error)
                {
                    if (error.Length < MaxErrorChars)
                    {
                        error.AppendLine(e.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
        }

        internal static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                // Already gone.
            }
        }

        /// <summary>A child's standard output; the end of the data checks that the program succeeded.</summary>
        private sealed class ProcessOutputStream : Stream
        {
            private readonly Process _process;
            private readonly StringBuilder _error;
            private readonly Stream _output;
            private readonly CancellationTokenSource _timeout;
            private readonly CancellationTokenRegistration _registration;
            private readonly CancellationToken _cancellationToken;
            private bool _finished;

            public ProcessOutputStream(Process process, StringBuilder error, TimeSpan timeout, CancellationToken cancellationToken)
            {
                _process = process;
                _error = error;
                _output = process.StandardOutput.BaseStream;
                _cancellationToken = cancellationToken;
                _timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _timeout.CancelAfter(timeout);
                _registration = _timeout.Token.Register(() => Kill(process));
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int read;
                try
                {
                    read = _output.Read(buffer);
                }
                catch (IOException) when (_timeout.IsCancellationRequested)
                {
                    read = 0;
                }

                if (read == 0 && buffer.Length > 0 && !_finished)
                {
                    _finished = true;
                    _process.WaitForExit();
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (_timeout.IsCancellationRequested)
                    {
                        throw new IOException($"{Path.GetFileName(_process.StartInfo.FileName)} took too long and was stopped.");
                    }

                    if (_process.ExitCode != 0)
                    {
                        string message;
                        lock (_error)
                        {
                            message = _error.ToString().Trim();
                        }

                        throw new IOException($"{Path.GetFileName(_process.StartInfo.FileName)} couldn't decompress the file" + (message.Length > 0 ? ": " + FirstLine(message) : "."));
                    }
                }

                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Kill(_process);
                    _registration.Dispose();
                    _timeout.Dispose();
                    _process.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        internal static string FirstLine(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return newline < 0 ? trimmed : trimmed.Substring(0, newline);
        }
    }
}
