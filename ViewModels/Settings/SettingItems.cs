// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Settings
{
    /// <summary>
    /// One setting as the settings window shows it: a card with an icon, a title, a description and a control. Each
    /// kind (switch, choice, number, folder, list, action, information) is a subclass the window has a template for.
    /// The value lives in AppSettings: an item only reads and writes it through the delegates it is given, so the
    /// window never holds a copy that could disagree with the menu, the omnibar or the settings file.
    /// </summary>
    public abstract class SettingItem : ViewModelBase
    {
        protected SettingItem(string title, string description, Symbol icon)
        {
            Title = title;
            Description = description;
            Icon = icon;
        }

        public string Title { get; }

        public string Description { get; }

        public Symbol Icon { get; }

        /// <summary>Extra words the search matches besides the title and description.</summary>
        public string Keywords { get; init; } = string.Empty;

        /// <summary>False when this system can't have the setting (it is shown, greyed, with the reason as its description).</summary>
        public bool IsAvailable { get; init; } = true;

        /// <summary>The change applies on the next start (it is read once at startup); the window then offers a restart.</summary>
        public bool RequiresRestart { get; init; }

        /// <summary>Set by the page that holds the item: shown in search results.</summary>
        public string PageTitle { get; internal set; }

        /// <summary>Called after the value changed: saves the settings and applies the change (set by the view model).</summary>
        internal Action<SettingItem> Changed { get; set; }

        /// <summary>Re-reads the value from the settings (it may have been changed elsewhere, e.g. from the menu).</summary>
        public virtual void Refresh()
        {
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Contains(Title, word) && !Contains(Description, word) && !Contains(Keywords, word) && !Contains(PageTitle, word))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Contains(string text, string word) =>
            !string.IsNullOrEmpty(text) && text.Contains(word, StringComparison.CurrentCultureIgnoreCase);

        protected void OnChanged() => Changed?.Invoke(this);
    }

    /// <summary>An on/off setting (a switch with "On"/"Off" beside it, as Windows 11 shows them).</summary>
    public sealed class ToggleSetting : SettingItem
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;

        public ToggleSetting(string title, string description, Symbol icon, Func<bool> get, Action<bool> set)
            : base(title, description, icon)
        {
            _get = get;
            _set = set;
        }

        public bool Value
        {
            get => _get();
            set
            {
                if (value == _get())
                {
                    return;
                }

                _set(value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(StateText));
                OnChanged();
            }
        }

        public string StateText => Value ? "On" : "Off";

        public override void Refresh()
        {
            RaisePropertyChanged(nameof(Value));
            RaisePropertyChanged(nameof(StateText));
        }
    }

    /// <summary>One choice of a <see cref="ChoiceSetting"/>: the stored value and what the person reads.</summary>
    public sealed class SettingOption
    {
        public SettingOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>A setting with a fixed set of values, shown as a drop-down.</summary>
    public sealed class ChoiceSetting : SettingItem
    {
        private readonly Func<string> _get;
        private readonly Action<string> _set;

        public ChoiceSetting(string title, string description, Symbol icon, IReadOnlyList<SettingOption> options, Func<string> get, Action<string> set)
            : base(title, description, icon)
        {
            Options = options;
            _get = get;
            _set = set;
        }

        public IReadOnlyList<SettingOption> Options { get; }

        public SettingOption Selected
        {
            get
            {
                string value = _get();
                return Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)) ?? Options.FirstOrDefault();
            }
            set
            {
                if (value == null || string.Equals(value.Value, _get(), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _set(value.Value);
                RaisePropertyChanged();
                OnChanged();
            }
        }

        public override void Refresh() => RaisePropertyChanged(nameof(Selected));
    }

    /// <summary>A number in a range, with a unit (ms, items, GB…), shown as a spin box.</summary>
    public sealed class NumberSetting : SettingItem
    {
        private readonly Func<double> _get;
        private readonly Action<double> _set;

        public NumberSetting(string title, string description, Symbol icon, double minimum, double maximum, double increment, string unit,
            Func<double> get, Action<double> set)
            : base(title, description, icon)
        {
            Minimum = minimum;
            Maximum = maximum;
            Increment = increment;
            Unit = unit;
            _get = get;
            _set = set;
        }

        public double Minimum { get; }

        public double Maximum { get; }

        public double Increment { get; }

        /// <summary>The unit shown after the box ("ms", "folders", "GB").</summary>
        public string Unit { get; }

        /// <summary>Decimals the box shows: none for whole steps.</summary>
        public string FormatString => Increment % 1 == 0 ? "N0" : "0.0#";

        /// <summary>The allowed range, for the description line.</summary>
        public string RangeText => string.Format(CultureInfo.CurrentCulture, "{0:#,0.##} to {1:#,0.##} {2}", Minimum, Maximum, Unit).TrimEnd();

        /// <summary>Decimal for NumericUpDown (its value type); null when the box is cleared, which keeps the old value.</summary>
        public decimal? Value
        {
            get => (decimal)_get();
            set
            {
                if (value == null)
                {
                    RaisePropertyChanged();
                    return;
                }

                double number = Math.Clamp((double)value.Value, Minimum, Maximum);
                if (Math.Abs(number - _get()) < 1e-9)
                {
                    if ((double)value.Value != number)
                    {
                        RaisePropertyChanged(); // typed out of range: show the clamped value
                    }

                    return;
                }

                _set(number);
                RaisePropertyChanged();
                OnChanged();
            }
        }

        public override void Refresh() => RaisePropertyChanged(nameof(Value));
    }

    /// <summary>A folder (empty = the built-in default), chosen with the system folder picker.</summary>
    public sealed class FolderSetting : SettingItem
    {
        private readonly Func<string> _get;
        private readonly Action<string> _set;

        public FolderSetting(string title, string description, Symbol icon, string defaultText, Func<string> get, Action<string> set)
            : base(title, description, icon)
        {
            DefaultText = defaultText;
            _get = get;
            _set = set;
            ClearCommand = new RelayCommand(_ => Path = null, _ => !string.IsNullOrEmpty(_get()));
        }

        /// <summary>What an empty value means ("Default: next to the settings file").</summary>
        public string DefaultText { get; }

        /// <summary>The button that empties the value.</summary>
        public string ClearText { get; init; } = "Use default";

        public string Path
        {
            get => _get();
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (string.Equals(normalized, _get(), StringComparison.Ordinal))
                {
                    return;
                }

                _set(normalized);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(DisplayText));
                (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
                OnChanged();
            }
        }

        public string DisplayText => string.IsNullOrEmpty(Path) ? DefaultText : Path;

        /// <summary>Set by the view model: opens the folder picker and stores the choice.</summary>
        public ICommand BrowseCommand { get; internal set; }

        public ICommand ClearCommand { get; }

        public override void Refresh()
        {
            RaisePropertyChanged(nameof(Path));
            RaisePropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>
    /// A list of folders or names (search locations, exclusions). Folders are added with the folder picker, names by
    /// typing; each entry has a remove button. Edits write the list in the settings at once.
    /// </summary>
    public sealed class ListSetting : SettingItem
    {
        private readonly Func<List<string>> _list;
        private string _newEntry = string.Empty;

        public ListSetting(string title, string description, Symbol icon, bool isFolderList, string emptyText, Func<List<string>> list)
            : base(title, description, icon)
        {
            IsFolderList = isFolderList;
            EmptyText = emptyText;
            _list = list;
            Entries = new ObservableCollection<string>(list() ?? new List<string>());
            RemoveCommand = new RelayCommand(entry => Remove(entry as string));
            AddTypedCommand = new RelayCommand(_ => AddTyped(), _ => !string.IsNullOrWhiteSpace(_newEntry));
        }

        public bool IsFolderList { get; }

        public bool IsNameList => !IsFolderList;

        /// <summary>Shown when the list is empty ("Everything on this computer").</summary>
        public string EmptyText { get; }

        public ObservableCollection<string> Entries { get; }

        public bool IsEmpty => Entries.Count == 0;

        /// <summary>The name being typed (name lists).</summary>
        public string NewEntry
        {
            get => _newEntry;
            set
            {
                if (SetProperty(ref _newEntry, value ?? string.Empty))
                {
                    (AddTypedCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand RemoveCommand { get; }

        public ICommand AddTypedCommand { get; }

        /// <summary>Set by the view model for folder lists: opens the folder picker and adds the choice.</summary>
        public ICommand BrowseCommand { get; internal set; }

        /// <summary>Adds an entry unless the list has it already (case-insensitively). False when nothing was added.</summary>
        public bool Add(string entry)
        {
            entry = entry?.Trim();
            if (string.IsNullOrEmpty(entry) || Entries.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            List<string> list = _list();
            if (list == null)
            {
                return false;
            }

            list.Add(entry);
            Entries.Add(entry);
            RaisePropertyChanged(nameof(IsEmpty));
            OnChanged();
            return true;
        }

        public void Remove(string entry)
        {
            List<string> list = _list();
            if (entry == null || list == null)
            {
                return;
            }

            list.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
            Entries.Remove(entry);
            RaisePropertyChanged(nameof(IsEmpty));
            OnChanged();
        }

        private void AddTyped()
        {
            if (Add(NewEntry))
            {
                NewEntry = string.Empty;
            }
        }

        public override void Refresh()
        {
            List<string> list = _list() ?? new List<string>();
            if (!list.SequenceEqual(Entries))
            {
                Entries.Clear();
                foreach (string entry in list)
                {
                    Entries.Add(entry);
                }

                RaisePropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>A card with one or two buttons (rebuild the index, open the settings file, reset).</summary>
    public sealed class ActionSetting : SettingItem
    {
        public ActionSetting(string title, string description, Symbol icon, string buttonText, ICommand command)
            : base(title, description, icon)
        {
            ButtonText = buttonText;
            Command = command;
        }

        public string ButtonText { get; }

        public ICommand Command { get; }

        public string SecondaryButtonText { get; init; }

        public ICommand SecondaryCommand { get; init; }

        public bool HasSecondary => SecondaryCommand != null;

        /// <summary>Red button: the action discards something (activity data, every setting).</summary>
        public bool IsDanger { get; init; }

        /// <summary>Accent button: the main action of its page.</summary>
        public bool IsAccent { get; init; }
    }

    /// <summary>A read-only value (the settings file's location, the index status), re-read on refresh.</summary>
    public sealed class InfoSetting : SettingItem
    {
        private readonly Func<string> _value;

        public InfoSetting(string title, string description, Symbol icon, Func<string> value)
            : base(title, description, icon)
        {
            _value = value;
        }

        public string Value => _value();

        /// <summary>Optional button beside the value ("Copy").</summary>
        public string ButtonText { get; init; }

        public ICommand Command { get; init; }

        public bool HasButton => Command != null;

        public override void Refresh() => RaisePropertyChanged(nameof(Value));
    }

    /// <summary>The About page's header card: the app's icon, name, version and platform.</summary>
    public sealed class AboutSetting : SettingItem
    {
        public AboutSetting(string title, string version, string details)
            : base(title, version, Symbol.Info)
        {
            Version = version;
            Details = details;
        }

        public string Version { get; }

        public string Details { get; }
    }

    /// <summary>The About page's note from the author: a heading, a paragraph, a signature and a link (the project's page).</summary>
    public sealed class AuthorNoteSetting : SettingItem
    {
        public AuthorNoteSetting(string title, string message, string signature, string linkText, ICommand linkCommand)
            : base(title, message, Symbol.Heart)
        {
            Message = message;
            Signature = signature;
            LinkText = linkText;
            LinkCommand = linkCommand;
        }

        public string Message { get; }

        public string Signature { get; }

        public string LinkText { get; }

        public ICommand LinkCommand { get; }
    }

    /// <summary>A titled group of cards on a page ("Panels", "Navigation tree"…).</summary>
    public sealed class SettingGroup
    {
        public SettingGroup(string title, IReadOnlyList<SettingItem> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }

        public bool HasTitle => !string.IsNullOrEmpty(Title);

        public IReadOnlyList<SettingItem> Items { get; }
    }

    /// <summary>A page of the settings window: its place in the navigation and its groups.</summary>
    public sealed class SettingsPage
    {
        public SettingsPage(string key, string title, Symbol icon, IReadOnlyList<SettingGroup> groups)
        {
            Key = key;
            Title = title;
            Icon = icon;
            Groups = groups;
            foreach (SettingItem item in groups.SelectMany(g => g.Items))
            {
                item.PageTitle = title;
            }
        }

        public string Key { get; }

        public string Title { get; }

        public Symbol Icon { get; }

        public IReadOnlyList<SettingGroup> Groups { get; }

        public IEnumerable<SettingItem> Items => Groups.SelectMany(g => g.Items);
    }
}
