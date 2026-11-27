// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ByteExplorer.Core.Tags;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.Sidebar;
using ByteExplorer.Views.Tags;
using Serilog;

namespace ByteExplorer.Views.Sidebar
{
    /// <summary>
    /// The sidebar's Tags section: opening a tag (left click), its menu (right click), folding sub-tags, and files dropped
    /// on a tag (they get the tag; nothing is copied or moved).
    /// </summary>
    public partial class TagsSidebarView : UserControl
    {
        public static readonly StyledProperty<string> VariantProperty =
            AvaloniaProperty.Register<TagsSidebarView, string>(nameof(Variant), "Modern");

        private Border _dropRow;

        public TagsSidebarView()
        {
            InitializeComponent();
            TagList.AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropRow(null));
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        /// <summary>"Modern" or "Nemo" (the Nemo rows are rounded, as its other sidebar rows).</summary>
        public string Variant
        {
            get => GetValue(VariantProperty);
            set => SetValue(VariantProperty, value);
        }

        private TagsSidebarViewModel ViewModel => DataContext as TagsSidebarViewModel;

        private IDialogService Dialogs => TopLevel.GetTopLevel(this) as IDialogService;

        /// <summary>The rows take the Nemo sidebar's "nemo" row class (rounded, its own hover colors).</summary>
        public static readonly StyledProperty<bool> IsNemoProperty =
            AvaloniaProperty.Register<TagsSidebarView, bool>(nameof(IsNemo));

        public bool IsNemo
        {
            get => GetValue(IsNemoProperty);
            private set => SetValue(IsNemoProperty, value);
        }

        /// <summary>The rows' inner margin: the Modern rows indent like its other sidebar rows; the rounded rows pad themselves.</summary>
        public static readonly StyledProperty<Thickness> RowMarginProperty =
            AvaloniaProperty.Register<TagsSidebarView, Thickness>(nameof(RowMargin), new Thickness(17, 0, 10, 0));

        public Thickness RowMargin
        {
            get => GetValue(RowMarginProperty);
            private set => SetValue(RowMarginProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == VariantProperty)
            {
                IsNemo = string.Equals(Variant, "Nemo", StringComparison.OrdinalIgnoreCase);
                RowMargin = IsNemo ? new Thickness(-4, 0, 2, 0) : new Thickness(17, 0, 10, 0);
            }
        }

        private static (Border Row, TagRowViewModel Item) RowFrom(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Button)
                {
                    return (null, null); // the expander handles its own click
                }

                if (visual is Border border && border.Classes.Contains("tagRow"))
                {
                    return (border, border.Tag as TagRowViewModel);
                }
            }

            return (null, null);
        }

        private void OnRowReleased(object sender, PointerReleasedEventArgs e)
        {
            (Border row, TagRowViewModel item) = RowFrom(e.Source);
            if (item == null)
            {
                return;
            }

            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                ViewModel?.Open(item);
                e.Handled = true;
            }
            else if (e.InitialPressMouseButton == MouseButton.Right)
            {
                ShowMenu(row, item);
                e.Handled = true;
            }
        }

        private void OnExpanderClick(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.Tag is TagRowViewModel row)
            {
                ViewModel?.ToggleExpanded(row);
            }
        }

        private void ShowMenu(Control anchor, TagRowViewModel row)
        {
            TagsSidebarViewModel vm = ViewModel;
            TagStore store = vm?.Store;
            TagDefinition tag = store?.Find(row.Id);
            if (tag == null)
            {
                return;
            }

            var items = new List<Control>
            {
                Item("Show items", Symbol.Search, () => vm.Open(row)),
                new Separator(),
                Item("New sub-tag…", Symbol.Add, async () => await TagActions.PromptNewTagAsync(Dialogs, store, tag.Id)),
                Item("Rename…", Symbol.Rename, async () => await TagActions.RenameAsync(Dialogs, store, tag)),
                TagActions.ColorMenu(store, tag),
                Item("Edit in the tag manager…", Symbol.Settings, () =>
                {
                    TagEditorWindow editor = TagEditorWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, store, Array.Empty<string>());
                    editor.ViewModel.SelectById(tag.Id);
                }),
                new Separator(),
                DangerItem("Delete tag…", async () => await TagActions.DeleteAsync(Dialogs, store, tag))
            };
            new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer }.Open(anchor);
        }

        /// <summary>The section header's + button: a new top-level tag (a name with slashes makes sub-tags).</summary>
        public async void CreateTag()
        {
            try
            {
                if (ViewModel?.Store is TagStore store)
                {
                    await TagActions.PromptNewTagAsync(Dialogs, store);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Creating a tag failed");
            }
        }

        // ------------------------------------------------------------------ drops: tag the dropped items

        private void OnDragOver(object sender, DragEventArgs e)
        {
            (Border row, TagRowViewModel item) = RowFrom(e.Source);
            if (item != null && FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths) && paths.Count > 0)
            {
                e.DragEffects = DragDropEffects.Link;
                SetDropRow(row);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                SetDropRow(null);
            }

            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            SetDropRow(null);
            try
            {
                (_, TagRowViewModel item) = RowFrom(e.Source);
                if (item != null && FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths) && paths.Count > 0)
                {
                    ViewModel?.Store.Assign(paths, item.Id);
                    e.DragEffects = DragDropEffects.Link;
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tagging dropped items failed");
            }
        }

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

        // ------------------------------------------------------------------ menu helpers

        private static MenuItem Item(string header, Symbol symbol, Action action)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 } };
            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "A tag menu action failed");
                }
            };
            return item;
        }

        private static MenuItem Item(string header, Symbol symbol, Func<System.Threading.Tasks.Task> action) =>
            Item(header, symbol, () => action().Forget("tag menu: " + header));

        private static MenuItem DangerItem(string header, Func<System.Threading.Tasks.Task> action)
        {
            MenuItem item = Item(header, Symbol.Delete, action);
            item.Classes.Add("danger");
            return item;
        }
    }
}
