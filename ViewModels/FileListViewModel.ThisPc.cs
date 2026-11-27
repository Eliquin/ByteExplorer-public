// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// This PC home surface — devices/drives, pinned folders, and a
    /// Recent list, matching the Windows 11 File Explorer layout for
    /// the computer root.
    ///
    /// Drives are NOT independently probed here. Doing so used to be a
    /// second, parallel implementation of exactly the same readiness/
    /// timeout/capacity logic DriveEnumerationService already owns for
    /// the navigation tree — real duplication with a real cost: two
    /// independent code paths that could silently drift apart, and a
    /// real gap, since this page's copy only ever ran once per
    /// navigation and so never reflected a drive plugged in or removed
    /// while already looking at this page (unlike the tree, which
    /// refreshes every few seconds). Both problems are solved the same
    /// way: NavigationViewModel is the single source of truth for drive
    /// state, and broadcasts every refresh via DrivesRefreshed;
    /// MainViewModel forwards that here (see UpdateThisPcDrives), so
    /// this page now inherits the tree's own live-refresh behavior for
    /// free and can never drift from what the tree itself shows.
    /// </summary>
    public sealed partial class FileListViewModel
    {
        private const int MaxRecentItems = 40;

        private ObservableCollection<ThisPcDriveItem> _thisPcDrives =
            new ObservableCollection<ThisPcDriveItem>();
        private ObservableCollection<ThisPcRecentItem> _thisPcRecent =
            new ObservableCollection<ThisPcRecentItem>();
        private FolderChangeWatcher _thisPcRecentWatcher;

        public ObservableCollection<ThisPcDriveItem> ThisPcDrives
        {
            get => _thisPcDrives;
            private set => SetProperty(ref _thisPcDrives, value);
        }

        public ObservableCollection<ThisPcRecentItem> ThisPcRecent
        {
            get => _thisPcRecent;
            private set => SetProperty(ref _thisPcRecent, value);
        }

        /// <summary>
        /// Called by MainViewModel whenever NavigationViewModel has a
        /// fresh drive snapshot (initial build, every ~3s periodic
        /// refresh, or an on-demand refresh triggered from the tree's
        /// shell context menu) — see NavigationViewModel.DrivesRefreshed.
        /// Deliberately kept updated even while this page isn't the one
        /// currently visible: the cost (mapping a handful of drives into
        /// a new ObservableCollection) is negligible, and it means the
        /// data is already current the instant the person DOES navigate
        /// here, with zero latency, rather than waiting on a fresh probe
        /// at that point. Only the visible status text is gated behind
        /// IsThisPcView — this must never overwrite the status bar of
        /// whatever folder the person is actually looking at right now.
        /// </summary>
        public void UpdateThisPcDrives(IReadOnlyList<DriveNodeInfo> drives)
        {
            ThisPcDrives = new ObservableCollection<ThisPcDriveItem>(MapToThisPcDriveItems(drives));

            if (IsThisPcView)
            {
                UpdateThisPcStatusText();
            }
        }

        /// <summary>
        /// Loads the Windows 11-style This PC page. Drives are already
        /// current (see UpdateThisPcDrives) — only Recent actually needs
        /// fetching here, plus starting live watching on the shell
        /// Recent folder itself so newly-opened files appear on their
        /// own, the same way expanded tree folders and the open center
        /// folder already do.
        /// </summary>
        public async Task LoadThisPcAsync()
        {
            NavigationRequested?.Invoke(this, EventArgs.Empty);
            HasStartedNavigation = true;
            _loadCts?.Cancel();
            CancelFolderSizes();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            // No single folder is displayed on this surface — whatever
            // was being live-watched before (a regular folder, or a
            // previous visit's Recent watch) no longer applies.
            StopAllLiveWatching();

            SetSurface(FileListSurface.ThisPc);
            RecordArrival(HistoryEntry.ThisPc);
            IsLoading = true;
            ErrorMessage = null;
            CurrentPath = null;
            Items = null;
            TimelineGroups = null;
            ColumnPanes = null;
            NexusClusters = null;
            SelectedItem = null;
            SelectedItems.Clear();
            StatusText = "Loading This PC…";

            try
            {
                ThisPcRecentItem[] recent = await Task.Run(() => EnumerateRecentItems(cts.Token), cts.Token).ConfigureAwait(true);

                if (!ReferenceEquals(_loadCts, cts))
                {
                    return; // superseded
                }

                ThisPcRecent = new ObservableCollection<ThisPcRecentItem>(recent);
                UpdateThisPcStatusText();

                await StartRecentWatchingAsync(cts).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // superseded
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load This PC view");
                ErrorMessage = "Couldn't load This PC.";
                StatusText = string.Empty;
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    IsLoading = false;
                }
            }
        }

        private void UpdateThisPcStatusText()
        {
            int driveCount = _thisPcDrives.Count;
            int recentCount = _thisPcRecent.Count;

            StatusText = string.Format(
                "{0:N0} drive{1} · {2:N0} recent item{3}",
                driveCount,
                driveCount == 1 ? string.Empty : "s",
                recentCount,
                recentCount == 1 ? string.Empty : "s");
        }

        public async Task OpenThisPcDriveAsync(ThisPcDriveItem drive)
        {
            if (drive == null || string.IsNullOrEmpty(drive.RootPath))
            {
                return;
            }

            if (drive.IsUnreachable)
            {
                OperationFailed?.Invoke(this, "This drive isn't responding right now.");
                return;
            }

            if (drive.Phone?.NeedsMount == true)
            {
                // Linux: the desktop sees the phone but hasn't mounted it; mount it (gio), then open its folder.
                StatusText = $"Connecting to {drive.Phone.Name}…";
                try
                {
                    string folder = await Core.Platform.Phones.MountedPhoneProvider.MountAsync(drive.Phone, CancellationToken.None).ConfigureAwait(true);
                    await LoadFolderAsync(folder).ConfigureAwait(true);
                }
                catch (IOException ex)
                {
                    StatusText = string.Empty;
                    OperationFailed?.Invoke(this, Infrastructure.FriendlyErrors.With("Couldn't open the phone.", ex));
                }

                return;
            }

            if (!drive.IsReady)
            {
                OperationFailed?.Invoke(this, "This drive is not ready.");
                return;
            }

            await LoadFolderAsync(drive.RootPath).ConfigureAwait(true);
        }

        public async Task OpenThisPcRecentAsync(ThisPcRecentItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullPath))
            {
                return;
            }

            try
            {
                if (item.IsDirectory && Directory.Exists(item.FullPath))
                {
                    await LoadFolderAsync(item.FullPath).ConfigureAwait(true);
                    return;
                }

                // Files and .lnk shortcuts — ShellExecute resolves links.
                ShellLauncher.Open(item.FullPath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to open recent item {Path}", item.FullPath);
                OpenItemFailed?.Invoke(this, "Couldn't open \"" + item.DisplayName + "\". " + ex.Message);
            }
        }

        private static ThisPcDriveItem[] MapToThisPcDriveItems(IReadOnlyList<DriveNodeInfo> drives)
        {
            if (drives == null || drives.Count == 0)
            {
                return Array.Empty<ThisPcDriveItem>();
            }

            var list = new ThisPcDriveItem[drives.Count];
            for (int i = 0; i < drives.Count; i++)
            {
                DriveNodeInfo drive = drives[i];
                Core.Models.FolderOptionsSettings options = FolderOptions.Current;
                string label = DriveLabels.Format(drive, options.ShowDriveLetters, options.DriveLettersFirst);

                list[i] = new ThisPcDriveItem(
                    drive.RootPath,
                    label,
                    drive.DriveType,
                    drive.IsReady,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.ProbeTimedOut,
                    drive.Phone);
            }

            // Fixed → Removable → Network → Optical → other
            Array.Sort(list, (a, b) =>
            {
                int ra = a.IsPhone ? 2 : DriveRank(a.DriveType); // phones after the removable drives
                int rb = b.IsPhone ? 2 : DriveRank(b.DriveType);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.RootPath, b.RootPath, StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        private static int DriveRank(DriveType type)
        {
            switch (type)
            {
                case DriveType.Fixed: return 0;
                case DriveType.Removable: return 1;
                case DriveType.Network: return 3;
                case DriveType.CDRom: return 4;
                default: return 5;
            }
        }

        private static ThisPcRecentItem[] EnumerateRecentItems(CancellationToken cancellationToken)
        {
            string recentDir;
            try
            {
                recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            }
            catch
            {
                return Array.Empty<ThisPcRecentItem>();
            }

            if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir))
            {
                return Array.Empty<ThisPcRecentItem>();
            }

            var entries = new List<Tuple<DateTime, ThisPcRecentItem>>();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(recentDir);
            }
            catch
            {
                return Array.Empty<ThisPcRecentItem>();
            }

            foreach (string path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(path);
                    DateTime accessed = info.LastAccessTimeUtc;
                    if (accessed == DateTime.MinValue)
                    {
                        accessed = info.LastWriteTimeUtc;
                    }

                    string name = Path.GetFileNameWithoutExtension(info.Name);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = info.Name;
                    }

                    // Prefer resolving .lnk targets so double-click opens the real item
                    // and folders navigate. Fall back to the shortcut path itself.
                    string targetPath = path;
                    bool isDirectory = false;
                    long size = info.Length;

                    if (string.Equals(info.Extension, ".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        string resolved = TryResolveShortcutTarget(path);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            targetPath = resolved;
                            if (Directory.Exists(resolved))
                            {
                                isDirectory = true;
                                size = 0;
                            }
                            else if (File.Exists(resolved))
                            {
                                try { size = new FileInfo(resolved).Length; }
                                catch { size = 0; }
                            }
                        }
                    }

                    entries.Add(Tuple.Create(
                        accessed,
                        new ThisPcRecentItem(name, targetPath, isDirectory, accessed, size)));
                }
                catch
                {
                    // Skip unreadable entries
                }
            }

            entries.Sort((a, b) => b.Item1.CompareTo(a.Item1));

            int take = Math.Min(MaxRecentItems, entries.Count);
            var result = new ThisPcRecentItem[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = entries[i].Item2;
            }

            return result;
        }

        /// <summary>
        /// Lightweight .lnk target resolution without a full COM ShellLink
        /// dependency. Reads the standard shell-link binary layout enough to
        /// extract a local path when present; returns null when the link is
        /// exotic (PIDL-only, network special cases) so the caller can still
        /// open the .lnk via ShellExecute.
        /// </summary>
        private static string TryResolveShortcutTarget(string lnkPath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(lnkPath);
                // Shell Link header is 0x4C bytes; magic = 0x0000004C
                if (data.Length < 0x4C) return null;
                if (BitConverter.ToInt32(data, 0) != 0x4C) return null;

                int flags = BitConverter.ToInt32(data, 0x14);
                int pos = 0x4C;

                // Skip shell item id list if present (HasLinkTargetIDList = bit 0)
                if ((flags & 0x01) != 0)
                {
                    if (pos + 2 > data.Length) return null;
                    int idListSize = BitConverter.ToUInt16(data, pos);
                    pos += 2 + idListSize;
                }

                // LinkInfo (HasLinkInfo = bit 1)
                if ((flags & 0x02) == 0) return null;
                if (pos + 4 > data.Length) return null;

                int linkInfoSize = BitConverter.ToInt32(data, pos);
                if (linkInfoSize < 0x1C || pos + linkInfoSize > data.Length) return null;

                int linkInfoFlags = BitConverter.ToInt32(data, pos + 8);
                // VolumeIDAndLocalBasePath = bit 0
                if ((linkInfoFlags & 0x01) == 0) return null;

                // [MS-SHLLINK] 2.3: LinkInfoHeaderSize >= 0x24 means the Unicode offsets are present. They are the
                // only lossless source: the ANSI fields hold the path in the code page of the machine that wrote
                // the link ('?' for anything it can't represent).
                int linkInfoHeaderSize = BitConverter.ToInt32(data, pos + 4);
                int linkInfoEnd = pos + linkInfoSize;
                string basePath = null;
                string suffix = null;
                if (linkInfoHeaderSize >= 0x24)
                {
                    basePath = ReadUnicodeZ(data, pos + BitConverter.ToInt32(data, pos + 0x1C), linkInfoEnd);
                    suffix = ReadUnicodeZ(data, pos + BitConverter.ToInt32(data, pos + 0x20), linkInfoEnd);
                }

                if (string.IsNullOrEmpty(basePath))
                {
                    basePath = ReadAnsiZ(data, pos + BitConverter.ToInt32(data, pos + 16), linkInfoEnd);
                    suffix = ReadAnsiZ(data, pos + BitConverter.ToInt32(data, pos + 0x18), linkInfoEnd);
                }

                if (string.IsNullOrWhiteSpace(basePath)) return null;
                if (string.IsNullOrEmpty(suffix)) return basePath;
                return basePath.EndsWith("\\", StringComparison.Ordinal) ? basePath + suffix : basePath + "\\" + suffix; // .lnk targets are Windows paths
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The system ANSI code page, which is what a .lnk's non-Unicode path fields use. Not Encoding.Default:
        /// on .NET (Core) that is always UTF-8, which would garble any non-ASCII path.
        /// </summary>
        private static readonly System.Text.Encoding AnsiEncoding = CreateAnsiEncoding();

        private static System.Text.Encoding CreateAnsiEncoding()
        {
            try
            {
                return System.Text.CodePagesEncodingProvider.Instance.GetEncoding(0) ?? System.Text.Encoding.Latin1;
            }
            catch
            {
                return System.Text.Encoding.Latin1;
            }
        }

        private static string ReadAnsiZ(byte[] data, int start, int limit)
        {
            if (start <= 0 || start >= limit || limit > data.Length) return null;
            int end = Array.IndexOf(data, (byte)0, start, limit - start);
            if (end < 0) end = limit;
            return end > start ? AnsiEncoding.GetString(data, start, end - start) : null;
        }

        private static string ReadUnicodeZ(byte[] data, int start, int limit)
        {
            if (start <= 0 || start >= limit || limit > data.Length) return null;
            int end = start;
            while (end + 1 < limit && (data[end] | data[end + 1]) != 0) end += 2;
            return end > start ? System.Text.Encoding.Unicode.GetString(data, start, end - start) : null;
        }

        /// <summary>
        /// Starts live-watching the shell Recent folder so items opened
        /// elsewhere (this app, Explorer, any other program) appear here
        /// without needing to navigate away and back. Best-effort, same
        /// as every other FolderChangeWatcher use in this app — reuses
        /// FileList.EnableLiveFolderWatching / LiveRefreshDebounceMs
        /// rather than introducing a separate setting for what is, in
        /// every respect that matters, the same feature applied to one
        /// more folder.
        /// </summary>
        private async Task StartRecentWatchingAsync(CancellationTokenSource cts)
        {
            bool enabled = _settingsService?.Current?.FileList?.EnableLiveFolderWatching ?? true;
            if (!enabled || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            string recentDir;
            try
            {
                recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not resolve the Recent folder path for live watching");
                return;
            }

            if (string.IsNullOrEmpty(recentDir))
            {
                return;
            }

            int debounceMs = _settingsService?.Current?.FileList?.LiveRefreshDebounceMs ?? 400;

            // The shell Recent folder is always local, never a network
            // path — the network-create-timeout parameter is passed
            // through anyway purely for consistency with every other
            // FolderChangeWatcher.CreateAsync call site in this app; it
            // will simply never be exercised here.
            FolderChangeWatcher watcher = await FolderChangeWatcher.CreateAsync(
                recentDir,
                directoriesOnly: false,
                debounceMs,
                cancellationToken: cts.Token,
                logger: _logger).ConfigureAwait(false);

            if (_disposed || !ReferenceEquals(_loadCts, cts))
            {
                watcher.Dispose();
                return;
            }

            watcher.Changed += OnRecentFolderChanged;
            _thisPcRecentWatcher = watcher;
        }

        private void OnRecentFolderChanged(object sender, EventArgs e)
        {
            // Raised on a background thread — marshal to the UI thread
            // before touching UI-bound state, same reasoning as every
            // other watcher reaction in this app.
            try
            {
                var ignored = _dispatcher.InvokeAsync(() =>
                {
                    if (!IsThisPcView)
                    {
                        return;
                    }

                    var ignoredReload = ReloadRecentItemsAsync();
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to marshal a Recent-folder change reload — likely shutting down");
            }
        }

        private async Task ReloadRecentItemsAsync()
        {
            CancellationTokenSource cts = _loadCts;
            if (cts == null || cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                ThisPcRecentItem[] recent = await Task.Run(() => EnumerateRecentItems(cts.Token), cts.Token).ConfigureAwait(true);

                if (!ReferenceEquals(_loadCts, cts) || !IsThisPcView)
                {
                    return;
                }

                ThisPcRecent = new ObservableCollection<ThisPcRecentItem>(recent);
                UpdateThisPcStatusText();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to refresh Recent items after a live change notification");
            }
        }

        private void StopRecentWatching()
        {
            if (_thisPcRecentWatcher != null)
            {
                _thisPcRecentWatcher.Changed -= OnRecentFolderChanged;
                _thisPcRecentWatcher.Dispose();
                _thisPcRecentWatcher = null;
            }
        }
    }
}
