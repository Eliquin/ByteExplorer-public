// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ByteExplorer.Views.Sidebar
{
    /// <summary>
    /// The order of the left panel's sections, chosen in the settings (Navigation.SidebarSectionOrder). Each layout marks
    /// the top-level element of a section with <see cref="KeyProperty"/> and, where it draws a line between sections, the
    /// line inside it with <see cref="IsDividerProperty"/>; <see cref="Apply"/> moves the marked elements into the chosen
    /// order and shows a line above every section but the first. Sections a layout doesn't have are skipped, and a saved
    /// order missing a section keeps that section where the layout put it.
    /// </summary>
    /// <remarks>
    /// The Modern layout's tree must keep the panel's remaining height (it is a virtualizing list), so it stays in place
    /// as the "pivot": sections ordered before it go into the scroller above it, the others into the one below it. The
    /// Nemo and Finder sidebars are a single stack, where every section moves.
    /// </remarks>
    public static class SidebarOrder
    {
        public const string Places = "Places";
        public const string QuickAccess = "QuickAccess";
        public const string Recent = "Recent";
        public const string Lens = "Lens";
        public const string Drives = "Drives";
        public const string Tags = "Tags";
        public const string Network = "Network";

        /// <summary>Every section key with the name the settings show for it.</summary>
        public static IReadOnlyList<(string Key, string Label)> Sections { get; } = new[]
        {
            (Places, "Home and recent places"),
            (QuickAccess, "Quick access (pinned)"),
            (Lens, "Lens (smart views: today, photos, documents…)"),
            (Recent, "Frequent folders"),
            (Drives, "Drives and folders"),
            (Tags, "Tags"),
            (Network, "Network"),
        };

        public static readonly AttachedProperty<string> KeyProperty =
            AvaloniaProperty.RegisterAttached<Control, string>("Key", typeof(SidebarOrder));

        public static readonly AttachedProperty<bool> IsDividerProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("IsDivider", typeof(SidebarOrder));

        public static string GetKey(Control control) => control.GetValue(KeyProperty);

        public static void SetKey(Control control, string value) => control.SetValue(KeyProperty, value);

        public static bool GetIsDivider(Control control) => control.GetValue(IsDividerProperty);

        public static void SetIsDivider(Control control, bool value) => control.SetValue(IsDividerProperty, value);

        public static string LabelFor(string key) => Sections.FirstOrDefault(s => s.Key == key).Label ?? key;

        /// <summary>
        /// The layout's own order: the marked sections of <paramref name="above"/>, then <paramref name="pivotKey"/> (when
        /// there is a <paramref name="below"/> panel, the pivot sits between the two), then those of <paramref name="below"/>.
        /// </summary>
        public static List<string> ReadOrder(Panel above, Panel below, string pivotKey)
        {
            var order = new List<string>();
            order.AddRange(KeyedChildren(above).Select(GetKey));
            if (below != null)
            {
                if (!string.IsNullOrEmpty(pivotKey))
                {
                    order.Add(pivotKey);
                }

                order.AddRange(KeyedChildren(below).Select(GetKey));
            }

            return order;
        }

        /// <summary>
        /// The order to show: the saved keys this layout has (each once), with the layout's other sections slotted in
        /// after the section that precedes them in the layout's own order (or first). An empty saved order is the default.
        /// </summary>
        public static List<string> Effective(IReadOnlyList<string> layoutOrder, IReadOnlyList<string> saved)
        {
            var result = new List<string>();
            if (saved != null)
            {
                foreach (string key in saved)
                {
                    if (layoutOrder.Contains(key) && !result.Contains(key))
                    {
                        result.Add(key);
                    }
                }
            }

            for (int i = 0; i < layoutOrder.Count; i++)
            {
                string key = layoutOrder[i];
                if (result.Contains(key))
                {
                    continue;
                }

                int insertAt = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int at = result.IndexOf(layoutOrder[j]);
                    if (at >= 0)
                    {
                        insertAt = at + 1;
                        break;
                    }
                }

                result.Insert(insertAt, key);
            }

            return result;
        }

        /// <summary>
        /// Moves the marked sections of the panels into <paramref name="order"/> (see <see cref="Effective"/>). Unmarked
        /// children (a sidebar title) stay where they are, before the sections.
        /// </summary>
        public static void Apply(Panel above, Panel below, string pivotKey, IReadOnlyList<string> order)
        {
            ArgumentNullException.ThrowIfNull(above);
            var sections = new Dictionary<string, Control>(StringComparer.Ordinal);
            foreach (Panel panel in new[] { above, below })
            {
                if (panel == null)
                {
                    continue;
                }

                foreach (Control child in KeyedChildren(panel).ToList())
                {
                    sections[GetKey(child)] = child;
                    panel.Children.Remove(child);
                }
            }

            int pivotIndex = below == null ? int.MaxValue : IndexOf(order, pivotKey);
            for (int i = 0; i < order.Count; i++)
            {
                if (sections.Remove(order[i], out Control section))
                {
                    (i < pivotIndex || below == null ? above : below).Children.Add(section);
                }
            }

            foreach (Control leftover in sections.Values)
            {
                above.Children.Add(leftover); // not in the order (cannot happen with Effective): never lose a section
            }

            UpdateDividers(above);
            if (below != null)
            {
                UpdateDividers(below);
            }
        }

        private static int IndexOf(IReadOnlyList<string> order, string key)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] == key)
                {
                    return i;
                }
            }

            return order.Count;
        }

        /// <summary>A line above every section of the panel but its first.</summary>
        private static void UpdateDividers(Panel panel)
        {
            bool first = true;
            foreach (Control section in KeyedChildren(panel))
            {
                if (section is Panel group)
                {
                    foreach (Control child in group.Children)
                    {
                        if (GetIsDivider(child))
                        {
                            child.IsVisible = !first;
                        }
                    }
                }

                first = false;
            }
        }

        private static IEnumerable<Control> KeyedChildren(Panel panel) =>
            panel.Children.Where(c => !string.IsNullOrEmpty(GetKey(c)));
    }
}
