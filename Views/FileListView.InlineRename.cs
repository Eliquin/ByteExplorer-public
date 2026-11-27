// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.Controls;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// In-place rename (F2, the Rename button and menu): a text box over the item's name, as in Explorer, Finder and
    /// GNOME Files. Enter or a click elsewhere commits, Esc cancels, scrolling or leaving the folder commits; the name
    /// before the extension is selected. The view model validates and performs the rename (RenameItemAsync); views
    /// without in-place editing (Timeline, Columns, Nexus) and items that cannot be shown use the rename dialog.
    /// </summary>
    public partial class FileListView
    {
        private TextBox _renameBox;
        private TaskCompletionSource<string> _renameResult;
        private ListBox _renameList;

        private Task<string> RenameInlineAsync(FileListItemViewModel item)
        {
            CompleteInlineRename(commit: true); // one edit at a time

            ListBox list = _activeFlatList;
            if (list == null || !(list.ItemsSource is VirtualizingFileList items) || !list.IsEffectivelyVisible)
            {
                return Task.FromResult(FileListViewModel.InlineRenameUnavailable);
            }

            int index = items.IndexOf(item);
            if (index < 0)
            {
                return Task.FromResult(FileListViewModel.InlineRenameUnavailable);
            }

            list.ScrollIntoView(index);
            list.UpdateLayout();
            FileItemView view = (list.ContainerFromIndex(index) as Control)?.GetVisualDescendants().OfType<FileItemView>().FirstOrDefault();
            if (view == null || view.Bounds.Width <= 0)
            {
                return Task.FromResult(FileListViewModel.InlineRenameUnavailable);
            }

            Rect name = view.NameBounds();
            Point? origin = view.TranslatePoint(name.Position, Root);
            if (origin == null)
            {
                return Task.FromResult(FileListViewModel.InlineRenameUnavailable);
            }

            bool centered = view.Kind == FileItemKind.Icon;
            double width = Math.Max(name.Width, centered ? 120 : 160);
            double left = centered ? origin.Value.X + (name.Width - width) / 2 : origin.Value.X;
            left = Math.Max(0, Math.Min(left, Root.Bounds.Width - width));

            // Folder options' hidden extensions: the shown name is edited, and the extension is kept (as Explorer does).
            string hiddenExtension = FolderOptions.HiddenExtensionOf(item.Name, item.IsDirectory);
            var box = new TextBox
            {
                Text = hiddenExtension.Length > 0 ? item.DisplayName : item.Name,
                FontSize = view.Kind == FileItemKind.Icon ? 11 : (view.FontSize > 0 ? view.FontSize : 13),
                Width = width,
                MinHeight = 0,
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
                Margin = new Thickness(left, origin.Value.Y + (centered ? 0 : Math.Max(0, (name.Height - 24) / 2)), 0, 0),
                AcceptsReturn = false,
                ZIndex = 100
            };
            box[!TextBox.BackgroundProperty] = box.GetResourceObservable("ContentBrush").ToBinding();
            box[!TextBox.BorderBrushProperty] = box.GetResourceObservable("SelectedAccent").ToBinding();
            box[!TextBox.ForegroundProperty] = box.GetResourceObservable("TextPrimary").ToBinding();

            _renameBox = box;
            _renameHiddenExtension = hiddenExtension;
            _renameList = list;
            _renameResult = new TaskCompletionSource<string>();
            // Tunnel, handled events too: the window's key bindings (Escape cancels file operations) and the TextBox
            // itself would otherwise consume Escape and Enter before a bubbling handler saw them.
            box.AddHandler(KeyDownEvent, OnRenameBoxKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            box.LostFocus += OnRenameBoxLostFocus;
            Root.Children.Add(box);

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_renameBox, box))
                {
                    return;
                }

                box.Focus();
                if (hiddenExtension.Length > 0)
                {
                    box.SelectAll();
                    return;
                }

                string extension = item.IsDirectory ? string.Empty : Path.GetExtension(item.Name);
                box.SelectionStart = 0;
                box.SelectionEnd = !string.IsNullOrEmpty(extension) && item.Name.Length > extension.Length ? item.Name.Length - extension.Length : item.Name.Length;
            }, DispatcherPriority.Input);

            return _renameResult.Task;
        }

        /// <summary>The extension the edit keeps (Folder options hide it), or "".</summary>
        private string _renameHiddenExtension = string.Empty;

        private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CompleteInlineRename(commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CompleteInlineRename(commit: false);
            }
        }

        private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e) => CompleteInlineRename(commit: true);

        /// <summary>Ends the edit in progress, if any: commits the typed name or cancels, removes the box, refocuses the list.</summary>
        private void CompleteInlineRename(bool commit)
        {
            TextBox box = _renameBox;
            TaskCompletionSource<string> result = _renameResult;
            ListBox list = _renameList;
            if (box == null || result == null)
            {
                return;
            }

            _renameBox = null;
            _renameResult = null;
            _renameList = null;
            box.RemoveHandler(KeyDownEvent, OnRenameBoxKeyDown);
            box.LostFocus -= OnRenameBoxLostFocus;
            Root.Children.Remove(box);
            string typed = box.Text;
            if (commit && !string.IsNullOrWhiteSpace(typed) && _renameHiddenExtension.Length > 0)
            {
                typed = typed.TrimEnd() + _renameHiddenExtension;
            }

            result.TrySetResult(commit ? typed : null);

            if (list != null && list.IsEffectivelyVisible)
            {
                int anchor = list.Selection.AnchorIndex;
                (anchor >= 0 ? list.ContainerFromIndex(anchor) : null)?.Focus(NavigationMethod.Directional);
            }
        }
    }
}
