// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Tabs
{
    /// <summary>What a tab shows for a place: its title, the tip's text and its icon.</summary>
    public readonly struct TabDescription
    {
        public TabDescription(string title, string toolTip, Symbol icon)
        {
            Title = title;
            ToolTip = toolTip;
            Icon = icon;
        }

        public string Title { get; }
        public string ToolTip { get; }
        public Symbol Icon { get; }
    }

    /// <summary>
    /// One tab: a whole workspace, not just a folder. It owns its primary pane, its second pane while split, and which of
    /// the two has focus, so each tab keeps its own split view, view mode, selection, scroll position, history (Back and
    /// Forward) and search, as the Files app does with dual-pane tabs. The window's toolbar, address bar, status bar and
    /// details pane follow the selected tab's active pane (<see cref="MainViewModel.FileListViewModel"/>).
    /// <para>
    /// A tab restored from the last session sleeps (<see cref="IsSleeping"/>) until it is first selected: its panes
    /// exist but read nothing from disk, so twenty restored tabs cost one folder read at start, as Edge's sleeping tabs.
    /// </para>
    /// </summary>
    public sealed class TabViewModel : ViewModelBase
    {
        private readonly Func<string, TabDescription> _describe;
        private FileListViewModel _secondary;
        private bool _isSplitViewActive;
        private bool _isSecondaryPaneFocused;
        private bool _isSelected;
        private bool _isPinned;
        private bool _isLoading;
        private bool _isDropTarget;
        private string _title = "New tab";
        private string _toolTip = "New tab";
        private Symbol _icon = Symbol.TabDesktop;
        private string _pendingLocation;
        private string _pendingSecondLocation;
        private bool _pendingSecondFocused;

        internal TabViewModel(long id, FileListViewModel primary, Func<string, TabDescription> describe)
        {
            Id = id;
            Primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _describe = describe ?? throw new ArgumentNullException(nameof(describe));
            Primary.PropertyChanged += OnPanePropertyChanged;
            Refresh();
        }

        /// <summary>Unique for the window's lifetime (tests and the views tell tabs apart by it).</summary>
        public long Id { get; }

        /// <summary>The tab's left (or only) pane; never null while the tab is open.</summary>
        public FileListViewModel Primary { get; }

        /// <summary>The tab's right pane, while split; null otherwise.</summary>
        public FileListViewModel Secondary => _secondary;

        public bool IsSplitViewActive => _isSplitViewActive;

        /// <summary>The second pane has focus (only meaningful while split).</summary>
        public bool IsSecondaryPaneFocused
        {
            get => _isSecondaryPaneFocused;
            internal set
            {
                if (SetProperty(ref _isSecondaryPaneFocused, value))
                {
                    Refresh();
                }
            }
        }

        /// <summary>The pane the window acts on while this tab is selected.</summary>
        public FileListViewModel ActivePane => _isSplitViewActive && _isSecondaryPaneFocused && _secondary != null ? _secondary : Primary;

        public bool IsSelected
        {
            get => _isSelected;
            internal set => SetProperty(ref _isSelected, value);
        }

        /// <summary>Pinned tabs sit first, are narrow (icon only) in a horizontal bar, have no close button, and come back at every start.</summary>
        public bool IsPinned
        {
            get => _isPinned;
            internal set
            {
                if (SetProperty(ref _isPinned, value))
                {
                    RaisePropertyChanged(nameof(PinMenuText));
                }
            }
        }

        /// <summary>The text of the tab menu's pin item.</summary>
        public string PinMenuText => _isPinned ? "Unpin tab" : "Pin tab";

        public string Title
        {
            get => _title;
            private set => SetProperty(ref _title, value);
        }

        /// <summary>The tip: the full path of the active pane (and of the other pane, while split).</summary>
        public string ToolTip
        {
            get => _toolTip;
            private set => SetProperty(ref _toolTip, value);
        }

        public Symbol Icon
        {
            get => _icon;
            private set => SetProperty(ref _icon, value);
        }

        /// <summary>The active pane is reading its folder (the tab shows a spinner in place of its icon).</summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        /// <summary>Files are dragged over the tab (it shows where they will go).</summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set => SetProperty(ref _isDropTarget, value);
        }

        /// <summary>Restored but not shown yet: its panes haven't read their folders (see the class remarks).</summary>
        public bool IsSleeping => _pendingLocation != null;

        /// <summary>Closed: its panes are released; anything still holding the tab must leave it alone.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>Where the primary pane is (or will be, for a sleeping tab).</summary>
        public string Location => _pendingLocation ?? TabLocation.Of(Primary);

        /// <summary>Where the second pane is (or will be), while split; null otherwise.</summary>
        public string SecondLocation => _pendingLocation != null ? _pendingSecondLocation : _isSplitViewActive ? TabLocation.Of(_secondary) : null;

        /// <summary>Whether the tab is (or will be, once woken) split.</summary>
        public bool WantsSplit => _pendingLocation != null ? _pendingSecondLocation != null : _isSplitViewActive;

        /// <summary>Whether the second pane has (or will have) focus.</summary>
        public bool WantsSecondFocused => _pendingLocation != null ? _pendingSecondFocused : _isSplitViewActive && _isSecondaryPaneFocused;

        /// <summary>The search shown when the person left this tab (the window's search follows the selected tab).</summary>
        internal string SavedSearch { get; set; }

        /// <summary>The tab it was opened from (for "open next to it" placement); cleared when that tab closes.</summary>
        internal TabViewModel Opener { get; set; }

        internal void Sleep(string location, string secondLocation, bool secondFocused)
        {
            _pendingLocation = location;
            _pendingSecondLocation = string.IsNullOrEmpty(secondLocation) ? null : secondLocation;
            _pendingSecondFocused = secondFocused && _pendingSecondLocation != null;
            RaisePropertyChanged(nameof(IsSleeping));
            Refresh();
        }

        /// <summary>Takes the places a sleeping tab is to show; the tab is awake from then on.</summary>
        internal (string Location, string SecondLocation, bool SecondFocused) TakePending()
        {
            var pending = (_pendingLocation, _pendingSecondLocation, _pendingSecondFocused);
            _pendingLocation = null;
            _pendingSecondLocation = null;
            _pendingSecondFocused = false;
            RaisePropertyChanged(nameof(IsSleeping));
            return pending;
        }

        /// <summary>Shows a second pane (already loaded, so it appears with its folder in it).</summary>
        internal void AttachSecondary(FileListViewModel pane)
        {
            if (pane == null || _secondary != null)
            {
                throw new InvalidOperationException("The tab is already split.");
            }

            // Same order as before tabs: the pane first, then the split, so a window laying out the split finds the pane.
            _secondary = pane;
            pane.PropertyChanged += OnPanePropertyChanged;
            RaisePropertyChanged(nameof(Secondary));
            _isSplitViewActive = true;
            RaisePropertyChanged(nameof(IsSplitViewActive));
            RaisePropertyChanged(nameof(ActivePane));
            Refresh();
        }

        /// <summary>Ends the split; returns the second pane for the caller to release.</summary>
        internal FileListViewModel DetachSecondary()
        {
            FileListViewModel pane = _secondary;
            if (pane == null)
            {
                return null;
            }

            IsSecondaryPaneFocused = false;
            _isSplitViewActive = false;
            RaisePropertyChanged(nameof(IsSplitViewActive));
            pane.PropertyChanged -= OnPanePropertyChanged;
            _secondary = null;
            RaisePropertyChanged(nameof(Secondary));
            RaisePropertyChanged(nameof(ActivePane));
            Refresh();
            return pane;
        }

        /// <summary>Marks the tab closed and unhooks it from its panes; returns them for the caller to release.</summary>
        internal (FileListViewModel Primary, FileListViewModel Secondary) Close()
        {
            IsClosed = true;
            Primary.PropertyChanged -= OnPanePropertyChanged;
            if (_secondary != null)
            {
                _secondary.PropertyChanged -= OnPanePropertyChanged;
            }

            Opener = null;
            return (Primary, _secondary);
        }

        /// <summary>Re-reads the title, tip and icon (a pane moved, the drive labels changed).</summary>
        internal void Refresh()
        {
            if (IsClosed)
            {
                return;
            }

            FileListViewModel active = ActivePane;
            string location = _pendingLocation != null
                ? (_pendingSecondFocused && _pendingSecondLocation != null ? _pendingSecondLocation : _pendingLocation)
                : TabLocation.Of(active);

            IsLoading = _pendingLocation == null && active.IsLoading;
            if (location == null)
            {
                Title = "New tab";
                ToolTip = "New tab";
                Icon = Symbol.TabDesktop;
                return;
            }

            TabDescription description = _describe(location);
            Title = description.Title;
            Icon = description.Icon;

            string other = _pendingLocation != null
                ? (_pendingSecondLocation == null ? null : _pendingSecondFocused ? _pendingLocation : _pendingSecondLocation)
                : _isSplitViewActive ? TabLocation.Of(ReferenceEquals(active, Primary) ? _secondary : Primary) : null;
            ToolTip = other == null
                ? description.ToolTip
                : description.ToolTip + "\n" + _describe(other).ToolTip + "  (other pane)";
        }

        private void OnPanePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(FileListViewModel.CurrentPath):
                case nameof(FileListViewModel.IsHomeView):
                case nameof(FileListViewModel.IsThisPcView):
                case nameof(FileListViewModel.IsTrashView):
                case nameof(FileListViewModel.IsLoading):
                case null:
                case "":
                    Refresh();
                    break;
            }
        }

        public override string ToString() => Title;
    }
}
