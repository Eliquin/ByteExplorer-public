// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;

namespace ByteExplorer.Controls
{
    /// <summary>
    /// The ListBox of every file list: multiple selection (click, Ctrl, Shift, as in Explorer), no automatic scrolling to
    /// the selected item, and three differences from Avalonia's ListBox, all for large folders:
    /// <list type="bullet">
    /// <item>
    /// Its selection is a plain <see cref="SelectionModel{T}"/> (index ranges). The ListBox's default model also keeps
    /// a SelectedItems list in step, item by item, with a linear removal per deselected item: selecting all of 100,000
    /// files and then inverting took seconds, and every change created every selected item.
    /// </item>
    /// <item>
    /// Changes are reported by <see cref="SelectionModelChangedEvent"/>, which carries no item lists. Avalonia's own
    /// SelectionChanged routed event copies the added and removed items into arrays whenever anything handles it, which
    /// creates every item of a large range; leaving it unhandled avoids that.
    /// </item>
    /// <item>
    /// A recycled container keeps its item template's controls. Avalonia's ItemsControl clears a recycled container's
    /// Content and ContentTemplate, so the next item it shows builds the whole template again (controls created,
    /// attached to the logical tree and styled from scratch); profiling a fast scroll put that rebuild at about three
    /// quarters of the layout time. Here the next item is set straight into Content, where the item template's
    /// recycling (IRecyclingDataTemplate) only swaps the data the bindings read. This applies when the list has an
    /// ItemTemplate (all file lists do); otherwise Avalonia's behaviour is used unchanged.
    /// </item>
    /// </list>
    /// The theme stays the ListBox theme (<see cref="StyleKeyOverride"/>).
    /// </summary>
    public class FileListBox : ListBox
    {
        /// <summary>Raised (bubbling) after the selection changed, once per change or batch.</summary>
        public static readonly RoutedEvent<SelectionModelChangedEventArgs> SelectionModelChangedEvent =
            RoutedEvent.Register<FileListBox, SelectionModelChangedEventArgs>("SelectionModelChanged", RoutingStrategies.Bubble);

        public FileListBox()
        {
            var selection = new SelectionModel<object> { SingleSelect = false };
            selection.SelectionChanged += OnSelectionModelChanged;
            Selection = selection;

            // A rubber band or a selection carried over to new items must not yank the view to the first selected
            // item; keyboard navigation brings the focused item into view through the panel instead.
            AutoScrollToSelectedItem = false;
        }

        /// <summary>
        /// The margin of every item container (the item spacing setting), or null to leave the container theme's. It is set
        /// on the container, outside the item, so the gap belongs to the list: a press there starts a rubber band.
        /// </summary>
        public static readonly StyledProperty<Thickness?> ItemMarginProperty =
            AvaloniaProperty.Register<FileListBox, Thickness?>(nameof(ItemMargin));

        public Thickness? ItemMargin
        {
            get => GetValue(ItemMarginProperty);
            set => SetValue(ItemMarginProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(ListBox);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ItemMarginProperty && ItemsPanelRoot is Panel panel)
            {
                // Every container the panel holds, the recycled (hidden) ones too; the panel re-measures its cells.
                foreach (Control container in panel.Children)
                {
                    ApplyItemMargin(container);
                }
            }
        }

        private void ApplyItemMargin(Control container)
        {
            if (ItemMargin is Thickness margin)
            {
                container.Margin = margin;
            }
            else
            {
                container.ClearValue(MarginProperty);
            }
        }

        protected override void PrepareContainerForItemOverride(Control container, object item, int index)
        {
            if (ItemTemplate != null && container is ContentControl content && !ReferenceEquals(container, item))
            {
                // Set (rather than "set if unset", as the base does) so a kept container takes the new item.
                if (!ReferenceEquals(content.ContentTemplate, ItemTemplate))
                {
                    content.SetCurrentValue(ContentControl.ContentTemplateProperty, ItemTemplate);
                }

                content.SetCurrentValue(ContentControl.ContentProperty, item);
            }

            base.PrepareContainerForItemOverride(container, item, index);
            container.Classes.Set("alternate", index % 2 == 1);
            ApplyItemMargin(container);
        }

        /// <summary>
        /// Every second row has the "alternate" class, for layouts that stripe their lists (Finder). Kept here rather than
        /// with :nth-child, which needs the panel to report child indexes (the virtualizing panel does not); updated when a
        /// recycled container moves to another index.
        /// </summary>
        protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
        {
            base.ContainerIndexChangedOverride(container, oldIndex, newIndex);
            container.Classes.Set("alternate", newIndex % 2 == 1);
        }

        protected override void ClearContainerForItemOverride(Control container)
        {
            if (ItemTemplate == null || !(container is ContentControl))
            {
                base.ClearContainerForItemOverride(container);
                return;
            }

            // A recycled row stops asking for its (scrolled-away) item's thumbnail; the visual tree here is a few levels.
            foreach (Avalonia.Visual visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(container))
            {
                if (visual is FileItemView view)
                {
                    view.ReleaseThumbnail();
                    break;
                }
            }

            // Content and ContentTemplate are kept (see class remarks); only the selection state is reset, as the base
            // does. Virtualizing panels unregister a container before clearing it, so this change cannot reach the
            // selection model (it ignores containers without an index).
            container.ClearValue(IsSelectedProperty);
        }

        private void OnSelectionModelChanged(object sender, SelectionModelSelectionChangedEventArgs e)
        {
            RaiseEvent(new SelectionModelChangedEventArgs(SelectionModelChangedEvent, e.SelectedIndexes.Count > 0));
        }
    }

    /// <summary>Arguments of <see cref="FileListBox.SelectionModelChangedEvent"/>.</summary>
    public sealed class SelectionModelChangedEventArgs : RoutedEventArgs
    {
        public SelectionModelChangedEventArgs(RoutedEvent routedEvent, bool itemsWereSelected)
            : base(routedEvent)
        {
            ItemsWereSelected = itemsWereSelected;
        }

        /// <summary>True when the change selected at least one item (as opposed to only deselecting).</summary>
        public bool ItemsWereSelected { get; }
    }
}
