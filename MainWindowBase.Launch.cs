// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Launch;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Tabs;
using Serilog;

namespace ByteExplorer
{
    /// <summary>
    /// Folders the system asks ByteExplorer to open (the command line at start, or a later start handing its request
    /// over through <see cref="SingleInstance"/>): the default file manager's half inside the window.
    /// </summary>
    public abstract partial class MainWindowBase
    {
        private readonly TaskCompletionSource _initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the window has opened its first place (requests wait for it).</summary>
        internal Task FirstPlaceShown => _initialized.Task;

        /// <summary>
        /// The first place at start when the command line names a folder: that folder instead of the startup place, as
        /// Explorer opens the folder it was given. When the start would bring tabs back (the last session, pinned tabs),
        /// they come back and the folder opens in a new tab after them.
        /// </summary>
        private async Task OpenStartupRequestAsync(LaunchRequest request)
        {
            FolderOptionsSettings options = FolderOptions.Current;
            var saved = ViewModel.SettingsService?.Current?.Tabs?.SessionTabs;
            bool restoresTabs = saved != null && saved.Count > 0 &&
                                (options.OpenAt == FolderOptionsSettings.OpenAtLastSession || saved.Any(t => t.Pinned));
            if (restoresTabs)
            {
                await ViewModel.OpenStartupLocationAsync();
                await OpenRequestedFolderAsync(request, newTab: true);
            }
            else
            {
                await OpenRequestedFolderAsync(request, newTab: false);
            }
        }

        /// <summary>A request from a later start: the window comes to the front and the folder opens in a new tab.</summary>
        internal async void HandleLaunchRequest(LaunchRequest request)
        {
            try
            {
                await FirstPlaceShown;
                BringToFront();
                if (request.HasFolder)
                {
                    await OpenRequestedFolderAsync(request, newTab: true);
                }
                else if (request.FromShell)
                {
                    // Win+E: a new tab at the startup place, as Explorer opens a new window there.
                    FolderOptionsSettings options = FolderOptions.Current;
                    string place = options.OpenAt switch
                    {
                        FolderOptionsSettings.OpenAtThisPc => TabLocation.ThisPc,
                        FolderOptionsSettings.OpenAtFolder when TabLocation.IsValid(options.StartupFolder) => options.StartupFolder,
                        _ => TabLocation.Home
                    };
                    await ViewModel.OpenInNewTabAsync(place, select: true);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not open {Folder} from another start", request?.Folder);
            }
        }

        private async Task OpenRequestedFolderAsync(LaunchRequest request, bool newTab)
        {
            string folder = request.Folder;
            bool exists = await Task.Run(() => System.IO.Directory.Exists(folder));
            if (!exists)
            {
                if (!newTab && !ActivePane.HasStartedNavigation)
                {
                    await ViewModel.OpenStartupLocationAsync(); // something to show behind the message
                }

                await ShowMessageAsync("Folder not found", "ByteExplorer was asked to open a folder that does not exist (or can't be reached):\n" + folder);
                return;
            }

            ViewModel.ClearSearch();
            if (newTab)
            {
                TabViewModel tab = await ViewModel.Tabs.OpenInNewTabAsync(folder, select: true); // returns once loaded
                if (tab != null && !string.IsNullOrEmpty(request.Select))
                {
                    SelectItem(tab.Primary, request.Select);
                }
            }
            else
            {
                await RevealAsync(ActivePane, folder, request.Select);
            }
        }

        private static void SelectItem(FileListViewModel pane, string path)
        {
            foreach (object entry in pane.Items ?? Array.Empty<object>())
            {
                if (entry is FileListItemViewModel item && Core.PathUtil.AreSame(item.FullPath, path))
                {
                    pane.SelectedItem = item;
                    return;
                }
            }
        }

        /// <summary>
        /// Shows the window in front: restored if minimized, activated, and briefly topmost (on Windows the start that
        /// handed the request over has already allowed this process to take the foreground).
        /// </summary>
        private void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();
            Topmost = true;
            Topmost = false;
        }
    }
}
