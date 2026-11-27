// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Detects drives via System.IO.DriveInfo. Every property read that
    /// can touch hardware (IsReady, VolumeLabel, TotalSize,
    /// AvailableFreeSpace) is wrapped individually — a single unready or
    /// misbehaving drive (e.g. an empty card reader) must not prevent
    /// the rest of the drives from reporting correctly, and must not
    /// throw past this service.
    ///
    /// Network drives get a stronger guarantee on top of that: each one
    /// is probed independently under <see cref="BlockingOperationRunner"/>,
    /// with its own hard timeout (Navigation.NetworkDriveProbeTimeoutMs)
    /// enforced on a dedicated thread rather than the shared ThreadPool.
    /// Without this, a single unresponsive mapped network drive — one
    /// whose IsReady/VolumeLabel/TotalSize reads block on the OS's own
    /// network timeout — would stall this entire method (previously a
    /// single sequential loop over every drive), meaning every OTHER
    /// drive, including local fixed disks, would fail to appear until
    /// that one bad drive's native call eventually gave up, which can
    /// take well over a minute. Local/removable/optical drives are never
    /// probed with a timeout: they were never observed to hang, and
    /// adding one would only cost latency for no benefit.
    ///
    /// The whole method is additionally wrapped in a last-resort
    /// catch-all: this is called both from a caller with its own
    /// try/catch (NavigationViewModel.InitializeAsync) and one without
    /// (the periodic background refresh timer), and an unhandled
    /// exception escaping a Timer-driven background Task can crash the
    /// process. This method must never throw anything other than
    /// OperationCanceledException for a genuinely cancelled caller.
    /// </summary>
    public sealed class DriveEnumerationService : IDriveEnumerationService
    {
        private const int DefaultNetworkProbeTimeoutMs = 1500;

        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;

        public DriveEnumerationService(ISettingsService settingsService = null, ILogger logger = null)
        {
            _settingsService = settingsService;
            _logger = (logger ?? Log.Logger).ForContext<DriveEnumerationService>();
        }

        public Task<IReadOnlyList<DriveNodeInfo>> GetDrivesAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                try
                {
                    DriveInfo[] drives;
                    try
                    {
                        drives = DriveInfo.GetDrives();
                    }
                    catch (IOException ex)
                    {
                        _logger.Warning(ex, "DriveInfo.GetDrives failed — reporting no drives");
                        return (IReadOnlyList<DriveNodeInfo>)Array.Empty<DriveNodeInfo>();
                    }

                    int probeTimeoutMs = _settingsService?.Current?.Navigation?.NetworkDriveProbeTimeoutMs ?? DefaultNetworkProbeTimeoutMs;

                    var probeTasks = new List<Task<DriveNodeInfo>>(drives.Length);
                    if (OperatingSystem.IsWindows())
                    {
                        foreach (DriveInfo drive in drives)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            probeTasks.Add(BuildDriveNodeInfoAsync(drive, probeTimeoutMs, cancellationToken));
                        }
                    }
                    else
                    {
                        // Linux/macOS: only the volumes a desktop file manager shows (see MountedVolumes), not every
                        // pseudo file system DriveInfo.GetDrives returns there.
                        foreach (MountedVolume volume in MountedVolumes.GetDisplayVolumes())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            probeTasks.Add(BuildUnixVolumeNodeInfoAsync(volume, probeTimeoutMs, cancellationToken));
                        }
                    }

                    DriveNodeInfo[] probed = await Task.WhenAll(probeTasks).ConfigureAwait(false);
                    var combined = new List<DriveNodeInfo>(probed);
                    combined.AddRange(PhoneDrives());
                    DriveNodeInfo[] results = combined.ToArray();

                    LogIfChanged(results);
                    return (IReadOnlyList<DriveNodeInfo>)results;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unexpected error detecting drives — reporting no drives for this cycle");
                    return (IReadOnlyList<DriveNodeInfo>)Array.Empty<DriveNodeInfo>();
                }
            }, cancellationToken);
        }

        private string _lastLoggedDrives;

        /// <summary>
        /// Writes the drive list to the log when it changes (a drive or phone came or went, one became ready), naming
        /// them; the poll every few seconds no longer adds a line each time, which buried everything else in the log.
        /// </summary>
        private void LogIfChanged(DriveNodeInfo[] drives)
        {
            var parts = new string[drives.Length];
            for (int i = 0; i < drives.Length; i++)
            {
                DriveNodeInfo d = drives[i];
                string kind = d.Phone != null ? "phone" : d.DriveType.ToString();
                parts[i] = $"{d.RootPath} ({kind}{(d.IsReady ? string.Empty : ", not ready")})";
            }

            string summary = string.Join(", ", parts);
            if (string.Equals(summary, Interlocked.Exchange(ref _lastLoggedDrives, summary), StringComparison.Ordinal))
            {
                return;
            }

            _logger.Information("Detected {Count} drives: {Drives}", drives.Length, summary);
        }

        // ------------------------------------------------------------------ phones

        private const int PhoneRefreshIntervalMs = 5000;
        private const int PhoneListTimeoutMs = 20000;
        private IReadOnlyList<Platform.Phones.PhoneDevice> _phones = Array.Empty<Platform.Phones.PhoneDevice>();
        private long _phonesRefreshedAt = long.MinValue / 2;
        private int _phoneRefreshRunning;

        /// <summary>
        /// Connected phones as drives, from a list refreshed in the background at most every few seconds: asking phones
        /// is slow (each answers over USB, a locked one may take seconds), and the drive list is polled every 3 seconds.
        /// A phone plugged in appears within a few seconds; the poll itself never waits for one.
        /// </summary>
        private IEnumerable<DriveNodeInfo> PhoneDrives()
        {
            if (!Platform.Phones.Phones.ShowsPhones)
            {
                Volatile.Write(ref _phones, Array.Empty<Platform.Phones.PhoneDevice>());
                yield break; // turned off (Folder options): gone from the list at once, and nothing asks them
            }

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _phonesRefreshedAt) >= PhoneRefreshIntervalMs &&
                Interlocked.CompareExchange(ref _phoneRefreshRunning, 1, 0) == 0)
            {
                _ = RefreshPhonesAsync(); // catches everything itself
            }

            foreach (Platform.Phones.PhoneDevice phone in Volatile.Read(ref _phones))
            {
                string root = phone.RootPath ?? phone.MountAddress;
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                yield return new DriveNodeInfo(root, phone.Name, DriveType.Unknown, isReady: true,
                    Math.Max(0, phone.TotalBytes), Math.Max(0, phone.FreeBytes)) { Phone = phone };
            }
        }

        private async Task RefreshPhonesAsync()
        {
            try
            {
                IReadOnlyList<Platform.Phones.PhoneDevice> phones = await BlockingOperationRunner.RunAsync(
                    _ => Platform.Phones.Phones.GetDevices(), PhoneListTimeoutMs, CancellationToken.None, threadName: "ByteExplorer.Phones").ConfigureAwait(false);
                Volatile.Write(ref _phones, phones);
            }
            catch (TimeoutException)
            {
                _logger.Debug("Listing phones took longer than {TimeoutMs}ms; keeping the previous list", PhoneListTimeoutMs);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Listing phones failed");
            }
            finally
            {
                Interlocked.Exchange(ref _phonesRefreshedAt, Environment.TickCount64);
                Interlocked.Exchange(ref _phoneRefreshRunning, 0);
            }
        }

        private async Task<DriveNodeInfo> BuildDriveNodeInfoAsync(DriveInfo drive, int probeTimeoutMs, CancellationToken cancellationToken)
        {
            if (drive.DriveType != DriveType.Network)
            {
                // Local media: fast, never observed to hang — no
                // dedicated-thread/timeout overhead needed or wanted.
                return BuildDriveNodeInfo(drive, probeTimedOut: false);
            }

            try
            {
                return await BlockingOperationRunner.RunAsync(
                    _ => BuildDriveNodeInfo(drive, probeTimedOut: false),
                    probeTimeoutMs,
                    cancellationToken,
                    threadName: "ByteExplorer.DriveProbe").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Debug(
                    "Network drive {DriveName} did not respond within {TimeoutMs}ms — reporting unreachable for this cycle",
                    drive.Name, probeTimeoutMs);
                return new DriveNodeInfo(drive.Name, null, DriveType.Network, isReady: false, totalSize: 0, availableFreeSpace: 0, probeTimedOut: true);
            }
            catch (IOException ex)
            {
                _logger.Debug(ex, "I/O error probing network drive {DriveName}", drive.Name);
                return new DriveNodeInfo(drive.Name, null, DriveType.Network, isReady: false, totalSize: 0, availableFreeSpace: 0, probeTimedOut: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Debug(ex, "Access denied probing network drive {DriveName}", drive.Name);
                return new DriveNodeInfo(drive.Name, null, DriveType.Network, isReady: false, totalSize: 0, availableFreeSpace: 0);
            }
        }

        /// <summary>
        /// A Unix volume: its mount point as the root path, the volume's own label (the mount folder's name, which is
        /// what GNOME/KDE/Finder show; DriveInfo.VolumeLabel is just the mount point there), and a DriveType derived
        /// from where it is mounted, since .NET reports every local disk on Linux as Fixed, USB sticks included.
        /// Network volumes get the same probe timeout as Windows network drives.
        /// </summary>
        private async Task<DriveNodeInfo> BuildUnixVolumeNodeInfoAsync(MountedVolume volume, int probeTimeoutMs, CancellationToken cancellationToken)
        {
            DriveType type = volume.Kind switch
            {
                VolumeKind.Removable => DriveType.Removable,
                VolumeKind.Network => DriveType.Network,
                VolumeKind.Optical => DriveType.CDRom,
                _ => DriveType.Fixed
            };

            DriveNodeInfo Probe()
            {
                long total = 0;
                long free = 0;
                bool ready = Directory.Exists(volume.RootPath);
                if (ready && volume.DriveInfo != null)
                {
                    (total, free) = TryReadCapacity(volume.DriveInfo);
                }

                return new DriveNodeInfo(volume.RootPath, volume.Label, type, ready, total, free);
            }

            if (type != DriveType.Network)
            {
                return Probe();
            }

            try
            {
                return await BlockingOperationRunner.RunAsync(_ => Probe(), probeTimeoutMs, cancellationToken, threadName: "ByteExplorer.DriveProbe").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Debug(ex, "Network volume {Root} did not respond — reporting unreachable for this cycle", volume.RootPath);
                return new DriveNodeInfo(volume.RootPath, volume.Label, DriveType.Network, isReady: false, totalSize: 0, availableFreeSpace: 0, probeTimedOut: ex is TimeoutException);
            }
        }

        private DriveNodeInfo BuildDriveNodeInfo(DriveInfo drive, bool probeTimedOut)
        {
            bool isReady = false;
            string volumeLabel = null;
            long totalSize = 0;
            long freeSpace = 0;

            try
            {
                isReady = drive.IsReady;
            }
            catch (IOException ex)
            {
                _logger.Debug(ex, "Drive {DriveName} not ready", drive.Name);
                isReady = false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Debug(ex, "Drive {DriveName} access denied", drive.Name);
                isReady = false;
            }

            if (isReady)
            {
                volumeLabel = TryReadVolumeLabel(drive);
                (totalSize, freeSpace) = TryReadCapacity(drive);
            }

            return new DriveNodeInfo(drive.Name, volumeLabel, drive.DriveType, isReady, totalSize, freeSpace, probeTimedOut);
        }

        private string TryReadVolumeLabel(DriveInfo drive)
        {
            try
            {
                return drive.VolumeLabel;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static (long totalSize, long freeSpace) TryReadCapacity(DriveInfo drive)
        {
            try
            {
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (IOException)
            {
                return (0, 0);
            }
            catch (UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }
    }
}
