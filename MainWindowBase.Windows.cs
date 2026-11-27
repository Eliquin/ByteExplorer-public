// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Tabs;
using Serilog;

namespace ByteExplorer
{
    /// <summary>
    /// Several main windows in one process (Ctrl+N, "Open in new window"), as Explorer opens them: each window has its own
    /// tabs, panes and layout, and they all share the app's services (<see cref="AppServices"/>: one settings file, one
    /// search index, one operation queue). Not a second process, which would build the index again and write the settings
    /// file from two places. The app quits when the last main window closes; the settings window and dialogs never keep
    /// it running.
    /// </summary>
    public abstract partial class MainWindowBase
    {
        /// <summary>The open main windows, least recently active first (the active one is last).</summary>
        private static readonly List<MainWindowBase> s_openWindows = new List<MainWindowBase>();

        /// <summary>The place a window opened with Ctrl+N shows instead of the startup place (null: the first window).</summary>
        private string _initialLocation;

        /// <summary>Raised (on the UI thread) when the last open main window has closed: the app quits.</summary>
        internal static event EventHandler LastWindowClosed;

        /// <summary>The open main windows, least recently active first.</summary>
        internal static IReadOnlyList<MainWindowBase> OpenWindows => s_openWindows.ToArray();

        /// <summary>The main window used last (a later start's folder opens there); null when none is open.</summary>
        internal static MainWindowBase MostRecentlyActive => s_openWindows.Count == 0 ? null : s_openWindows[s_openWindows.Count - 1];

        /// <summary>
        /// The services for a new window: those of the open windows, or new ones for the first window. Only open windows
        /// count: a window made and never shown (a test checking which class a layout makes) keeps its own and shares
        /// them with nobody.
        /// </summary>
        private static AppServices ServicesForNewWindow(IUiDispatcher dispatcher) =>
            MostRecentlyActive?.ViewModel.Services is { IsDisposed: false } open ? open : AppServices.CreateForWindows(dispatcher);

        /// <summary>Whether another open main window shares this one's services (closing this one then doesn't quit).</summary>
        private bool HasOtherOpenWindows =>
            s_openWindows.Any(w => !ReferenceEquals(w, this) && !w._closed && ReferenceEquals(w.ViewModel.Services, ViewModel.Services));

        private void RegisterOpenWindow()
        {
            if (!s_openWindows.Contains(this))
            {
                s_openWindows.Add(this);
            }
        }

        /// <summary>Activated: this becomes the window launch requests and shared dialogs go to, and it catches up on the sidebar.</summary>
        private void OnMainWindowActivated()
        {
            if (_closed || !s_openWindows.Remove(this))
            {
                return;
            }

            s_openWindows.Add(this);
            ViewModel.MarkActive();
            if (HasOtherOpenWindows)
            {
                ViewModel.SyncSidebarWithSettings(); // another window may have pinned or unpinned something
            }
        }

        private void UnregisterOpenWindow()
        {
            if (!s_openWindows.Remove(this) || s_openWindows.Count > 0)
            {
                return;
            }

            // After the Closed handlers have run (a restart starts the new process from one of them).
            Dispatcher.UIThread.Post(() =>
            {
                if (s_openWindows.Count == 0)
                {
                    LastWindowClosed?.Invoke(null, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// Ctrl+N: a new main window of this layout showing the active pane's place (Explorer's behaviour), Home when it
        /// shows nothing yet.
        /// </summary>
        public MainWindowBase OpenNewWindow() => OpenNewWindow(TabLocation.Of(ActivePane) ?? TabLocation.Home);

        /// <summary>
        /// Opens a new main window of this layout showing <paramref name="location"/> (a folder, or Home / This PC). It is
        /// placed a little down and right of this one, the same size, so it doesn't hide exactly behind or over it.
        /// </summary>
        public MainWindowBase OpenNewWindow(string location)
        {
            try
            {
                var window = (MainWindowBase)Activator.CreateInstance(GetType());
                window._initialLocation = string.IsNullOrEmpty(location) ? TabLocation.Home : location;
                if (WindowState == WindowState.Normal)
                {
                    window.Width = Bounds.Width > 0 ? Bounds.Width : Width;
                    window.Height = Bounds.Height > 0 ? Bounds.Height : Height;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(Position.X + 32, Position.Y + 32);
                }
                else
                {
                    window.WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
                }

                window.Show();
                return window;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not open a new window at {Location}", location);
                ShowMessageAsync("Couldn't open a new window", ex.Message).Forget("new window failure message");
                return null;
            }
        }

        /// <summary>
        /// The other open main windows close (each saves nothing: the last one to close keeps the session), before a
        /// restart closes this one. Their close asks nothing: this window, closing last, asks about running operations.
        /// </summary>
        private void CloseOtherWindows()
        {
            foreach (MainWindowBase window in s_openWindows.ToArray())
            {
                if (!ReferenceEquals(window, this) && !window._closed)
                {
                    window.Close();
                }
            }
        }
    }
}
