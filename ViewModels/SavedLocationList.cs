// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.ViewModels
{
    /// <summary>One saved network location in the sidebar.</summary>
    public sealed class SavedLocation
    {
        public SavedLocation(string path, string name, bool isAvailable)
        {
            Path = path;
            Name = name;
            IsAvailable = isAvailable;
        }

        /// <summary>The folder path or network address, as saved.</summary>
        public string Path { get; }

        public string Name { get; }

        /// <summary>Always true: checking a network address would block on the network (kept for the row's binding).</summary>
        public bool IsAvailable { get; }
    }

    /// <summary>
    /// A saved list of network locations kept in the settings file: the sidebar shows
    /// <see cref="Items"/>; Add, Remove and Toggle change it and save. Shared by both panes and both layouts.
    /// </summary>
    public sealed class SavedLocationList : ViewModelBase
    {
        private readonly ISettingsService _settings;
        private readonly Func<NavigationSettings, List<string>> _read;
        private readonly Action<NavigationSettings, List<string>> _write;

        public SavedLocationList(ISettingsService settings, Func<NavigationSettings, List<string>> read, Action<NavigationSettings, List<string>> write)
        {
            _settings = settings;
            _read = read;
            _write = write;
        }

        public ObservableCollection<SavedLocation> Items { get; } = new ObservableCollection<SavedLocation>();

        public bool HasItems => Items.Count > 0;

        /// <summary>Rebuilds the list from the settings (after they load, and when drives come and go).</summary>
        public void Reload()
        {
            List<string> saved = Saved();
            Items.Clear();
            foreach (string path in saved)
            {
                Items.Add(Create(path));
            }

            RaisePropertyChanged(nameof(HasItems));
        }

        /// <summary>Rebuilds the list only when the settings hold other places than it shows (another window changed them).</summary>
        public void ReloadIfChanged()
        {
            if (!Saved().SequenceEqual(Items.Select(i => i.Path), StringComparer.Ordinal))
            {
                Reload();
            }
        }

        public bool Contains(string path) => !string.IsNullOrEmpty(path) && IndexOf(Saved(), path) >= 0;

        public Task AddAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Contains(path))
            {
                return Task.CompletedTask;
            }

            List<string> saved = Saved();
            saved.Add(path.Trim());
            return CommitAsync(saved);
        }

        public Task RemoveAsync(string path)
        {
            List<string> saved = Saved();
            int index = IndexOf(saved, path);
            if (index < 0)
            {
                return Task.CompletedTask;
            }

            saved.RemoveAt(index);
            return CommitAsync(saved);
        }

        public Task ToggleAsync(string path) => Contains(path) ? RemoveAsync(path) : AddAsync(path);

        private List<string> Saved()
        {
            NavigationSettings navigation = _settings?.Current?.Navigation;
            return navigation == null ? new List<string>() : new List<string>(_read(navigation) ?? new List<string>());
        }

        /// <summary>Addresses compare case-insensitively (server and share names are).</summary>
        private int IndexOf(List<string> saved, string path)
        {
            for (int i = 0; i < saved.Count; i++)
            {
                if (string.Equals(saved[i], path?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task CommitAsync(List<string> saved)
        {
            NavigationSettings navigation = _settings?.Current?.Navigation;
            if (navigation == null)
            {
                return;
            }

            _write(navigation, saved);
            Reload();
            try
            {
                await _settings.SaveAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not save the sidebar places");
            }
        }

        private static SavedLocation Create(string path) =>
            new SavedLocation(path, Core.Platform.NetworkLocations.DisplayName(path), true);
    }
}
