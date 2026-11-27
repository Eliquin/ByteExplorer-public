// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Omnibar;
using ByteExplorer.Views;
using ByteExplorer.Views.Dialogs;
using ByteExplorer.Views.Menus;
using Serilog;

namespace ByteExplorer
{
    /// <summary>
    /// The controls of a window layout that <see cref="MainWindowBase"/> drives. Each layout arranges them its own way
    /// (where the sidebar is, how tall the title bar is), and hands them over once, right after InitializeComponent.
    /// Members marked optional may be null; the rest are required.
    /// </summary>
    public sealed class MainWindowParts
    {
        /// <summary>The areas that move the window when dragged and maximize it on a double click.</summary>
        public IReadOnlyList<Control> DragRegions { get; init; }

        /// <summary>The app's own minimize/maximize/close buttons; hidden on macOS, where the traffic lights are used.</summary>
        public Control CaptionButtons { get; init; }

        public Button MaximizeButton { get; init; }

        public SymbolIcon MaximizeIcon { get; init; }

        /// <summary>The 1-pixel window frame drawn while the app draws its own frame and the window is not maximized.</summary>
        public Border FrameBorder { get; init; }

        /// <summary>Edge and corner grips (Tag = a <see cref="WindowEdge"/> name), shown only where the app draws its own frame.</summary>
        public Panel ResizeGrips { get; init; }

        /// <summary>Height of the layout's title bar, for the platform's caption area (Windows, macOS).</summary>
        public double TitleBarHeight { get; init; } = 32;

        /// <summary>The address bar (breadcrumbs, typing with suggestions, commands).</summary>
        public Views.Omnibar.OmnibarView Omnibar { get; init; }

        public TextBox GlobalSearchBox { get; init; }

        /// <summary>The global search results view (Down in the search box moves into it, Escape there comes back).</summary>
        public Views.Search.SearchResultsView SearchResults { get; init; }

        /// <summary>The sidebar: rows with the navRow class inside it accept dropped files (Tag = folder path).</summary>
        public Control Sidebar { get; init; }

        public ColumnDefinition SidebarColumn { get; init; }

        /// <summary>Optional: the splitter next to the sidebar, hidden with it.</summary>
        public Control SidebarSplitter { get; init; }

        public double SidebarDefaultWidth { get; init; } = 220;

        public double SidebarMinWidth { get; init; } = 120;

        public double SidebarMaxWidth { get; init; } = 600;

        public ColumnDefinition DetailsColumn { get; init; }

        public Control DetailsSplitter { get; init; }

        public Control DetailsPanel { get; init; }

        public double DetailsDefaultWidth { get; init; } = 240;

        public double DetailsMinWidth { get; init; } = 160;

        public RowDefinition StatusBarRow { get; init; }

        /// <summary>Optional: the command toolbar and its row (the Modern layout has one).</summary>
        public Control Toolbar { get; init; }

        public RowDefinition ToolbarRow { get; init; }

        public Control StatusBar { get; init; }

        /// <summary>The grid of the two file list panes: columns primary, splitter, secondary.</summary>
        public Grid PaneGrid { get; init; }

        /// <summary>The tab bar, docked in <see cref="TabbedArea"/> (top, left or right of the panes, by the setting).</summary>
        public Views.Tabs.TabStripView TabStrip { get; init; }

        /// <summary>Holds the tab bar and the main view (panes, search results).</summary>
        public DockPanel TabbedArea { get; init; }

        /// <summary>Where the tabs go in the title bar (Tab bar position: Title bar); null where the layout has no room for them.</summary>
        public Panel TitleBarTabHost { get; init; }

        /// <summary>The window title's text in the title bar (hidden while the tabs are there).</summary>
        public Control TitleText { get; init; }

        /// <summary>The title bar's row (taller while the tabs are in it).</summary>
        public RowDefinition TitleBarRow { get; init; }

        /// <summary>Where vertical tabs on the left go: the window's left edge, before the sidebar; null: beside the files.</summary>
        public Panel LeftTabHost { get; init; }

        public Control PaneSplitter { get; init; }

        public FileListView PrimaryPane { get; init; }

        public FileListView SecondaryPane { get; init; }

        public Border ToastBorder { get; init; }

        public TextBlock ToastText { get; init; }

        /// <summary>Optional: the file operations panel; the toast is raised above it while it is shown.</summary>
        public Control OperationsPanel { get; init; }

        /// <summary>
        /// The panel holding the left panel's ordered sections (children marked with SidebarOrder.Key); with
        /// <see cref="SidebarSectionsBelow"/>, the panel above <see cref="SidebarPivotKey"/>'s fixed section.
        /// </summary>
        public Panel SidebarSectionsAbove { get; init; }

        /// <summary>Optional: the panel below the fixed section (the Modern layout's tree), for sections ordered after it.</summary>
        public Panel SidebarSectionsBelow { get; init; }

        /// <summary>With <see cref="SidebarSectionsBelow"/>: the key of the section that stays in place between the two panels.</summary>
        public string SidebarPivotKey { get; init; }

        /// <summary>Optional: lines between the fixed section and the panels above and below it, hidden while a panel is empty.</summary>
        public Control SidebarLineAbovePivot { get; init; }

        public Control SidebarLineBelowPivot { get; init; }

        /// <summary>Optional: the panel that takes the left panel design's classes (Settings › Left panel › Design).</summary>
        public Control SidebarDesignHost { get; init; }

        /// <summary>The window's customizable areas (address bar, toolbar, left panel, main view, details panel, status bar).</summary>
        public IReadOnlyList<Views.AreaPart> Areas { get; init; } = Array.Empty<Views.AreaPart>();
    }

    /// <summary>
    /// What every window layout (Modern, Nemo) shares: window chrome per OS, keyboard shortcuts, the address bar and
    /// breadcrumbs, the sidebar's rows (with drop targets), panel toggles, split view, search, toasts, startup and
    /// shutdown, and the dialogs the view models ask for (<see cref="IDialogService"/>). A layout is only its XAML plus
    /// a <see cref="MainWindowParts"/>; the behaviour lives here once, and in <see cref="MainViewModel"/> and
    /// <see cref="FileListViewModel"/>.
    /// <para>
    /// Event handlers the layouts' XAML refers to are protected members of this class, so each layout wires the same
    /// handlers by name.
    /// </para>
    /// </summary>
    public abstract partial class MainWindowBase : Window, IDialogService, Views.Sidebar.IPinnedAreaHost, ViewModels.Settings.ISettingsHost
    {
        private const int ToastDwellMs = 3200;
        private const double ToastOffscreenX = 340;

        private readonly Dictionary<FileListViewModel, Action> _paneUnhooks = new Dictionary<FileListViewModel, Action>();
        private MainWindowParts _parts;
        private GridLength _lastSidebarWidth;
        private GridLength _lastDetailsWidth;
        private bool _closeConfirmed;
        private bool _closing;
        private bool _closed;
        private int _toastGeneration;
        private Border _dropHighlight;

        protected MainWindowBase()
        {
            var dispatcher = new UiDispatcher(Dispatcher.UIThread);
            ViewModel = new MainViewModel(dispatcher, this, ServicesForNewWindow(dispatcher));
            ViewModel.OpenNewWindow = location => OpenNewWindow(location);
        }

        /// <summary>The window's view model (also its DataContext).</summary>
        public MainViewModel ViewModel { get; }

        /// <summary>The pane the toolbar, address bar and shortcuts act on.</summary>
        protected FileListViewModel ActivePane => ViewModel.FileListViewModel;

        /// <summary>
        /// Wires the layout's controls. Called once by the layout's constructor, after InitializeComponent: setting the
        /// DataContext before the controls exist would bind them twice.
        /// </summary>
        protected void AttachParts(MainWindowParts parts)
        {
            _parts = parts ?? throw new ArgumentNullException(nameof(parts));
            _lastSidebarWidth = new GridLength(parts.SidebarDefaultWidth);
            _lastDetailsWidth = new GridLength(parts.DetailsDefaultWidth);

            DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // The window used last (this one, if it's gone): a closed window has no clipboard to give.
            ClipboardFileService.Initialize(() => (MostRecentlyActive ?? this).Clipboard, () => (MostRecentlyActive ?? this).StorageProvider);

            ConfigureChrome();
            ConfigureKeyBindings();
            // Quick access icons take their colors from the palette; re-read them when the theme changes.
            ViewModel.Pins.BrushResolver = ResolveBrush;
            ActualThemeVariantChanged += (_, _) => ViewModel.Pins.RefreshBrushes();
            Activated += (_, _) => ViewModel.Pins.RefreshAvailabilityAsync().Forget("check Quick access availability");
            HookDropTargets();
            if (parts.SearchResults != null)
            {
                parts.SearchResults.SearchBoxFocusRequested += OnSearchResultsWantSearchBox;
            }

            foreach (FileListViewModel pane in ViewModel.AllPanes)
            {
                HookFailureMessages(pane);
            }

            ViewModel.PaneAdded += (_, pane) => HookFailureMessages(pane);
            ViewModel.PaneRemoved += (_, pane) => UnhookFailureMessages(pane);

            // WPF re-queried CanExecute after input; Avalonia does not (see Infrastructure.CommandManager).
            AddHandler(KeyUpEvent, (_, _) => CommandManager.InvalidateRequerySuggested(), RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, (_, _) => CommandManager.InvalidateRequerySuggested(), RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerPressedEvent, OnWindowPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(TextInputEvent, OnWindowTextInputTunnel, RoutingStrategies.Tunnel);
            ViewModel.Omnibar.EditEnded += OnOmnibarEditEnded;
            ViewModel.CustomizationChanged += OnCustomizationChanged;
            ClipboardFileService.FilesAvailabilityChanged += OnClipboardFilesChanged;
            FolderOptions.Changed += OnFolderOptionsChanged;
            CaptureLayoutDefaults();
            RegisterWindowCommands();
            AttachTabs();
            AddHandler(GotFocusEvent, OnPaneGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
            // A click anywhere in a pane (its empty background too, which takes no focus) makes it the active pane.
            AddHandler(PointerPressedEvent, OnPanePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

            Opened += OnOpened;
            Activated += (_, _) => ClipboardFileService.RefreshAsync().Forget("clipboard refresh");
            Activated += (_, _) => OnMainWindowActivated();
            AsyncRelayCommand.Failed += OnCommandFailed;
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    UpdateMaximizeGlyph();
                }
            };
        }

        /// <summary>
        /// Called on macOS after the traffic lights replace the caption buttons: a layout moves whatever sits under the
        /// buttons (top-left, about 78×28 pixels) out of their way.
        /// </summary>
        protected virtual void MakeRoomForTrafficLights()
        {
        }

        // ------------------------------------------------------------------ startup / shutdown

        private async void OnOpened(object sender, EventArgs e)
        {
            RegisterOpenWindow();
            try
            {
                await ViewModel.InitializeAsync();

                // A window opened with Ctrl+N shows the place it was opened for: not the startup place, not the last
                // session's tabs (those belong to the app's start), not the command line's folder, and no greeting.
                if (_initialLocation != null)
                {
                    if (!ActivePane.HasStartedNavigation)
                    {
                        await ViewModel.OpenLocationAsync(_initialLocation);
                    }

                    ApplyAppearanceSettings(ViewModel.SettingsService.Current?.Appearance);
                    ApplyCustomization();
                    ApplyPanels();
                    return;
                }

                // Open on the Home dashboard (the WPF window started on an empty pane), unless something was asked for while
                // initializing (a click during startup, even one still loading): Home would replace it.
                LaunchRequestOnce(out Infrastructure.Launch.LaunchRequest request);
                if (!ActivePane.HasStartedNavigation)
                {
                    if (request.HasFolder)
                    {
                        await OpenStartupRequestAsync(request); // the folder on the command line (the system opened it)
                    }
                    else
                    {
                        await ViewModel.OpenStartupLocationAsync(); // Folder options: Home, This PC, a folder or the last session
                    }
                }

                AppSettings settings = ViewModel.SettingsService.Current;
                ApplyAppearanceSettings(settings?.Appearance);
                ApplyCustomization();
                App.ApplyTheme(settings?.Appearance?.Theme);
                ApplyPanels();
                ShowGreetingIfEnabled();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Window initialization failed");
            }
            finally
            {
                _initialized.TrySetResult();
            }
        }

        /// <summary>The command line's request, for the first window only (a window made later, as in tests, starts as usual).</summary>
        private static void LaunchRequestOnce(out Infrastructure.Launch.LaunchRequest request)
        {
            request = App.StartupRequest ?? Infrastructure.Launch.LaunchRequest.Empty;
            App.StartupRequest = Infrastructure.Launch.LaunchRequest.Empty;
        }

        private async void OnWindowClosing(object sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed)
            {
                return;
            }

            // Window.Closing cannot await; cancel, ask, and close again once decided.
            e.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            try
            {
                // Another window stays open: the app goes on, so nothing is asked, stopped or saved here. Its operations
                // keep running (the other windows show them), and the window closed last keeps the session.
                if (HasOtherOpenWindows)
                {
                    _closeConfirmed = true;
                    if (!_closed)
                    {
                        Close();
                    }

                    return;
                }

                if (ViewModel.Operations.HasActiveJobs)
                {
                    bool stop = await ConfirmAsync(
                        "Quit ByteExplorer?",
                        "File operations are still running. If you quit now they are cancelled: what was copied or moved so far stays, and nothing is left half-written.",
                        "Cancel them and quit");
                    if (!stop)
                    {
                        return;
                    }

                    await ViewModel.Operations.StopAllAsync(TimeSpan.FromSeconds(10)); // each stops at a safe point before the app exits
                }

                // Only the first build is worth a question: a background rebuild just stops, and the saved index stays.
                if (ViewModel.IsIndexing && !ViewModel.IsSearchEnabled)
                {
                    bool quit = await ConfirmAsync(
                        "Quit ByteExplorer?",
                        "The search index is still being built for the first time.\n\nIf you quit now, it is built again the next time ByteExplorer starts.",
                        "Quit anyway");
                    if (!quit)
                    {
                        return;
                    }
                }

                // Folder options: the folders open now are where the next start can pick up.
                await ViewModel.SaveSessionAsync();

                if (ViewModel.StopIndexingCommand.CanExecute(null))
                {
                    ViewModel.StopIndexingCommand.Execute(null);
                }

                bool goodbye = ViewModel.SettingsService?.Current?.Greeting?.EnableGoodbye ?? false;
                if (goodbye)
                {
                    int dwellMs = Math.Clamp(ViewModel.SettingsService?.Current?.Greeting?.GoodbyeDwellMs ?? 1800, 0, 10000);
                    ShowToast(GreetingPhrases.RandomGoodbye(DateTime.Now));
                    ViewModel.SoundFeedbackService?.PlayGoodbye();
                    await Task.Delay(dwellMs);
                }

                await ViewModel.Tags.FlushAsync();
                _closeConfirmed = true;
                if (!_closed)
                {
                    Close(); // unless CloseWithoutAsking closed it during the goodbye
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Close sequence failed; closing now");
                _closeConfirmed = true;
                if (!_closed)
                {
                    Close();
                }
            }
            finally
            {
                _closing = false;
            }
        }

        /// <summary>
        /// Closes now, without the questions and the goodbye of a normal close: running operations are cancelled, a first
        /// index build stops, tags are saved in the background. For callers that can't answer a dialog or wait (the
        /// test harness between tests; a session ending).
        /// </summary>
        internal void CloseWithoutAsking()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                // Only the last window stops the app's work; another window's close leaves it to the windows still open.
                if (!HasOtherOpenWindows)
                {
                    ViewModel.Operations.CancelAll();
                    if (ViewModel.StopIndexingCommand.CanExecute(null))
                    {
                        ViewModel.StopIndexingCommand.Execute(null);
                    }

                    ViewModel.Tags.FlushAsync().Forget("tags flush on close");
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Closing without asking: shutdown step failed; closing anyway");
            }

            _closeConfirmed = true;
            Close();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _closed = true;
            UnregisterOpenWindow();
            try
            {
                AsyncRelayCommand.Failed -= OnCommandFailed;
                ViewModel.Omnibar.EditEnded -= OnOmnibarEditEnded;
                ViewModel.CustomizationChanged -= OnCustomizationChanged;
                ClipboardFileService.FilesAvailabilityChanged -= OnClipboardFilesChanged; // a static event: never keep a closed window
                FolderOptions.Changed -= OnFolderOptionsChanged;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.Dispose();
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "MainViewModel disposal failed during shutdown");
            }
        }

        private void ApplyAppearanceSettings(AppearanceSettings appearance)
        {
            if (appearance == null)
            {
                return;
            }

            // The WPF build saved "Segoe UI Variable Text" as its default font; read that as "use the app's font" (Inter),
            // so only a font the person chose replaces Inter.
            if (!string.IsNullOrWhiteSpace(appearance.FontFamily) &&
                !string.Equals(appearance.FontFamily, AppearanceSettings.LegacyDefaultFontFamily, StringComparison.OrdinalIgnoreCase))
            {
                // A chosen font falls back to Inter where it is not installed (a Windows font on Linux).
                FontFamily = new FontFamily(appearance.FontFamily + ", avares://Avalonia.Fonts.Inter/Assets#Inter");
            }

            if (appearance.FontSize >= 8 && appearance.FontSize <= 32)
            {
                FontSize = appearance.FontSize;
            }
        }

        /// <summary>
        /// Lays out the four panels from the view model (left pane, details pane, toolbar, status bar), which remembers
        /// them in the settings. Called at startup and whenever one of them changes (menu, buttons, commands).
        /// </summary>
        private void ApplyPanels()
        {
            SetSidebarVisible(ViewModel.IsNavigationPaneVisible);
            SetDetailsPanelVisible(ViewModel.IsDetailsPaneVisible);
            SetRowVisible(_parts.ToolbarRow, _parts.Toolbar, ViewModel.IsToolbarVisible, ref _toolbarHeight);
            SetRowVisible(_parts.StatusBarRow, _parts.StatusBar, ViewModel.IsStatusBarVisible, ref _statusBarHeight);
        }

        private GridLength _toolbarHeight;
        private GridLength _statusBarHeight;

        /// <summary>Collapses or restores a row panel (toolbar, status bar), remembering its height.</summary>
        private static void SetRowVisible(RowDefinition row, Control panel, bool visible, ref GridLength remembered)
        {
            if (row == null || panel == null)
            {
                return;
            }

            if (!visible)
            {
                if (row.Height.Value > 0 || row.Height.IsAuto)
                {
                    remembered = row.Height;
                }

                row.Height = new GridLength(0);
            }
            else if (row.Height.Value <= 0 && !row.Height.IsAuto)
            {
                row.Height = remembered.Value > 0 || remembered.IsAuto ? remembered : GridLength.Auto;
            }

            panel.IsVisible = visible;
        }

        // ------------------------------------------------------------------ window chrome

        /// <summary>
        /// Windows: the client area extends into the native frame (Aero Snap, shadow, resize borders and the system menu
        /// keep working), with the app's own caption buttons. macOS: the same, but with the system's traffic-light
        /// buttons, so the custom ones are hidden and the layout makes room for them. Linux: X11/Wayland window managers
        /// do not support an extended client area, so the window is undecorated and the app provides moving (title bar
        /// drag), maximizing (double click) and resizing (edge grips) itself, as the WPF build did with WindowChrome.
        /// </summary>
        private void ConfigureChrome()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaTitleBarHeightHint = _parts.TitleBarHeight;
                if (OperatingSystem.IsMacOS())
                {
                    ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                    _parts.CaptionButtons.IsVisible = false;
                    MakeRoomForTrafficLights();
                }
                else
                {
                    ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
                }
            }
            else
            {
                SystemDecorations = SystemDecorations.None;
                _parts.ResizeGrips.IsVisible = true;
            }

            foreach (Control region in _parts.DragRegions ?? Array.Empty<Control>())
            {
                region.PointerPressed += OnDragRegionPointerPressed;
            }
        }

        private void OnDragRegionPointerPressed(object sender, PointerPressedEventArgs e)
        {
            // Only a press on the region itself or its passive content: buttons and text boxes in it handle their own.
            if (!(sender is Control region) || !e.GetCurrentPoint(region).Properties.IsLeftButtonPressed ||
                IsInside(e.Source as Visual, _parts.CaptionButtons) || IsInteractive(e.Source as Visual, region))
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }

            BeginMoveDrag(e);
        }

        /// <summary>True when the press landed on an input control inside the drag region (a button, a text box).</summary>
        private static bool IsInteractive(Visual source, Visual region)
        {
            for (Visual current = source; current != null && !ReferenceEquals(current, region); current = current.GetVisualParent())
            {
                if (current is Button || current is TextBox || current is Slider)
                {
                    return true;
                }
            }

            return false;
        }

        protected void OnResizeGripPressed(object sender, PointerPressedEventArgs e)
        {
            if (WindowState != WindowState.Normal || !(sender is Control grip) || !(grip.Tag is string edgeName) ||
                !Enum.TryParse(edgeName, out WindowEdge edge))
            {
                return;
            }

            BeginResizeDrag(edge, e);
            e.Handled = true;
        }

        protected void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        protected void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) => ToggleMaximize();

        protected void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize() =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void UpdateMaximizeGlyph()
        {
            bool maximized = WindowState == WindowState.Maximized;
            _parts.MaximizeIcon.Symbol = maximized ? Symbol.SquareMultiple : Symbol.Maximize;
            ToolTip.SetTip(_parts.MaximizeButton, maximized ? "Restore" : "Maximize");

            // A maximized undecorated window has no frame to draw or grab.
            _parts.FrameBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
            _parts.ResizeGrips.IsVisible = SystemDecorations == SystemDecorations.None && !maximized;
        }

        // ------------------------------------------------------------------ keyboard

        /// <summary>
        /// Window-wide shortcuts. Built in code so they use the platform's command modifier (Cmd on macOS, Ctrl
        /// elsewhere). A focused text box handles its own Delete/Ctrl+C/V/X/A first, so typing in the address bar never
        /// deletes or copies files.
        /// </summary>
        /// <summary>Whether the keyboard focus is inside a control that handles the file keys itself (see <see cref="Views.IFileKeyScope"/>).</summary>
        private bool IsFocusInFileKeyScope()
        {
            for (Visual visual = FocusManager?.GetFocusedElement() as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Views.IFileKeyScope)
                {
                    return true;
                }
            }

            return false;
        }

        private void ConfigureKeyBindings()
        {
            KeyModifiers command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
                                   ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

            // A key binding consumes its key only when its command can run (Avalonia checks CanExecute first). So each
            // binding reports its target's real CanExecute, and keys a text box uses for editing (Backspace, Delete,
            // Escape, clipboard, select all, Enter) never run while one has focus: otherwise Escape in the address bar
            // or search box would always be taken by "cancel file operation" and Backspace would navigate back.
            void Bind(Key key, KeyModifiers modifiers, Func<FileListViewModel, System.Windows.Input.ICommand> target)
            {
                bool textEditingKey = ((modifiers == KeyModifiers.None || modifiers == KeyModifiers.Shift) &&
                                       (key == Key.Back || key == Key.Delete || key == Key.Escape || key == Key.Enter)) ||
                                      ((modifiers & command) != 0 && (key == Key.C || key == Key.X || key == Key.V || key == Key.A || key == Key.Z));
                bool scopedFileKey = key == Key.Delete || key == Key.F2 || key == Key.Enter ||
                                     ((modifiers & command) != 0 && (key == Key.C || key == Key.X || key == Key.V));
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture(key, modifiers),
                    Command = new RelayCommand(
                        _ => target(ActivePane)?.Execute(null),
                        _ =>
                        {
                            if (textEditingKey && FocusManager?.GetFocusedElement() is TextBox)
                            {
                                return false;
                            }

                            if (scopedFileKey && IsFocusInFileKeyScope())
                            {
                                return false; // the tree or Quick access acts on its own row (see IFileKeyScope)
                            }

                            System.Windows.Input.ICommand resolved = target(ActivePane);
                            return resolved != null && resolved.CanExecute(null);
                        })
                });
            }

            void BindAction(Key key, KeyModifiers modifiers, Action action) =>
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = new RelayCommand(_ => action()) });

            Bind(Key.Delete, KeyModifiers.None, vm => vm.DeleteCommand);
            Bind(Key.Delete, KeyModifiers.Shift, vm => vm.DeletePermanentCommand);
            Bind(Key.C, command, vm => vm.CopyCommand);
            Bind(Key.X, command, vm => vm.CutCommand);
            Bind(Key.V, command, vm => vm.PasteCommand);
            Bind(Key.A, command, vm => vm.SelectAllCommand);
            Bind(Key.C, command | KeyModifiers.Shift, vm => vm.CopyPathCommand);
            Bind(Key.N, command | KeyModifiers.Shift, vm => vm.NewFolderCommand);
            Bind(Key.F2, KeyModifiers.None, vm => vm.RenameCommand);
            Bind(Key.F5, KeyModifiers.None, vm => vm.RefreshCommand);
            Bind(Key.R, command, vm => vm.RefreshCommand);
            Bind(Key.Enter, KeyModifiers.Alt, vm => vm.PropertiesCommand);
            // Total Commander's "calculate all folder sizes".
            Bind(Key.Enter, KeyModifiers.Alt | KeyModifiers.Shift, vm => vm.CalculateAllFolderSizesCommand);
            Bind(Key.Left, KeyModifiers.Alt, vm => vm.NavigateBackCommand);
            Bind(Key.Right, KeyModifiers.Alt, vm => vm.NavigateForwardCommand);
            Bind(Key.Up, KeyModifiers.Alt, vm => vm.NavigateUpCommand);
            Bind(Key.H, command, vm => vm.ToggleShowHiddenItemsCommand);

            // Backspace goes back, as in Explorer (a focused text box consumes it first). Finder's keys on macOS.
            Bind(Key.Back, KeyModifiers.None, vm => vm.NavigateBackCommand);
            Bind(Key.BrowserBack, KeyModifiers.None, vm => vm.NavigateBackCommand);
            Bind(Key.BrowserForward, KeyModifiers.None, vm => vm.NavigateForwardCommand);
            if (OperatingSystem.IsMacOS())
            {
                Bind(Key.OemOpenBrackets, KeyModifiers.Meta, vm => vm.NavigateBackCommand);
                Bind(Key.OemCloseBrackets, KeyModifiers.Meta, vm => vm.NavigateForwardCommand);
                Bind(Key.Up, KeyModifiers.Meta, vm => vm.NavigateUpCommand);
                Bind(Key.Down, KeyModifiers.Meta, vm => vm.OpenSelectedItemCommand);
                Bind(Key.Back, KeyModifiers.Meta, vm => vm.DeleteCommand);
            }

            // Explorer's view shortcuts (Ctrl+Shift+1..7) and zoom keys (Ctrl and +/-, as Ctrl+wheel does).
            FileListViewPreset[] presets =
            {
                FileListViewPreset.ExtraLargeIcons, FileListViewPreset.LargeIcons, FileListViewPreset.MediumIcons,
                FileListViewPreset.SmallIcons, FileListViewPreset.List, FileListViewPreset.Details, FileListViewPreset.Tiles
            };
            Key[] digits = { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7 };
            for (int i = 0; i < presets.Length; i++)
            {
                FileListViewPreset preset = presets[i];
                BindAction(digits[i], command | KeyModifiers.Shift, () => ActivePane.SelectedPreset = preset);
            }

            foreach (Key key in new[] { Key.OemPlus, Key.Add })
            {
                BindAction(key, command, () => ActivePane.AdjustZoom(16));
            }

            foreach (Key key in new[] { Key.OemMinus, Key.Subtract })
            {
                BindAction(key, command, () => ActivePane.AdjustZoom(-16));
            }

            // Undo the last file operation. A focused text box keeps Ctrl+Z for its own text.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Z, command),
                Command = new RelayCommand(
                    _ => ViewModel.Operations.UndoCommand.Execute(null),
                    _ => !(FocusManager?.GetFocusedElement() is TextBox) && ViewModel.Operations.UndoCommand.CanExecute(null))
            });

            // Redo: Ctrl+Y (Windows, Linux) and Ctrl+Shift+Z (macOS, many Linux apps). Text boxes keep both.
            foreach (KeyGesture gesture in new[] { new KeyGesture(Key.Y, command), new KeyGesture(Key.Z, command | KeyModifiers.Shift) })
            {
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = gesture,
                    Command = new RelayCommand(
                        _ => ViewModel.Operations.RedoCommand.Execute(null),
                        _ => !(FocusManager?.GetFocusedElement() is TextBox) && ViewModel.Operations.RedoCommand.CanExecute(null))
                });
            }

            // Pin the current folder to Quick access (or unpin it), as browsers bookmark and Finder adds to its sidebar.
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D, command),
                Command = new RelayCommand(_ => ViewModel.TogglePinCommand.Execute(null), _ => ViewModel.TogglePinCommand.CanExecute(null))
            });

            BindAction(Key.L, command, BeginAddressEdit);
            BindAction(Key.F4, KeyModifiers.None, BeginAddressEdit);
            BindAction(Key.D, KeyModifiers.Alt, BeginAddressEdit); // Explorer's and browsers' "address bar"
            BindAction(Key.F, command, FocusSearch);
            BindAction(Key.F3, KeyModifiers.None, FocusSearch);
            BindAction(Key.OemComma, command, OpenSettings); // the settings shortcut of VS Code, browsers and macOS apps
            AddExplorerKeys(command);
            foreach (Key key in new[] { Key.OemPipe, Key.OemBackslash })
            {
                // Split view on or off (VS Code's "split editor" key), in every layout.
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture(key, command),
                    Command = new RelayCommand(_ => ViewModel.ToggleSplitViewCommand.Execute(null), _ => ViewModel.ToggleSplitViewCommand.CanExecute(null))
                });
            }
        }

        private Views.Settings.SettingsWindow _settingsWindow;

        /// <summary>
        /// Opens the settings window, or brings it forward when it is already open: one settings window per main window,
        /// not modal, so the effect of a change shows in the main window while the settings stay open.
        /// </summary>
        public void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }

                _settingsWindow.Activate();
                return;
            }

            var window = new Views.Settings.SettingsWindow(new ViewModels.Settings.SettingsViewModel(ViewModel, this));
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, window))
                {
                    _settingsWindow = null;
                }
            };
            _settingsWindow = window;
            window.Show(this);
        }

        /// <summary>Opens the tag manager over the settings window when it is open, else over this window.</summary>
        public void OpenTagManager() =>
            Views.Tags.TagEditorWindow.ShowFor((Window)_settingsWindow ?? this, ViewModel.Tags, Array.Empty<string>());

        /// <summary>The layout has a command toolbar (the settings' Toolbar switch is shown only then).</summary>
        public bool HasToolbar => _parts?.Toolbar != null;

        /// <summary>Opens a folder in the active pane (ending a search that covers it) and selects a file in it.</summary>
        public void RevealInFileList(string folder, string fileToSelect)
        {
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                ShowMessageAsync("Folder not found", "The folder does not exist yet:\n" + folder).Forget("MainWindowBase.RevealInFileList");
                return;
            }

            Activate();
            ViewModel.ClearSearch();
            RevealAsync(ActivePane, folder, fileToSelect).Forget("MainWindowBase.RevealInFileList");
        }

        private static async Task RevealAsync(FileListViewModel pane, string folder, string fileToSelect)
        {
            await pane.LoadFolderAsync(folder).ConfigureAwait(true);
            if (string.IsNullOrEmpty(fileToSelect))
            {
                return;
            }

            foreach (object entry in pane.Items)
            {
                if (entry is FileListItemViewModel item && ByteExplorer.Core.PathUtil.AreSame(item.FullPath, fileToSelect))
                {
                    pane.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>
        /// Restarts ByteExplorer: the window closes the normal way (so it saves its state and plays its goodbye), and once
        /// it has closed the same executable starts again with the same arguments. A close that is cancelled (a running
        /// file operation the person keeps) cancels the restart too.
        /// </summary>
        public void RestartApplication()
        {
            EventHandler restart = null;
            restart = (_, _) =>
            {
                Closed -= restart;
                try
                {
                    System.Diagnostics.Process.Start(RestartStartInfo());
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Could not start ByteExplorer again");
                }
            };
            Closed += restart;
            _settingsWindow?.Close();
            CloseOtherWindows(); // the whole app restarts, not this window alone
            Close();
        }

        /// <summary>
        /// The command line that starts this process again: the apphost with its arguments, or, under "dotnet
        /// ByteExplorer.dll", the dotnet host with the assembly first.
        /// </summary>
        internal static System.Diagnostics.ProcessStartInfo RestartStartInfo()
        {
            string processPath = Environment.ProcessPath;
            string[] args = Environment.GetCommandLineArgs();
            var info = new System.Diagnostics.ProcessStartInfo(processPath) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            bool dotnetHost = string.Equals(System.IO.Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            for (int i = dotnetHost ? 0 : 1; i < args.Length; i++)
            {
                info.ArgumentList.Add(args[i]);
            }

            return info;
        }

        /// <summary>The open settings window, if any (tests).</summary>
        internal Views.Settings.SettingsWindow SettingsWindowForTests => _settingsWindow;

        private void FocusSearch()
        {
            if (_parts.GlobalSearchBox.IsEnabled)
            {
                _parts.GlobalSearchBox.Focus();
                _parts.GlobalSearchBox.SelectAll();
            }
        }

        // ------------------------------------------------------------------ address bar (omnibar)

        private void BeginAddressEdit() => _parts.Omnibar?.BeginEdit();

        /// <summary>
        /// Typing "/" or "~" (Unix) or "\" (Windows) where no text box has focus starts the omnibar with it, as in GNOME
        /// Files: no file name contains a separator, so these never belong to type-to-select.
        /// </summary>
        private void OnWindowTextInputTunnel(object sender, TextInputEventArgs e)
        {
            string text = e.Text;
            if (string.IsNullOrEmpty(text) || text.Length != 1 || FocusManager?.GetFocusedElement() is TextBox || ViewModel.Omnibar.IsEditing)
            {
                return;
            }

            char c = text[0];
            bool starts = OperatingSystem.IsWindows() ? c == '\\' : (c == '/' || c == '~');
            if (starts)
            {
                e.Handled = true;
                _parts.Omnibar?.BeginEdit(text);
                return;
            }

            // Folder options: typing in the file list types into the search box (instead of jumping to the item).
            if (FolderOptions.Current.TypingInList == Core.Models.FolderOptionsSettings.TypeSearches && !char.IsControl(c) && c != ' ' &&
                _parts.GlobalSearchBox is TextBox search && search.IsEnabled && search.IsEffectivelyVisible &&
                (IsInside(e.Source as Visual, _parts.PrimaryPane) || IsInside(e.Source as Visual, _parts.SecondaryPane)))
            {
                e.Handled = true;
                search.Focus();
                search.Text = text;
                search.CaretIndex = text.Length;
            }
        }

        /// <summary>After going somewhere from the omnibar, the file list gets the keyboard back (arrows, type-to-select).</summary>
        private void OnOmnibarEditEnded(object sender, bool committed)
        {
            if (!committed)
            {
                return;
            }

            FileListView pane = _parts.SecondaryPane != null && _parts.SecondaryPane.IsVisible && ReferenceEquals(ActivePane, ViewModel.SecondaryFileListViewModel)
                ? _parts.SecondaryPane
                : _parts.PrimaryPane;
            pane?.FocusItems();
        }

        /// <summary>Commands only the window can run, offered by the omnibar ("&gt; details").</summary>
        private void RegisterWindowCommands()
        {
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Settings", "options preferences configure", Symbol.Settings, "Ctrl+,", null, OpenSettings));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("New window", "window open another second explorer", Symbol.Window, Modifier + "+N", null,
                () => OpenNewWindow()));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Manage tags", "tag labels colors sub-tags", Symbol.Bookmark, null, null, OpenTagManager));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Show or hide the details pane", "preview right panel info", Symbol.PanelRight, null, null, ToggleDetailsPanel));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Show or hide the sidebar", "navigation left panel tree", Symbol.PanelLeft, null, null, ToggleSidebar));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Show or hide the status bar", "bottom bar panel", Symbol.PanelLeft, null, null,
                () => ViewModel.IsStatusBarVisible = !ViewModel.IsStatusBarVisible));
            if (_parts.Toolbar != null)
            {
                ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Show or hide the toolbar", "command bar ribbon panel", Symbol.PanelLeft, null, null,
                    () => ViewModel.IsToolbarVisible = !ViewModel.IsToolbarVisible));
            }

            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Tree follows the current folder (on/off)", "navigation tree sync follow select", Symbol.PanelLeft, null, null,
                () => ViewModel.TreeFollowsCurrentFolder = !ViewModel.TreeFollowsCurrentFolder));
            ViewModel.Omnibar.RegisterCommand(new OmnibarCommand("Search everywhere", "find index global", Symbol.Search,
                (OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl") + "+F", null, FocusSearch));
        }

        /// <summary>
        /// A press anywhere outside the omnibar ends editing, as a click away does in Explorer. The mouse's back and
        /// forward (X1/X2) buttons navigate anywhere in the window.
        /// </summary>
        private void OnWindowPointerPressedTunnel(object sender, PointerPressedEventArgs e)
        {
            PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed || kind == PointerUpdateKind.XButton2Pressed)
            {
                System.Windows.Input.ICommand navigate = kind == PointerUpdateKind.XButton1Pressed
                    ? ActivePane.NavigateBackCommand
                    : ActivePane.NavigateForwardCommand;
                if (navigate.CanExecute(null))
                {
                    navigate.Execute(null);
                }

                e.Handled = true;
                return;
            }

            if (ViewModel.Omnibar.IsEditing && !IsInside(e.Source as Visual, _parts.Omnibar))
            {
                ViewModel.Omnibar.CancelEdit();
            }
        }

        // ------------------------------------------------------------------ sidebar rows

        private IBrush ResolveBrush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out object value) && value is IBrush brush ? brush : Brushes.Gray;

        // ------------------------------------------------------------------ Quick access (IPinnedAreaHost)

        IDialogService Views.Sidebar.IPinnedAreaHost.Dialogs => this;

        FileListViewModel Views.Sidebar.IPinnedAreaHost.ActiveFileList => ActivePane;

        /// <summary>
        /// Opens a pinned place: the trash (the Windows Recycle Bin is the shell's own place), a network address (connected
        /// first), or a folder, in the active pane, the other one or a new tab. A pin whose folder is gone offers to unpin it.
        /// </summary>
        public async Task OpenPinAsync(ViewModels.Sidebar.PinnedItemViewModel item, ViewModels.Tabs.FolderOpenTarget target)
        {
            try
            {
                if (item.IsTrash)
                {
                    await OpenTrashAsync(target);
                    return;
                }

                if (Core.Platform.NetworkLocations.IsNetworkAddress(item.Path) && item.Path.Contains("://", StringComparison.Ordinal))
                {
                    await ViewModel.OpenNetworkLocationAsync(item.Path);
                    return;
                }

                ViewModel.Pins.RefreshAvailability();
                if (!item.IsAvailable)
                {
                    bool unpin = await ConfirmAsync($"\"{item.Name}\" isn't available",
                        $"The folder can't be found right now:\n{item.Path}\n\nIt may have been moved or deleted, or be on a drive that isn't connected. Unpin it from Quick access?",
                        "Unpin");
                    if (unpin)
                    {
                        await ViewModel.Pins.UnpinAsync(item);
                    }

                    return;
                }

                await ViewModel.OpenFolderAsync(item.Path, target);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Opening the pinned place {Path} failed", item?.Path);
            }
        }

        public Task DropIntoAsync(IReadOnlyList<string> paths, string folder, bool move) => ActivePane.ExecuteDropAsync(paths, folder, move);

        public Task TrashAsync(IReadOnlyList<string> paths) => ActivePane.DeletePathsAsync(paths, permanent: false);

        /// <summary>
        /// The trash: its view (every item with where it came from, Restore, Delete permanently, Empty) in the pane the
        /// target names, on every system. On Windows the Recycle Bin used to open in Explorer, and elsewhere its files/
        /// folder opened as a plain folder, where restoring wasn't possible.
        /// </summary>
        protected Task OpenTrashAsync(ViewModels.Tabs.FolderOpenTarget target = ViewModels.Tabs.FolderOpenTarget.ActivePane) =>
            ViewModel.OpenFolderAsync(ViewModels.Tabs.TabLocation.Trash, target);

        protected async void OnHomeRowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
            {
                return;
            }

            e.Handled = true;
            try
            {
                ViewModel.ClearSearch(); // as the other rows: the Home dashboard shows instead of the search results
                await ActivePane.LoadHomeAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to open the Home dashboard");
            }
        }

        protected async void OnThisPcRowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
            {
                return;
            }

            e.Handled = true;
            try
            {
                ViewModel.ClearSearch();
                await ActivePane.LoadThisPcAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Failed to open This PC");
            }
        }

        /// <summary>A sidebar row whose Tag is a folder path (frequent folders, drives): left click opens, right click shows its menu.</summary>
        protected async void OnFolderRowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            try
            {
                if (!(sender is Control control) || !(control.Tag is string path) || string.IsNullOrEmpty(path))
                {
                    return;
                }

                e.Handled = true;
                if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    if (Directory.Exists(path))
                    {
                        FileContextMenu.ShowForItems(control, ActivePane, new[] { path }, pathsAreSelection: false);
                    }

                    return;
                }

                if (e.InitialPressMouseButton == MouseButton.Middle)
                {
                    // As a pin's row: a new tab behind this one, as browsers and Explorer do (or the other pane: Settings › Tabs).
                    await ViewModel.OpenWithMiddleClickAsync(path);
                    return;
                }

                if (e.InitialPressMouseButton != MouseButton.Left)
                {
                    return;
                }

                try
                {
                    ViewModel.ClearSearch();
                    await ActivePane.LoadFolderAsync(path);
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Sidebar folder open failed for {Path}", path);
                }
        
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Error(ex, "Sidebar folder row action failed");
            }
        }

        /// <summary>A saved network location row (Tag = <see cref="SavedLocation"/>): left click connects and opens, right click offers removing it.</summary>
        /// <summary>A lens in the left panel: shows it in place of the file list (the same lens again refreshes it).</summary>
        protected void OnLensRowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (sender is Control control && control.Tag is ViewModels.Lens.LensEntryViewModel lens && e.InitialPressMouseButton == MouseButton.Left)
            {
                e.Handled = true;
                ViewModel.OpenLensAsync(lens).Forget("open a lens");
            }
        }

        protected void OnNetworkRowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            try
            {
                if (!(sender is Control control) || !(control.Tag is SavedLocation location))
                {
                    return;
                }

                e.Handled = true;
                if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    FileContextMenu.ShowForNetworkLocation(control,
                        () => ViewModel.OpenNetworkLocationAsync(location.Path),
                        () => ViewModel.NetworkLocations.RemoveAsync(location.Path));
                }
                else if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    ViewModel.OpenNetworkLocationAsync(location.Path).Forget("open network location");
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Network location row action failed");
            }
        }

        /// <summary>One drag-over/leave/drop handler for every sidebar row; the row and its folder come from the event source.</summary>
        private void HookDropTargets()
        {
            if (_parts.Sidebar == null)
            {
                return;
            }

            _parts.Sidebar.AddHandler(DragDrop.DragOverEvent, OnSidebarDragOver);
            _parts.Sidebar.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(null));
            _parts.Sidebar.AddHandler(DragDrop.DropEvent, OnSidebarDrop);
        }

        private static (Border Row, string Folder) SidebarDropTarget(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Border border && border.Classes.Contains("navRow"))
                {
                    return (border, border.Tag as string);
                }
            }

            return (null, null);
        }

        private void OnSidebarDragOver(object sender, DragEventArgs e)
        {
            (Border row, string folder) = SidebarDropTarget(e.Source);
            if (row == null || string.IsNullOrEmpty(folder) || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
            {
                return; // let the navigation tree handle its own rows
            }

            e.DragEffects = FileDragDropHelper.ResolveEffect(e, folder, paths);
            e.Handled = true;
            SetDropHighlight(e.DragEffects != DragDropEffects.None ? row : null);
        }

        private async void OnSidebarDrop(object sender, DragEventArgs e)
        {
            try
            {
                (Border row, string folder) = SidebarDropTarget(e.Source);
                SetDropHighlight(null);
                if (row == null || string.IsNullOrEmpty(folder) || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
                {
                    return;
                }

                e.Handled = true;
                await Views.Menus.DropChoice.RunAsync(row, e, folder, paths, ActivePane);
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Dropping files failed");
            }
        }

        private void SetDropHighlight(Border row)
        {
            if (ReferenceEquals(_dropHighlight, row))
            {
                return;
            }

            _dropHighlight?.Classes.Remove("dropTarget");
            _dropHighlight = row;
            _dropHighlight?.Classes.Add("dropTarget");
        }

        // ------------------------------------------------------------------ panels and panes

        protected void OnToggleLeftPanelClick(object sender, RoutedEventArgs e) => ToggleSidebar();

        /// <summary>The Tags section's + button: a new tag (a name with slashes makes sub-tags).</summary>
        protected void OnNewTagClick(object sender, RoutedEventArgs e) =>
            System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<Views.Sidebar.TagsSidebarView>(this.GetVisualDescendants()))?.CreateTag();

        protected void OnToggleDetailsPanelClick(object sender, RoutedEventArgs e) => ToggleDetailsPanel();

        protected void OnCycleViewClick(object sender, RoutedEventArgs e) => ActivePane.CycleViewPreset();

        protected void ToggleSidebar() => ViewModel.IsNavigationPaneVisible = !ViewModel.IsNavigationPaneVisible;

        protected void ToggleDetailsPanel() => ViewModel.IsDetailsPaneVisible = !ViewModel.IsDetailsPaneVisible;

        private void SetSidebarVisible(bool visible)
        {
            ColumnDefinition column = _parts.SidebarColumn;
            if (!visible)
            {
                if (column.Width.Value > 0)
                {
                    _lastSidebarWidth = column.Width;
                }

                column.MinWidth = 0;
                column.Width = new GridLength(0);
            }
            else
            {
                if (column.Width.Value <= 0)
                {
                    column.Width = _lastSidebarWidth.Value > 0 ? _lastSidebarWidth : new GridLength(_parts.SidebarDefaultWidth);
                }

                column.MinWidth = _parts.SidebarMinWidth;
            }

            column.MaxWidth = _parts.SidebarMaxWidth;
            _parts.Sidebar.IsVisible = visible;
            if (_parts.SidebarSplitter != null)
            {
                _parts.SidebarSplitter.IsVisible = visible;
            }
        }

        private void SetDetailsPanelVisible(bool visible)
        {
            ColumnDefinition column = _parts.DetailsColumn;
            if (!visible)
            {
                if (column.Width.Value > 0)
                {
                    _lastDetailsWidth = column.Width;
                }

                column.MinWidth = 0;
                column.Width = new GridLength(0);
            }
            else
            {
                if (column.Width.Value <= 0)
                {
                    column.Width = _lastDetailsWidth.Value > 0 ? _lastDetailsWidth : new GridLength(_parts.DetailsDefaultWidth);
                }

                column.MinWidth = _parts.DetailsMinWidth;
            }

            _parts.DetailsSplitter.IsVisible = visible;
            _parts.DetailsPanel.IsVisible = visible;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsSplitViewActive):
                    UpdateSplitViewColumns(ViewModel.IsSplitViewActive);
                    UpdateActivePaneMarks();
                    break;
                case nameof(MainViewModel.IsSecondaryPaneFocused):
                    UpdateActivePaneMarks();
                    break;
                case nameof(MainViewModel.IsNavigationPaneVisible):
                case nameof(MainViewModel.IsDetailsPaneVisible):
                case nameof(MainViewModel.IsToolbarVisible):
                case nameof(MainViewModel.IsStatusBarVisible):
                    ApplyPanels();
                    break;
                case nameof(MainViewModel.Theme):
                    App.ApplyTheme(ViewModel.Theme);
                    break;
            }
        }

        /// <summary>
        /// The two pane columns start at zero width: a "*" column would still take half the space with an invisible
        /// pane in it.
        /// </summary>
        private void UpdateSplitViewColumns(bool isActive)
        {
            _parts.PaneGrid.ColumnDefinitions[1].Width = isActive ? GridLength.Auto : new GridLength(0);
            _parts.PaneGrid.ColumnDefinitions[2].Width = isActive ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _parts.PaneSplitter.IsVisible = isActive;
            _parts.SecondaryPane.IsVisible = isActive;
        }

        private void OnPanePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!ViewModel.IsSplitViewActive || !(e.Source is Visual source))
            {
                return;
            }

            if (IsInside(source, _parts.PrimaryPane))
            {
                ViewModel.SetFocusedPane(isSecondary: false);
            }
            else if (IsInside(source, _parts.SecondaryPane))
            {
                ViewModel.SetFocusedPane(isSecondary: true);
            }
        }

        /// <summary>
        /// In split view the active pane (the one the toolbar, address bar, status bar, sidebar and shortcuts act on) has an
        /// accent line along its top; the other one does not. Without it, every layout left the person guessing which pane
        /// a click in the sidebar would change.
        /// </summary>
        private void UpdateActivePaneMarks()
        {
            bool split = ViewModel.IsSplitViewActive;
            bool secondary = split && ViewModel.IsSecondaryPaneFocused;
            _parts.PrimaryPane.Classes.Set("splitPane", split);
            _parts.SecondaryPane.Classes.Set("splitPane", split);
            _parts.PrimaryPane.Classes.Set("activePane", split && !secondary);
            _parts.SecondaryPane.Classes.Set("activePane", secondary);
        }

        /// <summary>The pane that last received keyboard focus is the one the toolbar, address bar and shortcuts act on.</summary>
        private void OnPaneGotFocus(object sender, GotFocusEventArgs e)
        {
            if (!(e.Source is Visual source))
            {
                return;
            }

            if (IsInside(source, _parts.PrimaryPane))
            {
                ViewModel.SetFocusedPane(isSecondary: false);
            }
            else if (_parts.SecondaryPane.IsVisible && IsInside(source, _parts.SecondaryPane))
            {
                ViewModel.SetFocusedPane(isSecondary: true);
            }
        }

        /// <summary>
        /// Failures the view models report are shown as messages; each pane's events are hooked once, when it is made (every
        /// tab's panes), and unhooked when it is released, so a closed tab's pane isn't kept alive by the window.
        /// </summary>
        private void HookFailureMessages(FileListViewModel pane)
        {
            if (pane == null || _paneUnhooks.ContainsKey(pane))
            {
                return;
            }

            EventHandler<string> message = (_, text) => ShowMessage(text);
            EventHandler<string> toast = (_, text) => ShowToast(text); // non-modal: the list is still usable
            pane.OperationFailed += message;
            pane.OpenItemFailed += message;
            pane.GroupedViewUnavailable += message;
            pane.ViewBuildFailed += toast;
            _paneUnhooks[pane] = () =>
            {
                pane.OperationFailed -= message;
                pane.OpenItemFailed -= message;
                pane.GroupedViewUnavailable -= message;
                pane.ViewBuildFailed -= toast;
            };
        }

        private void UnhookFailureMessages(FileListViewModel pane)
        {
            if (pane != null && _paneUnhooks.Remove(pane, out Action unhook))
            {
                unhook();
            }
        }

        // ------------------------------------------------------------------ search

        /// <summary>
        /// Keys in the search box: Escape clears the search, Down moves into the results, Enter opens the selected result
        /// (or the best match), Ctrl+Enter shows it in its folder.
        /// </summary>
        protected void OnGlobalSearchKeyDown(object sender, KeyEventArgs e)
        {
            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.Escape)
            {
                ViewModel.ClearSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && ViewModel.IsSearchActive && _parts.SearchResults != null)
            {
                _parts.SearchResults.FocusResults();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ViewModel.IsSearchActive && _parts.SearchResults?.CurrentOrFirst is ViewModels.Search.SearchResultRow row)
            {
                Task open = e.KeyModifiers == command ? ViewModel.OpenSearchResultLocationAsync(row) : ViewModel.OpenSearchResultAsync(row);
                open.Forget("open search result");
                e.Handled = true;
            }
        }

        private void OnSearchResultsWantSearchBox(object sender, EventArgs e)
        {
            _parts.GlobalSearchBox?.Focus();
            _parts.GlobalSearchBox?.SelectAll();
        }

        // ------------------------------------------------------------------ toast

        private void ShowGreetingIfEnabled()
        {
            if (!(ViewModel.SettingsService?.Current?.Greeting?.EnableGreeting ?? false))
            {
                return;
            }

            ShowToast(GreetingPhrases.RandomGreeting(DateTime.Now));
            ViewModel.SoundFeedbackService?.PlayGreeting();
        }

        /// <summary>
        /// Slides the toast in, holds it, slides it out. The Border's transitions animate Opacity and RenderTransform;
        /// a generation counter makes a newer toast cancel an older one's hide step.
        /// </summary>
        private async void ShowToast(string message)
        {
            try
            {
                int generation = ++_toastGeneration;
                _parts.ToastText.Text = message ?? string.Empty;
                Thickness margin = _parts.ToastBorder.Margin;
                double above = _parts.OperationsPanel?.IsVisible == true ? _parts.OperationsPanel.Bounds.Height + 12 : 0;
                _parts.ToastBorder.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 20 + above);
                _parts.ToastBorder.Opacity = 1;
                _parts.ToastBorder.RenderTransform = TransformOperations.Parse("translateX(0px)");
                await Task.Delay(ToastDwellMs);

                if (generation == _toastGeneration)
                {
                    _parts.ToastBorder.Opacity = 0;
                    _parts.ToastBorder.RenderTransform = TransformOperations.Parse($"translateX({ToastOffscreenX}px)");
                }
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Toast failed");
            }
        }

        // ------------------------------------------------------------------ IDialogService

        public async Task<string> PromptRenameAsync(string currentName, bool isDirectory) =>
            await new RenameDialog(currentName, isDirectory).ShowDialog<string>(this);

        public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, bool permanent)
        {
            string title = permanent ? "Delete permanently" : OperatingSystem.IsWindows() ? "Delete" : "Move to Trash";
            string where = OperatingSystem.IsWindows() ? "the Recycle Bin" : "the Trash";
            string message = DescribeForDelete(paths, permanent, where);
            return ConfirmAsync(title, message, permanent ? "Delete" : OperatingSystem.IsWindows() ? "Delete" : "Move to Trash", destructive: permanent);
        }

        /// <summary>
        /// The delete question naming what goes: "Move "report.pdf" to the Trash?", a folder "and everything in it", or for
        /// several items a list of their names (the first few, then "and 12 more").
        /// </summary>
        internal static string DescribeForDelete(IReadOnlyList<string> paths, bool permanent, string where)
        {
            const int Listed = 5;
            string verb = permanent ? "Permanently delete" : "Move";
            string destination = permanent ? string.Empty : $" to {where}";
            string tail = permanent ? "\n\nThis can't be undone." : string.Empty;
            if (paths.Count == 1)
            {
                string name = Core.PathUtil.GetName(paths[0]);
                bool folder = System.IO.Directory.Exists(paths[0]);
                string what = folder ? $"the folder \"{name}\" and everything in it" : $"\"{name}\"";
                return $"{verb} {what}{destination}?{tail}";
            }

            var list = new System.Text.StringBuilder($"{verb} these {paths.Count:N0} items{destination}?\n");
            for (int i = 0; i < Math.Min(Listed, paths.Count); i++)
            {
                string name = Core.PathUtil.GetName(paths[i]);
                list.Append("\n•  ").Append(name).Append(System.IO.Directory.Exists(paths[i]) ? "  (folder)" : string.Empty);
            }

            if (paths.Count > Listed)
            {
                list.Append($"\n…and {paths.Count - Listed:N0} more");
            }

            return list.Append(tail).ToString();
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => ConfirmAsync(title, message, confirmText, destructive: false);

        private async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive)
        {
            var dialog = new MessageDialog(title, message, confirmText, "Cancel", destructive);
            return await dialog.ShowDialog<bool>(this);
        }

        public void ShowProperties(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return;
            }

            new PropertiesWindow(paths).Show(this);
        }

        public async Task<string> PromptTextAsync(string title, string message, string initialText, string placeholder, string okText, Func<string, string> validate) =>
            await new TextPromptDialog(title, message, initialText, placeholder, okText, validate).ShowDialog<string>(this);

        public Task ShowMessageAsync(string title, string message) =>
            new MessageDialog(title, message, "OK", null, destructive: false).ShowDialog<bool>(this);

        public Task<PasswordAnswer> PromptPasswordAsync(PasswordRequest request) =>
            new PasswordDialog(request).ShowDialog<PasswordAnswer>(this);

        /// <summary>
        /// The name clash question. A cancelled operation closes it (the worker stops waiting); a hidden or closing window
        /// can't show a modal dialog, so the operation is cancelled rather than guessed for.
        /// </summary>
        public async Task<ConflictDecision> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVisible || _closeConfirmed)
            {
                return new ConflictDecision(ConflictChoice.Cancel, false);
            }

            var dialog = new ConflictDialog(conflict);
            using (cancellationToken.Register(() => UiThread.Post(() => dialog.Close())))
            {
                await dialog.ShowDialog(this);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return dialog.Decision;
        }

        /// <summary>The error question; a hidden or closing window can't ask, so the item is skipped (and reported).</summary>
        public async Task<ErrorChoice> ResolveErrorAsync(ErrorInfo error, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVisible || _closeConfirmed)
            {
                return ErrorChoice.Skip;
            }

            var dialog = new ErrorDialog(error);
            using (cancellationToken.Register(() => UiThread.Post(() => dialog.Close())))
            {
                await dialog.ShowDialog(this);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return dialog.Choice;
        }

        /// <summary>
        /// Shows a message without waiting for it. A modal dialog needs a visible owner: while the window is hidden or
        /// closing the message is only logged (ShowDialog would throw).
        /// </summary>
        protected void ShowMessage(string message)
        {
            if (!IsVisible || _closeConfirmed)
            {
                Log.Logger.Information("Message not shown (window not visible): {Message}", message);
                return;
            }

            ShowMessageAsync("ByteExplorer", message).Forget("message dialog");
        }

        /// <summary>A command's work threw: it was logged by the command; tell the person instead of failing silently.</summary>
        /// <summary>A command failed (a static event every window hears): told once, in the window used last.</summary>
        private void OnCommandFailed(object sender, Exception ex)
        {
            if (HasOtherOpenWindows && !ReferenceEquals(MostRecentlyActive, this))
            {
                return;
            }

            ShowMessage("Something went wrong.\n\n" + ex.Message);
        }

        // ------------------------------------------------------------------ helpers

        protected static bool IsInside(Visual visual, Visual ancestor)
        {
            for (Visual current = visual; current != null; current = current.GetVisualParent())
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
