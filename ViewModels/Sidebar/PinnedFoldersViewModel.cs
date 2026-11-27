// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels.Sidebar
{
    /// <summary>One pinned place in Quick access.</summary>
    public sealed class PinnedItemViewModel : ViewModelBase
    {
        private string _alias;
        private string _colorName;
        private bool _isAvailable = true;
        private bool _isActive;
        private IBrush _iconBrush;
        private IBrush _monochromeBrush;

        internal PinnedItemViewModel(string path, string alias, string colorName)
        {
            Path = path;
            _alias = alias;
            _colorName = colorName;
            IsTrash = string.Equals(path, PinColors.TrashKey, StringComparison.Ordinal);
            (Icon, DefaultColorKey) = PinnedFoldersViewModel.IconFor(path);
        }

        /// <summary>The folder, ZIP file or network path; <see cref="PinColors.TrashKey"/> for the trash.</summary>
        public string Path { get; }

        public bool IsTrash { get; }

        /// <summary>The trash's folder on Linux and macOS; null on Windows, where the Recycle Bin is a shell place.</summary>
        public string FolderPath => IsTrash ? SpecialFolders.RecycleBin : Path;

        /// <summary>The name shown: the person's own name for it, else the folder's.</summary>
        public string Name => !string.IsNullOrEmpty(_alias) ? _alias : FolderName;

        public string FolderName => IsTrash
            ? (OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash")
            : PathUtil.GetName(Path) is { Length: > 0 } name ? name : Path;

        public string Alias
        {
            get => _alias;
            internal set
            {
                if (SetProperty(ref _alias, value))
                {
                    RaisePropertyChanged(nameof(Name));
                    RaisePropertyChanged(nameof(ToolTip));
                }
            }
        }

        public string ColorName
        {
            get => _colorName;
            internal set
            {
                if (SetProperty(ref _colorName, value))
                {
                    RaisePropertyChanged(nameof(HasCustomColor));
                }
            }
        }

        public bool HasCustomColor => _colorName != null;

        public Symbol Icon { get; }

        /// <summary>The palette key of the icon's usual color ("MonitorBlue" for Desktop).</summary>
        public string DefaultColorKey { get; }

        /// <summary>The icon's color: the chosen one, else its usual one from the palette.</summary>
        public IBrush IconBrush
        {
            get => _iconBrush;
            internal set => SetProperty(ref _iconBrush, value);
        }

        /// <summary>For layouts with gray icons (Nemo): the chosen color, else gray.</summary>
        public IBrush MonochromeBrush
        {
            get => _monochromeBrush;
            internal set => SetProperty(ref _monochromeBrush, value);
        }

        /// <summary>False when the folder isn't reachable now (deleted, a disconnected drive): the row is dimmed.</summary>
        public bool IsAvailable
        {
            get => _isAvailable;
            internal set
            {
                if (SetProperty(ref _isAvailable, value))
                {
                    RaisePropertyChanged(nameof(ToolTip));
                }
            }
        }

        /// <summary>The active pane shows this folder: the row is highlighted ("you are here").</summary>
        public bool IsActive
        {
            get => _isActive;
            internal set => SetProperty(ref _isActive, value);
        }

        public string ToolTip
        {
            get
            {
                string where = IsTrash ? FolderName : Path;
                if (!string.IsNullOrEmpty(_alias))
                {
                    where = $"{_alias} — {where}";
                }

                return _isAvailable ? where : where + "\nNot available right now";
            }
        }
    }

    /// <summary>
    /// Quick access: the places pinned at the top of the sidebar, in the person's order, with their own names and
    /// colors, saved in the settings (Navigation.Pinned). One list for what were separately "quick access" (fixed) and
    /// "bookmarks" (Ctrl+D) before. Shared by both panes and both layouts.
    /// <para>
    /// First run: Desktop, Downloads, Documents, Pictures, Music, Videos and the trash, as the sidebar always listed them
    /// (one that doesn't exist on this machine is dimmed), followed by the older bookmarks. After that, only what the
    /// person changes.
    /// </para>
    /// <para>
    /// Availability is checked for local folders only (a network path is never touched on the UI thread), when the
    /// list loads and when drives come or go. The row of the folder the active pane shows is highlighted.
    /// </para>
    /// All members are used on the UI thread.
    /// </summary>
    public sealed class PinnedFoldersViewModel : ViewModelBase
    {
        public const int MaxPins = 200;

        private static readonly Dictionary<string, IBrush> ColorBrushes = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase);
        private readonly ISettingsService _settings;
        private Func<string, IBrush> _brushResolver;
        private string _activePath;

        public PinnedFoldersViewModel(ISettingsService settings)
        {
            _settings = settings;
            Items.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HasItems));
        }

        public ObservableCollection<PinnedItemViewModel> Items { get; } = new ObservableCollection<PinnedItemViewModel>();

        public bool HasItems => Items.Count > 0;

        /// <summary>Looks up palette brushes by key for the current theme (set by the window; re-applied on theme changes).</summary>
        public Func<string, IBrush> BrushResolver
        {
            get => _brushResolver;
            set
            {
                _brushResolver = value;
                RefreshBrushes();
            }
        }

        /// <summary>Builds the list from the settings, creating the defaults the first time.</summary>
        public void Reload()
        {
            NavigationSettings navigation = _settings?.Current?.Navigation;
            if (navigation == null)
            {
                return;
            }

            bool created = false;
            if (navigation.Pinned == null)
            {
                navigation.Pinned = CreateDefaults(navigation.Bookmarks);
                navigation.Bookmarks = new List<string>();
                created = true;
            }

            // Saved paths follow this computer (PortablePaths): "::documents" is this computer's Documents folder, and a
            // profile path saved on another computer is mapped to the same place here. When a mapping happened, the
            // list is saved again in portable form, so it moves with the settings from now on.
            Items.Clear();
            bool migrated = false;
            foreach (PinnedFolderSettings pin in navigation.Pinned)
            {
                string path = PortablePaths.Resolve(pin.Path);
                if (!string.Equals(PortablePaths.ToPortable(path), pin.Path, StringComparison.Ordinal))
                {
                    migrated = true;
                }

                if (!Items.Any(i => PathUtil.AreSame(i.Path, path)))
                {
                    Items.Add(Create(path, pin.Name, pin.Color));
                }
            }

            RefreshAvailabilityAsync().Forget("check Quick access availability");
            RefreshBrushes();
            UpdateActive(_activePath);
            if (created || migrated)
            {
                SaveAsync().Forget(created ? "save the first Quick access" : "save Quick access in portable form");
            }
        }

        /// <summary>
        /// The default pins: Desktop, Downloads, Documents, Pictures, Music, Videos and the trash (as the sidebar always
        /// listed them), then the older bookmarks. Saved as tokens (PortablePaths), so they are this computer's folders
        /// wherever the settings file goes.
        /// </summary>
        internal static List<PinnedFolderSettings> CreateDefaults(IEnumerable<string> bookmarks)
        {
            var pins = new List<PinnedFolderSettings>();
            var seen = new HashSet<string>(PathUtil.Comparer);
            foreach (string folder in new[] { SpecialFolders.Desktop, SpecialFolders.Downloads, SpecialFolders.Documents, SpecialFolders.Pictures, SpecialFolders.Music, SpecialFolders.Videos })
            {
                // All of them, as before: one that doesn't exist yet is shown dimmed, and the person keeps or unpins it.
                if (!string.IsNullOrEmpty(folder) && seen.Add(folder))
                {
                    pins.Add(new PinnedFolderSettings { Path = folder });
                }
            }

            pins.Add(new PinnedFolderSettings { Path = PinColors.TrashKey });
            foreach (string bookmark in bookmarks ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(bookmark) && seen.Add(bookmark.Trim()))
                {
                    pins.Add(new PinnedFolderSettings { Path = bookmark.Trim() });
                }
            }

            return pins;
        }

        // ------------------------------------------------------------------ queries

        public bool Contains(string path) => IndexOf(path) >= 0;

        public int IndexOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }

            for (int i = 0; i < Items.Count; i++)
            {
                PinnedItemViewModel item = Items[i];
                if (string.Equals(item.Path, path, StringComparison.Ordinal) || (!item.IsTrash && PathUtil.AreSame(item.Path, path)) ||
                    (item.IsTrash && item.FolderPath != null && PathUtil.AreSame(item.FolderPath, path)))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>True for what can be pinned: an existing folder or ZIP file, a path inside a ZIP, or a network path.</summary>
        public static bool CanPin(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return NetworkLocations.IsNetworkAddress(path) || SafeExists(path) || ArchiveBrowser.IsInsideArchive(path);
        }

        // ------------------------------------------------------------------ changes

        /// <summary>Pins folders at <paramref name="index"/> (the end when -1); already pinned ones and non-folders are skipped.</summary>
        public async Task<int> PinAsync(IReadOnlyList<string> paths, int index = -1)
        {
            int added = 0;
            int at = index < 0 || index > Items.Count ? Items.Count : index;
            foreach (string raw in paths ?? Array.Empty<string>())
            {
                string path = raw == PinColors.TrashKey ? raw : PathUtil.TrimTrailingSeparator(raw?.Trim());
                if (Items.Count >= MaxPins || Contains(path) || (path != PinColors.TrashKey && !CanPin(path)))
                {
                    continue;
                }

                PinnedItemViewModel item = Create(path, null, null);
                ApplyBrushes(item);
                item.IsAvailable = true;
                item.IsActive = _activePath != null && PathUtil.AreSame(path, _activePath);
                Items.Insert(at++, item);
                added++;
            }

            if (added > 0)
            {
                await SaveAsync().ConfigureAwait(true);
            }

            return added;
        }

        public Task PinAsync(string path, int index = -1) => PinAsync(new[] { path }, index);

        public Task UnpinAsync(PinnedItemViewModel item)
        {
            if (item == null || !Items.Remove(item))
            {
                return Task.CompletedTask;
            }

            return SaveAsync();
        }

        public Task UnpinAsync(string path)
        {
            int index = IndexOf(path);
            return index < 0 ? Task.CompletedTask : UnpinAsync(Items[index]);
        }

        /// <summary>Pins the folder, or unpins it when it is pinned (Ctrl+D, the omnibar's pin).</summary>
        public Task ToggleAsync(string path) => Contains(path) ? UnpinAsync(path) : PinAsync(path);

        /// <summary>
        /// Moves a pin to the position it is dropped at: <paramref name="insertIndex"/> counts slots between rows (0 is above
        /// the first, Count is below the last), as an insertion line shows them.
        /// </summary>
        public Task MoveToSlotAsync(PinnedItemViewModel item, int insertIndex)
        {
            int from = Items.IndexOf(item);
            if (from < 0)
            {
                return Task.CompletedTask;
            }

            int to = Math.Clamp(insertIndex, 0, Items.Count);
            if (to > from)
            {
                to--; // the item leaves its old slot first
            }

            if (to == from)
            {
                return Task.CompletedTask;
            }

            Items.Move(from, to);
            return SaveAsync();
        }

        /// <summary>One position up (-1) or down (+1): Alt+arrows and "Move up/down".</summary>
        public Task MoveByAsync(PinnedItemViewModel item, int delta)
        {
            int from = Items.IndexOf(item);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= Items.Count)
            {
                return Task.CompletedTask;
            }

            Items.Move(from, to);
            return SaveAsync();
        }

        /// <summary>Shows the pin under another name; empty or the folder's own name goes back to the folder's name.</summary>
        public Task RenameAsync(PinnedItemViewModel item, string name)
        {
            if (item == null)
            {
                return Task.CompletedTask;
            }

            string trimmed = name?.Trim();
            item.Alias = string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, item.FolderName, StringComparison.Ordinal)
                ? null
                : trimmed.Length > 100 ? trimmed.Substring(0, 100) : trimmed;
            return SaveAsync();
        }

        /// <summary>Colors the pin's icon (one of <see cref="PinColors"/>), or back to its usual color with null.</summary>
        public Task SetColorAsync(PinnedItemViewModel item, string colorName)
        {
            if (item == null)
            {
                return Task.CompletedTask;
            }

            item.ColorName = PinColors.Normalize(colorName);
            ApplyBrushes(item);
            return SaveAsync();
        }

        // ------------------------------------------------------------------ state

        /// <summary>Highlights the pin of the folder the active pane shows.</summary>
        public void UpdateActive(string currentPath)
        {
            _activePath = currentPath;
            foreach (PinnedItemViewModel item in Items)
            {
                string folder = item.FolderPath;
                item.IsActive = currentPath != null && folder != null && PathUtil.AreSame(folder, currentPath);
            }
        }

        /// <summary>Dims pins whose local folder is gone (deleted, a drive unplugged); network paths are left alone.</summary>
        public void RefreshAvailability()
        {
            foreach (PinnedItemViewModel item in Items)
            {
                item.IsAvailable = IsAvailable(item);
            }
        }

        private int _availabilityRunning;

        /// <summary>
        /// The same check off the UI thread (on every window activation, when drives change, at load): a pin on a mapped
        /// drive that is disconnected can take seconds to answer, which froze the window on each Alt+Tab. The rows update
        /// when the answers arrive; a check already under way is not started twice.
        /// </summary>
        public async Task RefreshAvailabilityAsync()
        {
            if (Interlocked.Exchange(ref _availabilityRunning, 1) == 1)
            {
                return;
            }

            try
            {
                PinnedItemViewModel[] items = Items.ToArray();
                bool[] available = await Task.Run(() => items.Select(IsAvailable).ToArray()).ConfigureAwait(true);
                for (int i = 0; i < items.Length; i++)
                {
                    items[i].IsAvailable = available[i];
                }
            }
            finally
            {
                Interlocked.Exchange(ref _availabilityRunning, 0);
            }
        }

        private static bool IsAvailable(PinnedItemViewModel item) =>
            item.IsTrash || NetworkLocations.IsNetworkAddress(item.Path) || SafeExists(item.Path) || ArchiveBrowser.IsInsideArchive(item.Path);

        public void RefreshBrushes()
        {
            foreach (PinnedItemViewModel item in Items)
            {
                ApplyBrushes(item);
            }
        }

        private void ApplyBrushes(PinnedItemViewModel item)
        {
            IBrush custom = item.ColorName != null ? BrushOfColor(item.ColorName) : null;
            item.IconBrush = custom ?? Resolve(item.DefaultColorKey);
            item.MonochromeBrush = custom ?? Resolve("IconGray");
        }

        private IBrush Resolve(string key) => _brushResolver?.Invoke(key) ?? Brushes.Gray;

        private static IBrush BrushOfColor(string name)
        {
            if (!ColorBrushes.TryGetValue(name, out IBrush brush))
            {
                string hex = PinColors.HexOf(name);
                brush = hex == null ? null : new SolidColorBrush(Color.Parse(hex)).ToImmutable();
                ColorBrushes[name] = brush;
            }

            return brush;
        }

        /// <summary>
        /// Rebuilds the list only when the settings hold other pins than it shows: another window pinned, unpinned,
        /// renamed, recolored or moved one (the settings are shared by every window).
        /// </summary>
        public void ReloadIfChanged()
        {
            List<PinnedFolderSettings> saved = _settings?.Current?.Navigation?.Pinned;
            if (saved == null)
            {
                return;
            }

            bool same = saved.Count == Items.Count;
            for (int i = 0; same && i < saved.Count; i++)
            {
                PinnedItemViewModel item = Items[i];
                PinnedFolderSettings pin = saved[i];
                same = string.Equals(PortablePaths.ToPortable(item.Path), pin.Path, StringComparison.Ordinal) &&
                       string.Equals(item.Alias ?? string.Empty, pin.Name ?? string.Empty, StringComparison.Ordinal) &&
                       string.Equals(item.ColorName ?? string.Empty, pin.Color ?? string.Empty, StringComparison.Ordinal);
            }

            if (!same)
            {
                Reload();
            }
        }

        private Task SaveAsync()
        {
            NavigationSettings navigation = _settings?.Current?.Navigation;
            if (navigation == null)
            {
                return Task.CompletedTask;
            }

            var pins = new List<PinnedFolderSettings>(Items.Count);
            foreach (PinnedItemViewModel item in Items)
            {
                pins.Add(new PinnedFolderSettings { Path = PortablePaths.ToPortable(item.Path), Name = item.Alias, Color = item.ColorName });
            }

            navigation.Pinned = pins;
            return SaveSettingsAsync();
        }

        private async Task SaveSettingsAsync()
        {
            try
            {
                await _settings.SaveAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not save Quick access");
            }
        }

        private static PinnedItemViewModel Create(string path, string alias, string color) => new PinnedItemViewModel(path, alias, color);

        /// <summary>The icon and its usual palette color: the special folders keep theirs (as Explorer shows them).</summary>
        internal static (Symbol Icon, string ColorKey) IconFor(string path)
        {
            if (string.Equals(path, PinColors.TrashKey, StringComparison.Ordinal))
            {
                return (Symbol.Delete, "IconGray");
            }

            if (Same(path, SpecialFolders.Desktop))
            {
                return (Symbol.Desktop, "MonitorBlue");
            }

            if (Same(path, SpecialFolders.Downloads))
            {
                return (Symbol.ArrowDownload, "CheckGreen");
            }

            if (Same(path, SpecialFolders.Documents))
            {
                return (Symbol.Document, "DocBlue");
            }

            if (Same(path, SpecialFolders.Pictures))
            {
                return (Symbol.Image, "PictureBlue");
            }

            if (Same(path, SpecialFolders.Music))
            {
                return (Symbol.MusicNote2, "MusicPink");
            }

            if (Same(path, SpecialFolders.Videos))
            {
                return (Symbol.Video, "VideoPurple");
            }

            if (Same(path, SpecialFolders.Home))
            {
                return (Symbol.Home, "IconGray");
            }

            if (NetworkLocations.IsNetworkAddress(path))
            {
                return (Symbol.GlobeDesktop, "IconGray");
            }

            return ArchiveBrowser.IsArchiveFile(path) ? (Symbol.FolderZip, "FolderYellow") : (Symbol.Folder, "FolderYellow");
        }

        private static bool Same(string path, string special) => !string.IsNullOrEmpty(special) && PathUtil.AreSame(path, special);

        private static bool SafeExists(string path)
        {
            try
            {
                return Directory.Exists(path) || (ArchiveBrowser.IsArchiveFile(path) && File.Exists(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ the active pane

        private MainViewModel _main;
        private FileListViewModel _pane;

        /// <summary>Follows the active pane's location to highlight its pin.</summary>
        internal void Follow(MainViewModel main)
        {
            _main = main;
            _main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.FileListViewModel))
                {
                    WatchPane(_main.FileListViewModel);
                }
            };
            WatchPane(main.FileListViewModel);
        }

        private void WatchPane(FileListViewModel pane)
        {
            if (_pane != null)
            {
                _pane.PropertyChanged -= OnPanePropertyChanged;
            }

            _pane = pane;
            if (_pane != null)
            {
                _pane.PropertyChanged += OnPanePropertyChanged;
            }

            UpdateActive(_pane?.CurrentPath);
        }

        private void OnPanePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileListViewModel.CurrentPath))
            {
                UpdateActive(_pane?.CurrentPath);
            }
        }
    }
}
