// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.ComponentModel;
using ByteExplorer.Core.Models;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One vertical column in the macOS-style Columns (Miller) browser.
    /// Holds the folder path it represents and the items shown inside
    /// it, backed by the same lazy VirtualizingFileList every other
    /// view uses — a column no longer eagerly constructs a
    /// FileListItemViewModel for every entry up front (see
    /// FileListViewModel.BuildColumnPaneAsync), which used to mean a
    /// folder with tens of thousands of items froze the UI thread the
    /// moment Columns view (or a drill-down into it) was opened.
    ///
    /// SourceIndex is the column's own unfiltered DirectoryIndex —
    /// kept around so the status bar's filter box can re-derive a
    /// filtered VirtualizingFileList for just this column (see
    /// FileListViewModel.RebuildFocusedColumnFilterAsync) without
    /// re-enumerating the filesystem, and without disturbing sibling
    /// columns the way rebuilding the whole ColumnPanes collection
    /// from scratch would.
    /// </summary>
    public sealed class ColumnPane : INotifyPropertyChanged
    {
        private bool _isFocused;

        public ColumnPane(string path, string header, DirectoryIndex sourceIndex, VirtualizingFileList items)
        {
            Path = path;
            Header = header;
            SourceIndex = sourceIndex;
            Items = items;
        }

        public string Path { get; }
        public string Header { get; }

        /// <summary>The column's own unfiltered index — see class remarks.</summary>
        public DirectoryIndex SourceIndex { get; }

        public VirtualizingFileList Items { get; }

        /// <summary>
        /// Every item currently selected within this specific column
        /// (Extended selection — Ctrl/Shift-click all work per column
        /// now, not just a single item). Populated from code-behind's
        /// OnColumnSelectionChanged, which is also where this becomes
        /// the app's global SelectedItems whenever this column is the
        /// focused one — see FileListViewModel.OnColumnSelectionChanged.
        /// </summary>
        public ObservableCollection<FileListItemViewModel> SelectedItems { get; } = new ObservableCollection<FileListItemViewModel>();

        /// <summary>
        /// Whether this is the column the user most recently interacted
        /// with — drives the status bar's item count/selection summary,
        /// where the filter box applies, and where Paste/New Folder
        /// target. Visually highlighted in the column header (see
        /// FileListView.xaml) so it's never ambiguous which column is
        /// "active" when several are open side by side.
        /// </summary>
        public bool IsFocused
        {
            get => _isFocused;
            set
            {
                if (_isFocused == value)
                {
                    return;
                }

                _isFocused = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
