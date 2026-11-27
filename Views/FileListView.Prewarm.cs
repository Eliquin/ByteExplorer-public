// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.ViewModels;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Idle-time preparation of the rows about to scroll into view. After the view model's background prefetch has
    /// built the item objects, the UI thread uses its idle time to lay out their text (names, dates, types, sizes) into
    /// the shared text cache, through a realized <see cref="FileItemView"/> so the layouts match what will be drawn.
    /// When those rows appear, drawing them shapes no text.
    /// <para>
    /// The work runs at Background priority in slices of at most <see cref="SliceBudget"/>, so input, layout and
    /// rendering always go first; any newer scroll, folder change or view switch cancels the remaining work.
    /// </para>
    /// </summary>
    public partial class FileListView
    {
        private static readonly TimeSpan SliceBudget = TimeSpan.FromMilliseconds(4);

        /// <summary>Turns idle-time preparation on or off (on by default; the performance probes compare both).</summary>
        public static bool IsPrewarmEnabled { get; set; } = true;
        private int _prewarmGeneration;
        private FileItemView _prewarmTemplate;

        private void StartPrewarm(ListBox list, VirtualizingWrapPanel panel, VirtualizingFileList items, bool forward)
        {
            int generation = ++_prewarmGeneration;
            if (!IsPrewarmEnabled)
            {
                return;
            }

            int first = panel.FirstRealizedIndex;
            int last = panel.LastRealizedIndex;
            if (first < 0 || last < first || items.Count == 0)
            {
                return;
            }

            int visible = last - first + 1;
            int start = forward ? last + 1 : first - 1;
            int end = forward ? Math.Min(items.Count - 1, last + 2 * visible) : Math.Max(0, first - visible);
            int step = forward ? 1 : -1;
            int next = start;

            void Slice()
            {
                if (generation != _prewarmGeneration || !ReferenceEquals(list, _activeFlatList) || !ReferenceEquals(items, _activeFlatItems))
                {
                    return;
                }

                FileItemView template = TemplateView(list);
                if (template == null)
                {
                    return;
                }

                long deadline = Stopwatch.GetTimestamp() + (long)(SliceBudget.TotalSeconds * Stopwatch.Frequency);
                while (forward ? next <= end : next >= end)
                {
                    if (items[next] is FileListItemViewModel item && !template.Prewarm(item))
                    {
                        return;
                    }

                    next += step;
                    if (Stopwatch.GetTimestamp() > deadline)
                    {
                        Dispatcher.UIThread.Post(Slice, DispatcherPriority.Background);
                        return;
                    }
                }
            }

            Dispatcher.UIThread.Post(Slice, DispatcherPriority.Background);
        }

        /// <summary>
        /// After a folder opens: grows the list's container pool to about one extra page (see
        /// VirtualizingWrapPanel.WarmContainerPool), a few containers per idle slice.
        /// </summary>
        private void StartPoolWarming(ListBox list, VirtualizingWrapPanel panel)
        {
            int generation = _prewarmGeneration;
            if (!IsPrewarmEnabled)
            {
                return;
            }

            void Slice()
            {
                if (generation != _prewarmGeneration || !ReferenceEquals(list, _activeFlatList) || list.ItemsPanelRoot != panel)
                {
                    return;
                }

                int realized = panel.LastRealizedIndex - panel.FirstRealizedIndex + 1;
                int target = Math.Max(16, realized);
                if (realized <= 0 || panel.PooledContainerCount >= target)
                {
                    return;
                }

                long deadline = Stopwatch.GetTimestamp() + (long)(SliceBudget.TotalSeconds * Stopwatch.Frequency);
                while (panel.PooledContainerCount < target && Stopwatch.GetTimestamp() < deadline)
                {
                    if (panel.WarmContainerPool(4) == 0)
                    {
                        return;
                    }
                }

                Dispatcher.UIThread.Post(Slice, DispatcherPriority.Background);
            }

            Dispatcher.UIThread.Post(Slice, DispatcherPriority.Background);
        }

        /// <summary>A realized item view of the list, whose size and settings the prepared layouts must match.</summary>
        private FileItemView TemplateView(ListBox list)
        {
            if (_prewarmTemplate != null && _prewarmTemplate.IsAttachedToVisualTree() &&
                ReferenceEquals(_prewarmTemplate.FindAncestorOfType<ListBox>(), list) && _prewarmTemplate.IsEffectivelyVisible)
            {
                return _prewarmTemplate;
            }

            _prewarmTemplate = list.GetRealizedContainers()
                .Select(container => container.GetVisualDescendants().OfType<FileItemView>().FirstOrDefault())
                .FirstOrDefault(view => view != null && view.IsEffectivelyVisible && view.Bounds.Width > 0);
            return _prewarmTemplate;
        }
    }
}
