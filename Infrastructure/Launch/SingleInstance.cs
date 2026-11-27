// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using Serilog;

namespace ByteExplorer.Infrastructure.Launch
{
    /// <summary>
    /// One ByteExplorer per user and data folder for what the system opens: when ByteExplorer is the default file
    /// manager every folder double-clicked on the desktop starts "ByteExplorer.exe folder", and each would otherwise be
    /// a new copy of the app (a second index, a second settings writer, seconds to start). The first copy owns a named
    /// mutex and listens on a named pipe; a later start with a folder (or from Win+E) sends it its request, lets it come
    /// to the front, and exits. The running copy opens the folder in a new tab.
    /// <para>
    /// The pipe is the current user's only (PipeOptions.CurrentUserOnly: another account can't send it requests). If the
    /// running copy doesn't answer within a few seconds (hung, or still starting), the new start goes on as a copy of its
    /// own, so a folder always opens. Copies with different data folders (two portable installs) don't share.
    /// </para>
    /// </summary>
    public static class SingleInstance
    {
        private const string Greeting = "BX1";
        private const int MaxMessageLength = 64 * 1024;
        private static Mutex s_mutex;
        private static CancellationTokenSource s_listening;

        /// <summary>The mutex and pipe name: per user and per data folder.</summary>
        public static string ChannelName { get; } = BuildChannelName();

        /// <summary>Whether this process owns the instance mutex (it is the copy others send their requests to).</summary>
        public static bool IsPrimary { get; private set; }

        /// <summary>Takes the instance mutex if no other copy holds it. Held until the process exits.</summary>
        public static bool TryBecomePrimary()
        {
            try
            {
                var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? @"Local\" : string.Empty) + ChannelName);
                bool owned;
                try
                {
                    owned = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // the last owner crashed: ours now
                }

                if (!owned)
                {
                    mutex.Dispose();
                    return false;
                }

                s_mutex = mutex;
                IsPrimary = true;
                return true;
            }
            catch (Exception ex)
            {
                // No named mutexes (a locked-down system): run as a copy of its own, as before this existed.
                Log.Logger.Warning(ex, "Could not take the single-instance mutex");
                return false;
            }
        }

        /// <summary>
        /// Hands <paramref name="request"/> to the running copy. True once it confirmed; false when none answered in
        /// <paramref name="timeout"/> (the caller then starts normally).
        /// </summary>
        public static bool TryForward(LaunchRequest request, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                return ForwardAsync(request, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Logger.Information(ex, "The running ByteExplorer did not take the request; starting another copy");
                return false;
            }
        }

        private static async Task<bool> ForwardAsync(LaunchRequest request, CancellationToken token)
        {
            using var client = new NamedPipeClientStream(".", ChannelName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            string hello = await reader.ReadLineAsync(token).ConfigureAwait(false);
            string[] parts = hello?.Split(' ') ?? Array.Empty<string>();
            if (parts.Length != 2 || parts[0] != Greeting || !int.TryParse(parts[1], out int processId))
            {
                return false;
            }

            // This process is the one the person just started, so Windows lets it give the foreground away: the running
            // copy may then bring its window to the front (Windows refuses a background process that tries on its own).
            if (OperatingSystem.IsWindows())
            {
                AllowSetForegroundWindow(processId);
            }

            await writer.WriteLineAsync(Serialize(request).AsMemory(), token).ConfigureAwait(false);
            string answer = await reader.ReadLineAsync(token).ConfigureAwait(false);
            return answer == "ok";
        }

        /// <summary>
        /// Starts taking requests from later starts (primary copy only): each is passed to <paramref name="onRequest"/> on
        /// a background thread, after it was acknowledged.
        /// </summary>
        public static void StartListening(Action<LaunchRequest> onRequest)
        {
            ArgumentNullException.ThrowIfNull(onRequest);
            if (!IsPrimary || s_listening != null)
            {
                return;
            }

            s_listening = new CancellationTokenSource();
            CancellationToken token = s_listening.Token;
            Task.Run(() => ListenAsync(onRequest, token));
        }

        /// <summary>Stops listening (tests; the process exit ends it otherwise).</summary>
        public static void StopListening()
        {
            s_listening?.Cancel();
            s_listening = null;
        }

        private static async Task ListenAsync(Action<LaunchRequest> onRequest, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(ChannelName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
                    exchange.CancelAfter(TimeSpan.FromSeconds(5)); // a client that connects and says nothing can't block the next
                    LaunchRequest request = await ReceiveAsync(server, exchange.Token).ConfigureAwait(false);
                    if (request != null)
                    {
                        onRequest(request);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "A request from another ByteExplorer start failed");
                    try
                    {
                        await Task.Delay(200, token).ConfigureAwait(false); // never a hot loop on a persistent failure
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task<LaunchRequest> ReceiveAsync(Stream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync($"{Greeting} {Environment.ProcessId}".AsMemory(), token).ConfigureAwait(false);
            string line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            LaunchRequest request = line == null || line.Length > MaxMessageLength ? null : Deserialize(line);
            await writer.WriteLineAsync((request != null ? "ok" : "bad").AsMemory(), token).ConfigureAwait(false);
            return request;
        }

        private sealed class Message
        {
            public bool FromShell { get; set; }
            public string Folder { get; set; }
            public string Select { get; set; }
        }

        internal static string Serialize(LaunchRequest request) =>
            JsonSerializer.Serialize(new Message { FromShell = request.FromShell, Folder = request.Folder, Select = request.Select });

        /// <summary>A request from its message; null for anything malformed (only rooted paths are taken).</summary>
        internal static LaunchRequest Deserialize(string line)
        {
            try
            {
                Message message = JsonSerializer.Deserialize<Message>(line);
                if (message == null)
                {
                    return null;
                }

                string folder = Rooted(message.Folder);
                string select = Rooted(message.Select);
                return new LaunchRequest { FromShell = message.FromShell, Folder = folder, Select = select };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Rooted(string path) =>
            !string.IsNullOrEmpty(path) && path.Length < 32768 && path.IndexOf('\0') < 0 && Path.IsPathFullyQualified(path) ? path : null;

        private static string BuildChannelName()
        {
            string dataDirectory = AppPaths.DataDirectory ?? string.Empty;
            string key = Environment.UserName + "|" + (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? dataDirectory.ToLowerInvariant() : dataDirectory);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return "ByteExplorer-" + Convert.ToHexString(hash, 0, 8);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
