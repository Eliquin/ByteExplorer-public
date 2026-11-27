// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Platform;
using ByteExplorer.Core.Services;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.DefaultApp;
using Serilog;

namespace ByteExplorer.ViewModels.Settings
{
    /// <summary>What the settings view model needs from the main window (implemented by MainWindowBase).</summary>
    public interface ISettingsHost
    {
        /// <summary>The layout has a command toolbar (Modern); Trove has none, so its switch is left out.</summary>
        bool HasToolbar { get; }

        /// <summary>Opens the folder in the main window's file list and selects the file, if one is given.</summary>
        void RevealInFileList(string folder, string fileToSelect);

        /// <summary>Closes the app and starts it again (after the settings are saved).</summary>
        void RestartApplication();

        /// <summary>Opens the tag manager (the tag editor without items).</summary>
        void OpenTagManager();

        /// <summary>The layout's own order of the left panel's sections (keys of Views.Sidebar.SidebarOrder).</summary>
        IReadOnlyList<string> SidebarLayoutOrder { get; }

        /// <summary>The order the left panel shows now (the saved order applied to the layout's sections).</summary>
        IReadOnlyList<string> SidebarEffectiveOrder { get; }
    }

    /// <summary>What the settings view model needs from its own window: pickers, the clipboard and dialogs over it.</summary>
    public interface ISettingsWindowServices
    {
        Task<bool> ConfirmAsync(string title, string message, string confirmText);

        Task ShowMessageAsync(string title, string message);

        Task<string> PickFolderAsync(string title);

        Task<string> PickOpenFileAsync(string title);

        Task<string> PickSaveFileAsync(string title, string suggestedName);

        Task CopyTextAsync(string text);
    }

    /// <summary>
    /// The settings window's view model, laid out like Windows 11 Settings: pages in a navigation list, each a set of
    /// titled groups of cards, and a search across every setting. Every card writes AppSettings directly and saves at
    /// once; a change that the running app can apply is applied right away (theme, panels, menu style, hidden items,
    /// thumbnails, index schedule…), one read only at startup marks the window "restart to apply" and offers to
    /// restart. Values changed elsewhere (the menu, the omnibar, the status bar) are re-read when the main view model
    /// reports a change and when the window is activated, so the window never shows a stale value.
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase, IDisposable
    {
        public const string SearchPageKey = "search";

        private static readonly ILogger Logger = Log.ForContext<SettingsViewModel>();

        private readonly MainViewModel _main;
        private readonly ISettingsHost _host;
        private readonly List<SettingItem> _allItems;
        private ISettingsWindowServices _window;
        private SettingsPage _selectedPage;
        private string _searchText = string.Empty;
        private IReadOnlyList<SettingGroup> _visibleGroups;
        private bool _isRestartPending;
        private FileListViewModel _observedPane;

        public SettingsViewModel(MainViewModel main, ISettingsHost host)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));
            _host = host;
            Pages = BuildPages();
            _allItems = Pages.SelectMany(p => p.Items).ToList();
            foreach (SettingItem item in _allItems)
            {
                item.Changed = OnItemChanged;
            }

            RestartCommand = new RelayCommand(_ => Restart());
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
            _selectedPage = Pages[0];
            _visibleGroups = _selectedPage.Groups;
            _main.PropertyChanged += OnMainPropertyChanged;
            ObservePane(_main.FileListViewModel);
        }

        public IReadOnlyList<SettingsPage> Pages { get; }

        /// <summary>The page in the navigation list; null while search results are shown.</summary>
        public SettingsPage SelectedPage
        {
            get => _selectedPage;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedPage) && !IsSearching)
                {
                    return;
                }

                _selectedPage = value;
                RaisePropertyChanged();
                if (IsSearching)
                {
                    _searchText = string.Empty; // picking a page leaves the search
                    RaisePropertyChanged(nameof(SearchText));
                    RaisePropertyChanged(nameof(IsSearching));
                }

                UpdateVisibleGroups();
            }
        }

        /// <summary>Text of the search box: filters every setting of every page by title, description and keywords.</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    RaisePropertyChanged(nameof(IsSearching));
                    UpdateVisibleGroups();
                }
            }
        }

        public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

        /// <summary>The heading of the content area: the page's title or "Search results".</summary>
        public string Heading => IsSearching ? "Search results" : _selectedPage?.Title;

        /// <summary>The groups shown: the selected page's, or the search results grouped by page.</summary>
        public IReadOnlyList<SettingGroup> VisibleGroups
        {
            get => _visibleGroups;
            private set => SetProperty(ref _visibleGroups, value);
        }

        public bool HasNoResults => IsSearching && _visibleGroups.Count == 0;

        public string NoResultsText => $"No settings match \"{_searchText.Trim()}\".";

        /// <summary>A change was made that applies on the next start: the window shows a bar with "Restart now".</summary>
        public bool IsRestartPending
        {
            get => _isRestartPending;
            private set => SetProperty(ref _isRestartPending, value);
        }

        public ICommand RestartCommand { get; }

        public ICommand ClearSearchCommand { get; }

        /// <summary>The window hands its pickers and clipboard over when it opens.</summary>
        public void AttachWindow(ISettingsWindowServices window) => _window = window;

        /// <summary>Goes to a page by key ("appearance", "search"…).</summary>
        public void ShowPage(string key)
        {
            SettingsPage page = Pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                SelectedPage = page;
            }
        }

        /// <summary>Re-reads every value (the window was activated: settings may have changed from the main window).</summary>
        public void RefreshAll()
        {
            foreach (SettingItem item in _allItems)
            {
                item.Refresh();
            }
        }

        /// <summary>Every setting card, for tests.</summary>
        public IReadOnlyList<SettingItem> AllItems => _allItems;

        public T Find<T>(string title) where T : SettingItem =>
            _allItems.OfType<T>().FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.Ordinal));

        public void Dispose()
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
            ObservePane(null);
        }

        // ------------------------------------------------------------------ change handling

        private AppSettings Settings => _main.SettingsService.Current;

        private void OnItemChanged(SettingItem item)
        {
            if (item.RequiresRestart)
            {
                IsRestartPending = true;
            }

            _main.PersistSettings();
        }

        private void OnMainPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.FileListViewModel))
            {
                ObservePane(_main.FileListViewModel);
            }

            RefreshAll();
        }

        private void ObservePane(FileListViewModel pane)
        {
            if (_observedPane != null)
            {
                _observedPane.PropertyChanged -= OnPanePropertyChanged;
            }

            _observedPane = pane;
            if (_observedPane != null)
            {
                _observedPane.PropertyChanged += OnPanePropertyChanged;
            }
        }

        private void OnPanePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileListViewModel.ShowHiddenItems) || e.PropertyName == nameof(FileListViewModel.ShowSystemItems))
            {
                RefreshAll();
            }
        }

        private void UpdateVisibleGroups()
        {
            if (IsSearching)
            {
                VisibleGroups = Pages
                    .Select(p => new SettingGroup(p.Title, p.Items.Where(i => i.Matches(_searchText)).ToList()))
                    .Where(g => g.Items.Count > 0)
                    .ToList();
            }
            else
            {
                VisibleGroups = _selectedPage.Groups;
            }

            RaisePropertyChanged(nameof(Heading));
            RaisePropertyChanged(nameof(HasNoResults));
            RaisePropertyChanged(nameof(NoResultsText));
        }

        private void Restart()
        {
            _host?.RestartApplication();
        }

        /// <summary>Applies a change to every pane (every tab's, both of a split tab, of every window: the settings are the app's).</summary>
        private void ForEachPane(Action<FileListViewModel> action)
        {
            foreach (FileListViewModel pane in _main.AllPanesOfAllWindows)
            {
                if (pane != null)
                {
                    action(pane);
                }
            }
        }

        // ------------------------------------------------------------------ pages

        private IReadOnlyList<SettingsPage> BuildPages() => new[]
        {
            AppearancePage(),
            LayoutPage(),
            LeftPanelPage(),
            FolderOptionsPage(),
            TabsPage(),
            FilesPage(),
            FileOperationsPage(),
            SearchPage(),
            TagsPage(),
            NetworkPage(),
            SoundsPage(),
            PrivacyPage(),
            AdvancedPage(),
            SettingsFilePage(),
            AboutPage(),
        };

        private SettingsPage AppearancePage()
        {
            var themes = new[] { new SettingOption("System", "Use system setting"), new SettingOption("Light", "Light"), new SettingOption("Dark", "Dark") };
            var layouts = new[]
            {
                new SettingOption(AppearanceSettings.LayoutModern, "Modern"),
                new SettingOption(AppearanceSettings.LayoutTrove, "Trove"),
                new SettingOption(AppearanceSettings.LayoutFolio, "Folio"),
            };
            return new SettingsPage("appearance", "Appearance", Symbol.PaintBrush, new[]
            {
                new SettingGroup("Theme", new SettingItem[]
                {
                    new ChoiceSetting("App theme", "Light or dark colors, or the operating system's choice (followed live)", Symbol.DarkTheme,
                        themes, () => _main.Theme, value => _main.SetThemeCommand.Execute(value)) { Keywords = "dark light mode colors" },
                }),
                new SettingGroup("Window", new SettingItem[]
                {
                    new ChoiceSetting("Window layout", "Modern: address bar and toolbar above the panes. Trove: a full-height sidebar and one title bar. Folio: a Mac-style window, after macOS Finder", Symbol.Window,
                        layouts, () => Settings.Appearance.WindowLayout == AppearanceSettings.LayoutTrove || Settings.Appearance.WindowLayout == AppearanceSettings.LayoutFolio
                            ? Settings.Appearance.WindowLayout
                            : AppearanceSettings.LayoutModern,
                        value => Settings.Appearance.WindowLayout = value) { RequiresRestart = true, Keywords = "nemo modern style" },
                }),
                new SettingGroup("Text", new SettingItem[]
                {
                    new ChoiceSetting("Font", "The typeface of the whole window", Symbol.TextFont, FontOptions(),
                        () => Settings.Appearance.FontFamily, value => Settings.Appearance.FontFamily = value) { RequiresRestart = true, Keywords = "typeface" },
                    new NumberSetting("Base text size", "The base size of text in the window", Symbol.TextFont, 8, 32, 0.5, "pt",
                        () => Settings.Appearance.FontSize, value => Settings.Appearance.FontSize = value) { RequiresRestart = true, Keywords = "font scale bigger smaller" },
                }),
                new SettingGroup("Folders", new SettingItem[]
                {
                    new ColorSetting("Folder color", "The color of folder icons everywhere: the views, the tree, the address bar and the details pane", Symbol.Folder,
                        () => Settings.Appearance.FolderColorHex, value =>
                        {
                            Settings.Appearance.FolderColorHex = value;
                            _main.NotifyCustomizationChanged();
                        }) { Keywords = "folder colour yellow icon" },
                }),
                AreasGroup(),
                BackgroundImageGroup(),
                new SettingGroup("Default look", new SettingItem[]
                {
                    new ActionSetting("Reset to default", "Return the folder color, the window areas' text sizes, colors and backgrounds, the background image and the left panel's section order to the default look",
                        Symbol.ArrowReset, "Reset", new AsyncRelayCommand(_ => ResetLookAsync())) { IsDanger = true, Keywords = "default theme colors fonts background restore customizations" },
                }),
            });
        }

        // ------------------------------------------------------------------ window areas

        /// <summary>The area the "Window areas" group edits (its first item chooses it).</summary>
        private string _selectedArea = AreaAppearance.AddressBar;

        private SettingGroup AreasGroup()
        {
            var areaOptions = AreaAppearance.All.Select(a => new SettingOption(a.Key, a.Label)).ToList();
            var textSize = new NumberSetting("Text size", "The size of the area's text, and its icons with it", Symbol.TextFont, AreaAppearance.MinScale * 100,
                AreaAppearance.MaxScale * 100, 5, "%", () => Math.Round(SelectedArea()?.TextScale * 100 ?? 100),
                value => ChangeArea(a => a.TextScale = value / 100.0)) { Keywords = "font size bigger smaller zoom scale" };
            var textColor = new ColorSetting("Text color", "The color of the area's text and icons", Symbol.TextFont,
                () => SelectedArea()?.TextColorHex ?? string.Empty, value => ChangeArea(a => a.TextColorHex = value)) { Keywords = "foreground font colour" };
            var background = new ColorSetting("Background color", "The color behind the area", Symbol.PaintBrush,
                () => SelectedArea()?.BackgroundColorHex ?? string.Empty, value => ChangeArea(a => a.BackgroundColorHex = value)) { Keywords = "colour fill" };
            var everyArea = new ActionSetting("Every area", "Give all six areas this background color, or take every area's background color off",
                Symbol.PaintBrush, "Use everywhere", new RelayCommand(_ => SetEveryBackground(SelectedArea()?.BackgroundColorHex ?? string.Empty)))
            {
                SecondaryButtonText = "Clear all",
                SecondaryCommand = new RelayCommand(_ => SetEveryBackground(string.Empty)),
                Keywords = "background colour all areas",
            };
            var area = new ChoiceSetting("Area", "The part of the window the settings below change", Symbol.Window, areaOptions,
                () => _selectedArea, value =>
                {
                    _selectedArea = value;
                    textSize.Refresh();
                    textColor.Refresh();
                    background.Refresh();
                }) { Keywords = "address bar toolbar left panel main view details status bar location" };
            _areaItems = new SettingItem[] { area, textSize, textColor, background };
            return new SettingGroup("Window areas", new SettingItem[] { area, textSize, textColor, background, everyArea });
        }

        private SettingItem[] _areaItems = Array.Empty<SettingItem>();

        private AreaAppearance SelectedArea() => Settings.Appearance.FindArea(_selectedArea);

        /// <summary>Changes the selected area; an area back at the theme's look is dropped from the settings.</summary>
        private void ChangeArea(Action<AreaAppearance> change)
        {
            AreaAppearance area = Settings.Appearance.GetOrAddArea(_selectedArea);
            change(area);
            area.Sanitize();
            if (area.IsDefault)
            {
                Settings.Appearance.Areas.Remove(area);
            }

            _main.NotifyCustomizationChanged();
        }

        private void SetEveryBackground(string hex)
        {
            foreach ((string key, _) in AreaAppearance.All)
            {
                AreaAppearance area = Settings.Appearance.GetOrAddArea(key);
                area.BackgroundColorHex = hex;
                area.Sanitize();
                if (area.IsDefault)
                {
                    Settings.Appearance.Areas.Remove(area);
                }
            }

            _main.NotifyCustomizationChanged();
            foreach (SettingItem item in _areaItems)
            {
                item.Refresh();
            }
        }

        private SettingGroup BackgroundImageGroup()
        {
            var fits = new[]
            {
                new SettingOption(AppearanceSettings.ImageFitFill, "Fill (crop to the view)"),
                new SettingOption(AppearanceSettings.ImageFitFit, "Fit (whole image)"),
                new SettingOption(AppearanceSettings.ImageFitStretch, "Stretch"),
                new SettingOption(AppearanceSettings.ImageFitTile, "Tile"),
                new SettingOption(AppearanceSettings.ImageFitCenter, "Center"),
            };
            var image = new FolderSetting("Background image", "A picture behind the files in the main view", Symbol.Image, "None",
                () => string.IsNullOrEmpty(Settings.Appearance.MainViewBackgroundImage) ? null : Settings.Appearance.MainViewBackgroundImage,
                value =>
                {
                    Settings.Appearance.MainViewBackgroundImage = value ?? string.Empty;
                    _main.NotifyCustomizationChanged();
                }) { ClearText = "Remove", Keywords = "wallpaper picture photo main view" };
            image.BrowseCommand = new AsyncRelayCommand(_ => BrowseImageAsync(image));
            return new SettingGroup("Main view background", new SettingItem[]
            {
                image,
                new NumberSetting("Image strength", "How strongly the image shows through (lower keeps the files easier to read)", Symbol.Image, 5, 100, 5, "%",
                    () => Math.Round(Settings.Appearance.MainViewBackgroundImageOpacity * 100),
                    value =>
                    {
                        Settings.Appearance.MainViewBackgroundImageOpacity = value / 100.0;
                        Settings.Appearance.Sanitize();
                        _main.NotifyCustomizationChanged();
                    }) { Keywords = "opacity transparency wallpaper" },
                new ChoiceSetting("Image fit", "How the image fills the main view", Symbol.Image, fits,
                    () => Settings.Appearance.MainViewBackgroundImageFit,
                    value =>
                    {
                        Settings.Appearance.MainViewBackgroundImageFit = value;
                        _main.NotifyCustomizationChanged();
                    }) { Keywords = "stretch tile center wallpaper" },
            });
        }

        private async Task BrowseImageAsync(FolderSetting setting)
        {
            string file = _window == null ? null : await _window.PickOpenFileAsync("Choose a background image").ConfigureAwait(true);
            if (!string.IsNullOrEmpty(file))
            {
                setting.Path = file;
            }
        }

        /// <summary>"Reset to default": the window areas, the background image and the section order go back to the default look.</summary>
        private async Task ResetLookAsync()
        {
            if (_window != null && !await _window.ConfirmAsync("Reset to default",
                    "Return every window area's text size, colors and background, the background image and the left panel's section order to the default look?",
                    "Reset").ConfigureAwait(true))
            {
                return;
            }

            ResetLook();
        }

        /// <summary>The reset itself (no question): applied at once and saved.</summary>
        internal void ResetLook()
        {
            Settings.Appearance.ResetCustomizations();
            Settings.Navigation.SidebarSectionOrder = new List<string>();
            _selectedArea = AreaAppearance.AddressBar;
            _main.NotifyCustomizationChanged();
            RefreshAll();
        }

        private static IReadOnlyList<SettingOption> FontOptions()
        {
            var options = new List<SettingOption> { new SettingOption(AppearanceSettings.LegacyDefaultFontFamily, "Default (Inter)") };
            try
            {
                options.AddRange(FontManager.Current.SystemFonts
                    .Select(f => f.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, AppearanceSettings.LegacyDefaultFontFamily, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .Select(n => new SettingOption(n, n)));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not list the system fonts");
            }

            return options;
        }

        private SettingsPage LayoutPage()
        {
            var panels = new List<SettingItem>
            {
                new ToggleSetting("Navigation pane", "The left panel: Quick access, network locations and the folder tree", Symbol.PanelLeft,
                    () => _main.IsNavigationPaneVisible, v => _main.IsNavigationPaneVisible = v) { Keywords = "sidebar left tree" },
                new ToggleSetting("Details pane", "The right panel: preview and properties of the selection", Symbol.PanelRight,
                    () => _main.IsDetailsPaneVisible, v => _main.IsDetailsPaneVisible = v) { Keywords = "preview right info" },
            };
            if (_host?.HasToolbar ?? true)
            {
                panels.Add(new ToggleSetting("Toolbar", "The command bar under the address bar", Symbol.Apps,
                    () => _main.IsToolbarVisible, v => _main.IsToolbarVisible = v) { Keywords = "command bar" });
            }

            panels.Add(new ToggleSetting("Status bar", "The bar at the bottom: item counts, selection and file operations", Symbol.Table,
                () => _main.IsStatusBarVisible, v => _main.IsStatusBarVisible = v) { Keywords = "bottom" });

            var submenus = new[]
            {
                new SettingOption(AppearanceSettings.SubmenuHorizontal, "Horizontal (beside the menu)"),
                new SettingOption(AppearanceSettings.SubmenuVertical, "Vertical (unfold in place)"),
            };
            return new SettingsPage("layout", "Layout", Symbol.Window, new[]
            {
                new SettingGroup("Panels", panels),
                new SettingGroup("Menu", new SettingItem[]
                {
                    new ChoiceSetting("Menu categories", "How the ≡ menu shows the items of a category such as View or Sort by", Symbol.AppsList, submenus,
                        () => _main.IsMenuSubmenuHorizontal ? AppearanceSettings.SubmenuHorizontal : AppearanceSettings.SubmenuVertical,
                        v => _main.IsMenuSubmenuHorizontal = v == AppearanceSettings.SubmenuHorizontal) { Keywords = "submenu horizontal vertical cascade hamburger" },
                }),
                new SettingGroup("Navigation tree", new SettingItem[]
                {
                    new ToggleSetting("Follow the current folder", "Select the folder shown in the file list in the tree when you navigate from the main view", Symbol.FolderOpen,
                        () => _main.TreeFollowsCurrentFolder, v => _main.TreeFollowsCurrentFolder = v) { Keywords = "sync tree select" },
                    new ToggleSetting("Expand to the current folder", "Open the tree's branches down to a followed folder that is not visible yet", Symbol.FolderOpen,
                        () => _main.ExpandTreeToCurrentFolder, v => _main.ExpandTreeToCurrentFolder = v) { Keywords = "tree reveal" },
                }),
            });
        }

        /// <summary>
        /// One view's item spacing: the empty gap between items, where a rubber band selection can start (a press on an item
        /// drags it instead). Applied to both panes at once.
        /// </summary>
        private NumberSetting SpacingSetting(string title, string what, Func<FileListSettings, int> get, Action<FileListSettings, int> set) =>
            new NumberSetting(title, what + ". The rubber band selection starts in this gap", Symbol.Grid, 0, FileListSettings.MaxItemSpacing, 1, "px",
                () => get(Settings.FileList), value =>
                {
                    set(Settings.FileList, (int)Math.Round(value));
                    Settings.FileList.Sanitize();
                    ForEachPane(p => p.NotifyDisplaySettingsChanged());
                }) { Keywords = "spacing gap padding margin rubber band selection distance items rows" };

        private SettingsPage LeftPanelPage()
        {
            // Without a window (tests of the view model alone), the sections in their usual order.
            IReadOnlyList<string> Defaults() => _host?.SidebarLayoutOrder ?? Views.Sidebar.SidebarOrder.Sections.Select(s => s.Key).ToList();
            IReadOnlyList<string> Current() => _host?.SidebarEffectiveOrder ?? Views.Sidebar.SidebarOrder.Effective(Defaults(), Settings.Navigation.SidebarSectionOrder);
            var designs = new[]
            {
                new SettingOption(NavigationSettings.SidebarDesignClassic, "Classic: lines between sections"),
                new SettingOption(NavigationSettings.SidebarDesignQuiet, "Quiet labels: small capitals, compact"),
                new SettingOption(NavigationSettings.SidebarDesignCards, "Soft cards: tinted groups with icons"),
                new SettingOption(NavigationSettings.SidebarDesignGuides, "Icon headers with guides: like the tree"),
                new SettingOption(NavigationSettings.SidebarDesignChips, "Folded chips: collapsed sections as chips"),
            };
            return new SettingsPage("leftpanel", "Left panel", Symbol.PanelLeft, new[]
            {
                new SettingGroup("Design", new SettingItem[]
                {
                    new ChoiceSetting("Left panel design", "How the sections look in the Modern layout. Only the look changes: sections open, close, add and move the same way in every design", Symbol.PaintBrush, designs,
                        () => Settings.Navigation.SidebarDesign ?? NavigationSettings.SidebarDesignClassic,
                        v =>
                        {
                            Settings.Navigation.SidebarDesign = v;
                            _main.NotifyCustomizationChanged(); // applied at once
                        }) { Keywords = "sidebar navigation pane look style theme compact cards chips quiet guides headers" },
                }),
                new SettingGroup("Sections", new SettingItem[]
                {
                    new OrderSetting("Section order", "The order of the sections in the left panel, from top to bottom", Symbol.TextBulletList,
                        Current, Defaults, Views.Sidebar.SidebarOrder.LabelFor,
                        order =>
                        {
                            Settings.Navigation.SidebarSectionOrder = order ?? new List<string>();
                            _main.NotifyCustomizationChanged();
                        }) { Keywords = "sidebar navigation pane reorder move sections quick access tags network drives" },
                }),
            });
        }

        /// <summary>
        /// Windows Explorer's Folder Options, in one place: how a window opens, how items open, what the views show, the
        /// tree, and what the app keeps on the recent lists. Every change applies at once (MainViewModel.ApplyFolderOptions).
        /// </summary>
        /// <summary>Tabs: where the bar sits, what a new tab shows, how folders open in tabs.</summary>
        private SettingsPage TabsPage()
        {
            TabSettings Tabs() => Settings.Tabs;
            void Set(Action<TabSettings> change)
            {
                change(Settings.Tabs);
                _main.NotifyTabSettingsChanged(); // the bar moves, shows or hides at once, in every window
            }

            var placement = new[]
            {
                new SettingOption(TabSettings.PlacementTitleBar, "Title bar (as Windows 11; Modern layout)"),
                new SettingOption(TabSettings.PlacementTop, "Top (above the files)"),
                new SettingOption(TabSettings.PlacementLeft, "Left (vertical tabs)"),
                new SettingOption(TabSettings.PlacementRight, "Right (vertical tabs)"),
            };
            var newTab = new[]
            {
                new SettingOption(TabSettings.NewTabHome, "Home"),
                new SettingOption(TabSettings.NewTabThisPc, "This PC"),
                new SettingOption(TabSettings.NewTabCurrentFolder, "The folder of the current tab"),
            };
            var middle = new[]
            {
                new SettingOption(TabSettings.MiddleClickNewTab, "Opens it in a new tab"),
                new SettingOption(TabSettings.MiddleClickOtherPane, "Opens it in the other pane (split view)"),
            };

            return new SettingsPage("tabs", "Tabs", Symbol.TabDesktop, new[]
            {
                new SettingGroup("Tab bar", new SettingItem[]
                {
                    new ChoiceSetting("Tab bar position", "In the title bar, above the files, or a vertical list at the left or right (easier to read with many tabs). The Trove and Folio layouts show title bar tabs on top. Also in the tab bar's right-click menu", Symbol.PanelTopExpand, placement,
                        () => Tabs().Placement, v => Set(t => t.Placement = v)) { Keywords = "vertical horizontal side top left right edge" },
                    new ToggleSetting("Collapse vertical tabs to icons", "Vertical tabs show only their icons (point at one for its name). The bar's own button switches it too", Symbol.PanelLeftContract,
                        () => Tabs().VerticalCollapsed, v => Set(t => t.VerticalCollapsed = v)) { Keywords = "compact narrow icons" },
                    new ToggleSetting("Show the tab bar with only one tab", "Off: the bar appears when a second tab is opened", Symbol.TabDesktop,
                        () => Tabs().ShowWithOneTab, v => Set(t => t.ShowWithOneTab = v)) { Keywords = "hide single" },
                }),
                new SettingGroup("Opening tabs", new SettingItem[]
                {
                    new ChoiceSetting("A new tab shows", "What Ctrl+T and the + button open", Symbol.TabAdd, newTab,
                        () => Tabs().NewTabOpens, v => Set(t => t.NewTabOpens = v)) { Keywords = "new tab page start" },
                    new ChoiceSetting("Middle-clicking a folder", "In the file list, the folder tree, Quick access and Recent", Symbol.FolderOpen, middle,
                        () => Tabs().MiddleClickOpens, v => Set(t => t.MiddleClickOpens = v)) { Keywords = "middle click wheel button other pane split" },
                    new ToggleSetting("Switch to a folder opened in a new tab", "Off: a middle-clicked folder opens in a tab behind the current one, as links do in a browser", Symbol.TabDesktop,
                        () => Tabs().SwitchToNewTab, v => Set(t => t.SwitchToNewTab = v)) { Keywords = "background foreground focus" },
                }),
            });
        }

        private SettingsPage FolderOptionsPage()
        {
            FolderOptionsSettings Options() => Settings.FolderOptions;
            void Set(Action<FolderOptionsSettings> change)
            {
                change(Settings.FolderOptions);
                _main.ApplyFolderOptions();
            }

            var openAt = new[]
            {
                new SettingOption(FolderOptionsSettings.OpenAtHome, "Home"),
                new SettingOption(FolderOptionsSettings.OpenAtThisPc, "This PC"),
                new SettingOption(FolderOptionsSettings.OpenAtFolder, "A folder of my choice"),
                new SettingOption(FolderOptionsSettings.OpenAtLastSession, "Where I left off"),
            };
            var clicks = new[]
            {
                new SettingOption("Double", "Double-click to open"),
                new SettingOption("Single", "Single-click to open"),
            };
            var typing = new[]
            {
                new SettingOption(FolderOptionsSettings.TypeSelects, "Select the typed item in the view"),
                new SettingOption(FolderOptionsSettings.TypeSearches, "Type into the search box"),
            };
            var startupFolder = new FolderSetting("Folder to open", "Used when \"Open ByteExplorer to\" is \"A folder of my choice\"", Symbol.FolderOpen,
                "Not chosen (opens Home)", () => Options().StartupFolder, v => Set(o => o.StartupFolder = v ?? string.Empty)) { Keywords = "start startup launch" };
            startupFolder.BrowseCommand = new AsyncRelayCommand(_ => BrowseFolderAsync(startupFolder));

            var view = new List<SettingItem>
            {
                new ToggleSetting("Show hidden files and folders", "Files and folders marked hidden (and names starting with a dot on Linux and macOS)", Symbol.Eye,
                    () => Settings.Navigation.ShowHiddenItems, v => SetShowHidden(v)) { Keywords = "dotfiles" },
                new ToggleSetting("Show protected operating system files", "Files and folders the operating system marks as system files", Symbol.Shield,
                    () => Settings.Navigation.ShowSystemItems, v => SetShowSystem(v)),
                new ToggleSetting("Show thumbnails", "Picture and video previews instead of file type icons. Off: always show icons, never thumbnails", Symbol.Image,
                    () => Settings.FileList.ShowThumbnails, v =>
                    {
                        Settings.FileList.ShowThumbnails = v;
                        ForEachPane(p => p.NotifyDisplaySettingsChanged());
                    }) { Keywords = "preview images" },
                new ToggleSetting(
                    OperatingSystem.IsWindows() ? "Show previews of documents (Windows preview handlers)" : "Show previews of documents",
                    OperatingSystem.IsWindows()
                        ? "Word, Excel, PowerPoint, PDF and other files in the details pane, shown by the same preview handlers as in File Explorer (run in a separate process). Off: no preview handler is ever started"
                        : "In the details pane: the first page of a PDF (needs pdftoppm, from the poppler-utils package) and the text of Word (.docx) and OpenDocument (.odt) files",
                    Symbol.DocumentText,
                    () => Settings.FileList.ShowDocumentPreviews, v =>
                    {
                        Settings.FileList.ShowDocumentPreviews = v;
                        ForEachPane(p => p.Preview.Reload());
                    }) { Keywords = "preview pane office word excel powerpoint pdf document handler details" },
                new ToggleSetting("Hide extensions for known file types", "\"Report\" instead of \"Report.pdf\" for the types ByteExplorer knows. Renaming keeps the extension", Symbol.DocumentText,
                    () => Options().HideKnownExtensions, v => Set(o => o.HideKnownExtensions = v)) { Keywords = "file name extension suffix" },
                new ToggleSetting("Hide empty drives", "Leave out card readers without a card and empty disc drives", Symbol.HardDrive,
                    () => Options().HideEmptyDrives, v => Set(o => o.HideEmptyDrives = v)) { Keywords = "removable card reader dvd" },
            };
            if (OperatingSystem.IsWindows())
            {
                // Windows only: the icons come from the Windows shell (elsewhere ByteExplorer's own icons are all there is).
                view.Add(new ToggleSetting("Show Windows icons",
                    "File Explorer's icons for files (each program's own icon, documents by the app that opens them) and drives. Folders keep ByteExplorer's folder icon and color. Off: ByteExplorer's own icons, colored by kind",
                    Symbol.Image, () => Settings.FileList.UseShellIcons, v =>
                    {
                        Settings.FileList.UseShellIcons = v;
                        _main.NotifySystemIconsChanged();
                    }) { Keywords = "icon shell explorer system native" });
                view.Add(new ToggleSetting("Show drive letters", "\"Local Disk (C:)\"; off: \"Local Disk\"", Symbol.HardDrive,
                    () => Options().ShowDriveLetters, v => Set(o => o.ShowDriveLetters = v)) { Keywords = "drive letter" });
                view.Add(new ToggleSetting("Drive letters before the name", "\"(C:) Local Disk\" instead of \"Local Disk (C:)\"", Symbol.HardDrive,
                    () => Options().DriveLettersFirst, v => Set(o => o.DriveLettersFirst = v)) { Keywords = "drive letter first" });
            }

            view.Add(new ToggleSetting("Display the full path in the title bar", "The window's title is the open folder's path instead of \"ByteExplorer\"", Symbol.Window,
                () => Options().FullPathInTitleBar, v => Set(o => o.FullPathInTitleBar = v)) { Keywords = "title caption path" });
            view.Add(new ToggleSetting("Use check boxes to select items", "A check box on each item: tick several items without holding Ctrl (touch screens, touchpads)", Symbol.SelectAllOn,
                () => Options().UseCheckBoxes, v => Set(o => o.UseCheckBoxes = v)) { Keywords = "checkbox select touch" });
            view.Add(new ToggleSetting("Show pop-up descriptions for items", "Pointing at an item shows its type, size and date", Symbol.Info,
                () => Options().ShowItemTooltips, v => Set(o => o.ShowItemTooltips = v)) { Keywords = "tooltip tip hover" });
            view.Add(new ToggleSetting("Display file size in pop-up descriptions", "Include each file's size in its pop-up description", Symbol.Info,
                () => Options().ShowSizeInTooltips, v => Set(o => o.ShowSizeInTooltips = v)) { Keywords = "tooltip size folder tips" });

            return new SettingsPage("folderoptions", "Folder options", Symbol.Settings, new[]
            {
                DefaultFileManagerGroup(),
                new SettingGroup("General", new SettingItem[]
                {
                    new ChoiceSetting("Open ByteExplorer to", "Where a new window opens. \"Where I left off\" reopens the tabs open at the last close, each with its folders and split view. Pinned tabs always come back", Symbol.Home, openAt,
                        () => Options().OpenAt, v => Set(o => o.OpenAt = v)) { Keywords = "start startup launch restore previous session" },
                    startupFolder,
                    new ChoiceSetting("Click items as follows", "Single-click: names are underlined when pointed at. Ctrl+click and Shift+click select in both modes", Symbol.Open, clicks,
                        () => Options().OpenWithSingleClick ? "Single" : "Double", v => Set(o => o.OpenWithSingleClick = v == "Single")) { Keywords = "single click double click open" },
                    new ChoiceSetting("Right-click menu",
                        Infrastructure.Shell.ShellContextMenu.IsSupported
                            ? "Hybrid: ByteExplorer's menu with \"Show more options\" for the Windows one (or Shift+right-click), as Windows 11. Windows: the Windows menu, with every program's entries (7-Zip, Git, antivirus…)"
                            : "The Windows menus are available on Windows only; ByteExplorer's menu is used here",
                        Symbol.TextBulletList, new[]
                        {
                            new SettingOption(FolderOptionsSettings.ContextMenuHybrid, "Hybrid: ByteExplorer's, with Show more options"),
                            new SettingOption(FolderOptionsSettings.ContextMenuByteExplorer, "ByteExplorer's menu"),
                            new SettingOption(FolderOptionsSettings.ContextMenuWindows, "Windows menu"),
                        },
                        () => Options().ContextMenu, v => Set(o => o.ContextMenu = v))
                    {
                        IsAvailable = Infrastructure.Shell.ShellContextMenu.IsSupported,
                        Keywords = "context menu right click shell windows explorer show more options extensions 7-zip",
                    },
                    new ChoiceSetting("When typing into the file list", "Jump to the item whose name starts with what you type, or search for it", Symbol.Search, typing,
                        () => Options().TypingInList, v => Set(o => o.TypingInList = v)) { Keywords = "type ahead keyboard search" },
                }),
                new SettingGroup("View", view),
                new SettingGroup("Navigation pane", new SettingItem[]
                {
                    new ToggleSetting("Show all folders", "Your home folder, with its subfolders, above This PC in the folder tree", Symbol.Folder,
                        () => Options().ShowAllFolders, v => Set(o => o.ShowAllFolders = v)) { Keywords = "tree home user folder" },
                    new ToggleSetting("Show phones and cameras", "Phones and cameras connected by USB, in This PC and the folder tree. Off: ByteExplorer never talks to them", Symbol.Phone,
                        () => Options().ShowPhones, v => Set(o => o.ShowPhones = v)) { Keywords = "android mtp usb device portable" },
                }),
                new SettingGroup("Privacy", new SettingItem[]
                {
                    new ToggleSetting("Show recently used files", "On Home and This PC", Symbol.History,
                        () => Options().ShowRecentFiles, v => Set(o => o.ShowRecentFiles = v)) { Keywords = "recent files history" },
                    new ToggleSetting("Show frequently used folders", "On Home and in the left panel", Symbol.Star,
                        () => Options().ShowFrequentFolders, v => Set(o => o.ShowFrequentFolders = v)) { Keywords = "frequent folders favorites history" },
                    new ActionSetting("Clear activity history", "Forget every visited folder, opened file and search", Symbol.Delete,
                        "Clear", new AsyncRelayCommand(_ => ClearActivityAsync())) { IsDanger = true, Keywords = "history delete recent" },
                }),
            });
        }

        /// <summary>
        /// "Make ByteExplorer the default file explorer": the switch reads the system's state each time (the registry on
        /// Windows, xdg-mime on Linux), so it never claims what the system doesn't do; a failed change shows why.
        /// </summary>
        private SettingGroup DefaultFileManagerGroup()
        {
            bool supported = DefaultFileManager.IsSupported;
            string description = !supported
                ? DefaultFileManager.UnsupportedReason
                : OperatingSystem.IsWindows()
                    ? "Folders and drives you open on the desktop or from other programs, and Win+E, open in ByteExplorer (in a new tab when it is running). Control Panel, the Recycle Bin and other system folders stay in File Explorer. Off: File Explorer again"
                    : "Folders opened from the desktop and from other apps open in ByteExplorer (in a new tab when it is running). Off: your previous file manager again";
            var items = new List<SettingItem>
            {
                new ToggleSetting("Make ByteExplorer the default file explorer", description, Symbol.Folder,
                    () => supported && DefaultFileManager.IsDefault, SetDefaultFileManager)
                {
                    IsAvailable = supported, Keywords = "default file manager explorer replace open folders win+e association",
                },
            };
            if (OperatingSystem.IsWindows())
            {
                items.Add(new InfoSetting("Before removing ByteExplorer", "Turn the switch off first. If ByteExplorer was deleted while still the default, double-click this file (kept while the switch is on) to give folders back to File Explorer", Symbol.Info,
                    () => DefaultFileManager.RestoreScriptPath)
                {
                    ButtonText = "Copy path", Command = new AsyncRelayCommand(_ => CopyAsync(DefaultFileManager.RestoreScriptPath)), Keywords = "uninstall restore explorer reg",
                });
            }

            return new SettingGroup("Default file explorer", items);
        }

        private void SetDefaultFileManager(bool on)
        {
            try
            {
                DefaultFileManager.SetDefault(on);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Error(ex, "Could not change the default file manager");
                _window?.ShowMessageAsync("Could not change the default file explorer", FriendlyErrors.Explain(ex)).Forget("default file manager message");
            }
        }

        private SettingsPage FilesPage() => new SettingsPage("files", "Files and folders", Symbol.Folder, new[]
        {
            new SettingGroup("Item spacing", new SettingItem[]
            {
                SpacingSetting("Details view", "Space between rows", f => f.ItemSpacingDetails, (f, v) => f.ItemSpacingDetails = v),
                SpacingSetting("List view", "Space between rows", f => f.ItemSpacingList, (f, v) => f.ItemSpacingList = v),
                SpacingSetting("Icons views", "Space around each item, at every icon size", f => f.ItemSpacingIcons, (f, v) => f.ItemSpacingIcons = v),
                SpacingSetting("Tiles view", "Space around each tile", f => f.ItemSpacingTiles, (f, v) => f.ItemSpacingTiles = v),
            }),
            new SettingGroup("Folder sizes", new SettingItem[]
            {
                new ToggleSetting("Calculate folder sizes automatically", "Count every folder's size in the open folder as it opens, on this computer's drives (never network drives or phones). Off: right-click › Calculate size, or Alt+Shift+Enter for all", Symbol.Storage,
                    () => Settings.FileList.AutoCalculateFolderSizes, v =>
                    {
                        Settings.FileList.AutoCalculateFolderSizes = v;
                        if (v)
                        {
                            ForEachPane(p => p.StartAutomaticFolderSizesNow());
                        }
                    }) { Keywords = "folder size directory total space calculate" },
            }),
            new SettingGroup("Opening", new SettingItem[]
            {
                new ToggleSetting("Open archives as folders",
                    "Browse ZIP and TAR files (and 7z, RAR, ISO, CAB, .xz… when bsdtar or 7-Zip is installed) like folders, read-only. Off: they open in their associated app",
                    Symbol.FolderZip,
                    () => Settings.FileList.OpenZipFilesAsFolders, v => Settings.FileList.OpenZipFilesAsFolders = v) { Keywords = "archive compressed zip tar 7z rar iso gz extract" },
            }),
            new SettingGroup("Live updates", new SettingItem[]
            {
                new ToggleSetting("Update the open folder", "Show files as they are added, changed or removed on disk. Applies to folders opened from now on", Symbol.ArrowSync,
                    () => Settings.FileList.EnableLiveFolderWatching, v => Settings.FileList.EnableLiveFolderWatching = v) { Keywords = "watch refresh automatic" },
                new ToggleSetting("Update the folder tree", "Keep expanded folders in the tree in step with the disk. Applies to folders expanded from now on", Symbol.ArrowSync,
                    () => Settings.Navigation.EnableLiveFolderWatching, v => Settings.Navigation.EnableLiveFolderWatching = v) { Keywords = "watch refresh automatic" },
                new NumberSetting("Watched tree folders", "At most this many expanded tree folders are watched at once", Symbol.Folder, 4, 500, 1, "folders",
                    () => Settings.Navigation.MaxLiveWatchedFolders, v => Settings.Navigation.MaxLiveWatchedFolders = (int)v) { Keywords = "watch limit" },
            }),
            new SettingGroup("Large folders", new SettingItem[]
            {
                new NumberSetting("Item limit for Timeline and Nexus", "These views lay out a whole folder at once; larger folders open in Details instead", Symbol.Grid, 100, 1000000, 100, "items",
                    () => Settings.FileList.MaxItemsForGroupedViews, v => Settings.FileList.MaxItemsForGroupedViews = (int)v) { Keywords = "grouped views big" },
            }),
        });

        private void SetShowHidden(bool value)
        {
            // The pane's command flips the setting, saves it and reloads the lists that show hidden items.
            if (Settings.Navigation.ShowHiddenItems != value)
            {
                _main.FileListViewModel.ToggleShowHiddenItemsCommand.Execute(null);
            }
        }

        private void SetShowSystem(bool value)
        {
            if (Settings.Navigation.ShowSystemItems != value)
            {
                _main.FileListViewModel.ToggleShowSystemItemsCommand.Execute(null);
            }
        }

        private SettingsPage FileOperationsPage()
        {
            var verification = new[]
            {
                new SettingOption("None", "None (fastest)"),
                new SettingOption("Size", "Compare sizes"),
                new SettingOption("Sha256", "Compare contents (SHA-256)"),
            };
            const double Gb = 1024d * 1024 * 1024;
            var backupFolder = new FolderSetting("Backup folder", "Where copies are kept while an operation runs. Choose another drive to survive a failing disk", Symbol.Folder,
                "Default: the system's temporary folder", () => Settings.FileList.BackupFolderPath, v => Settings.FileList.BackupFolderPath = v) { Keywords = "backup location" };
            backupFolder.BrowseCommand = new AsyncRelayCommand(_ => BrowseFolderAsync(backupFolder));
            return new SettingsPage("operations", "File operations", Symbol.DocumentCopy, new[]
            {
                new SettingGroup("Safety net", new SettingItem[]
                {
                    new ToggleSetting("Back up before changing files", "Keep a copy of what a move, rename or delete changes until it has succeeded", Symbol.Shield,
                        () => Settings.FileList.BackupBeforeFileOperation, v => Settings.FileList.BackupBeforeFileOperation = v) { Keywords = "backup safety undo" },
                    backupFolder,
                    new NumberSetting("Keep backups for", "Older backups are deleted at startup (0 keeps none after success)", Symbol.History, 0, 365, 1, "days",
                        () => Settings.FileList.BackupRetentionDays, v => Settings.FileList.BackupRetentionDays = (int)v) { Keywords = "backup retention" },
                    new NumberSetting("Largest backup", "Operations bigger than this run without a backup", Symbol.Storage, 0, 1024, 0.5, "GB",
                        () => Settings.FileList.BackupMaxTotalSizeBytes / Gb, v => Settings.FileList.BackupMaxTotalSizeBytes = (long)(v * Gb)) { Keywords = "backup size limit" },
                }),
                new SettingGroup("When an item fails", new SettingItem[]
                {
                    new ToggleSetting("Ask what to do", "Stop and ask (Retry, Skip, Skip all, Cancel) when an item can't be copied, moved or deleted. Off: skip it and list every failure at the end",
                        Symbol.ErrorCircle, () => Settings.FileList.AskOnFileOperationErrors, v => Settings.FileList.AskOnFileOperationErrors = v) { Keywords = "error retry skip prompt locked in use" },
                }),
                new SettingGroup("Verification", new SettingItem[]
                {
                    new ChoiceSetting("Check copies", "How every copied or moved file is confirmed before it takes its name (and, for a move, before the original is removed)", Symbol.Shield, verification,
                        () => Settings.FileList.VerificationMode, v => Settings.FileList.VerificationMode = v) { Keywords = "verify hash checksum integrity" },
                }),
                new SettingGroup("Retries", new SettingItem[]
                {
                    new NumberSetting("Retries", "How often a briefly locked or unreachable file is tried again", Symbol.ArrowClockwise, 0, 20, 1, "times",
                        () => Settings.FileList.MaxRetries, v => Settings.FileList.MaxRetries = (int)v) { Keywords = "retry locked" },
                    new NumberSetting("First wait", "The pause before the first retry; it doubles with each one", Symbol.ArrowClockwise, 0, 60000, 50, "ms",
                        () => Settings.FileList.InitialRetryDelayMs, v => Settings.FileList.InitialRetryDelayMs = (int)v) { Keywords = "retry delay" },
                    new NumberSetting("Longest wait", "The pause between retries never grows past this", Symbol.ArrowClockwise, 0, 120000, 100, "ms",
                        () => Settings.FileList.MaxRetryDelayMs, v => Settings.FileList.MaxRetryDelayMs = (int)v) { Keywords = "retry delay" },
                }),
            });
        }

        private SettingsPage SearchPage()
        {
            GlobalSearchServiceAccess search = new GlobalSearchServiceAccess(_main);
            var locations = new ListSetting("Indexed locations", "Folders the search index covers. Changes apply when the index is rebuilt", Symbol.Folder, true,
                "Every drive on this computer", () => Settings.Index.Locations) { Keywords = "index folders roots" };
            locations.BrowseCommand = new AsyncRelayCommand(_ => BrowseIntoListAsync(locations));
            var excludedFolders = new ListSetting("Excluded folders", "Folders, and everything in them, the index leaves out", Symbol.Folder, true,
                "None", () => Settings.Index.ExcludedFolders) { Keywords = "index skip ignore" };
            excludedFolders.BrowseCommand = new AsyncRelayCommand(_ => BrowseIntoListAsync(excludedFolders));
            var excludedNames = new ListSetting("Excluded names", "Files and folders with these names are left out wherever they are (for example node_modules or .git)", Symbol.Filter, false,
                "None", () => Settings.Index.ExcludedNames) { Keywords = "index skip ignore patterns" };
            return new SettingsPage(SearchPageKey, "Search", Symbol.Search, new[]
            {
                new SettingGroup("Index", new SettingItem[]
                {
                    new InfoSetting("Index status", "What the search index holds and when it was built", Symbol.Search, () => _main.IndexDetailStatus),
                    new ActionSetting("Rebuild the index", "Scan the indexed locations again now; search keeps working meanwhile", Symbol.ArrowClockwise,
                        "Rebuild now", _main.RefreshIndexCommand) { SecondaryButtonText = "Stop", SecondaryCommand = _main.StopIndexingCommand, IsAccent = true, Keywords = "refresh reindex" },
                }),
                new SettingGroup("What is indexed", new SettingItem[] { locations, excludedFolders, excludedNames }),
                new SettingGroup("File contents", new SettingItem[]
                {
                    new ToggleSetting("Index file contents", "Read the text inside documents, PDFs and code in the background, so content: searches work. Off deletes the content index",
                        Symbol.DocumentText, () => Settings.Index.IndexFileContents, v =>
                        {
                            Settings.Index.IndexFileContents = v;
                            search.ApplyContent();
                        }) { Keywords = "content text inside full-text pdf word" },
                    new ToggleSetting("Search file contents", "Words typed in the search box also find files by their text, listed after the name matches",
                        Symbol.Search, () => Settings.Index.SearchFileContents, v => Settings.Index.SearchFileContents = v) { Keywords = "content text inside full-text" },
                    new NumberSetting("Largest file to read", "Files larger than this are found by name only", Symbol.Storage, 1, 500, 1, "MB",
                        () => Settings.Index.ContentMaxFileSizeMB, v =>
                        {
                            Settings.Index.ContentMaxFileSizeMB = (int)Math.Round(v);
                            search.ApplyContent();
                        }) { Keywords = "content size limit" },
                    new InfoSetting("Content index status", "Files whose contents are indexed, and progress", Symbol.DocumentText,
                        () => _main.SearchService?.ContentStatus ?? string.Empty) { Keywords = "content progress" },
                }),
                new SettingGroup("Keeping it current", new SettingItem[]
                {
                    new ToggleSetting("Follow changes", "Update the index as files are created, renamed and deleted", Symbol.ArrowSync,
                        () => Settings.Index.WatchForChanges, v => Settings.Index.WatchForChanges = v) { Keywords = "watch live" },
                    new ToggleSetting("Rebuild automatically", "Rebuild the whole index on a schedule, to catch what change tracking missed", Symbol.History,
                        () => Settings.Index.EnableAutoRebuild, v =>
                        {
                            Settings.Index.EnableAutoRebuild = v;
                            search.ApplySchedule();
                        }) { Keywords = "schedule" },
                    new NumberSetting("Rebuild every", "Time between automatic rebuilds", Symbol.History, 0.5, 720, 0.5, "hours",
                        () => Settings.Index.AutoRebuildIntervalHours, v =>
                        {
                            Settings.Index.AutoRebuildIntervalHours = v;
                            search.ApplySchedule();
                        }) { Keywords = "schedule interval" },
                }),
            });
        }

        /// <summary>The search service is internal to the main view model; this keeps the lambdas above short.</summary>
        private sealed class GlobalSearchServiceAccess
        {
            private readonly MainViewModel _main;

            public GlobalSearchServiceAccess(MainViewModel main) => _main = main;

            public void ApplySchedule() => _main.SearchService?.ApplyScheduleSettings();

            /// <summary>On a worker thread: turning content indexing off waits for the indexer to stop and deletes its files.</summary>
            public void ApplyContent()
            {
                ByteExplorer.Infrastructure.Search.GlobalSearchService service = _main.SearchService;
                if (service != null)
                {
                    Task.Run(service.ApplyContentSettings);
                }
            }
        }

        private SettingsPage TagsPage()
        {
            ByteExplorer.Core.Tags.TagStore store = _main.Tags;
            return new SettingsPage("tags", "Tags", Symbol.Bookmark, new[]
            {
                new SettingGroup("Your tags", new SettingItem[]
                {
                    new InfoSetting("Tags in use", "Right-click files and folders › Tags to tag them; the sidebar's Tags section lists every tag", Symbol.Bookmark,
                        () => $"{store.Tags.Count:N0} tag{(store.Tags.Count == 1 ? string.Empty : "s")} on {store.ItemCount:N0} item{(store.ItemCount == 1 ? string.Empty : "s")}"),
                    new ActionSetting("Manage tags", "Create, rename, recolor, nest and describe tags", Symbol.Settings,
                        "Open", new RelayCommand(_ => _host?.OpenTagManager())) { IsAccent = true, Keywords = "edit color sub-tags rename delete" },
                }),
                new SettingGroup("Upkeep", new SettingItem[]
                {
                    new ActionSetting("Remove tags of missing items",
                        "Done at every start too. Items on a drive or share that is not connected keep their tags", Symbol.Delete,
                        "Clean up", new AsyncRelayCommand(_ => CleanUpTagsAsync(store))) { Keywords = "prune deleted orphan" },
                    new InfoSetting("Tags file", "Tags and comments are kept here, with the paths in your user folders saved so they work on another computer", Symbol.DocumentSettings,
                        () => store.FilePath ?? "(not saved)") { ButtonText = "Show", Command = new RelayCommand(_ => Reveal(store.FilePath)), Keywords = "tags.json location" },
                }),
            });
        }

        private async Task CleanUpTagsAsync(ByteExplorer.Core.Tags.TagStore store)
        {
            int removed = await store.PruneMissingAsync().ConfigureAwait(true);
            RefreshAll();
            if (_window != null)
            {
                await _window.ShowMessageAsync("Tags", removed == 0
                    ? "Every tagged item is still there."
                    : $"Removed the tags of {removed:N0} item{(removed == 1 ? string.Empty : "s")} that no longer exist.").ConfigureAwait(true);
            }
        }

        private SettingsPage NetworkPage() => new SettingsPage("network", "Network", Symbol.Globe, new[]
        {
            new SettingGroup("Timeouts", new SettingItem[]
            {
                new NumberSetting("Opening a network folder", "How long a network folder may take to list before it reports a timeout", Symbol.Globe, 1000, 120000, 500, "ms",
                    () => Settings.FileList.NetworkEnumerationTimeoutMs, v => Settings.FileList.NetworkEnumerationTimeoutMs = (int)v) { Keywords = "smb share slow" },
                new NumberSetting("Expanding a network drive", "How long a network drive in the tree may take to respond", Symbol.Server, 500, 60000, 500, "ms",
                    () => Settings.Navigation.NetworkDriveTimeoutMs, v => Settings.Navigation.NetworkDriveTimeoutMs = (int)v) { Keywords = "smb share slow tree" },
                new NumberSetting("Checking that a drive is there", "The quick presence check when the drive list refreshes; keep it short", Symbol.Server, 250, 10000, 250, "ms",
                    () => Settings.Navigation.NetworkDriveProbeTimeoutMs, v => Settings.Navigation.NetworkDriveProbeTimeoutMs = (int)v) { Keywords = "probe drive list" },
            }),
        });

        private SettingsPage SoundsPage() => new SettingsPage("sounds", "Sounds and greetings", Symbol.Speaker2, new[]
        {
            new SettingGroup("Greetings", new SettingItem[]
            {
                new ToggleSetting("Greeting at startup", "A short welcome message when the window opens", Symbol.Info,
                    () => Settings.Greeting.EnableGreeting, v => Settings.Greeting.EnableGreeting = v) { Keywords = "welcome toast" },
                new ToggleSetting("Goodbye when closing", "A short farewell before the window closes", Symbol.Info,
                    () => Settings.Greeting.EnableGoodbye, v => Settings.Greeting.EnableGoodbye = v) { Keywords = "farewell exit" },
                new NumberSetting("Goodbye duration", "How long the farewell stays before the window closes", Symbol.History, 500, 6000, 100, "ms",
                    () => Settings.Greeting.GoodbyeDwellMs, v => Settings.Greeting.GoodbyeDwellMs = (int)v),
            }),
            new SettingGroup("Sounds", new SettingItem[]
            {
                new ToggleSetting("Sound feedback", "Play sounds at all. Off silences everything below", Symbol.Speaker2,
                    () => Settings.Greeting.EnableSoundFeedback, v => Settings.Greeting.EnableSoundFeedback = v) { Keywords = "audio mute" },
                new ToggleSetting("Action sounds", "A sound when a copy, move or delete finishes or fails", Symbol.Speaker2,
                    () => Settings.Greeting.EnableActionSounds, v => Settings.Greeting.EnableActionSounds = v) { Keywords = "audio" },
                new ToggleSetting("Navigation sounds", "A soft click when you open a folder", Symbol.Speaker2,
                    () => Settings.Greeting.EnableNavigationSounds, v => Settings.Greeting.EnableNavigationSounds = v) { Keywords = "audio click" },
            }),
        });

        private SettingsPage PrivacyPage() => new SettingsPage("privacy", "Privacy", Symbol.Shield, new[]
        {
            new SettingGroup("Activity", new SettingItem[]
            {
                new ToggleSetting("Usage history", "Remember visited folders, opened files and searches for the Home dashboard. Kept only on this device", Symbol.History,
                    () => Settings.Analytics.EnableUsageAnalytics, v => Settings.Analytics.EnableUsageAnalytics = v) { Keywords = "analytics tracking statistics dashboard" },
                new ActionSetting("Clear activity data", "Forget every visited folder, opened file, search and statistic", Symbol.Delete,
                    "Clear", new AsyncRelayCommand(_ => ClearActivityAsync())) { IsDanger = true, Keywords = "analytics delete history" },
            }),
            new SettingGroup("History limits", new SettingItem[]
            {
                new NumberSetting("Folders remembered", "Visited folders kept for the dashboard", Symbol.Folder, 50, 5000, 10, "folders",
                    () => Settings.Analytics.MaxTrackedFolders, v => Settings.Analytics.MaxTrackedFolders = (int)v) { RequiresRestart = true, Keywords = "analytics" },
                new NumberSetting("Activity entries", "Recent actions kept in the activity log", Symbol.History, 20, 2000, 10, "entries",
                    () => Settings.Analytics.MaxLogEntries, v => Settings.Analytics.MaxLogEntries = (int)v) { RequiresRestart = true, Keywords = "analytics log" },
                new NumberSetting("Sessions remembered", "App sessions kept for the usage statistics", Symbol.History, 10, 2000, 10, "sessions",
                    () => Settings.Analytics.MaxSessionHistory, v => Settings.Analytics.MaxSessionHistory = (int)v) { RequiresRestart = true, Keywords = "analytics" },
                new NumberSetting("Favorite folders fade after", "How quickly a folder you stopped visiting drops in the favorites (half-life)", Symbol.Star, 1, 365, 1, "days",
                    () => Settings.Analytics.FavoriteFolderHalfLifeDays, v => Settings.Analytics.FavoriteFolderHalfLifeDays = v) { RequiresRestart = true, Keywords = "analytics decay" },
            }),
        });

        private async Task ClearActivityAsync()
        {
            if (_window != null && !await _window.ConfirmAsync("Clear activity data",
                    "Forget every visited folder, opened file, search and statistic? This cannot be undone.", "Clear").ConfigureAwait(true))
            {
                return;
            }

            _main.ClearActivityData();
        }

        private SettingsPage AdvancedPage() => new SettingsPage("advanced", "Advanced", Symbol.Wrench, new[]
        {
            new SettingGroup("Performance", new SettingItem[]
            {
                new NumberSetting("Items prepared at once", "Rows of the file list kept fully built in memory, however large the folder", Symbol.Gauge, 50, 20000, 50, "items",
                    () => Settings.FileList.ResidentItemWindow, v => Settings.FileList.ResidentItemWindow = (int)v) { RequiresRestart = true, Keywords = "memory window virtualization" },
                new NumberSetting("Items prepared ahead", "Rows built ahead of scrolling", Symbol.Gauge, 0, 5000, 10, "items",
                    () => Settings.FileList.PrefetchBufferSize, v => Settings.FileList.PrefetchBufferSize = (int)v) { RequiresRestart = true, Keywords = "prefetch scrolling" },
                new NumberSetting("Tiles added per scroll step", "How many new tiles a scroll step may create, to keep scrolling smooth", Symbol.Gauge, 5, 2000, 5, "tiles",
                    () => Settings.FileList.MaxNewTilesPerScrollPass, v => Settings.FileList.MaxNewTilesPerScrollPass = (int)v) { RequiresRestart = true, Keywords = "scrolling" },
                new NumberSetting("Selection delay", "The pause after a selection change before the status bar and preview follow", Symbol.Gauge, 0, 1000, 10, "ms",
                    () => Settings.FileList.SelectionDebounceMs, v => Settings.FileList.SelectionDebounceMs = (int)v) { RequiresRestart = true, Keywords = "debounce" },
                new NumberSetting("Folder listings cached", "Recently opened folders kept in memory for instant Back", Symbol.Gauge, 1, 100, 1, "folders",
                    () => Settings.FileList.DirectoryIndexCacheCapacity, v => Settings.FileList.DirectoryIndexCacheCapacity = (int)v) { RequiresRestart = true, Keywords = "cache memory" },
                new NumberSetting("Cached listings stay fresh for", "After this, a cached folder is read again", Symbol.Gauge, 0, 300000, 500, "ms",
                    () => Settings.FileList.DirectoryIndexCacheTtlMs, v => Settings.FileList.DirectoryIndexCacheTtlMs = (int)v) { RequiresRestart = true, Keywords = "cache ttl" },
            }),
            new SettingGroup("Live update timing", new SettingItem[]
            {
                new NumberSetting("Open folder refresh delay", "Changes on disk are gathered this long before the file list updates", Symbol.ArrowSync, 100, 5000, 50, "ms",
                    () => Settings.FileList.LiveRefreshDebounceMs, v => Settings.FileList.LiveRefreshDebounceMs = (int)v) { RequiresRestart = true, Keywords = "debounce watch" },
                new NumberSetting("Tree refresh delay", "Changes on disk are gathered this long before a tree folder reloads", Symbol.ArrowSync, 100, 5000, 50, "ms",
                    () => Settings.Navigation.LiveRefreshDebounceMs, v => Settings.Navigation.LiveRefreshDebounceMs = (int)v) { RequiresRestart = true, Keywords = "debounce watch" },
                new NumberSetting("Navigation sound spacing", "At most one navigation sound in this time", Symbol.Speaker2, 0, 2000, 10, "ms",
                    () => Settings.Greeting.NavigationSoundThrottleMs, v => Settings.Greeting.NavigationSoundThrottleMs = (int)v) { RequiresRestart = true, Keywords = "throttle" },
                new NumberSetting("Activity save delay", "Usage history is written at most this often", Symbol.History, 500, 60000, 500, "ms",
                    () => Settings.Analytics.SaveDebounceMs, v => Settings.Analytics.SaveDebounceMs = (int)v) { RequiresRestart = true, Keywords = "analytics debounce" },
            }),
        });

        private SettingsPage SettingsFilePage()
        {
            string path = _main.SettingsService.FilePath;
            return new SettingsPage("file", "Settings file", Symbol.DocumentSettings, new[]
            {
                new SettingGroup("Location", new SettingItem[]
                {
                    new InfoSetting("settings.json", "Every setting on these pages is stored in this file, with a comment explaining each one", Symbol.DocumentSettings,
                        () => path) { ButtonText = "Copy path", Command = new AsyncRelayCommand(_ => CopyAsync(path)), Keywords = "json path location config" },
                    new ActionSetting("Open the settings file", "Edit it in your text editor. Edits made while ByteExplorer runs are overwritten when it saves", Symbol.Open,
                        "Open", new RelayCommand(_ => OpenFile(path))) { SecondaryButtonText = "Show in ByteExplorer", SecondaryCommand = new RelayCommand(_ => Reveal(path)), Keywords = "json edit editor" },
                    new InfoSetting("Data folder", "Where the settings, usage history, search index and logs are kept", Symbol.Folder,
                        () => AppPaths.DataDirectory) { ButtonText = "Open", Command = new RelayCommand(_ => _host?.RevealInFileList(AppPaths.DataDirectory, null)), Keywords = "logs index storage portable" },
                }),
                new SettingGroup("Backup", new SettingItem[]
                {
                    new ActionSetting("Export settings", "Save a copy of your settings, to keep or to use on another computer", Symbol.ArrowExport,
                        "Export…", new AsyncRelayCommand(_ => ExportAsync())) { Keywords = "backup save copy" },
                    new ActionSetting("Import settings", "Replace your settings with ones exported before", Symbol.ArrowImport,
                        "Import…", new AsyncRelayCommand(_ => ImportAsync())) { Keywords = "restore load" },
                }),
                new SettingGroup("Reset", new SettingItem[]
                {
                    new ActionSetting("Reset all settings", "Return every setting to its default. Quick access pins and network locations are kept", Symbol.ArrowReset,
                        "Reset", new AsyncRelayCommand(_ => ResetAsync())) { IsDanger = true, Keywords = "defaults factory restore" },
                }),
            });
        }

        private SettingsPage AboutPage()
        {
            Assembly assembly = typeof(SettingsViewModel).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                             ?? assembly.GetName().Version?.ToString() ?? "unknown";
            string avalonia = typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "?";
            string details = $"{RuntimeInformation.FrameworkDescription} · Avalonia {avalonia} · {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
            return new SettingsPage("about", "About", Symbol.Info, new[]
            {
                new SettingGroup(null, new SettingItem[]
                {
                    new AboutSetting("ByteExplorer", "Version " + version, details) { Keywords = "version build" },
                    new AuthorNoteSetting("A note from the author",
                        "ByteExplorer was coded with love, in C#, one evening at a time. I wanted a file explorer that is fast, beautiful and simply gets out of your way, so I set out to build the one I always dreamed of. Every view, every shortcut and every little detail is here because I wanted it to feel just right. Thank you for using it; I hope it makes your days with files a little brighter.",
                        "— Eliquin",
                        ProjectUrl.Replace("https://", string.Empty, StringComparison.Ordinal),
                        new RelayCommand(_ => OpenLink(ProjectUrl)),
                        "Support ByteExplorer on Ko-fi",
                        new RelayCommand(_ => OpenLink(SupportUrl))) { Keywords = "author thanks github project source code repository love donate donation support ko-fi kofi coffee tip" },
                    new InfoSetting("Logs", "Diagnostic logs, useful when reporting a problem", Symbol.DocumentText, () => Path.Combine(AppPaths.DataDirectory, "logs"))
                    {
                        ButtonText = "Open", Command = new RelayCommand(_ => _host?.RevealInFileList(Path.Combine(AppPaths.DataDirectory, "logs"), null)), Keywords = "log diagnostics",
                    },
                }),
                new SettingGroup("Legal", new SettingItem[]
                {
                    new InfoSetting("License", "ByteExplorer is free and open source under the Mozilla Public License 2.0", Symbol.DocumentText,
                        () => "MPL 2.0 · Copyright © 2026 Eliquin and contributors")
                    {
                        ButtonText = "Show", Command = new RelayCommand(_ => Reveal(Path.Combine(AppContext.BaseDirectory, "LICENSE"))), Keywords = "license mpl mozilla open source copyright legal",
                    },
                    new InfoSetting("Third-party notices", "The components ByteExplorer is built with (Avalonia, VLC, SkiaSharp, Serilog, the Inter font, the Fluent icons…), their licenses and where to get VLC's source", Symbol.Info,
                        () => "THIRD-PARTY-NOTICES.md and the licenses folder")
                    {
                        ButtonText = "Show", Command = new RelayCommand(_ => Reveal(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"))), Keywords = "licenses notices open source vlc lgpl gpl credits legal attribution",
                    },
                }),
            });
        }

        // ------------------------------------------------------------------ actions

        private async Task BrowseFolderAsync(FolderSetting setting)
        {
            string folder = _window == null ? null : await _window.PickFolderAsync(setting.Title).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(folder))
            {
                setting.Path = folder;
            }
        }

        private async Task BrowseIntoListAsync(ListSetting setting)
        {
            string folder = _window == null ? null : await _window.PickFolderAsync(setting.Title).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(folder))
            {
                setting.Add(folder);
            }
        }

        private async Task CopyAsync(string text)
        {
            if (_window != null)
            {
                await _window.CopyTextAsync(text).ConfigureAwait(true);
            }
        }

        private void OpenFile(string path)
        {
            try
            {
                ShellLauncher.Open(path);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not open the settings file {Path}", path);
                _window?.ShowMessageAsync("Open the settings file", "The file could not be opened: " + ex.Message).Forget("SettingsViewModel.OpenFile");
            }
        }

        /// <summary>The project's home on GitHub (source code, releases, issues).</summary>
        internal const string ProjectUrl = "https://github.com/Eliquin/ByteExplorer-public";

        /// <summary>The author's Ko-fi page, for those who want to support the project.</summary>
        internal const string SupportUrl = "https://ko-fi.com/eliquin081";

        /// <summary>
        /// Opens a web page (the project, the Ko-fi page) in the browser: only when clicked, the app never goes online on
        /// its own. Without a browser the link is copied instead.
        /// </summary>
        private void OpenLink(string url)
        {
            try
            {
                ShellLauncher.Open(url);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not open {Url}", url);
                _window?.CopyTextAsync(url).Forget("copy the link");
                _window?.ShowMessageAsync("Could not open the browser", "The link was copied instead:\n" + url).Forget("SettingsViewModel.OpenLink");
            }
        }

        private void Reveal(string path) => _host?.RevealInFileList(Path.GetDirectoryName(path), path);

        private async Task ExportAsync()
        {
            string target = _window == null ? null : await _window.PickSaveFileAsync("Export settings", "ByteExplorer-settings.json").ConfigureAwait(true);
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            try
            {
                await _main.SettingsService.SaveAsync().ConfigureAwait(true);
                string source = _main.SettingsService.FilePath;
                await Task.Run(() => File.Copy(source, target, overwrite: true)).ConfigureAwait(true);
                Logger.Information("Settings exported to {Path}", target);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not export the settings to {Path}", target);
                await (_window?.ShowMessageAsync("Export settings", "The settings could not be saved there: " + ex.Message) ?? Task.CompletedTask).ConfigureAwait(true);
            }
        }

        private async Task ImportAsync()
        {
            string source = _window == null ? null : await _window.PickOpenFileAsync("Import settings").ConfigureAwait(true);
            if (string.IsNullOrEmpty(source))
            {
                return;
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(source).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                await (_window?.ShowMessageAsync("Import settings", "The file could not be read: " + ex.Message) ?? Task.CompletedTask).ConfigureAwait(true);
                return;
            }

            if (!SettingsService.TryParse(json, out AppSettings imported, out string error))
            {
                await (_window?.ShowMessageAsync("Import settings", error) ?? Task.CompletedTask).ConfigureAwait(true);
                return;
            }

            if (_window != null && !await _window.ConfirmAsync("Import settings",
                    "Replace all your settings with the ones in this file? ByteExplorer restarts to apply them.", "Import").ConfigureAwait(true))
            {
                return;
            }

            await ReplaceAndRestartAsync(imported).ConfigureAwait(true);
        }

        private async Task ResetAsync()
        {
            if (_window != null && !await _window.ConfirmAsync("Reset all settings",
                    "Return every setting to its default? Quick access pins and network locations are kept. ByteExplorer restarts to apply the change.", "Reset").ConfigureAwait(true))
            {
                return;
            }

            var defaults = new AppSettings();
            // The places the person collected are content, not preferences: a reset keeps them.
            defaults.Navigation.Pinned = Settings.Navigation.Pinned;
            defaults.Navigation.Bookmarks = Settings.Navigation.Bookmarks;
            defaults.Navigation.NetworkLocations = Settings.Navigation.NetworkLocations;
            defaults.Index.LastBuildUtc = Settings.Index.LastBuildUtc;
            await ReplaceAndRestartAsync(defaults).ConfigureAwait(true);
        }

        private async Task ReplaceAndRestartAsync(AppSettings settings)
        {
            try
            {
                await _main.SettingsService.ReplaceAsync(settings).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not replace the settings");
                await (_window?.ShowMessageAsync("Settings", "The settings could not be saved: " + ex.Message) ?? Task.CompletedTask).ConfigureAwait(true);
                return;
            }

            RefreshAll();
            IsRestartPending = true;
            _host?.RestartApplication();
        }
    }
}
