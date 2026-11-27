// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Tabs
{
    /// <summary>The selected tab changed (old is null for the first tab, and for a selected tab that was closed).</summary>
    public sealed class TabSelectionChangedEventArgs : EventArgs
    {
        public TabSelectionChangedEventArgs(TabViewModel oldTab, TabViewModel newTab)
        {
            OldTab = oldTab;
            NewTab = newTab;
        }

        public TabViewModel OldTab { get; }
        public TabViewModel NewTab { get; }
    }

    /// <summary>
    /// The window's tabs: the list, the selected tab, and everything done to tabs (open, close, reopen, duplicate, pin,
    /// move, the keyboard's next and previous), with the rules the major browsers and Windows Explorer settled on:
    /// <list type="bullet">
    /// <item>There is always at least one tab. Closing the only tab leaves a new tab in its place.</item>
    /// <item>Pinned tabs come first; a tab can't be moved across the line between pinned and unpinned tabs (pinning moves
    /// it to the end of the pinned ones, unpinning to the start of the others).</item>
    /// <item>A new tab (Ctrl+T, +) goes at the end; a folder opened in a new tab goes right after the tab it came from, after
    /// the tabs already opened from that tab, so a row of folders opened one after another stays in order (Chrome's
    /// rule); a duplicate goes right after its original.</item>
    /// <item>Closing the selected tab selects the one after it, or before it when it was the last (Explorer, Edge).</item>
    /// <item>Closed tabs can be reopened (Ctrl+Shift+T), newest first, where they were, with their split and panes'
    /// folders; the last <see cref="MaxClosedTabs"/> are kept.</item>
    /// <item>"Close other tabs" and "Close tabs to the right/left" leave pinned tabs open (Edge).</item>
    /// </list>
    /// </summary>
    public sealed class TabsViewModel : ViewModelBase, IDisposable
    {
        public const int MaxClosedTabs = 25;

        private readonly ITabHost _host;
        private readonly Func<TabSettings> _settings;
        private readonly Action _saveSettings;
        private readonly ObservableCollection<TabViewModel> _tabs = new ObservableCollection<TabViewModel>();
        private readonly List<TabSessionEntry> _closed = new List<TabSessionEntry>();
        private readonly List<int> _closedIndexes = new List<int>();
        private TabViewModel _selected;
        private long _nextId = 1;
        private bool _disposed;
        private Task _lastWake = Task.CompletedTask;

        public TabsViewModel(ITabHost host, Func<TabSettings> settings, Action saveSettings)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _settings = settings ?? (() => null);
            _saveSettings = saveSettings ?? (() => { });
            Tabs = new ReadOnlyObservableCollection<TabViewModel>(_tabs);

            NewTabCommand = new RelayCommand(_ => NewTabAsync().Forget("new tab"));
            CloseTabCommand = new RelayCommand(p => CloseTab(Resolve(p)), p => Resolve(p) != null);
            CloseOtherTabsCommand = new RelayCommand(p => CloseOthers(Resolve(p)), p => Resolve(p) is TabViewModel t && _tabs.Any(o => o != t && !o.IsPinned));
            CloseTabsAfterCommand = new RelayCommand(p => CloseAfter(Resolve(p)), p => Resolve(p) is TabViewModel t && _tabs.Skip(_tabs.IndexOf(t) + 1).Any(o => !o.IsPinned));
            CloseTabsBeforeCommand = new RelayCommand(p => CloseBefore(Resolve(p)), p => Resolve(p) is TabViewModel t && _tabs.Take(_tabs.IndexOf(t)).Any(o => !o.IsPinned));
            DuplicateTabCommand = new RelayCommand(p => DuplicateAsync(Resolve(p)).Forget("duplicate tab"), p => Resolve(p) != null);
            ReopenClosedTabCommand = new RelayCommand(_ => ReopenClosedTabAsync().Forget("reopen closed tab"), _ => _closed.Count > 0);
            TogglePinCommand = new RelayCommand(p => TogglePin(Resolve(p)), p => Resolve(p) != null);
            NextTabCommand = new RelayCommand(_ => SelectRelative(1), _ => _tabs.Count > 1);
            PreviousTabCommand = new RelayCommand(_ => SelectRelative(-1), _ => _tabs.Count > 1);
            MoveTabForwardCommand = new RelayCommand(p => MoveRelative(Resolve(p), 1), p => CanMoveRelative(Resolve(p), 1));
            MoveTabBackCommand = new RelayCommand(p => MoveRelative(Resolve(p), -1), p => CanMoveRelative(Resolve(p), -1));
            SetPlacementCommand = new RelayCommand(p => Placement = p as string);
            ToggleCollapsedCommand = new RelayCommand(_ => IsCollapsed = !IsCollapsed);

            _tabs.CollectionChanged += (_, _) =>
            {
                RaisePropertyChanged(nameof(Count));
                RaisePropertyChanged(nameof(IsStripVisible));
            };

            // The window always has a tab: an empty one now, which the startup location (or the last session) fills.
            TabViewModel first = CreateTab(null);
            _tabs.Add(first);
            Select(first);
        }

        /// <summary>The tabs, in order (pinned first).</summary>
        public ReadOnlyObservableCollection<TabViewModel> Tabs { get; }

        public int Count => _tabs.Count;

        /// <summary>Raised after the selection moved, before a sleeping tab starts loading its folders.</summary>
        public event EventHandler<TabSelectionChangedEventArgs> SelectedTabChanged;

        /// <summary>Raised when a tab is gone for good (after its panes are released).</summary>
        public event EventHandler<TabViewModel> TabClosed;

        public TabViewModel SelectedTab
        {
            get => _selected;
            set
            {
                if (value != null && _tabs.Contains(value))
                {
                    Select(value);
                }
            }
        }

        public ICommand NewTabCommand { get; }
        public ICommand CloseTabCommand { get; }
        public ICommand CloseOtherTabsCommand { get; }
        public ICommand CloseTabsAfterCommand { get; }
        public ICommand CloseTabsBeforeCommand { get; }
        public ICommand DuplicateTabCommand { get; }
        public ICommand ReopenClosedTabCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand NextTabCommand { get; }
        public ICommand PreviousTabCommand { get; }
        public ICommand MoveTabForwardCommand { get; }
        public ICommand MoveTabBackCommand { get; }

        /// <summary>Moves the tab bar: parameter "Top", "TitleBar", "Left" or "Right".</summary>
        public ICommand SetPlacementCommand { get; }

        /// <summary>Vertical tabs: shrink to icons, or back.</summary>
        public ICommand ToggleCollapsedCommand { get; }

        public bool CanReopenClosedTab => _closed.Count > 0;

        // ------------------------------------------------------------------ placement and look

        /// <summary><see cref="TabSettings.PlacementTop"/>, <see cref="TabSettings.PlacementLeft"/> or <see cref="TabSettings.PlacementRight"/>.</summary>
        public string Placement
        {
            get => _settings()?.Placement ?? TabSettings.PlacementTop;
            set
            {
                TabSettings settings = _settings();
                if (settings == null || string.IsNullOrEmpty(value) || value == settings.Placement || !TabSettings.IsPlacement(value))
                {
                    return;
                }

                settings.Placement = value;
                RaisePlacementChanged();
                _saveSettings();
            }
        }

        public bool IsVertical => Placement == TabSettings.PlacementLeft || Placement == TabSettings.PlacementRight;
        public bool IsTop => Placement == TabSettings.PlacementTop;

        /// <summary>In the title bar (a window without one shows the tabs on top instead).</summary>
        public bool IsTitleBar => Placement == TabSettings.PlacementTitleBar;
        public bool IsLeft => Placement == TabSettings.PlacementLeft;
        public bool IsRight => Placement == TabSettings.PlacementRight;

        /// <summary>Vertical tabs shrunk to their icons.</summary>
        public bool IsCollapsed
        {
            get => IsVertical && (_settings()?.VerticalCollapsed ?? false);
            set
            {
                TabSettings settings = _settings();
                if (settings == null || settings.VerticalCollapsed == value)
                {
                    return;
                }

                settings.VerticalCollapsed = value;
                RaisePropertyChanged();
                _saveSettings();
            }
        }

        /// <summary>The vertical bar's width (expanded).</summary>
        public double VerticalWidth
        {
            get => _settings()?.VerticalWidth ?? TabSettings.DefaultVerticalWidth;
            set
            {
                TabSettings settings = _settings();
                double clamped = double.IsNaN(value) ? TabSettings.DefaultVerticalWidth : Math.Clamp(value, TabSettings.MinVerticalWidth, TabSettings.MaxVerticalWidth);
                if (settings == null || Math.Abs(settings.VerticalWidth - clamped) < 0.5)
                {
                    return;
                }

                settings.VerticalWidth = clamped;
                RaisePropertyChanged();
                _saveSettings();
            }
        }

        /// <summary>Whether the bar is shown: always, or (setting) only once there's a second tab.</summary>
        public bool IsStripVisible => (_settings()?.ShowWithOneTab ?? true) || _tabs.Count > 1;

        /// <summary>"Close tabs to the right" / "below", following the bar's direction.</summary>
        public string CloseAfterText => IsVertical ? "Close tabs below" : "Close tabs to the right";

        public string CloseBeforeText => IsVertical ? "Close tabs above" : "Close tabs to the left";

        /// <summary>The settings changed elsewhere (the settings window): everything that reads them is raised again.</summary>
        public void NotifySettingsChanged() => RaisePlacementChanged();

        private void RaisePlacementChanged()
        {
            RaisePropertyChanged(nameof(Placement));
            RaisePropertyChanged(nameof(IsVertical));
            RaisePropertyChanged(nameof(IsTop));
            RaisePropertyChanged(nameof(IsTitleBar));
            RaisePropertyChanged(nameof(IsLeft));
            RaisePropertyChanged(nameof(IsRight));
            RaisePropertyChanged(nameof(IsCollapsed));
            RaisePropertyChanged(nameof(VerticalWidth));
            RaisePropertyChanged(nameof(IsStripVisible));
            RaisePropertyChanged(nameof(CloseAfterText));
            RaisePropertyChanged(nameof(CloseBeforeText));
        }

        // ------------------------------------------------------------------ queries

        /// <summary>Every pane of every tab (both panes of a split tab).</summary>
        public IEnumerable<FileListViewModel> AllPanes()
        {
            foreach (TabViewModel tab in _tabs.ToArray())
            {
                yield return tab.Primary;
                if (tab.Secondary != null)
                {
                    yield return tab.Secondary;
                }
            }
        }

        public int PinnedCount
        {
            get
            {
                int count = 0;
                while (count < _tabs.Count && _tabs[count].IsPinned)
                {
                    count++;
                }

                return count;
            }
        }

        /// <summary>The tab for a command parameter: that tab when it's open, the selected one for none.</summary>
        private TabViewModel Resolve(object parameter)
        {
            TabViewModel tab = parameter as TabViewModel ?? _selected;
            return tab != null && !tab.IsClosed && _tabs.Contains(tab) ? tab : null;
        }

        /// <summary>Re-reads every tab's title (drive labels changed, folder options changed).</summary>
        public void RefreshTitles()
        {
            foreach (TabViewModel tab in _tabs)
            {
                tab.Refresh();
            }
        }

        // ------------------------------------------------------------------ selection

        public void Select(TabViewModel tab)
        {
            if (tab == null || tab.IsClosed || ReferenceEquals(tab, _selected))
            {
                return;
            }

            TabViewModel old = _selected;
            if (old != null)
            {
                old.IsSelected = false;
            }

            _selected = tab;
            tab.IsSelected = true;
            RaisePropertyChanged(nameof(SelectedTab));
            SelectedTabChanged?.Invoke(this, new TabSelectionChangedEventArgs(old, tab));
            if (tab.IsSleeping)
            {
                Task wake = WakeAsync(tab);
                wake.Forget("wake a restored tab");
                _lastWake = wake;
            }
        }

        /// <summary>Ctrl+1 … Ctrl+8 select that tab; Ctrl+9 the last one (as in every browser).</summary>
        public void SelectByNumber(int number)
        {
            if (_tabs.Count == 0 || number < 1 || number > 9)
            {
                return;
            }

            Select(number == 9 ? _tabs[^1] : number <= _tabs.Count ? _tabs[number - 1] : null);
        }

        public void SelectRelative(int step)
        {
            if (_tabs.Count < 2 || _selected == null)
            {
                return;
            }

            int index = (_tabs.IndexOf(_selected) + step) % _tabs.Count;
            Select(_tabs[index < 0 ? index + _tabs.Count : index]);
        }

        // ------------------------------------------------------------------ opening

        /// <summary>A new tab at the end (Ctrl+T, +), showing what the setting says, and selected.</summary>
        public Task<TabViewModel> NewTabAsync()
        {
            TabSettings settings = _settings();
            string location = settings?.NewTabOpens switch
            {
                TabSettings.NewTabThisPc => TabLocation.ThisPc,
                TabSettings.NewTabCurrentFolder => _selected?.Location ?? TabLocation.Home,
                _ => TabLocation.Home
            };
            return OpenAsync(location, null, select: true, insertAt: _tabs.Count);
        }

        /// <summary>
        /// Opens a place in a new tab next to the selected one (a middle click, "Open in new tab"); behind it unless the
        /// setting says to switch, or <paramref name="select"/> asks for it.
        /// </summary>
        public Task<TabViewModel> OpenInNewTabAsync(string location, bool? select = null)
        {
            if (!TabLocation.IsValid(location))
            {
                return Task.FromResult<TabViewModel>(null);
            }

            TabViewModel opener = _selected;
            bool show = select ?? (_settings()?.SwitchToNewTab ?? false);
            int index = opener == null ? _tabs.Count : _tabs.IndexOf(opener) + 1;
            while (index < _tabs.Count && ReferenceEquals(_tabs[index].Opener, opener))
            {
                index++;
            }

            return OpenAsync(location, opener, show, index);
        }

        /// <summary>Opens several places in tabs, in their order (the menu's "Open in new tabs" for a selection of folders).</summary>
        public async Task OpenInNewTabsAsync(IReadOnlyList<string> locations)
        {
            if (locations == null)
            {
                return;
            }

            foreach (string location in locations)
            {
                if (await OpenInNewTabAsync(location, select: false).ConfigureAwait(true) == null && _tabs.Count >= TabSettings.MaxTabs)
                {
                    break;
                }
            }
        }

        private async Task<TabViewModel> OpenAsync(string location, TabViewModel opener, bool select, int insertAt)
        {
            if (!HasRoomForAnotherTab())
            {
                return null;
            }

            TabViewModel tab = CreateTab(opener?.ActivePane ?? _selected?.ActivePane);
            tab.Opener = opener;
            Insert(tab, insertAt);
            if (select)
            {
                Select(tab);
            }

            await _host.LoadAsync(tab.Primary, location, fallBackToHome: false).ConfigureAwait(true);
            return tab;
        }

        private bool HasRoomForAnotherTab()
        {
            if (_tabs.Count < TabSettings.MaxTabs)
            {
                return true;
            }

            _host.ShowMessage($"A window holds up to {TabSettings.MaxTabs} tabs. Close some tabs to open more.");
            return false;
        }

        /// <summary>A copy of the tab right after it: the same folders, split and view (pinned tabs' copies aren't pinned).</summary>
        public async Task<TabViewModel> DuplicateAsync(TabViewModel source)
        {
            if (source == null || source.IsClosed || !HasRoomForAnotherTab())
            {
                return null;
            }

            string location = source.Location ?? TabLocation.Home;
            string second = source.WantsSplit ? source.SecondLocation : null;
            bool secondFocused = source.WantsSecondFocused;
            TabViewModel tab = CreateTab(source.Primary);
            tab.Opener = source;
            Insert(tab, _tabs.IndexOf(source) + 1);
            Select(tab);
            await LoadTabAsync(tab, location, second, secondFocused, source.Secondary, fallBackToHome: false).ConfigureAwait(true);
            return tab;
        }

        // ------------------------------------------------------------------ closing

        public void CloseTab(TabViewModel tab)
        {
            if (tab == null || tab.IsClosed || !_tabs.Contains(tab))
            {
                return;
            }

            if (_tabs.Count == 1)
            {
                // The window always keeps a tab: the only one becomes a new tab (unless it already is one).
                string fresh = (_settings()?.NewTabOpens) == TabSettings.NewTabThisPc ? TabLocation.ThisPc : TabLocation.Home;
                if (tab.WantsSplit || tab.Location != fresh || tab.IsPinned)
                {
                    Remember(tab, 0);
                    TabViewModel replacement = CreateTab(tab.Primary);
                    _tabs.Add(replacement);
                    Select(replacement);
                    Release(tab);
                    _host.LoadAsync(replacement.Primary, fresh, fallBackToHome: false).Forget("load a new tab");
                }

                return;
            }

            int index = _tabs.IndexOf(tab);
            Remember(tab, index);
            if (ReferenceEquals(tab, _selected))
            {
                Select(index + 1 < _tabs.Count ? _tabs[index + 1] : _tabs[index - 1]);
            }

            Release(tab);
        }

        private void CloseOthers(TabViewModel keep)
        {
            if (keep == null)
            {
                return;
            }

            Select(keep);
            foreach (TabViewModel tab in _tabs.Where(t => t != keep && !t.IsPinned).Reverse().ToArray())
            {
                CloseTab(tab);
            }
        }

        private void CloseAfter(TabViewModel tab)
        {
            if (tab == null)
            {
                return;
            }

            int index = _tabs.IndexOf(tab);
            if (_selected != null && _tabs.IndexOf(_selected) > index && !_selected.IsPinned)
            {
                Select(tab);
            }

            foreach (TabViewModel other in _tabs.Skip(index + 1).Where(t => !t.IsPinned).Reverse().ToArray())
            {
                CloseTab(other);
            }
        }

        private void CloseBefore(TabViewModel tab)
        {
            if (tab == null)
            {
                return;
            }

            int index = _tabs.IndexOf(tab);
            if (_selected != null && _tabs.IndexOf(_selected) < index && !_selected.IsPinned)
            {
                Select(tab);
            }

            foreach (TabViewModel other in _tabs.Take(index).Where(t => !t.IsPinned).Reverse().ToArray())
            {
                CloseTab(other);
            }
        }

        private void Remember(TabViewModel tab, int index)
        {
            string location = tab.Location;
            if (location == null)
            {
                return; // a tab that never showed anything: nothing to bring back
            }

            _closed.Add(new TabSessionEntry
            {
                Location = location,
                SecondLocation = tab.WantsSplit ? tab.SecondLocation ?? string.Empty : string.Empty,
                Split = tab.WantsSplit && !string.IsNullOrEmpty(tab.SecondLocation),
                SecondFocused = tab.WantsSecondFocused,
                Pinned = tab.IsPinned
            });
            _closedIndexes.Add(index);
            if (_closed.Count > MaxClosedTabs)
            {
                _closed.RemoveAt(0);
                _closedIndexes.RemoveAt(0);
            }

            RaisePropertyChanged(nameof(CanReopenClosedTab));
        }

        private void Release(TabViewModel tab)
        {
            _tabs.Remove(tab);
            foreach (TabViewModel other in _tabs)
            {
                if (ReferenceEquals(other.Opener, tab))
                {
                    other.Opener = null;
                }
            }

            (FileListViewModel primary, FileListViewModel secondary) = tab.Close();
            if (secondary != null)
            {
                _host.ReleasePane(secondary);
            }

            _host.ReleasePane(primary);
            TabClosed?.Invoke(this, tab);
        }

        /// <summary>Brings back the most recently closed tab where it was, selected.</summary>
        public async Task<TabViewModel> ReopenClosedTabAsync()
        {
            if (_closed.Count == 0 || !HasRoomForAnotherTab())
            {
                return null;
            }

            TabSessionEntry entry = _closed[^1];
            int index = _closedIndexes[^1];
            _closed.RemoveAt(_closed.Count - 1);
            _closedIndexes.RemoveAt(_closedIndexes.Count - 1);
            RaisePropertyChanged(nameof(CanReopenClosedTab));

            TabViewModel tab = CreateTab(_selected?.ActivePane);
            tab.IsPinned = entry.Pinned;
            Insert(tab, index);
            Select(tab);
            await LoadTabAsync(tab, entry.Location, entry.Split ? entry.SecondLocation : null, entry.SecondFocused, null, fallBackToHome: true).ConfigureAwait(true);
            return tab;
        }

        // ------------------------------------------------------------------ pinning and moving

        public void TogglePin(TabViewModel tab)
        {
            if (tab == null || tab.IsClosed)
            {
                return;
            }

            int from = _tabs.IndexOf(tab);
            int pinned = PinnedCount;
            if (tab.IsPinned)
            {
                tab.IsPinned = false;
                _tabs.Move(from, pinned - 1); // the first unpinned tab
            }
            else
            {
                tab.IsPinned = true;
                _tabs.Move(from, pinned); // the last pinned tab
            }

            _saveSettings(); // pinned tabs come back at every start
        }

        /// <summary>Moves a tab to <paramref name="index"/>, kept on its side of the pinned/unpinned line (dragging a tab).</summary>
        public void Move(TabViewModel tab, int index)
        {
            if (tab == null || tab.IsClosed)
            {
                return;
            }

            int from = _tabs.IndexOf(tab);
            if (from < 0)
            {
                return;
            }

            int pinned = PinnedCount;
            int to = tab.IsPinned ? Math.Clamp(index, 0, pinned - 1) : Math.Clamp(index, pinned, _tabs.Count - 1);
            if (to != from)
            {
                _tabs.Move(from, to);
                tab.Opener = null; // placed by hand: no longer part of an opener's row
            }
        }

        private bool CanMoveRelative(TabViewModel tab, int step)
        {
            if (tab == null)
            {
                return false;
            }

            int to = _tabs.IndexOf(tab) + step;
            int pinned = PinnedCount;
            return tab.IsPinned ? to >= 0 && to < pinned : to >= pinned && to < _tabs.Count;
        }

        private void MoveRelative(TabViewModel tab, int step)
        {
            if (CanMoveRelative(tab, step))
            {
                Move(tab, _tabs.IndexOf(tab) + step);
            }
        }

        private void Insert(TabViewModel tab, int index)
        {
            int pinned = PinnedCount;
            int at = tab.IsPinned ? Math.Clamp(index, 0, pinned) : Math.Clamp(index, pinned, _tabs.Count);
            _tabs.Insert(at, tab);
        }

        // ------------------------------------------------------------------ session

        /// <summary>The tabs as they are now, for the settings (and the index of the selected one).</summary>
        public List<TabSessionEntry> CaptureSession(out int selectedIndex)
        {
            var entries = new List<TabSessionEntry>(_tabs.Count);
            selectedIndex = 0;
            foreach (TabViewModel tab in _tabs)
            {
                string location = tab.Location;
                if (location == null)
                {
                    continue;
                }

                if (ReferenceEquals(tab, _selected))
                {
                    selectedIndex = entries.Count;
                }

                // A place inside a password-protected item is kept as the folder holding it (it is locked at the next
                // start, and its names stay out of the settings file).
                location = Core.Security.ProtectedFormat.OutsideProtected(location);
                string second = tab.WantsSplit ? tab.SecondLocation : null;
                second = second == null ? null : Core.Security.ProtectedFormat.OutsideProtected(second);
                entries.Add(new TabSessionEntry
                {
                    Location = location,
                    SecondLocation = second ?? string.Empty,
                    Split = !string.IsNullOrEmpty(second),
                    SecondFocused = tab.WantsSecondFocused && !string.IsNullOrEmpty(second),
                    Pinned = tab.IsPinned
                });
            }

            return entries;
        }

        /// <summary>
        /// Puts back saved tabs in place of the window's tabs: each sleeps until it is first selected; the selected one
        /// loads now. Pinned entries stay first whatever order the file has. Places that are gone show Home.
        /// </summary>
        public Task RestoreAsync(IReadOnlyList<TabSessionEntry> entries, int selectedIndex)
        {
            var valid = (entries ?? Array.Empty<TabSessionEntry>())
                .Where(e => e != null && TabLocation.IsValid(e.Location))
                .Take(TabSettings.MaxTabs)
                .ToList();
            if (valid.Count == 0)
            {
                return Task.CompletedTask;
            }

            TabSessionEntry selectedEntry = valid[Math.Clamp(selectedIndex, 0, valid.Count - 1)];
            valid = valid.Where(e => e.Pinned).Concat(valid.Where(e => !e.Pinned)).ToList();

            TabViewModel[] old = _tabs.ToArray();
            TabViewModel toSelect = null;
            foreach (TabSessionEntry entry in valid)
            {
                TabViewModel tab = CreateTab(_selected?.ActivePane);
                tab.IsPinned = entry.Pinned;
                string second = entry.Split && TabLocation.IsValid(entry.SecondLocation) ? entry.SecondLocation : null;
                tab.Sleep(entry.Location, second, entry.SecondFocused);
                _tabs.Add(tab);
                if (ReferenceEquals(entry, selectedEntry))
                {
                    toSelect = tab;
                }
            }

            _lastWake = Task.CompletedTask;
            Select(toSelect ?? _tabs[old.Length]);
            Task wake = _lastWake;
            foreach (TabViewModel tab in old)
            {
                Release(tab);
            }

            return wake; // the selected tab's folders (the others load when first selected)
        }

        /// <summary>Loads a sleeping tab's folders (its first selection).</summary>
        private async Task WakeAsync(TabViewModel tab)
        {
            (string location, string second, bool secondFocused) = tab.TakePending();
            await LoadTabAsync(tab, location, second, secondFocused, null, fallBackToHome: true).ConfigureAwait(true);
        }

        /// <summary>Shows a place in a tab's primary pane, and splits it with a second place when given.</summary>
        private async Task LoadTabAsync(TabViewModel tab, string location, string secondLocation, bool secondFocused,
            FileListViewModel secondViewFrom, bool fallBackToHome)
        {
            Task primary = _host.LoadAsync(tab.Primary, location ?? TabLocation.Home, fallBackToHome);
            if (!string.IsNullOrEmpty(secondLocation))
            {
                await SplitAsync(tab, secondLocation, secondViewFrom, fallBackToHome).ConfigureAwait(true);
                if (secondFocused && !tab.IsClosed && tab.IsSplitViewActive)
                {
                    tab.IsSecondaryPaneFocused = true;
                }
            }

            await primary.ConfigureAwait(true);
        }

        /// <summary>
        /// Splits a tab: a second pane showing <paramref name="location"/> (default: the primary pane's place), with the view
        /// of <paramref name="viewFrom"/> (default: the primary pane's). The pane is loaded before it is shown. Returns false
        /// when the tab closed or was split meanwhile (the new pane is released).
        /// </summary>
        public async Task<bool> SplitAsync(TabViewModel tab, string location = null, FileListViewModel viewFrom = null, bool fallBackToHome = false)
        {
            if (tab == null || tab.IsClosed || tab.IsSplitViewActive)
            {
                return false;
            }

            FileListViewModel pane = _host.CreatePane(viewFrom ?? tab.Primary);
            string place = location ?? TabLocation.Of(tab.Primary);
            try
            {
                if (place != null)
                {
                    await _host.LoadAsync(pane, place, fallBackToHome).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "Failed to load the second pane's folder");
            }

            if (tab.IsClosed || tab.IsSplitViewActive)
            {
                _host.ReleasePane(pane);
                return false;
            }

            tab.AttachSecondary(pane);
            return true;
        }

        /// <summary>Ends a tab's split; its second pane is released.</summary>
        public void Unsplit(TabViewModel tab)
        {
            FileListViewModel pane = tab?.DetachSecondary();
            if (pane != null)
            {
                _host.ReleasePane(pane);
            }
        }

        // ------------------------------------------------------------------ plumbing

        private TabViewModel CreateTab(FileListViewModel viewFrom)
        {
            FileListViewModel pane = _host.CreatePane(viewFrom);
            return new TabViewModel(_nextId++, pane, _host.Describe);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (TabViewModel tab in _tabs.ToArray())
            {
                (FileListViewModel primary, FileListViewModel secondary) = tab.Close();
                if (secondary != null)
                {
                    _host.ReleasePane(secondary);
                }

                _host.ReleasePane(primary);
            }

            _tabs.Clear();
        }
    }
}
