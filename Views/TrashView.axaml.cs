// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Code-behind of the trash surface (see the .axaml).
    /// <list type="bullet">
    /// <item>Right-click or the menu key on items: Restore, Delete permanently, Select all, Empty; on the background:
    /// Empty, Select all, Refresh. A right-click on an item outside the selection makes it the selection first.</item>
    /// <item>Keys act on the selected items, not on the hidden file list (see <see cref="IFileKeyScope"/>): Delete and
    /// Shift+Delete delete permanently (asked first); Ctrl+A selects all (through the pane's Select all). Enter and a
    /// double click do nothing: an item in the trash is restored, never opened or edited in place.</item>
    /// </list>
    /// </summary>
    public partial class TrashView : UserControl, IFileKeyScope
    {
        private FileListViewModel _vm;

        public TrashView()
        {
            InitializeComponent();
            TrashList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
            HintText.Text = OperatingSystem.IsMacOS()
                ? "Finder doesn't share where these items came from: drag an item out to put it back."
                : "Right-click items to restore them or delete them permanently.";
        }

        /// <summary>The selected rows, in list order.</summary>
        internal IReadOnlyList<TrashItemViewModel> SelectedItems =>
            TrashList.Selection.SelectedItems.OfType<TrashItemViewModel>().ToList();

        /// <summary>The list (tests).</summary>
        internal ListBox List => TrashList;

        public void SelectAll() => TrashList.SelectAll();

        /// <summary>Puts the keyboard in the list once its rows are laid out (the surface was just shown).</summary>
        public void FocusListSoon() => Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Loaded);

        private void FocusList()
        {
            if (!TrashList.IsEffectivelyVisible)
            {
                return;
            }

            TrashList.UpdateLayout();
            Control first = TrashList.ItemCount > 0 ? TrashList.ContainerFromIndex(Math.Max(0, TrashList.Selection.AnchorIndex)) : null;
            (first ?? (Control)TrashList).Focus(NavigationMethod.Directional);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _vm = DataContext as FileListViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }

            UpdateState();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(FileListViewModel.TrashItems):
                case nameof(FileListViewModel.IsLoading):
                case nameof(FileListViewModel.TrashSortColumn):
                case nameof(FileListViewModel.TrashSortAscending):
                case nameof(FileListViewModel.IsTrashView):
                case null:
                case "":
                    UpdateState();
                    break;
            }
        }

        /// <summary>The empty state and the sort arrow on the sorted column.</summary>
        private void UpdateState()
        {
            FileListViewModel vm = _vm;
            bool empty = vm != null && vm.IsTrashView && !vm.IsLoading && !vm.HasTrashItems;
            EmptyState.IsVisible = empty;
            EmptyText.Text = $"The {Core.Platform.Trash.DisplayName} is empty";

            string Mark(TrashSortColumn column, string text) =>
                vm != null && vm.TrashSortColumn == column ? text + (vm.TrashSortAscending ? "  ▲" : "  ▼") : text;
            NameHeaderText.Text = Mark(TrashSortColumn.Name, "Name");
            LocationHeaderText.Text = Mark(TrashSortColumn.OriginalLocation, "Original location");
            DeletedHeaderText.Text = Mark(TrashSortColumn.DateDeleted, "Date deleted");
            SizeHeaderText.Text = Mark(TrashSortColumn.Size, "Size");
            TypeHeaderText.Text = Mark(TrashSortColumn.Type, "Type");
        }

        private void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (_vm != null && sender is Control { Tag: string tag } && Enum.TryParse(tag, out TrashSortColumn column))
            {
                _vm.SortTrashBy(column);
            }
        }

        // ------------------------------------------------------------------ keys

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (_vm == null)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Delete when e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift:
                    IReadOnlyList<TrashItemViewModel> selected = SelectedItems;
                    if (selected.Count > 0)
                    {
                        Fire(_vm.DeleteFromTrashPermanentlyAsync(selected), "delete from the trash");
                    }

                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                    e.Handled = true; // nothing opens from the trash (see the class remarks)
                    return;
            }
        }

        // ------------------------------------------------------------------ menus

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (_vm == null)
            {
                return;
            }

            e.Handled = true;
            ListBoxItem container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (container != null && !container.IsSelected)
            {
                int index = TrashList.IndexFromContainer(container);
                if (index >= 0)
                {
                    TrashList.Selection.Clear();
                    TrashList.Selection.Select(index);
                }
            }

            IReadOnlyList<TrashItemViewModel> selected = container != null ? SelectedItems : Array.Empty<TrashItemViewModel>();
            ContextMenu menu = selected.Count > 0 ? ItemMenu(selected) : BackgroundMenu();
            Control anchor = container ?? (Control)this;
            menu.PlacementTarget = anchor;
            menu.Closed += (_, _) => menu.ItemsSource = null;
            menu.Open(anchor);
        }

        private ContextMenu ItemMenu(IReadOnlyList<TrashItemViewModel> selected)
        {
            FileListViewModel vm = _vm;
            var items = new List<Control>();
            MenuItem restore = Item("Restore", Symbol.ArrowUndo, () => Fire(vm.RestoreFromTrashAsync(selected), "restore from the trash"));
            restore.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            restore.IsEnabled = selected.Any(i => i.CanRestore);
            if (!restore.IsEnabled)
            {
                // A disabled item shows no tooltip of its own on every platform: the reason goes in the header too.
                restore.Header = "Restore (original location unknown)";
                ToolTip.SetTip(restore, "The trash on this system doesn't record where items came from. Drag them out instead.");
            }

            items.Add(restore);
            items.Add(Item("Delete permanently", Symbol.DeleteDismiss, () => Fire(vm.DeleteFromTrashPermanentlyAsync(selected), "delete from the trash"), "Delete"));
            items.Add(new Separator());
            items.Add(Item("Select all", Symbol.SelectAllOn, SelectAll, OperatingSystem.IsMacOS() ? "Cmd+A" : "Ctrl+A"));
            items.Add(new Separator());
            items.Add(EmptyItem(vm));
            return new ContextMenu { ItemsSource = items };
        }

        private ContextMenu BackgroundMenu()
        {
            FileListViewModel vm = _vm;
            var items = new List<Control>
            {
                EmptyItem(vm),
                new Separator(),
                Item("Select all", Symbol.SelectAllOn, SelectAll, OperatingSystem.IsMacOS() ? "Cmd+A" : "Ctrl+A"),
                Item("Refresh", Symbol.ArrowClockwise, () => Fire(vm.ReloadTrashAsync(), "reload the trash"), "F5")
            };
            return new ContextMenu { ItemsSource = items };
        }

        private static MenuItem EmptyItem(FileListViewModel vm)
        {
            MenuItem empty = Item(vm.EmptyTrashText, Symbol.Delete, () => Fire(vm.EmptyTrashAsync(), "empty the trash"));
            empty.IsEnabled = vm.HasTrashItems;
            return empty;
        }

        private static MenuItem Item(string header, Symbol symbol, Action action, string gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture);
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
                    Log.Logger.Error(ex, "Trash menu action {Header} failed", header);
                }
            };
            return item;
        }

        private static void Fire(Task task, string what) => task.Forget(what);
    }
}
