// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;

namespace ByteExplorer.ViewModels
{
    /// <summary>One visible row of the navigation tree: a node and how deep it sits.</summary>
    public sealed class NavigationTreeRow : INotifyPropertyChanged
    {
        private const double IndentPerLevel = 16;
        private bool _hasChildren;

        internal NavigationTreeRow(NavigationNodeViewModel node, int depth)
        {
            Node = node;
            Depth = depth;
            Indent = new Thickness(depth * IndentPerLevel, 0, 0, 0);
            _hasChildren = node.Children.Count > 0;
        }

        public NavigationNodeViewModel Node { get; }
        public int Depth { get; }

        /// <summary>Left margin of the row content (precomputed: one Thickness per row, not one per layout pass).</summary>
        public Thickness Indent { get; }

        /// <summary>Whether the chevron is shown: the node has (or may have, a "Loading..." placeholder) children.</summary>
        public bool HasChildren
        {
            get => _hasChildren;
            internal set
            {
                if (_hasChildren != value)
                {
                    _hasChildren = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChildren)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// The navigation tree flattened into the list of rows currently visible (every node whose ancestors are all
    /// expanded), for a virtualizing list.
    /// <para>
    /// Why: Avalonia's TreeView virtualizes only its top level; each expanded node lays out all of its children, so
    /// expanding a folder with tens of thousands of subfolders created and measured every row (seconds of UI stall and
    /// hundreds of MB). A flat list in a virtualizing ListBox realizes only the rows on screen, whatever the depth or
    /// size, the approach VS Code and most large-tree UIs use.
    /// </para>
    /// <para>
    /// The list is maintained incrementally: expanding a node inserts its visible descendants right after it in one
    /// ranged Add; collapsing removes the contiguous block of deeper rows after it in one ranged Remove; a node's children
    /// being replaced (a load finishing, a folder watcher refresh) swaps that block in two ranged operations. Only visible
    /// nodes are subscribed to, and subscriptions are dropped with their rows. All changes arrive on the UI thread, where
    /// the node view models raise them.
    /// </para>
    /// </summary>
    public sealed class NavigationTreeRows : IList, IReadOnlyList<NavigationTreeRow>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
    {
        private readonly ObservableCollection<NavigationNodeViewModel> _roots;
        private readonly List<NavigationTreeRow> _rows = new List<NavigationTreeRow>();
        private readonly Dictionary<NavigationNodeViewModel, NavigationTreeRow> _rowByNode = new Dictionary<NavigationNodeViewModel, NavigationTreeRow>(ReferenceEqualityComparer.Instance);

        // A Children collection raises its own events without a back-reference to its node.
        private readonly Dictionary<object, NavigationNodeViewModel> _nodeByChildren = new Dictionary<object, NavigationNodeViewModel>(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        public NavigationTreeRows(ObservableCollection<NavigationNodeViewModel> roots)
        {
            _roots = roots ?? throw new ArgumentNullException(nameof(roots));
            _roots.CollectionChanged += OnRootsChanged;
            Rebuild();
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public int Count => _rows.Count;

        public NavigationTreeRow this[int index] => _rows[index];

        /// <summary>The row index of a node, or -1 when the node is not visible.</summary>
        public int IndexOf(NavigationNodeViewModel node)
        {
            if (node == null || !_rowByNode.TryGetValue(node, out NavigationTreeRow row))
            {
                return -1;
            }

            return _rows.IndexOf(row);
        }

        /// <summary>The visible row of the node's parent (the nearest shallower row above it), or null for a root.</summary>
        public NavigationTreeRow ParentOf(NavigationTreeRow row)
        {
            int index = _rows.IndexOf(row);
            for (int i = index - 1; i >= 0; i--)
            {
                if (_rows[i].Depth < row.Depth)
                {
                    return _rows[i];
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _roots.CollectionChanged -= OnRootsChanged;
            foreach (NavigationTreeRow row in _rows)
            {
                Unsubscribe(row.Node);
            }

            _rows.Clear();
            _rowByNode.Clear();
            _nodeByChildren.Clear();
        }

        // ------------------------------------------------------------------ building

        private void Rebuild()
        {
            foreach (NavigationTreeRow row in _rows)
            {
                Unsubscribe(row.Node);
            }

            _rows.Clear();
            _rowByNode.Clear();
            _nodeByChildren.Clear();
            var added = new List<NavigationTreeRow>();
            foreach (NavigationNodeViewModel root in _roots)
            {
                AppendVisible(root, 0, added);
            }

            _rows.AddRange(added);
            RaiseReset();
        }

        /// <summary>The node and, if expanded, its visible descendants, in display order (iterative: no stack depth limit).</summary>
        private void AppendVisible(NavigationNodeViewModel start, int startDepth, List<NavigationTreeRow> output)
        {
            var stack = new Stack<(NavigationNodeViewModel Node, int Depth)>();
            stack.Push((start, startDepth));
            while (stack.Count > 0)
            {
                (NavigationNodeViewModel node, int depth) = stack.Pop();
                var row = new NavigationTreeRow(node, depth);
                _rowByNode[node] = row;
                Subscribe(node);
                output.Add(row);

                if (node.IsExpanded)
                {
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((node.Children[i], depth + 1));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ change tracking

        private void Subscribe(NavigationNodeViewModel node)
        {
            node.PropertyChanged += OnNodePropertyChanged;
            node.Children.CollectionChanged += OnNodeChildrenChanged;
            _nodeByChildren[node.Children] = node;
        }

        private void Unsubscribe(NavigationNodeViewModel node)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
            node.Children.CollectionChanged -= OnNodeChildrenChanged;
            _nodeByChildren.Remove(node.Children);
        }

        private void OnRootsChanged(object sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void OnNodePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NavigationNodeViewModel.IsExpanded) || !(sender is NavigationNodeViewModel node))
            {
                return;
            }

            RefreshDescendants(node);
        }

        private void OnNodeChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (sender == null || !_nodeByChildren.TryGetValue(sender, out NavigationNodeViewModel node) ||
                !_rowByNode.TryGetValue(node, out NavigationTreeRow row))
            {
                return;
            }

            row.HasChildren = node.Children.Count > 0;
            if (!node.IsExpanded)
            {
                return;
            }

            // A reload adds and removes single folders (see NavigationNodeViewModel.ApplyLoadedChildren): only their
            // blocks move, so the rows around them, and the list's selection, stay. Anything else rebuilds the block.
            bool applied = e.Action switch
            {
                NotifyCollectionChangedAction.Add => InsertChildBlocks(node, row, e.NewStartingIndex, e.NewItems),
                NotifyCollectionChangedAction.Remove => RemoveChildBlocks(e.OldItems),
                _ => false
            };

            if (!applied)
            {
                RefreshDescendants(node);
            }
        }

        /// <summary>Inserts the rows of children just added to an expanded node. False when the position is unknown.</summary>
        private bool InsertChildBlocks(NavigationNodeViewModel node, NavigationTreeRow row, int startingIndex, IList newItems)
        {
            if (newItems == null || newItems.Count == 0 || startingIndex < 0)
            {
                return false;
            }

            // Before the next sibling's row, or after the node's last visible descendant.
            int insertAt;
            int nextSibling = startingIndex + newItems.Count;
            if (nextSibling < node.Children.Count)
            {
                if (!_rowByNode.TryGetValue(node.Children[nextSibling], out NavigationTreeRow siblingRow))
                {
                    return false;
                }

                insertAt = _rows.IndexOf(siblingRow);
            }
            else
            {
                insertAt = _rows.IndexOf(row);
                if (insertAt < 0)
                {
                    return false;
                }

                insertAt++;
                while (insertAt < _rows.Count && _rows[insertAt].Depth > row.Depth)
                {
                    insertAt++;
                }
            }

            if (insertAt < 0)
            {
                return false;
            }

            var added = new List<NavigationTreeRow>(newItems.Count);
            foreach (object item in newItems)
            {
                if (item is NavigationNodeViewModel child)
                {
                    AppendVisible(child, row.Depth + 1, added);
                }
            }

            _rows.InsertRange(insertAt, added);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, insertAt));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            return true;
        }

        /// <summary>Removes the rows of children just removed (each child's row and its visible descendants).</summary>
        private bool RemoveChildBlocks(IList oldItems)
        {
            if (oldItems == null)
            {
                return false;
            }

            foreach (object item in oldItems)
            {
                if (item is not NavigationNodeViewModel child || !_rowByNode.TryGetValue(child, out NavigationTreeRow childRow))
                {
                    continue;
                }

                int start = _rows.IndexOf(childRow);
                if (start < 0)
                {
                    continue;
                }

                int end = start + 1;
                while (end < _rows.Count && _rows[end].Depth > childRow.Depth)
                {
                    end++;
                }

                var removed = _rows.GetRange(start, end - start);
                foreach (NavigationTreeRow gone in removed)
                {
                    Unsubscribe(gone.Node);
                    _rowByNode.Remove(gone.Node);
                }

                _rows.RemoveRange(start, removed.Count);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, start));
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            return true;
        }

        /// <summary>Replaces the rows below the node with what is visible now: removes the old block, inserts the new one.</summary>
        private void RefreshDescendants(NavigationNodeViewModel node)
        {
            if (!_rowByNode.TryGetValue(node, out NavigationTreeRow row))
            {
                return;
            }

            int index = _rows.IndexOf(row);
            if (index < 0)
            {
                return;
            }

            int start = index + 1;
            int end = start;
            while (end < _rows.Count && _rows[end].Depth > row.Depth)
            {
                end++;
            }

            if (end > start)
            {
                var removed = _rows.GetRange(start, end - start);
                foreach (NavigationTreeRow gone in removed)
                {
                    Unsubscribe(gone.Node);
                    _rowByNode.Remove(gone.Node);
                }

                _rows.RemoveRange(start, removed.Count);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, start));
            }

            if (node.IsExpanded && node.Children.Count > 0)
            {
                var added = new List<NavigationTreeRow>(node.Children.Count);
                foreach (NavigationNodeViewModel child in node.Children)
                {
                    AppendVisible(child, row.Depth + 1, added);
                }

                _rows.InsertRange(start, added);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        private void RaiseReset()
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        // ------------------------------------------------------------------ read-only IList for the ItemsControl

        object IList.this[int index]
        {
            get => _rows[index];
            set => throw new NotSupportedException();
        }

        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => false;
        object ICollection.SyncRoot => this;
        bool ICollection.IsSynchronized => false;

        int IList.Add(object value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object value) => value is NavigationTreeRow row && _rowByNode.TryGetValue(row.Node, out NavigationTreeRow current) && ReferenceEquals(current, row);
        int IList.IndexOf(object value) => value is NavigationTreeRow row ? _rows.IndexOf(row) : -1;
        void IList.Insert(int index, object value) => throw new NotSupportedException();
        void IList.Remove(object value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_rows).CopyTo(array, index);

        public IEnumerator<NavigationTreeRow> GetEnumerator() => _rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _rows.GetEnumerator();
    }
}
