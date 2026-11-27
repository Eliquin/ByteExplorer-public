// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;
using ByteExplorer.ViewModels.Omnibar;
using ByteExplorer.ViewModels.Tabs;
using ByteExplorer.Views;

namespace ByteExplorer
{
    /// <summary>
    /// The window's part of the tabs: where the bar is docked, the keyboard shortcuts (the browsers' and Explorer's), the
    /// omnibar's tab commands, and the keyboard going to the file list after a tab is chosen.
    /// </summary>
    public abstract partial class MainWindowBase
    {
        private void AttachTabs()
        {
            if (_parts.TabStrip == null)
            {
                return;
            }

            _parts.TabStrip.Main = ViewModel;
            _parts.TabStrip.SupportsTitleBar = _parts.TitleBarTabHost != null;
            _parts.TabStrip.ContentFocusRequested += (_, _) => FocusActivePaneSoon();
            ViewModel.Tabs.PropertyChanged += OnTabsPropertyChanged;
            ApplyTabPlacement();

            // Tunnel, before any control sees the key: Ctrl+Tab would otherwise move the keyboard focus, and the list would
            // take Ctrl+PageDown for scrolling.
            AddHandler(KeyDownEvent, OnTabKeyDown, RoutingStrategies.Tunnel);

            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("New tab", "tab open add", Symbol.TabAdd, Modifier + "+T", null,
                () => ViewModel.Tabs.NewTabCommand.Execute(null)));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Reopen closed tab", "tab restore undo close", Symbol.TabDesktopArrowClockwise, Modifier + "+Shift+T",
                () => ViewModel.Tabs.CanReopenClosedTab, () => ViewModel.Tabs.ReopenClosedTabCommand.Execute(null)));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Duplicate tab", "tab copy clone", Symbol.TabDesktopCopy, null, null,
                () => ViewModel.Tabs.DuplicateTabCommand.Execute(null)));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Close other tabs", "tab close others", Symbol.TabDesktopMultiple, null, null,
                () => ViewModel.Tabs.CloseOtherTabsCommand.Execute(null)));
            if (_parts.TitleBarTabHost != null)
            {
                ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Tabs in the title bar", "tab bar position title bar windows 11", Symbol.Window, null, null,
                    () => ViewModel.Tabs.Placement = TabSettings.PlacementTitleBar));
            }

            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Tabs on top", "tab bar position top horizontal", Symbol.PanelTopExpand, null, null,
                () => ViewModel.Tabs.Placement = TabSettings.PlacementTop));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Tabs on the left", "tab bar position left vertical side", Symbol.PanelLeftExpand, null, null,
                () => ViewModel.Tabs.Placement = TabSettings.PlacementLeft));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Tabs on the right", "tab bar position right vertical side", Symbol.PanelRightExpand, null, null,
                () => ViewModel.Tabs.Placement = TabSettings.PlacementRight));
        }

        private static string Modifier => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

        private void OnTabsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabsViewModel.Placement))
            {
                ApplyTabPlacement();
            }
        }

        /// <summary>The title bar's height while the tabs are in it (the tabs need more room than the title's text).</summary>
        private const double TitleBarHeightWithTabs = 40;

        /// <summary>
        /// Puts the bar where the setting says: in the title bar (where the layout has room for it; elsewhere on top), at the
        /// window's left edge before the sidebar (where the layout has a place there; elsewhere beside the files), or docked
        /// above or right of the files. The bar is one control moved between these places, so its tabs, scroll position and
        /// state go with it.
        /// </summary>
        private void ApplyTabPlacement()
        {
            Views.Tabs.TabStripView strip = _parts.TabStrip;
            if (strip == null)
            {
                return;
            }

            TabsViewModel tabs = ViewModel.Tabs;
            bool inTitleBar = tabs.IsTitleBar && _parts.TitleBarTabHost != null;
            bool atWindowEdge = tabs.IsLeft && _parts.LeftTabHost != null;
            Panel home = inTitleBar ? _parts.TitleBarTabHost : atWindowEdge ? _parts.LeftTabHost : _parts.TabbedArea;
            if (!ReferenceEquals(strip.Parent, home))
            {
                (strip.Parent as Panel)?.Children.Remove(strip);
                home.Children.Insert(0, strip); // first: a dock panel's last child fills it
            }

            if (ReferenceEquals(home, _parts.TabbedArea))
            {
                DockPanel.SetDock(strip, tabs.IsLeft ? Dock.Left : tabs.IsRight ? Dock.Right : Dock.Top);
            }

            strip.IsInTitleBar = inTitleBar;
            ApplyTitleBarForTabs(inTitleBar);
            _parts.TabbedArea?.InvalidateMeasure();
        }

        /// <summary>The title bar grows to hold the tabs (and its caption buttons with it); the title's text gives way to them.</summary>
        private void ApplyTitleBarForTabs(bool tabsInTitleBar)
        {
            if (_parts.TitleBarRow == null)
            {
                return;
            }

            double height = tabsInTitleBar ? TitleBarHeightWithTabs : _parts.TitleBarHeight;
            _parts.TitleBarRow.Height = new GridLength(height);
            if (_parts.TitleText != null)
            {
                _parts.TitleText.IsVisible = !tabsInTitleBar;
            }

            if (_parts.CaptionButtons is Panel captions)
            {
                foreach (Control button in captions.Children)
                {
                    button.Height = height;
                }
            }

            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                ExtendClientAreaTitleBarHeightHint = height;
            }
        }

        /// <summary>
        /// The tab shortcuts every browser and Windows Explorer share: Ctrl+T new, Ctrl+W (Ctrl+F4) close, Ctrl+Shift+T reopen,
        /// Ctrl+Tab / Ctrl+Shift+Tab and Ctrl+PageDown / Ctrl+PageUp next and previous, Ctrl+1…8 a tab and Ctrl+9 the last,
        /// Ctrl+Shift+PageUp / PageDown move the tab. Cmd instead of Ctrl on macOS, except Ctrl+Tab (as there). They work
        /// with the keyboard in a text box too, as in browsers; Ctrl+Shift+1…7 stay Explorer's view shortcuts.
        /// </summary>
        private void OnTabKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || _parts.TabStrip == null)
            {
                return;
            }

            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            KeyModifiers modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
            TabsViewModel tabs = ViewModel.Tabs;
            bool handled = true;
            bool focusList = true;

            if (e.Key == Key.Tab && modifiers == KeyModifiers.Control)
            {
                tabs.SelectRelative(1);
            }
            else if (e.Key == Key.Tab && modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                tabs.SelectRelative(-1);
            }
            else if (modifiers == command && e.Key == Key.PageDown)
            {
                tabs.SelectRelative(1);
            }
            else if (modifiers == command && e.Key == Key.PageUp)
            {
                tabs.SelectRelative(-1);
            }
            else if (modifiers == (command | KeyModifiers.Shift) && e.Key == Key.PageDown)
            {
                tabs.MoveTabForwardCommand.Execute(null);
                focusList = false;
            }
            else if (modifiers == (command | KeyModifiers.Shift) && e.Key == Key.PageUp)
            {
                tabs.MoveTabBackCommand.Execute(null);
                focusList = false;
            }
            else if (modifiers == command && e.Key == Key.T)
            {
                tabs.NewTabCommand.Execute(null);
            }
            else if (modifiers == (command | KeyModifiers.Shift) && e.Key == Key.T)
            {
                tabs.ReopenClosedTabCommand.Execute(null);
            }
            else if (modifiers == command && (e.Key == Key.W || e.Key == Key.F4))
            {
                tabs.CloseTabCommand.Execute(null);
            }
            else if (modifiers == command && TabNumber(e.Key) is int number)
            {
                tabs.SelectByNumber(number);
            }
            else
            {
                handled = false;
            }

            if (handled)
            {
                e.Handled = true;
                if (focusList)
                {
                    FocusActivePaneSoon();
                }
            }
        }

        private static int? TabNumber(Key key)
        {
            if (key >= Key.D1 && key <= Key.D9)
            {
                return key - Key.D1 + 1;
            }

            if (key >= Key.NumPad1 && key <= Key.NumPad9)
            {
                return key - Key.NumPad1 + 1;
            }

            return null;
        }

        /// <summary>After choosing a tab, the keyboard goes to its active pane's list (arrows, type-to-select work at once).</summary>
        private void FocusActivePaneSoon()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ViewModel.IsSearchActive)
                {
                    return; // the tab's search came back: the results keep the keyboard where it is
                }

                FileListView pane = ViewModel.IsSplitViewActive && ViewModel.IsSecondaryPaneFocused && _parts.SecondaryPane != null
                    ? _parts.SecondaryPane
                    : _parts.PrimaryPane;
                pane?.FocusItems();
            }, DispatcherPriority.Loaded);
        }
    }
}
