// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Icons;

namespace ByteExplorer.Views.Sidebar
{
    /// <summary>
    /// The "Folded chips" left panel design's row above the folder tree: one chip (icon, title, count) for each collapsed
    /// section of the window, in the panel's order; a click opens the section again where it is. In the other designs it
    /// is hidden (style), and collapsed sections keep their own header. Rebuilt when a section opens, closes, moves or its
    /// count changes (<see cref="SidebarSection.StateChanged"/>), once per change burst.
    /// </summary>
    public sealed class SidebarChipDock : WrapPanel
    {
        private bool _rebuildQueued;
        private readonly List<Control> _watchedParents = new List<Control>();

        public SidebarChipDock()
        {
            Orientation = Orientation.Horizontal;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SidebarSection.StateChanged += OnSectionStateChanged;
            QueueRebuild();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            SidebarSection.StateChanged -= OnSectionStateChanged;
            Unwatch();
        }

        private void OnSectionStateChanged(object sender, EventArgs e) => QueueRebuild();

        private void QueueRebuild()
        {
            if (_rebuildQueued)
            {
                return;
            }

            _rebuildQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _rebuildQueued = false;
                Rebuild();
            }, DispatcherPriority.Loaded);
        }

        /// <summary>The collapsed sections of this window, in panel order (internal for the tests).</summary>
        internal IReadOnlyList<SidebarSection> CollapsedSections()
        {
            Visual root = this.FindAncestorOfType<MainWindowBase>();
            if (root == null)
            {
                return Array.Empty<SidebarSection>();
            }

            return root.GetVisualDescendants().OfType<SidebarSection>()
                .Where(s => !s.IsExpanded && (s.GetVisualParent() as Control)?.IsVisible != false)
                .ToList();
        }

        private void Rebuild()
        {
            Unwatch();
            Children.Clear();
            IReadOnlyList<SidebarSection> sections = CollapsedSections();
            foreach (SidebarSection section in sections)
            {
                Children.Add(BuildChip(section));
                // A section whose block hides (Recent with nothing in it) loses its chip, and gets it back.
                if (section.GetVisualParent() is Control parent)
                {
                    parent.PropertyChanged += OnParentPropertyChanged;
                    _watchedParents.Add(parent);
                }
            }

            Classes.Set("hasChips", Children.Count > 0);
        }

        private void OnParentPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsVisibleProperty)
            {
                QueueRebuild();
            }
        }

        private void Unwatch()
        {
            foreach (Control parent in _watchedParents)
            {
                parent.PropertyChanged -= OnParentPropertyChanged;
            }

            _watchedParents.Clear();
        }

        private static Button BuildChip(SidebarSection section)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var icon = new SymbolIcon { Symbol = section.Icon, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            icon[!SymbolIcon.ForegroundProperty] = icon.GetResourceObservable("SelectedAccent").ToBinding();
            content.Children.Add(icon);
            var title = new TextBlock { Text = section.Header, FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
            title[!TextBlock.ForegroundProperty] = title.GetResourceObservable("TextPrimary").ToBinding();
            content.Children.Add(title);
            if (!string.IsNullOrEmpty(section.CountText))
            {
                var count = new TextBlock { Text = section.CountText };
                count.Classes.Add("sidebarCount");
                content.Children.Add(count);
            }

            var chip = new Button { Content = content, Tag = section.SectionKey };
            chip.Classes.Add("sbChip");
            ToolTip.SetTip(chip, "Show " + section.Header);
            AutomationProperties.SetName(chip, "Show " + section.Header);
            chip.Click += (_, _) => section.IsExpanded = true;
            return chip;
        }
    }
}
