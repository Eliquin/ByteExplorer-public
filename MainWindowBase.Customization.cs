// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using ByteExplorer.Views;
using ByteExplorer.Views.Sidebar;
using Serilog;

namespace ByteExplorer
{
    /// <summary>
    /// The look-and-layout customizations chosen in the settings, applied to whichever layout this window is: the order
    /// of the left panel's sections (and, in the same pass, the window areas' text sizes, colors and backgrounds).
    /// Applied once the settings are loaded and again whenever the settings window changes one
    /// (MainViewModel.CustomizationChanged).
    /// </summary>
    public abstract partial class MainWindowBase
    {
        /// <summary>Longest side the background image is decoded at: sharp on a large screen, bounded in memory.</summary>
        private const int BackgroundImageMaxSide = 3840;

        private List<string> _sidebarLayoutOrder = new List<string>();
        private readonly Dictionary<RowDefinition, double> _rowBaseHeights = new Dictionary<RowDefinition, double>();
        private Border _backgroundImageLayer;
        private string _backgroundImagePath;
        private Bitmap _backgroundImage;
        private int _backgroundImageRequest;

        /// <summary>This layout's own order of the left panel's sections (the default the settings show and reset to).</summary>
        public IReadOnlyList<string> SidebarLayoutOrder => _sidebarLayoutOrder;

        /// <summary>The order shown now: the saved order applied to this layout's sections.</summary>
        public IReadOnlyList<string> SidebarEffectiveOrder =>
            SidebarOrder.Effective(_sidebarLayoutOrder, ViewModel.SettingsService?.Current?.Navigation?.SidebarSectionOrder);

        /// <summary>Records what the layout looks like before any customization (read from its XAML).</summary>
        private void CaptureLayoutDefaults()
        {
            if (_parts.SidebarSectionsAbove != null)
            {
                _sidebarLayoutOrder = SidebarOrder.ReadOrder(_parts.SidebarSectionsAbove, _parts.SidebarSectionsBelow, _parts.SidebarPivotKey);
            }

            foreach (AreaPart area in _parts.Areas)
            {
                if (area.Row != null && area.Row.Height.IsAbsolute && area.Row.Height.Value > 0)
                {
                    _rowBaseHeights[area.Row] = area.Row.Height.Value;
                }
            }
        }

        /// <summary>The area of this layout, or null if it has none by that key.</summary>
        internal AreaPart AreaFor(string area) => _parts.Areas.FirstOrDefault(a => a.Area == area);

        private void OnCustomizationChanged(object sender, EventArgs e) => ApplyCustomization();

        /// <summary>The cut items changed (cut, pasted, or the clipboard taken by another program): redraw them dimmed or not.</summary>
        private void OnClipboardFilesChanged(object sender, EventArgs e) => UiThread.Post(RedrawItems);

        /// <summary>Repaints the drawn item views (their look depends on state outside them: folder color, cut items).</summary>
        /// <summary>
        /// A folder option changed: the owner-drawn items re-read it (names, check boxes, tips, underline) and the views
        /// built from templates (Timeline, Columns, Nexus) are rebuilt with the new names and check boxes.
        /// </summary>
        private void OnFolderOptionsChanged(object sender, EventArgs e)
        {
            foreach (Visual visual in this.GetVisualDescendants())
            {
                if (visual is Controls.FileItemView view)
                {
                    view.ApplyFolderOptions();
                }
            }

            foreach (ViewModels.FileListViewModel pane in ViewModel?.AllPanes ?? Array.Empty<ViewModels.FileListViewModel>())
            {
                if (pane?.CurrentPath != null &&
                    pane.ViewMode is Infrastructure.FileListViewMode.Timeline or Infrastructure.FileListViewMode.Columns or Infrastructure.FileListViewMode.Nexus &&
                    pane.RefreshCommand.CanExecute(null))
                {
                    pane.RefreshCommand.Execute(null);
                }
            }
        }

        private void RedrawItems()
        {
            foreach (Visual visual in this.GetVisualDescendants())
            {
                if (visual is Controls.FileItemView or Views.Search.SearchResultRowView)
                {
                    visual.InvalidateVisual();
                }
            }
        }

        /// <summary>Applies every customization from the current settings; a failure in one leaves the others applied.</summary>
        internal void ApplyCustomization()
        {
            try
            {
                ApplySidebarOrder();
                ApplySidebarDesign();
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Applying the left panel's section order failed");
            }

            try
            {
                ApplyFolderColor();
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Applying the folder color failed");
            }

            try
            {
                ApplyAreas();
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Applying the window areas' look failed");
            }
        }

        private string _appliedFolderColor;

        /// <summary>
        /// The folder color setting: the views that draw their items read it as they draw (redrawn here), the theme
        /// resources follow (App.ApplyFolderColor), and the views that bind their icons (Timeline, Columns, Nexus) reload.
        /// </summary>
        private void ApplyFolderColor()
        {
            string hex = ViewModel.SettingsService?.Current?.Appearance?.FolderColorHex ?? string.Empty;
            if (hex == _appliedFolderColor)
            {
                return;
            }

            bool firstTime = _appliedFolderColor == null;
            _appliedFolderColor = hex;
            FileIconGlyphs.FolderColorHex = hex;
            App.ApplyFolderColor(hex);
            ViewModel.Pins.RefreshBrushes();
            RedrawItems();

            if (!firstTime)
            {
                foreach (ViewModels.FileListViewModel pane in ViewModel.AllPanes)
                {
                    if (pane != null && pane.ViewMode is FileListViewMode.Timeline or FileListViewMode.Columns or FileListViewMode.Nexus &&
                        pane.RefreshCommand.CanExecute(null))
                    {
                        pane.RefreshCommand.Execute(null);
                    }
                }
            }
        }

        private void ApplyAreas()
        {
            AppearanceSettings appearance = ViewModel.SettingsService?.Current?.Appearance;
            foreach (AreaPart area in _parts.Areas)
            {
                AreaStyler.Apply(area, appearance?.FindArea(area.Area));
            }

            // A fixed-height row grows with the largest text size of the areas in it (Nemo's title bar holds two).
            foreach ((RowDefinition row, double baseHeight) in _rowBaseHeights)
            {
                double scale = _parts.Areas.Where(a => a.Row == row).Select(a => appearance?.FindArea(a.Area)?.TextScale ?? 1.0).DefaultIfEmpty(1.0).Max();
                SetRowHeight(row, Math.Round(baseHeight * scale));
            }

            ApplyBackgroundImage(appearance);
            ApplyTabSelectedBackground(appearance);
        }

        /// <summary>
        /// The selected horizontal tab is drawn in the file list's own color so it reads as the list's front edge: with a
        /// customized main view color, it takes that color (with a background image, the color under the image).
        /// </summary>
        private void ApplyTabSelectedBackground(AppearanceSettings appearance)
        {
            if (_parts.TabStrip == null)
            {
                return;
            }

            Avalonia.Media.Color? color = AreaStyler.ParseColor(appearance?.FindArea(AreaAppearance.MainView)?.BackgroundColorHex);
            if (color is Avalonia.Media.Color c)
            {
                _parts.TabStrip.Resources["TabSelectedBg"] = new Avalonia.Media.SolidColorBrush(c);
            }
            else
            {
                _parts.TabStrip.Resources.Remove("TabSelectedBg");
            }

            // In the title bar the selected tab is the front of the address bar: it takes that bar's color.
            Avalonia.Media.Color? addressBar = AreaStyler.ParseColor(appearance?.FindArea(AreaAppearance.AddressBar)?.BackgroundColorHex);
            if (addressBar is Avalonia.Media.Color a)
            {
                _parts.TabStrip.Resources["TabTitleBarSelectedBg"] = new Avalonia.Media.SolidColorBrush(a);
            }
            else
            {
                _parts.TabStrip.Resources.Remove("TabTitleBarSelectedBg");
            }
        }

        /// <summary>Sets a row's height, or, while the row is hidden (toolbar or status bar off), the height it comes back at.</summary>
        private void SetRowHeight(RowDefinition row, double height)
        {
            bool hidden = row.Height.IsAbsolute && row.Height.Value <= 0;
            if (row == _parts.ToolbarRow && hidden)
            {
                _toolbarHeight = new GridLength(height);
            }
            else if (row == _parts.StatusBarRow && hidden)
            {
                _statusBarHeight = new GridLength(height);
            }
            else if (!hidden)
            {
                row.Height = new GridLength(height);
            }
        }

        /// <summary>
        /// The main view's background image: a layer under the files (which are then drawn on a transparent surface), on
        /// the main view's background color. The image is decoded off the UI thread, bounded to
        /// <see cref="BackgroundImageMaxSide"/>; a file that is missing or not an image shows no image and is logged.
        /// </summary>
        private void ApplyBackgroundImage(AppearanceSettings appearance)
        {
            AreaPart mainView = AreaFor(AreaAppearance.MainView);
            // The panel holding the panes and search results (inside the tabs' dock panel): the image is under the files,
            // not under the tab bar, which keeps its own color.
            if (mainView == null || (_parts.PaneGrid?.Parent ?? AreaStyler.ContentOf(mainView.Host)) is not Panel content)
            {
                return;
            }

            string path = appearance?.MainViewBackgroundImage;
            if (string.IsNullOrEmpty(path))
            {
                _backgroundImageRequest++;
                _backgroundImagePath = null;
                _backgroundImage = null;
                if (_backgroundImageLayer != null)
                {
                    content.Children.Remove(_backgroundImageLayer);
                    _backgroundImageLayer = null;
                    // The panes' panel is inside the area's content: its own transparency goes; the color's (set on the
                    // area's content by AreaStyler.Apply) stays as the color says.
                    AreaStyler.SetSurfacesTransparent(content, mainView.BackgroundKeys, transparent: false);
                }

                return; // the surfaces follow the background color alone (AreaStyler.Apply)
            }

            if (_backgroundImageLayer == null)
            {
                _backgroundImageLayer = new Border { Name = "BackgroundImageLayer", IsHitTestVisible = false, ClipToBounds = true };
                content.Children.Insert(0, _backgroundImageLayer);
            }

            AreaStyler.SetSurfacesTransparent(content, mainView.BackgroundKeys, transparent: true);
            _backgroundImageLayer.Opacity = appearance.MainViewBackgroundImageOpacity;
            if (path == _backgroundImagePath && _backgroundImage != null)
            {
                _backgroundImageLayer.Background = CreateImageBrush(_backgroundImage, appearance.MainViewBackgroundImageFit);
                return;
            }

            _backgroundImagePath = path;
            _backgroundImage = null;
            _backgroundImageLayer.Background = null;
            LoadBackgroundImageAsync(path, ++_backgroundImageRequest).Forget("background image");
        }

        private async Task LoadBackgroundImageAsync(string path, int request)
        {
            Bitmap bitmap = await Task.Run(() => System.IO.File.Exists(path) ? ImageLoader.Load(path, BackgroundImageMaxSide) : null);
            if (request != _backgroundImageRequest)
            {
                return; // another image (or none) was chosen meanwhile
            }

            if (bitmap == null)
            {
                Log.Logger.Warning("Background image {Path} is missing or not an image; none is shown", path);
                return;
            }

            _backgroundImage = bitmap;
            if (_backgroundImageLayer != null)
            {
                _backgroundImageLayer.Background = CreateImageBrush(bitmap, ViewModel.SettingsService?.Current?.Appearance?.MainViewBackgroundImageFit);
            }

            BackgroundImageLoaded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised when the main view's background image has been decoded and shown (tests wait for it).</summary>
        internal event EventHandler BackgroundImageLoaded;

        /// <summary>The main view's background image layer, while an image is chosen (tests).</summary>
        internal Border BackgroundImageLayerForTests => _backgroundImageLayer;

        private static ImageBrush CreateImageBrush(Bitmap bitmap, string fit)
        {
            var brush = new ImageBrush(bitmap) { AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            switch (fit)
            {
                case AppearanceSettings.ImageFitFit:
                    brush.Stretch = Stretch.Uniform;
                    break;
                case AppearanceSettings.ImageFitStretch:
                    brush.Stretch = Stretch.Fill;
                    break;
                case AppearanceSettings.ImageFitCenter:
                    brush.Stretch = Stretch.None;
                    break;
                case AppearanceSettings.ImageFitTile:
                    brush.Stretch = Stretch.None;
                    brush.TileMode = TileMode.Tile;
                    brush.AlignmentX = AlignmentX.Left;
                    brush.AlignmentY = AlignmentY.Top;
                    brush.DestinationRect = new RelativeRect(0, 0, bitmap.Size.Width, bitmap.Size.Height, RelativeUnit.Absolute);
                    break;
                default:
                    brush.Stretch = Stretch.UniformToFill;
                    break;
            }

            return brush;
        }

        /// <summary>The left panel's design: its classes on the panel (the styles in Themes/Styles.axaml do the rest).</summary>
        private void ApplySidebarDesign()
        {
            if (_parts.SidebarDesignHost == null)
            {
                return;
            }

            string design = ViewModel?.SettingsService?.Current?.Navigation?.SidebarDesign ?? Core.Models.NavigationSettings.SidebarDesignClassic;
            Classes classes = _parts.SidebarDesignHost.Classes;
            classes.Set("sbModern", design != Core.Models.NavigationSettings.SidebarDesignClassic);
            classes.Set("sbQuiet", design is Core.Models.NavigationSettings.SidebarDesignQuiet or Core.Models.NavigationSettings.SidebarDesignChips);
            classes.Set("sbCards", design == Core.Models.NavigationSettings.SidebarDesignCards);
            classes.Set("sbGuides", design == Core.Models.NavigationSettings.SidebarDesignGuides);
            classes.Set("sbChips", design == Core.Models.NavigationSettings.SidebarDesignChips);
        }

        private void ApplySidebarOrder()
        {
            if (_parts.SidebarSectionsAbove == null)
            {
                return;
            }

            SidebarOrder.Apply(_parts.SidebarSectionsAbove, _parts.SidebarSectionsBelow, _parts.SidebarPivotKey, SidebarEffectiveOrder);
            if (_parts.SidebarLineAbovePivot != null)
            {
                _parts.SidebarLineAbovePivot.IsVisible = _parts.SidebarSectionsAbove.Children.Count > 0;
            }

            if (_parts.SidebarLineBelowPivot != null && _parts.SidebarSectionsBelow != null)
            {
                _parts.SidebarLineBelowPivot.IsVisible = _parts.SidebarSectionsBelow.Children.Count > 0;
            }
        }
    }
}
