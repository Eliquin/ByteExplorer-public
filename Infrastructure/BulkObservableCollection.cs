// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// ObservableCollection that can swap its whole content with one Reset notification. Rebuilding a selection
    /// with Clear + Add raises one event per item, which made every click after a Select All on a large folder
    /// cost O(items) notifications.
    /// </summary>
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();

            // Copy first: items may be this collection itself or a view over it.
            var snapshot = new List<T>(items);
            Items.Clear();
            foreach (T item in snapshot)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
