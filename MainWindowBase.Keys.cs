// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ByteExplorer.Infrastructure;
using ByteExplorer.Views;

namespace ByteExplorer
{
    /// <summary>
    /// The rest of Windows Explorer's keys: F6 / Shift+F6 move between the navigation pane, the address bar and the
    /// files; F11 full screen; Ctrl+E search; Ctrl+N a new window; Alt+P and Alt+Shift+P the details pane. (Esc, which clears the selection, is the file list's own.)
    /// </summary>
    public abstract partial class MainWindowBase
    {
        /// <summary>The main file list (tests).</summary>
        internal FileListView PrimaryPaneForTests => _parts.PrimaryPane;

        private void AddExplorerKeys(KeyModifiers command)
        {
            void BindAction(Key key, KeyModifiers modifiers, System.Action action, System.Func<bool> canRun = null) =>
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture(key, modifiers),
                    Command = new RelayCommand(_ => action(), _ => canRun?.Invoke() ?? true)
                });

            BindAction(Key.F6, KeyModifiers.None, () => CycleFocus(forward: true));
            BindAction(Key.F6, KeyModifiers.Shift, () => CycleFocus(forward: false));
            BindAction(Key.F11, KeyModifiers.None, ToggleFullScreen);
            BindAction(Key.E, command, FocusSearch);
            BindAction(Key.N, command, () => OpenNewWindow()); // Explorer's new window, in this process (see MainWindowBase.Windows)
            BindAction(Key.P, KeyModifiers.Alt, ToggleDetailsPanel);
            BindAction(Key.P, KeyModifiers.Alt | KeyModifiers.Shift, ToggleDetailsPanel);
        }

        /// <summary>F11: full screen and back to how the window was (maximized or not), as Explorer and browsers do.</summary>
        private WindowState _stateBeforeFullScreen = WindowState.Normal;

        private void ToggleFullScreen()
        {
            if (WindowState == WindowState.FullScreen)
            {
                WindowState = _stateBeforeFullScreen;
            }
            else
            {
                _stateBeforeFullScreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
        }

        private bool IsFocusInFiles()
        {
            Avalonia.Visual focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
            return focused != null && (IsInside(focused, _parts.PrimaryPane) || IsInside(focused, _parts.SecondaryPane));
        }

        /// <summary>F6 order: files → navigation pane → address bar → files (Shift+F6 the other way); hidden parts are skipped.</summary>
        private void CycleFocus(bool forward)
        {
            Avalonia.Visual focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
            NavigationPaneView tree = this.GetVisualDescendants().OfType<NavigationPaneView>().FirstOrDefault(t => t.IsEffectivelyVisible);
            int current = IsFocusInFiles() ? 0 : (tree != null && focused != null && IsInside(focused, tree)) ? 1 : focused is TextBox ? 2 : 0;
            for (int step = 1; step <= 3; step++)
            {
                int next = ((current + (forward ? step : -step)) % 3 + 3) % 3;
                if (next == 0)
                {
                    FocusActivePaneSoon();
                    return;
                }

                if (next == 1 && tree != null)
                {
                    ListBox rows = tree.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
                    if (rows != null)
                    {
                        Control row = rows.SelectedIndex >= 0 ? rows.ContainerFromIndex(rows.SelectedIndex) : null;
                        (row ?? rows).Focus(NavigationMethod.Tab);
                        return;
                    }
                }

                if (next == 2 && _parts.Omnibar != null)
                {
                    BeginAddressEdit();
                    return;
                }
            }
        }
    }
}
