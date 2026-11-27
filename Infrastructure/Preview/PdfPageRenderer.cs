// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>
    /// Runs a program without a shell and returns its exit code (<see cref="PdfPageRenderer.TimedOut"/> when it was stopped
    /// after <paramref name="timeout"/>). Injected so the renderer is tested without poppler.
    /// </summary>
    public delegate Task<int> ToolRunner(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token);

    /// <summary>
    /// The first page of a PDF as a PNG, rendered by poppler's <c>pdftoppm</c> (poppler-utils), for the details pane on
    /// Linux and macOS (Windows uses the system's preview handler; pdftoppm is used there too when no handler exists and it
    /// is on PATH). An external program on purpose: no PDF library is referenced, and a crash or a hang on a malformed file
    /// stays in that process (it is killed after <see cref="Timeout"/>).
    /// <para>
    /// The arguments go through <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no quoting) and the PDF's path is
    /// always absolute, so a file named "-h.pdf" can't be read as an option. Pictures are cached in the user's cache folder
    /// (<c>$XDG_CACHE_HOME/ByteExplorer/pdf-previews</c>, never the shared /tmp), keyed by path, size, modification time and
    /// size in pixels, so going back to a PDF shows it at once; the cache keeps the newest <see cref="CacheLimit"/> pictures.
    /// </para>
    /// </summary>
    public sealed class PdfPageRenderer
    {
        /// <summary>The exit code a <see cref="ToolRunner"/> reports for a program it stopped.</summary>
        public const int TimedOut = -2;

        public const string ToolName = "pdftoppm";

        /// <summary>How many rendered pages the cache keeps.</summary>
        public const int CacheLimit = 64;

        private static readonly TimeSpan ToolLookupLifetime = TimeSpan.FromMinutes(1);

        private readonly Func<string> _findTool;
        private readonly ToolRunner _runner;
        private readonly string _cacheDirectory;
        private readonly object _lookupGate = new object();
        private string _toolPath;
        private long _toolLookupTicks;
        private bool _toolLookedUp;

        public PdfPageRenderer(Func<string> findTool, ToolRunner runner, string cacheDirectory)
        {
            _findTool = findTool ?? throw new ArgumentNullException(nameof(findTool));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        }

        /// <summary>How long pdftoppm gets for one page.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

        public string CacheDirectory => _cacheDirectory;

        /// <summary>pdftoppm from PATH, run as a process, caching in the user's cache folder.</summary>
        public static PdfPageRenderer CreateDefault() =>
            new PdfPageRenderer(() => ShellLauncher.FindOnPath(ToolName), RunProcessAsync, DefaultCacheDirectory());

        /// <summary>Whether pdftoppm was found (looked up again after a minute, so installing poppler needs no restart).</summary>
        public bool IsAvailable => ToolPath != null;

        private string ToolPath
        {
            get
            {
                lock (_lookupGate)
                {
                    long now = Environment.TickCount64;
                    if (!_toolLookedUp || now - _toolLookupTicks > ToolLookupLifetime.TotalMilliseconds)
                    {
                        try
                        {
                            _toolPath = _findTool();
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Debug(ex, "Could not look for {Tool}", ToolName);
                            _toolPath = null;
                        }

                        _toolLookupTicks = now;
                        _toolLookedUp = true;
                    }

                    return _toolPath;
                }
            }
        }

        /// <summary>Looks for pdftoppm again at the next use.</summary>
        public void ResetToolLookup()
        {
            lock (_lookupGate)
            {
                _toolLookedUp = false;
            }
        }

        /// <summary>
        /// Renders page 1 of <paramref name="pdfPath"/> to a PNG at most <paramref name="maxSide"/> pixels on its long side
        /// and returns the PNG's path, or null when the PDF can't be rendered (no pdftoppm, encrypted or broken file, too
        /// slow). Runs pdftoppm only when the page is not cached yet.
        /// </summary>
        public async Task<string> RenderFirstPageAsync(string pdfPath, int maxSide, CancellationToken token)
        {
            string tool = ToolPath;
            if (tool == null || string.IsNullOrEmpty(pdfPath))
            {
                return null;
            }

            string fullPath;
            FileInfo info;
            try
            {
                fullPath = Path.GetFullPath(pdfPath);
                info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }

            maxSide = Math.Clamp(maxSide, 64, 4096);
            string cached = Path.Combine(_cacheDirectory, CacheKey(fullPath, info.Length, info.LastWriteTimeUtc, maxSide) + ".png");
            if (File.Exists(cached))
            {
                TryTouch(cached);
                return cached;
            }

            Directory.CreateDirectory(_cacheDirectory);
            string root = Path.Combine(_cacheDirectory, "render-" + Guid.NewGuid().ToString("N"));
            string produced = root + ".png"; // -singlefile: no page-number suffix
            var arguments = new List<string>
            {
                "-png", "-f", "1", "-l", "1", "-singlefile",
                "-scale-to", maxSide.ToString(CultureInfo.InvariantCulture),
                fullPath, root,
            };

            try
            {
                int exitCode = await _runner(tool, arguments, Timeout, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (exitCode != 0 || !File.Exists(produced) || new FileInfo(produced).Length == 0)
                {
                    Log.Logger.Debug("{Tool} could not render {Path} (exit code {Code})", ToolName, fullPath, exitCode);
                    return null;
                }

                File.Move(produced, cached, overwrite: true);
                Prune();
                return cached;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Rendering {Path} with {Tool} failed", fullPath, ToolName);
                return null;
            }
            finally
            {
                TryDelete(produced);
            }
        }

        /// <summary>A file name for one rendering: a hash, so paths with any characters (and their length) don't matter.</summary>
        internal static string CacheKey(string fullPath, long length, DateTime lastWriteUtc, int maxSide)
        {
            string text = string.Join("|", fullPath, length.ToString(CultureInfo.InvariantCulture),
                lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture), maxSide.ToString(CultureInfo.InvariantCulture));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }

        /// <summary>Keeps the newest <see cref="CacheLimit"/> pictures.</summary>
        private void Prune()
        {
            try
            {
                FileInfo[] pictures = new DirectoryInfo(_cacheDirectory).GetFiles("*.png")
                    .Where(f => !f.Name.StartsWith("render-", StringComparison.Ordinal))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
                foreach (FileInfo old in pictures.Skip(CacheLimit))
                {
                    TryDelete(old.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Logger.Debug(ex, "Could not prune {Folder}", _cacheDirectory);
            }
        }

        private static void TryTouch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // the cache keeps what was used last
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only cache: it still works, it only prunes by age of creation.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or gone: the next prune tries again.
            }
        }

        private static string DefaultCacheDirectory()
        {
            string baseDirectory;
            if (OperatingSystem.IsWindows())
            {
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseDirectory, "ByteExplorer", "Cache", "pdf-previews");
            }

            if (OperatingSystem.IsMacOS())
            {
                baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
            }
            else
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                baseDirectory = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            }

            return Path.Combine(baseDirectory, "ByteExplorer", "pdf-previews");
        }

        /// <summary>
        /// Starts <paramref name="program"/> directly (no shell), drains its output (a full pipe would block it) and waits at
        /// most <paramref name="timeout"/>; a program that takes longer, or whose preview is no longer wanted, is killed.
        /// </summary>
        public static async Task<int> RunProcessAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info);
            if (process == null)
            {
                return -1;
            }

            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It exited just now.
                }

                token.ThrowIfCancellationRequested();
                return TimedOut;
            }

            await Task.WhenAll(output, errors).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                Log.Logger.Debug("{Program} exited with {Code}: {Errors}", Path.GetFileName(program), process.ExitCode, errors.Result);
            }

            return process.ExitCode;
        }
    }
}
