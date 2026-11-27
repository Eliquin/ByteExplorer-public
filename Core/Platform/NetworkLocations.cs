// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Saved network locations: what they are called in the sidebar, and how to turn one into a folder to browse.
    /// <list type="bullet">
    /// <item><b>Windows</b>: a UNC path (\\server\share) is a folder already; the system connects with the signed-in
    /// user's credentials (or ones saved in Credential Manager).</item>
    /// <item><b>Linux</b>: an address (smb://, sftp://, ftp://, dav://, nfs://, afp://) is mounted with GIO (gvfs, the
    /// layer GNOME Files, Nemo, Caja and Thunar use), then browsed through its FUSE folder under /run/user/&lt;uid&gt;/gvfs.
    /// Shares that need a password must be connected once from the desktop's file manager (or the password saved in the
    /// keyring): this app cannot answer GIO's password prompt.</item>
    /// <item><b>macOS</b>: the address is handed to Finder ("open smb://…"), which asks for credentials and mounts the
    /// share under /Volumes; the folder is opened once it appears.</item>
    /// <item>A plain folder path (an already mounted share) is opened as is, on every OS.</item>
    /// </list>
    /// </summary>
    public static class NetworkLocations
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
        private static readonly string[] Schemes = { "smb", "sftp", "ssh", "ftp", "ftps", "dav", "davs", "nfs", "afp" };

        /// <summary>"share on server" for smb://server/share or \\server\share; the last folder name for a path.</summary>
        public static string DisplayName(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return string.Empty;
            }

            address = address.Trim();
            if (IsUnc(address))
            {
                string[] parts = address.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $"{parts[parts.Length - 1]} on {parts[0]}" : parts.Length == 1 ? parts[0] : address;
            }

            if (Uri.TryCreate(address, UriKind.Absolute, out Uri uri) && IsNetworkScheme(uri.Scheme))
            {
                string path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
                string last = path.Length == 0 ? null : path.Substring(path.LastIndexOf('/') + 1);
                return string.IsNullOrEmpty(last) ? $"{uri.Host} ({uri.Scheme.ToUpperInvariant()})" : $"{last} on {uri.Host}";
            }

            string name = PathUtil.GetName(address);
            return string.IsNullOrEmpty(name) ? address : name;
        }

        /// <summary>
        /// Checks an address someone typed: null if it can be saved, else what is wrong with it. Accepts UNC paths (Windows),
        /// network URLs (Linux, macOS) and existing folder paths.
        /// </summary>
        public static string Validate(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return "Type an address.";
            }

            address = address.Trim();
            if (IsUnc(address))
            {
                return OperatingSystem.IsWindows() ? null : "\\\\server\\share addresses are for Windows. On this system use smb://server/share.";
            }

            if (Uri.TryCreate(address, UriKind.Absolute, out Uri uri) && IsNetworkScheme(uri.Scheme))
            {
                if (OperatingSystem.IsWindows())
                {
                    return "On Windows, type the address as \\\\server\\share.";
                }

                return string.IsNullOrEmpty(uri.Host) ? "The address needs a server name, like smb://server/share." : null;
            }

            return Path.IsPathRooted(address) ? null : "Type a network address (\\\\server\\share or smb://server/share) or a folder path.";
        }

        /// <summary>
        /// Connects if needed and returns the folder to browse. Throws <see cref="IOException"/> with a message for the
        /// person when it can't (server unreachable, needs a password, GIO missing).
        /// </summary>
        public static async Task<string> ConnectAsync(string address, CancellationToken cancellationToken)
        {
            address = address?.Trim();
            string problem = Validate(address);
            if (problem != null)
            {
                throw new IOException(problem);
            }

            if (IsUnc(address) || !Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || !IsNetworkScheme(uri.Scheme))
            {
                // A folder path: no connecting, but it may be a slow or gone network folder, so check off the UI thread.
                bool exists = await Task.Run(() => Directory.Exists(address), cancellationToken).ConfigureAwait(false);
                return exists ? address : throw new IOException($"\"{address}\" isn't reachable right now.");
            }

            if (OperatingSystem.IsMacOS())
            {
                return await ConnectWithFinderAsync(uri, cancellationToken).ConfigureAwait(false);
            }

            return await ConnectWithGioAsync(address, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ConnectWithGioAsync(string address, CancellationToken cancellationToken)
        {
            (int mountExit, string mountOutput) = await RunAsync("gio", new[] { "mount", address }, cancellationToken).ConfigureAwait(false);
            if (mountExit == -1)
            {
                throw new IOException("Connecting to network addresses needs GIO (the gvfs package, part of GNOME, Cinnamon, MATE and Xfce desktops). " +
                                      "Alternatively, mount the share yourself and add its folder.");
            }

            // "already mounted" is not a failure; anything else is, unless the location is browsable anyway.
            (int infoExit, string info) = await RunAsync("gio", new[] { "info", address }, cancellationToken).ConfigureAwait(false);
            string localPath = infoExit == 0 ? ParseLocalPath(info) : null;
            if (!string.IsNullOrEmpty(localPath) && Directory.Exists(localPath))
            {
                return localPath;
            }

            string reason = string.IsNullOrWhiteSpace(mountOutput) ? info : mountOutput;
            if (reason != null && reason.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                reason = "The share asks for a password. Connect to it once from your desktop's file manager (and let it remember the password), then try again.";
            }

            throw new IOException($"Couldn't connect to {address}.\n\n{reason?.Trim()}");
        }

        /// <summary>The "local path:" line of "gio info" (the gvfs FUSE folder of a mounted location).</summary>
        public static string ParseLocalPath(string gioInfo)
        {
            if (string.IsNullOrEmpty(gioInfo))
            {
                return null;
            }

            foreach (string line in gioInfo.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("local path:", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring("local path:".Length).Trim();
                }
            }

            return null;
        }

        private static async Task<string> ConnectWithFinderAsync(Uri uri, CancellationToken cancellationToken)
        {
            string share = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/')[0];
            string mounted = string.IsNullOrEmpty(share) ? null : Path.Combine("/Volumes", share);
            if (mounted != null && Directory.Exists(mounted))
            {
                return mounted;
            }

            (int exit, string output) = await RunAsync("open", new[] { uri.OriginalString }, cancellationToken).ConfigureAwait(false);
            if (exit != 0)
            {
                throw new IOException($"Couldn't hand {uri.OriginalString} to Finder.\n\n{output?.Trim()}");
            }

            if (mounted == null)
            {
                throw new IOException("Finder is connecting to the server; pick a share there and it will appear among the drives.");
            }

            // Finder may ask for a password first: wait for the share to appear.
            DateTime deadline = DateTime.UtcNow + ConnectTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(mounted))
                {
                    return mounted;
                }
            }

            throw new IOException($"{share} hasn't appeared yet. Finish connecting in Finder; it will then be listed among the drives.");
        }

        /// <summary>Runs a tool without a shell or a console; exit code -1 when it isn't installed. Output is stdout + stderr.</summary>
        internal static async Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var info = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (-1, null);
            }

            if (process == null)
            {
                return (-1, null);
            }

            using (process)
            {
                // No stdin: a password prompt reads end-of-file and fails at once instead of hanging.
                process.StandardInput.Close();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                    {
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IOException("The server didn't answer in time.");
                }

                string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
                Log.Logger.Debug("{Tool} {Arguments} exited with {Code}: {Output}", tool, string.Join(' ', arguments), process.ExitCode, output);
                return (process.ExitCode, output);
            }
        }

        private static bool IsUnc(string address) => address.StartsWith(@"\\", StringComparison.Ordinal) && address.Length > 2;

        private static bool IsNetworkScheme(string scheme) => Array.IndexOf(Schemes, scheme.ToLowerInvariant()) >= 0;

        /// <summary>True for UNC paths and network URLs: never touched on the UI thread (they can block for seconds).</summary>
        public static bool IsNetworkAddress(string address) =>
            !string.IsNullOrEmpty(address) && (IsUnc(address.Trim()) ||
                                               (Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri uri) && IsNetworkScheme(uri.Scheme)));
    }
}
