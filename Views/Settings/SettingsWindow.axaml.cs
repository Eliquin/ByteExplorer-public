// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ByteExplorer.ViewModels.Settings;
using ByteExplorer.Views.Dialogs;
using Serilog;

namespace ByteExplorer.Views.Settings
{
    /// <summary>
    /// The settings window. It hands the view model its pickers, clipboard and dialogs (owned by this window, so they
    /// open over it), re-reads every value when it is activated (the main window may have changed some meanwhile), and
    /// keeps the keys Windows 11 Settings has: Ctrl+F or Ctrl+E to search, Escape to leave the search or close.
    /// </summary>
    public partial class SettingsWindow : Window, ISettingsWindowServices
    {
        private readonly SettingsViewModel _viewModel;

        /// <summary>For the XAML loader and previewer only; the app always passes a view model.</summary>
        public SettingsWindow()
        {
            InitializeComponent();
        }

        public SettingsWindow(SettingsViewModel viewModel)
            : this()
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;
            viewModel.AttachWindow(this);
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.VisibleGroups))
                {
                    ContentScroller.Offset = default; // a new page starts at its top
                }
            };
            Activated += (_, _) => viewModel.RefreshAll();
            Closed += (_, _) => viewModel.Dispose();
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        }

        public SettingsViewModel ViewModel => _viewModel;

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if ((e.Key == Key.F || e.Key == Key.E) && e.KeyModifiers == command)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                if (_viewModel?.IsSearching == true)
                {
                    _viewModel.SearchText = string.Empty;
                }
                else
                {
                    Close();
                }

                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ ISettingsWindowServices

        public async Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
            await new MessageDialog(title, message, confirmText, "Cancel", destructive: true).ShowDialog<bool>(this);

        public Task ShowMessageAsync(string title, string message) =>
            new MessageDialog(title, message, "OK", null, destructive: false).ShowDialog<bool>(this);

        public async Task<string> PickFolderAsync(string title)
        {
            try
            {
                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                });
                return folders.FirstOrDefault()?.TryGetLocalPath();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The folder picker failed");
                return null;
            }
        }

        public async Task<string> PickOpenFileAsync(string title)
        {
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = new[] { JsonFiles, FilePickerFileTypes.All }
                });
                return files.FirstOrDefault()?.TryGetLocalPath();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The file picker failed");
                return null;
            }
        }

        public async Task<string> PickSaveFileAsync(string title, string suggestedName)
        {
            try
            {
                IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = suggestedName,
                    DefaultExtension = "json",
                    FileTypeChoices = new[] { JsonFiles },
                    ShowOverwritePrompt = true
                });
                return file?.TryGetLocalPath();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The save picker failed");
                return null;
            }
        }

        public async Task CopyTextAsync(string text)
        {
            try
            {
                if (Clipboard != null)
                {
                    await Clipboard.SetTextAsync(text);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not copy to the clipboard");
            }
        }

        private static readonly FilePickerFileType JsonFiles = new FilePickerFileType("Settings (JSON)")
        {
            Patterns = new[] { "*.json" },
            MimeTypes = new[] { "application/json" }
        };
    }
}
