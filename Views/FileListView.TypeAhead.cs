// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Type-to-select: typing while a list has focus selects the next item whose name starts with the typed text (see
    /// <see cref="TypeAheadSearch"/> for the rules). Driven by TextInput rather than KeyDown, so every keyboard layout and
    /// input method (accents, dead keys, non-Latin scripts) produces the characters the person actually typed.
    /// <para>
    /// Avalonia's built-in ItemsControl text search is off because it reads each item's text through the item objects,
    /// which on a virtualized 100,000-item folder would create every item; this search reads names straight from the
    /// folder index (<see cref="VirtualizingFileList.NameAt"/>).
    /// </para>
    /// </summary>
    public partial class FileListView
    {
        private readonly TypeAheadSearch _typeAhead = new TypeAheadSearch();
        private ListBox _typeAheadList;

        private void InitializeTypeAhead()
        {
            AddHandler(TextInputEvent, OnTextInputRouted, RoutingStrategies.Bubble);
        }

        private void OnTextInputRouted(object sender, TextInputEventArgs e)
        {
            string text = e.Text;
            if (e.Handled || _vm == null || string.IsNullOrEmpty(text) ||
                (_lastModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 || ContainsControlCharacter(text))
            {
                return;
            }

            ListBox list = FindList(e.Source as Visual, out _, out _);
            if (list == null || !(list.ItemsSource is IList items) || items.Count == 0)
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (!ReferenceEquals(list, _typeAheadList))
            {
                _typeAhead.Reset();
                _typeAheadList = list;
            }

            // A space only belongs to a name being typed; on its own it is left to the list (and the window).
            if (text == " " && !_typeAhead.IsActive(now))
            {
                return;
            }

            Func<int, string> nameAt = items is VirtualizingFileList virtualized
                ? virtualized.NameAt
                : index => ItemOf(items[index])?.Name;

            int current = list.Selection.AnchorIndex >= 0 ? list.Selection.AnchorIndex : list.SelectedIndex;
            int match = _typeAhead.Next(text, current, items.Count, nameAt, now);
            e.Handled = true;
            if (match < 0)
            {
                return;
            }

            list.Selection.BeginBatchUpdate();
            try
            {
                list.Selection.Clear();
                list.Selection.Select(match);
                list.Selection.AnchorIndex = match;
            }
            finally
            {
                list.Selection.EndBatchUpdate();
            }

            list.ScrollIntoView(match);
            list.ContainerFromIndex(match)?.Focus(NavigationMethod.Directional);
        }

        private static bool ContainsControlCharacter(string text)
        {
            foreach (char c in text)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
