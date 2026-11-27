// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading.Tasks;

namespace ByteExplorer.ViewModels.Tabs
{
    /// <summary>
    /// What the tabs need from the window's view model (<see cref="MainViewModel"/>): panes made and wired the way the
    /// window's other panes are, places loaded into them, and how a place reads in a tab.
    /// </summary>
    public interface ITabHost
    {
        /// <summary>
        /// A new pane, wired to the app's services (operations, pins, dialogs, the search index) and showing the view mode
        /// of <paramref name="viewFrom"/> (null: the saved default view).
        /// </summary>
        FileListViewModel CreatePane(FileListViewModel viewFrom);

        /// <summary>Unwires and disposes a pane of a closed tab or an ended split.</summary>
        void ReleasePane(FileListViewModel pane);

        /// <summary>
        /// Shows <paramref name="location"/> (a <see cref="TabLocation"/>) in the pane. With <paramref name="fallBackToHome"/>,
        /// a folder that's gone or doesn't answer within a moment (an unplugged drive, a sleeping share, a phone) shows Home
        /// instead: used for places that come from the settings, not for a place the person just chose.
        /// </summary>
        Task LoadAsync(FileListViewModel pane, string location, bool fallBackToHome);

        /// <summary>How a place reads in a tab.</summary>
        TabDescription Describe(string location);

        /// <summary>Tells the person something about the tabs (too many tabs).</summary>
        void ShowMessage(string message);
    }
}
