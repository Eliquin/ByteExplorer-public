// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Sidebar
{
    /// <summary>One tag in the sidebar's Tags section: its color, name, how many items carry it, and its place in the tree.</summary>
    public sealed class TagRowViewModel : ViewModelBase
    {
        private const double IndentPerLevel = 14;
        private bool _isExpanded = true;
        private bool _isActive;

        internal TagRowViewModel(TagDefinition tag, string fullName, int depth, int count, bool hasChildren)
        {
            Tag = tag;
            FullName = fullName;
            Depth = depth;
            Count = count;
            HasChildren = hasChildren;
            Brush = new SolidColorBrush(Color.Parse(tag.ColorHex)).ToImmutable();
        }

        public TagDefinition Tag { get; }

        public string Id => Tag.Id;

        public string Name => Tag.Name;

        /// <summary>"Work/Project A".</summary>
        public string FullName { get; }

        public int Depth { get; }

        public Thickness Indent => new Thickness(Depth * IndentPerLevel, 0, 0, 0);

        /// <summary>Items carrying the tag or one of its sub-tags.</summary>
        public int Count { get; }

        public string CountText => Count > 0 ? Count.ToString("N0") : string.Empty;

        public bool HasChildren { get; }

        public IBrush Brush { get; }

        public string ToolTip
        {
            get
            {
                string items = Count == 1 ? "1 item" : $"{Count:N0} items";
                string text = $"{FullName.Replace("/", " › ")} · {Tag.Color} · {items}";
                return string.IsNullOrEmpty(Tag.Description) ? text : text + "\n" + Tag.Description;
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        /// <summary>The tag's items are shown (the search box holds this tag's query).</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        /// <summary>Shown when every parent is expanded.</summary>
        public bool IsVisible { get; internal set; } = true;
    }

    /// <summary>
    /// The sidebar's Tags section: every tag as a tree (sub-tags under their parent, collapsible), each with its color and
    /// item count, filled automatically as tags are created anywhere (the context menu, the tag editor, a name typed with
    /// slashes) and kept in step with the store. Opening a tag shows its items (a tag: search). Rows rebuild once per burst
    /// of changes, not once per tagged item.
    /// </summary>
    public sealed class TagsSidebarViewModel : ViewModelBase
    {
        private readonly TagStore _store;
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);
        private bool _rebuildPending;
        private string _activeQuery;

        public TagsSidebarViewModel(TagStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            TagStore.AnyChanged += OnStoreChanged;
            Rebuild();
        }

        /// <summary>The visible rows (collapsed branches left out).</summary>
        public BulkObservableCollection<TagRowViewModel> Rows { get; } = new BulkObservableCollection<TagRowViewModel>();

        public bool HasTags => _store.Tags.Count > 0;

        public TagStore Store => _store;

        /// <summary>Opens a tag's items; set by the main view model (fills the search box).</summary>
        public Action<TagRowViewModel> OpenTag { get; set; }

        public void Open(TagRowViewModel row)
        {
            if (row != null)
            {
                OpenTag?.Invoke(row);
            }
        }

        public void ToggleExpanded(TagRowViewModel row)
        {
            if (row == null || !row.HasChildren)
            {
                return;
            }

            if (!_collapsed.Remove(row.Id))
            {
                _collapsed.Add(row.Id);
            }

            Rebuild();
        }

        /// <summary>Marks the row whose items are shown (the search box's query), clearing the others.</summary>
        public void SetActiveQuery(string query)
        {
            _activeQuery = query;
            foreach (TagRowViewModel row in Rows)
            {
                row.IsActive = IsActiveQuery(row);
            }
        }

        private bool IsActiveQuery(TagRowViewModel row) =>
            !string.IsNullOrEmpty(_activeQuery) && string.Equals(_activeQuery.Trim(), TagSearch.QueryFor(row.FullName), StringComparison.OrdinalIgnoreCase);

        private void OnStoreChanged(object sender, TagsChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _store) || _rebuildPending)
            {
                return;
            }

            _rebuildPending = true;
            ByteExplorer.Infrastructure.UiThread.Post(() =>
            {
                _rebuildPending = false;
                Rebuild();
            }, DispatcherPriority.Background);
        }

        /// <summary>Builds the rows from the store: tree order, counts including sub-tags, collapsed branches hidden.</summary>
        public void Rebuild()
        {
            IReadOnlyList<TagDefinition> tags = _store.Tags;
            var hasChildren = new HashSet<string>(tags.Where(t => t.ParentId != null).Select(t => t.ParentId), StringComparer.Ordinal);
            var rows = new List<TagRowViewModel>(tags.Count);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            foreach (TagDefinition tag in tags)
            {
                if (tag.ParentId != null && (hidden.Contains(tag.ParentId) || _collapsed.Contains(tag.ParentId)))
                {
                    hidden.Add(tag.Id);
                    continue;
                }

                var row = new TagRowViewModel(tag, _store.FullName(tag), _store.Depth(tag), _store.CountItems(tag.Id), hasChildren.Contains(tag.Id))
                {
                    IsExpanded = !_collapsed.Contains(tag.Id)
                };
                row.IsActive = IsActiveQuery(row);
                rows.Add(row);
            }

            Rows.ReplaceAll(rows);

            RaisePropertyChanged(nameof(HasTags));
        }

        public void Detach() => TagStore.AnyChanged -= OnStoreChanged;
    }
}
