// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Menus;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// The toolbar's New button (DataContext: the window's MainViewModel), Explorer's "New" menu: Folder, Shortcut, then
    /// Text Document and every kind of file the system's file manager offers (<see cref="NewItemTemplates"/>: Word, Excel,
    /// PowerPoint… when Office is installed; the Templates folder on Linux). Each new item goes into the active pane's
    /// folder and can be undone.
    /// </summary>
    public sealed class NewItemSelector : UserControl
    {
        /// <summary>How long a click waits for the system's file kinds while they are still being read (just after start).</summary>
        private static readonly TimeSpan KindsWait = TimeSpan.FromMilliseconds(1500);

        private readonly Button _button;
        private MenuFlyout _open;
        private bool _opening;

        public NewItemSelector()
        {
            _button = new Button
            {
                Classes = { "cmd" },
                Padding = new Thickness(10, 6),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new SymbolIcon { Symbol = Symbol.AddCircle, FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "New", Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center },
                        new SymbolIcon { Symbol = Symbol.ChevronDown, FontSize = 10, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            _button.Click += (_, _) => ShowMenuAsync().Forget("open the New menu");
            ToolTip.SetTip(_button, "Create a new item in this folder");
            Content = _button;

            // The file kinds are read in the background now, so the menu has them the first time it opens.
            NewItemTemplates.GetAsync().Forget("read the New menu's file kinds");
        }

        private FileListViewModel Pane => (DataContext as MainViewModel)?.FileListViewModel;

        /// <summary>The menu open now (tests look at its rows); null when closed.</summary>
        internal MenuFlyout OpenMenu => _open?.IsOpen == true ? _open : null;

        /// <summary>
        /// Opens the menu, built whole before it is shown: a new flyout with its rows in it at each click (the folder, and
        /// what can be made in it, may have changed). Rows put into a MenuFlyout while it opens never reached its popup in
        /// this Avalonia version, and the menu opened empty. A click just after start waits a moment for the system's
        /// file kinds; past that, the menu shows without them.
        /// </summary>
        private async Task ShowMenuAsync()
        {
            if (_opening)
            {
                return;
            }

            if (_open?.IsOpen == true)
            {
                _open.Hide(); // a second click on the button closes the menu
                return;
            }

            _opening = true;
            try
            {
                Task<IReadOnlyList<NewItemTemplate>> kinds = NewItemTemplates.GetAsync();
                if (!kinds.IsCompleted)
                {
                    await Task.WhenAny(kinds, Task.Delay(KindsWait));
                }

                var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
                foreach (Control item in BuildItems(Pane, null))
                {
                    flyout.Items.Add(item);
                }

                flyout.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_open, flyout))
                    {
                        _open = null;
                    }
                };
                _open = flyout;
                flyout.ShowAt(_button);
            }
            finally
            {
                _opening = false;
            }
        }

        /// <summary>
        /// The New items for a pane: Folder, Shortcut, Text Document, then the system's file kinds (also the right-click
        /// menu's New submenu). While the kinds are still being read, a placeholder row stands in and
        /// <paramref name="kindsArrived"/> is called on the UI thread once they are.
        /// </summary>
        internal static List<Control> BuildItems(FileListViewModel pane, Action kindsArrived)
        {
            var items = new List<Control>();
            items.Add(Item("Folder", Symbol.FolderAdd, () => pane?.NewFolderCommand.Execute(null), pane?.NewFolderCommand.CanExecute(null) == true,
                (OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl") + "+Shift+N"));
            bool canShortcut = pane != null && pane.CanCreateFromTemplates;
            items.Add(Item(OperatingSystem.IsWindows() ? "Shortcut…" : "Link…", Symbol.Link, () => CreateShortcutAsync(pane).Forget("new shortcut"), canShortcut));
            items.Add(new Separator());
            items.Add(Item("Text Document", Symbol.DocumentAdd, () => pane?.NewTextFileCommand.Execute(null), pane?.NewTextFileCommand.CanExecute(null) == true));

            Task<IReadOnlyList<NewItemTemplate>> templates = NewItemTemplates.GetAsync();
            if (!templates.IsCompleted)
            {
                items.Add(new MenuItem { Header = "Looking for more kinds of files…", IsEnabled = false });
                if (kindsArrived != null)
                {
                    templates.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(kindsArrived), TaskScheduler.Default);
                }
            }
            else if (templates.IsCompletedSuccessfully)
            {
                bool canTemplate = pane != null && pane.CanCreateFromTemplates;
                foreach (NewItemTemplate template in templates.Result)
                {
                    NewItemTemplate chosen = template;
                    MenuItem item = Item(template.DisplayName, IconFor(template.Extension), () => pane?.CreateNewFromTemplateAsync(chosen).Forget("new file"), canTemplate);
                    if (!string.IsNullOrEmpty(template.Extension))
                    {
                        ToolTip.SetTip(item, template.BaseName + template.Extension);
                    }

                    items.Add(item);
                }
            }

            return items;
        }

        private static Symbol IconFor(string extension) => extension?.ToLowerInvariant() switch
        {
            ".zip" => Symbol.FolderZip,
            ".pdf" => Symbol.DocumentPdf,
            ".bmp" or ".png" or ".jpg" => Symbol.Image,
            ".txt" or ".rtf" or ".md" => Symbol.DocumentText,
            _ => Symbol.Document
        };

        private static MenuItem Item(string header, Symbol symbol, Action action, bool enabled, string gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 }, IsEnabled = enabled };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture.Replace("Cmd", "Meta", StringComparison.Ordinal)); // display only
                }
                catch (ArgumentException)
                {
                }
            }

            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "A New menu item failed");
                }
            };
            return item;
        }

        /// <summary>
        /// New › Shortcut: asks what it should open (a file or folder), as Explorer's "Create Shortcut" does, then makes it in
        /// the open folder ("name - Shortcut.lnk"; a link on Linux and macOS), undoable.
        /// </summary>
        private static async Task CreateShortcutAsync(FileListViewModel pane)
        {
            string folder = pane?.EffectiveTargetPath;
            if (pane?.DialogService == null || string.IsNullOrEmpty(folder))
            {
                return;
            }

            string noun = Shortcuts.Noun;
            string target = await pane.DialogService.PromptTextAsync(
                "Create " + noun,
                $"Type the location of the file or folder the {noun} should open.",
                string.Empty,
                OperatingSystem.IsWindows() ? @"C:\Users\Me\Documents\Report.docx" : "/home/me/Documents/report.odt",
                "Create",
                text =>
                {
                    string path = Unquote(text);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return "Type a location.";
                    }

                    try
                    {
                        if (!Path.IsPathRooted(path))
                        {
                            return "Type the whole location, starting with the drive" + (OperatingSystem.IsWindows() ? " (C:\\…)." : " (/…).");
                        }
                    }
                    catch (ArgumentException)
                    {
                        return "That isn't a valid location.";
                    }

                    return File.Exists(path) || Directory.Exists(path) ? null : "There's no file or folder at this location.";
                });
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            await DropChoice.CreateShortcutsAsync(new[] { Unquote(target) }, folder, pane);
        }

        /// <summary>A path pasted with quotes around it (Explorer's "Copy as path") works too.</summary>
        private static string Unquote(string text) => (text ?? string.Empty).Trim().Trim('"');
    }
}
