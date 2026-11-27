// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Settings
{
    /// <summary>
    /// An ordered list (the left panel's sections): each entry moves up or down, and "Use default order" goes back to the
    /// layout's own order. The order lives in the settings; the item reads it through <c>current</c> and writes a whole
    /// new order through <c>set</c> (null = the default).
    /// </summary>
    public sealed class OrderSetting : SettingItem
    {
        private readonly Func<IReadOnlyList<string>> _current;
        private readonly Func<IReadOnlyList<string>> _defaults;
        private readonly Func<string, string> _label;
        private readonly Action<List<string>> _set;

        public OrderSetting(string title, string description, Symbol icon, Func<IReadOnlyList<string>> current,
            Func<IReadOnlyList<string>> defaults, Func<string, string> label, Action<List<string>> set)
            : base(title, description, icon)
        {
            _current = current;
            _defaults = defaults;
            _label = label;
            _set = set;
            MoveUpCommand = new RelayCommand(p => Move(p as OrderEntry, -1));
            MoveDownCommand = new RelayCommand(p => Move(p as OrderEntry, +1));
            ResetCommand = new RelayCommand(_ => Write(null), _ => !IsDefault);
            Rebuild();
        }

        public ObservableCollection<OrderEntry> Entries { get; } = new ObservableCollection<OrderEntry>();

        public ICommand MoveUpCommand { get; }

        public ICommand MoveDownCommand { get; }

        public ICommand ResetCommand { get; }

        /// <summary>The order shown is the layout's own.</summary>
        public bool IsDefault => _current().SequenceEqual(_defaults());

        /// <summary>The keys in the order shown (for tests and the window).</summary>
        public IReadOnlyList<string> Keys => Entries.Select(e => e.Key).ToList();

        /// <summary>Moves the entry by <paramref name="delta"/> places (-1 up, +1 down); nothing at either end.</summary>
        public void Move(OrderEntry entry, int delta)
        {
            List<string> keys = _current().ToList();
            int from = entry == null ? -1 : keys.IndexOf(entry.Key);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= keys.Count)
            {
                return;
            }

            (keys[from], keys[to]) = (keys[to], keys[from]);
            Write(keys);
        }

        public override void Refresh() => Rebuild();

        private void Write(List<string> keys)
        {
            _set(keys == null || keys.SequenceEqual(_defaults()) ? null : keys);
            Rebuild();
            OnChanged();
        }

        private void Rebuild()
        {
            IReadOnlyList<string> keys = _current();
            Entries.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                Entries.Add(new OrderEntry(keys[i], _label(keys[i]), i + 1, canMoveUp: i > 0, canMoveDown: i < keys.Count - 1));
            }

            RaisePropertyChanged(nameof(IsDefault));
            (ResetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>One entry of an <see cref="OrderSetting"/>.</summary>
    public sealed class OrderEntry
    {
        public OrderEntry(string key, string label, int position, bool canMoveUp, bool canMoveDown)
        {
            Key = key;
            Label = label;
            Position = position;
            CanMoveUp = canMoveUp;
            CanMoveDown = canMoveDown;
        }

        public string Key { get; }

        public string Label { get; }

        public int Position { get; }

        public bool CanMoveUp { get; }

        public bool CanMoveDown { get; }
    }
}
