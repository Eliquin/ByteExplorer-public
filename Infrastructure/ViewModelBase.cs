// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Base class for all view models. Deliberately minimal — no service
    /// locators, no messaging bus baked in here. Subsystems get their
    /// dependencies via constructor injection so each stays testable in
    /// isolation from the UI layer.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            // Most CanExecute predicates read view-model state; WPF re-queried them after input, Avalonia never does on
            // its own. Coalesced, so a burst of property changes costs one requery.
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
