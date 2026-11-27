// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.Sidebar;
using Serilog;

namespace ByteExplorer.Views.Sidebar
{
    /// <summary>What Quick access needs from its window: opening, dropping into folders, the trash, and dialogs.</summary>
    public interface IPinnedAreaHost
    {
        /// <summary>Opens a pinned place in the active pane, the other pane (turning split view on if needed) or a new tab.</summary>
        Task OpenPinAsync(PinnedItemViewModel item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget target);

        /// <summary>Copies or moves dropped files into a pinned folder (through the operations queue).</summary>
        Task DropIntoAsync(IReadOnlyList<string> paths, string folder, bool move);

        /// <summary>Moves dropped files to the trash (after the usual confirmation).</summary>
        Task TrashAsync(IReadOnlyList<string> paths);

        IDialogService Dialogs { get; }

        /// <summary>The pane the window acts on (shortcuts made by a drop are recorded in its undo history).</summary>
        ByteExplorer.ViewModels.FileListViewModel ActiveFileList { get; }
    }

    /// <summary>
    /// Quick access in the sidebar: the rows, and everything done with them by pointer, drag and keyboard.
    /// <para>
    /// <b>Drops</b>: a dragged pin lands at the slot under the pointer (above or below the row's middle), shown by an
    /// accent line. Files dragged from elsewhere onto the middle of a row go into that folder (copy or move, the usual
    /// modifiers) or, on the trash, to the trash; folders dropped near a row's edge, or below the rows, are pinned there,
    /// as dragging a folder into Explorer's Quick access pins it.
    /// </para>
    /// <para>
    /// Pins drag only within Quick access: they carry a private format, never files, so a slip of the mouse can't copy a
    /// whole folder somewhere.
    /// </para>
    /// </summary>
    public partial class PinnedAreaView : UserControl, IFileKeyScope
    {
        public static readonly StyledProperty<string> VariantProperty =
            AvaloniaProperty.Register<PinnedAreaView, string>(nameof(Variant), "Modern");

        /// <summary>The marker of a pin being dragged (its value is the pin's path; the item itself is kept here).</summary>
        private static readonly DataFormat<string> PinFormat = DataFormat.CreateStringApplicationFormat("x-byteexplorer-pin");

        private PinnedItemViewModel _pressed;
        private Point _pressPoint;
        private PinnedItemViewModel _dragging;
        private Border _dropRow;

        public PinnedAreaView()
        {
            InitializeComponent();
            ApplyVariant();

            PinList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
            PinList.AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
            PinList.AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
            PinList.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropVisuals());
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        /// <summary>"Modern" (colored icons) or "Nemo" (gray icons, rounded rows).</summary>
        public string Variant
        {
            get => GetValue(VariantProperty);
            set => SetValue(VariantProperty, value);
        }

        private PinnedFoldersViewModel Pins => DataContext as PinnedFoldersViewModel;

        private IPinnedAreaHost Host => this.FindAncestorOfType<IPinnedAreaHost>() ?? TopLevel.GetTopLevel(this) as IPinnedAreaHost;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == VariantProperty)
            {
                ApplyVariant();
            }
        }

        private void ApplyVariant()
        {
            string key = string.Equals(Variant, "Nemo", StringComparison.OrdinalIgnoreCase) ? "NemoPinTemplate" : "ModernPinTemplate";
            if (Resources.TryGetResource(key, null, out object template) && template is Avalonia.Controls.Templates.IDataTemplate dataTemplate)
            {
                PinList.ItemTemplate = dataTemplate;
            }
        }

        // ================================================================== pointer

        private static (Border Row, PinnedItemViewModel Item) RowFrom(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Button)
                {
                    return (null, null); // the unpin button handles its own click
                }

                if (visual is Border border && border.Classes.Contains("pinRow"))
                {
                    return (border, border.Tag as PinnedItemViewModel);
                }
            }

            return (null, null);
        }

        private void OnRowPressed(object sender, PointerPressedEventArgs e)
        {
            (Border row, PinnedItemViewModel item) = RowFrom(e.Source);
            if (item == null)
            {
                return;
            }

            _pressed = item;
            _pressPoint = e.GetPosition(this);
            row.Focus(NavigationMethod.Pointer);
        }

        private async void OnRowMoved(object sender, PointerEventArgs e)
        {
            try
            {
                if (_pressed == null || _dragging != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                    !FileDragDropHelper.HasMovedPastDragThreshold(_pressPoint, e.GetPosition(this)))
                {
                    return;
                }

                _dragging = _pressed;
                _pressed = null;
                Border source = RowOf(_dragging);
                source?.Classes.Add("dragging");
                try
                {
                    var data = new DataTransfer();
                    var entry = new DataTransferItem();
                    entry.Set(PinFormat, _dragging.Path);
                    data.Add(entry);
                    await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
                }
                finally
                {
                    source?.Classes.Remove("dragging");
                    _dragging = null;
                    ClearDropVisuals();
                }
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Dragging a pin failed");
            }
        }

        private void OnRowReleased(object sender, PointerReleasedEventArgs e)
        {
            (Border row, PinnedItemViewModel item) = RowFrom(e.Source);
            PinnedItemViewModel pressed = _pressed;
            _pressed = null;
            if (item == null)
            {
                return;
            }

            e.Handled = true;
            switch (e.InitialPressMouseButton)
            {
                case MouseButton.Left when ReferenceEquals(item, pressed):
                    Open(item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget.ActivePane);
                    break;
                case MouseButton.Middle:
                    Open(item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget.MiddleClick);
                    break;
                case MouseButton.Right:
                    ShowMenu(row, item);
                    break;
            }
        }

        private void OnUnpinClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.Tag is PinnedItemViewModel item)
            {
                Unpin(item);
            }
        }

        private void Open(PinnedItemViewModel item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget target)
        {
            IPinnedAreaHost host = Host;
            if (host != null)
            {
                host.OpenPinAsync(item, target).Forget("open pin");
            }
        }

        private void Unpin(PinnedItemViewModel item)
        {
            if (Pins == null)
            {
                return;
            }

            int index = Pins.Items.IndexOf(item);
            Pins.UnpinAsync(item).Forget("unpin");

            // Keep the keyboard in the list: the next row (or the last one) takes focus.
            if (index >= 0 && Pins.Items.Count > 0)
            {
                FocusRow(Math.Min(index, Pins.Items.Count - 1));
            }
        }

        // ================================================================== keyboard

        private void OnRowKeyDown(object sender, KeyEventArgs e)
        {
            (Border row, PinnedItemViewModel item) = RowFrom(e.Source);
            if (item == null || Pins == null)
            {
                return;
            }

            int index = Pins.Items.IndexOf(item);
            bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            switch (e.Key)
            {
                case Key.Up when alt:
                case Key.Down when alt:
                    int delta = e.Key == Key.Up ? -1 : 1;
                    Pins.MoveByAsync(item, delta).Forget("move pin");
                    FocusRow(Math.Clamp(index + delta, 0, Pins.Items.Count - 1));
                    break;
                case Key.Up:
                    FocusRow(Math.Max(0, index - 1));
                    break;
                case Key.Down:
                    FocusRow(Math.Min(Pins.Items.Count - 1, index + 1));
                    break;
                case Key.Home:
                    FocusRow(0);
                    break;
                case Key.End:
                    FocusRow(Pins.Items.Count - 1);
                    break;
                case Key.Enter when alt:
                    return;
                case Key.Enter:
                    Open(item, (e.KeyModifiers & KeyModifiers.Control) != 0
                        ? ByteExplorer.ViewModels.Tabs.FolderOpenTarget.OtherPane
                        : ByteExplorer.ViewModels.Tabs.FolderOpenTarget.ActivePane);
                    break;
                case Key.Delete:
                    Unpin(item);
                    break;
                case Key.F2:
                    RenameAsync(item).Forget("rename pin");
                    break;
                case Key.Apps:
                case Key.F10 when (e.KeyModifiers & KeyModifiers.Shift) != 0:
                    ShowMenu(row, item);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void FocusRow(int index)
        {
            if (index < 0)
            {
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Control container = PinList.ContainerFromIndex(index);
                Border row = container as Border ?? container?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("pinRow"));
                row?.Focus(NavigationMethod.Directional);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }

        private Border RowOf(PinnedItemViewModel item)
        {
            int index = Pins?.Items.IndexOf(item) ?? -1;
            Control container = index >= 0 ? PinList.ContainerFromIndex(index) : null;
            return container as Border ?? container?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("pinRow"));
        }

        // ================================================================== menu

        private void ShowMenu(Control anchor, PinnedItemViewModel item)
        {
            if (Pins == null || anchor == null)
            {
                return;
            }

            int index = Pins.Items.IndexOf(item);
            string folder = item.FolderPath;
            bool isFolder = folder != null && item.IsAvailable && !NetworkLocations.IsNetworkAddress(folder);
            var items = new List<Control>();

            var open = Item("Open", Symbol.Open, () => Open(item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget.ActivePane), "Enter");
            open.FontWeight = FontWeight.SemiBold;
            items.Add(open);
            bool middleOpensTab = !Infrastructure.TabOptions.MiddleClickOpensOtherPane;
            items.Add(Item("Open in new tab", Symbol.TabAdd, () => Open(item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget.NewTab), middleOpensTab ? "Middle-click" : null));
            items.Add(Item("Open in the other pane", Symbol.SplitVertical, () => Open(item, ByteExplorer.ViewModels.Tabs.FolderOpenTarget.OtherPane), middleOpensTab ? null : "Middle-click"));
            if (isFolder)
            {
                items.Add(Item("Open in terminal", Symbol.WindowConsole, () => Run(() => ShellLauncher.OpenTerminal(folder))));
                items.Add(Item(OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in Windows Explorer" : "Show in system file manager",
                    Symbol.FolderOpen, () => Run(() => ShellLauncher.RevealInSystemFileManager(folder))));
            }

            if (!item.IsTrash)
            {
                items.Add(Item("Copy path", Symbol.Link, () => ClipboardFileService.SetText(item.Path)));
            }

            items.Add(new Separator());
            items.Add(Item("Rename…", Symbol.Rename, () => RenameAsync(item).Forget("rename pin"), "F2"));
            items.Add(ColorMenu(item));
            var up = Item("Move up", Symbol.ArrowUp, () => Pins.MoveByAsync(item, -1).Forget("move pin"), "Alt+Up");
            up.IsEnabled = index > 0;
            items.Add(up);
            var down = Item("Move down", Symbol.ArrowUp, () => Pins.MoveByAsync(item, 1).Forget("move pin"), "Alt+Down");
            down.Icon = new SymbolIcon { Symbol = Symbol.ArrowUp, FontSize = 16, RenderTransform = new RotateTransform(180) };
            down.IsEnabled = index >= 0 && index < Pins.Items.Count - 1;
            items.Add(down);
            items.Add(new Separator());
            items.Add(Item("Unpin from Quick access", Symbol.Pin, () => Unpin(item), "Delete"));

            if (isFolder)
            {
                items.Add(new Separator());
                items.Add(Item("Properties", Symbol.Info, () => Host?.Dialogs?.ShowProperties(new[] { folder })));
            }

            var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
            menu.Open(anchor);
        }

        private MenuItem ColorMenu(PinnedItemViewModel item)
        {
            var colors = new List<Control>();
            var standard = new MenuItem { Header = "Usual color", ToggleType = MenuItemToggleType.Radio, IsChecked = item.ColorName == null, GroupName = "pincolor" };
            standard.Click += (_, _) => Pins.SetColorAsync(item, null).Forget("pin color");
            colors.Add(standard);
            colors.Add(new Separator());
            foreach ((string name, string hex) in PinColors.All)
            {
                var entry = new MenuItem
                {
                    Header = name,
                    Icon = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse(hex)) },
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "pincolor",
                    IsChecked = string.Equals(item.ColorName, name, StringComparison.Ordinal)
                };
                string chosen = name;
                entry.Click += (_, _) => Pins.SetColorAsync(item, chosen).Forget("pin color");
                colors.Add(entry);
            }

            return new MenuItem { Header = "Color", Icon = new SymbolIcon { Symbol = Symbol.Apps, FontSize = 16 }, ItemsSource = colors };
        }

        private async Task RenameAsync(PinnedItemViewModel item)
        {
            IDialogService dialogs = Host?.Dialogs;
            if (dialogs == null || Pins == null)
            {
                return;
            }

            string name = await dialogs.PromptTextAsync("Rename pin",
                $"The name shown in Quick access. The folder itself keeps its name ({item.FolderName}); leave empty to use it.",
                item.Name, item.FolderName, "Rename", text => text.Length > 100 ? "Use at most 100 characters." : null);
            if (name != null)
            {
                await Pins.RenameAsync(item, name);
            }
        }

        private static MenuItem Item(string header, Symbol symbol, Action action, string gesture = null)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            if (!string.IsNullOrEmpty(gesture))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gesture); // display only, as in the other menus
                }
                catch (ArgumentException)
                {
                    ToolTip.SetTip(item, gesture); // "Middle-click" isn't a key gesture: shown as a tip instead
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
                    Log.Logger.Error(ex, "Quick access menu action failed");
                }
            };
            return item;
        }

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Logger.Warning(ex, "Quick access action failed");
                Host?.Dialogs?.ShowMessageAsync("That didn't work", Infrastructure.FriendlyErrors.Explain(ex)).Forget("message");
            }
        }

        // ================================================================== drag and drop

        /// <summary>Where a drop at this point lands: the row under it, and the slot (insertion index) nearest to it.</summary>
        private (Border Row, PinnedItemViewModel Item, int Slot, double LineY, bool Middle) HitTest(DragEventArgs e)
        {
            PinnedFoldersViewModel pins = Pins;
            int count = pins?.Items.Count ?? 0;
            Point point = e.GetPosition(PinList);
            for (int i = 0; i < count; i++)
            {
                Control container = PinList.ContainerFromIndex(i);
                if (container == null)
                {
                    continue;
                }

                Rect bounds = container.Bounds;
                Point origin = container.TranslatePoint(new Point(0, 0), PinList) ?? bounds.Position;
                double top = origin.Y;
                double bottom = top + bounds.Height;
                if (point.Y < top || point.Y >= bottom)
                {
                    continue;
                }

                double offset = point.Y - top;
                bool upper = offset < bounds.Height / 2;
                bool middle = offset >= bounds.Height * 0.25 && offset <= bounds.Height * 0.75;
                Border row = container as Border ?? container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("pinRow"));
                return (row, pins.Items[i], upper ? i : i + 1, upper ? top : bottom, middle);
            }

            // Below (or above) the rows: the end (or the start).
            double end = count > 0 ? (PinList.ContainerFromIndex(count - 1)?.TranslatePoint(new Point(0, PinList.ContainerFromIndex(count - 1).Bounds.Height), PinList)?.Y ?? 0) : 0;
            bool above = count > 0 && point.Y < 0;
            return (null, null, above ? 0 : count, above ? 0 : end, false);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            (Border row, PinnedItemViewModel item, int slot, double lineY, bool middle) = HitTest(e);

            if (_dragging != null && e.DataTransfer.Contains(PinFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                ShowInsertLine(lineY);
                SetDropRow(null);
                return;
            }

            if (!FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropVisuals();
                return;
            }

            if (row != null && middle && item != null && item.IsAvailable)
            {
                if (item.IsTrash)
                {
                    e.DragEffects = DragDropEffects.Move;
                    HideInsertLine();
                    SetDropRow(row);
                    return;
                }

                string folder = item.FolderPath;
                if (folder != null && !folder.Contains("://", StringComparison.Ordinal)) // an smb:// address isn't a folder until connected
                {
                    e.DragEffects = FileDragDropHelper.ResolveEffect(e, folder, paths);
                    HideInsertLine();
                    SetDropRow(e.DragEffects != DragDropEffects.None ? row : null);
                    return;
                }
            }

            if (paths.Any(PinnedFoldersViewModel.CanPin) && paths.Any(Directory.Exists))
            {
                e.DragEffects = DragDropEffects.Link; // pin, as Explorer shows "Pin to Quick access"
                ShowInsertLine(lineY);
                SetDropRow(null);
                return;
            }

            e.DragEffects = DragDropEffects.None;
            ClearDropVisuals();
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            try
            {
                e.Handled = true;
                (Border row, PinnedItemViewModel item, int slot, double _, bool middle) = HitTest(e);
                PinnedItemViewModel dragged = _dragging;
                ClearDropVisuals();
                if (Pins == null)
                {
                    return;
                }

                if (dragged != null && e.DataTransfer.Contains(PinFormat))
                {
                    await Pins.MoveToSlotAsync(dragged, slot);
                    return;
                }

                if (!FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
                {
                    return;
                }

                IPinnedAreaHost host = Host;
                if (row != null && middle && item != null && item.IsAvailable && host != null)
                {
                    if (item.IsTrash)
                    {
                        e.DragEffects = DragDropEffects.None; // ByteExplorer moves them to the trash; the source must not delete
                        await host.TrashAsync(paths);
                        return;
                    }

                    string folder = item.FolderPath;
                    await Menus.DropChoice.RunAsync(this, e, folder, paths, host.ActiveFileList, move => host.DropIntoAsync(paths, folder, move));
                    return;
                }

                await Pins.PinAsync(paths.Where(p => Directory.Exists(p) || Core.Services.ArchiveBrowser.IsArchiveFile(p)).ToList(), slot);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Dropping on Quick access failed");
            }
        }

        private void ShowInsertLine(double y)
        {
            Point offset = PinList.TranslatePoint(new Point(0, y), this) ?? new Point(0, y);
            Thickness margin = InsertLine.Margin;
            InsertLine.Margin = new Thickness(margin.Left, Math.Max(0, offset.Y - 1), margin.Right, 0);
            InsertLine.IsVisible = true;
        }

        private void HideInsertLine() => InsertLine.IsVisible = false;

        private void SetDropRow(Border row)
        {
            if (ReferenceEquals(_dropRow, row))
            {
                return;
            }

            _dropRow?.Classes.Remove("dropTarget");
            _dropRow = row;
            _dropRow?.Classes.Add("dropTarget");
        }

        private void ClearDropVisuals()
        {
            HideInsertLine();
            SetDropRow(null);
        }
    }
}
