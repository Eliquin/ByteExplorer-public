// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Linq;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Appearance customization. FontFamily, FontSize, and
    /// BackgroundColorHex are already applied to the shell window as of
    /// Phase 2's settings pass. AccentColorHex is stored and persisted
    /// but not yet wired into the UI — it becomes live once the
    /// settings window (and proper theming/resource-dictionary support)
    /// lands. Storing it now means nothing about settings.json's shape
    /// has to change later, just what reads it.
    /// </summary>
    public sealed class AppearanceSettings
    {
        public string FontFamily { get; set; } = LegacyDefaultFontFamily;

        public double FontSize { get; set; } = 12.5;

        /// <summary>Reserved for future use (selection highlight, accents). Not yet applied.</summary>
        public string AccentColorHex { get; set; } = "#0078D4";

        public string BackgroundColorHex { get; set; } = "#FFFFFF";

        /// <summary>
        /// Which window layout the app opens at startup: "Modern" (title bar, address bar and command toolbar above the
        /// sidebar and file list; the WPF build called it "Dark"), "Trove" (formerly "Nemo", then "Dori": a sidebar the full height of the window
        /// and a single title bar, after the Linux Nemo file manager) or "Folio" (formerly "Finder": after macOS Finder). "Default" is the WPF build's original MainWindow,
        /// not ported; the cross-platform build opens the Modern window for it. All layouts share the same view models,
        /// so only layout and chrome differ, and both follow the color theme. Read once at startup; a change takes
        /// effect on the next launch. Default: "Modern"; unrecognized values fall back to "Default".
        /// </summary>
        public string WindowLayout { get; set; } = LayoutModern;

        /// <summary>
        /// Color theme: "System" (follow the operating system's light/dark setting, live), "Light" or "Dark". Switchable
        /// from the toolbar's "..." menu; applies immediately.
        /// </summary>
        public string Theme { get; set; } = "System";

        /// <summary>
        /// How the application menu (the three-bar button) shows a category's items: "Horizontal" opens them in a
        /// submenu beside the menu, the classic way; "Vertical" unfolds them in place under the category. Applies
        /// immediately. Default: "Horizontal".
        /// </summary>
        public string MenuSubmenuStyle { get; set; } = SubmenuHorizontal;

        /// <summary>The customized window areas (only those that differ from the theme); see <see cref="AreaAppearance"/>.</summary>
        public System.Collections.Generic.List<AreaAppearance> Areas { get; set; } = new System.Collections.Generic.List<AreaAppearance>();

        /// <summary>An image drawn behind the main view's files (full path); empty = none.</summary>
        public string MainViewBackgroundImage { get; set; } = string.Empty;

        /// <summary>How strongly the background image shows over the main view's background color (0.05 to 1).</summary>
        public double MainViewBackgroundImageOpacity { get; set; } = DefaultImageOpacity;

        /// <summary>How the background image fills the main view: Fill (cropped), Fit, Stretch, Tile or Center.</summary>
        public string MainViewBackgroundImageFit { get; set; } = ImageFitFill;

        /// <summary>The color folders are drawn in, "#RRGGBB"; empty = the default yellow.</summary>
        public string FolderColorHex { get; set; } = string.Empty;

        public const double DefaultImageOpacity = 0.35;
        public const string ImageFitFill = "Fill";
        public const string ImageFitFit = "Fit";
        public const string ImageFitStretch = "Stretch";
        public const string ImageFitTile = "Tile";
        public const string ImageFitCenter = "Center";

        /// <summary>The area's customization, or null when it looks as the theme has it.</summary>
        public AreaAppearance FindArea(string area) => Areas?.FirstOrDefault(a => a.Area == area);

        /// <summary>The area's customization, created (unchanged) when there is none yet.</summary>
        public AreaAppearance GetOrAddArea(string area)
        {
            Areas ??= new System.Collections.Generic.List<AreaAppearance>();
            AreaAppearance found = FindArea(area);
            if (found == null)
            {
                found = new AreaAppearance { Area = area };
                Areas.Add(found);
            }

            return found;
        }

        /// <summary>Anything of the window's look is customized (what "Reset to default" undoes).</summary>
        public bool HasCustomizations =>
            (Areas?.Any(a => !a.IsDefault) ?? false) || !string.IsNullOrEmpty(MainViewBackgroundImage) || !string.IsNullOrEmpty(FolderColorHex);

        /// <summary>Removes every window customization: area sizes and colors and the background image.</summary>
        public void ResetCustomizations()
        {
            Areas = new System.Collections.Generic.List<AreaAppearance>();
            FolderColorHex = string.Empty;
            MainViewBackgroundImage = string.Empty;
            MainViewBackgroundImageOpacity = DefaultImageOpacity;
            MainViewBackgroundImageFit = ImageFitFill;
        }

        public const string SubmenuHorizontal = "Horizontal";
        public const string SubmenuVertical = "Vertical";

        public const string LayoutDefault = "Default";
        public const string LayoutModern = "Modern";
        /// <summary>
        /// A sidebar the full height of the window and a single title bar. Called "Nemo", then "Dori", until it was
        /// renamed "Trove" (the window class is still NemoMainWindow); the older names in a settings file read as "Trove".
        /// </summary>
        public const string LayoutTrove = "Trove";

        /// <summary>The Trove layout's first name, still accepted (and rewritten as "Trove").</summary>
        public const string LegacyLayoutNemo = "Nemo";

        /// <summary>The Trove layout's second name, still accepted (and rewritten as "Trove").</summary>
        public const string LegacyLayoutDori = "Dori";

        /// <summary>After macOS Finder: a full-height sidebar, a unified toolbar, a path bar and a status bar at the bottom.</summary>
        /// <remarks>Called "Finder" until it was renamed "Folio" (the window class is still FinderMainWindow).</remarks>
        public const string LayoutFolio = "Folio";

        /// <summary>The Folio layout's former name, still accepted (and rewritten as "Folio").</summary>
        public const string LegacyLayoutFinder = "Finder";

        /// <summary>The Modern layout's name in the WPF build, still accepted (and rewritten as "Modern").</summary>
        public const string LegacyLayoutDark = "Dark";

        /// <summary>The font family the WPF build wrote as its default; the cross-platform build reads it as "the app's font".</summary>
        public const string LegacyDefaultFontFamily = "Segoe UI Variable Text";

        public void Sanitize()
        {
            // Areas: known ones, once each, sanitized; those left at the theme's look are dropped.
            var areas = new System.Collections.Generic.List<AreaAppearance>();
            foreach (AreaAppearance area in Areas ?? new System.Collections.Generic.List<AreaAppearance>())
            {
                if (area == null || !AreaAppearance.IsKnownArea(area.Area) || areas.Any(a => a.Area == area.Area))
                {
                    continue;
                }

                area.Sanitize();
                if (!area.IsDefault)
                {
                    areas.Add(area);
                }
            }

            Areas = areas;
            FolderColorHex = AreaAppearance.NormalizeColor(FolderColorHex);
            MainViewBackgroundImage = MainViewBackgroundImage?.Trim() ?? string.Empty;
            MainViewBackgroundImageOpacity = double.IsFinite(MainViewBackgroundImageOpacity)
                ? Math.Round(Math.Clamp(MainViewBackgroundImageOpacity, 0.05, 1.0), 2)
                : DefaultImageOpacity;
            string[] fits = { ImageFitFill, ImageFitFit, ImageFitStretch, ImageFitTile, ImageFitCenter };
            MainViewBackgroundImageFit = fits.FirstOrDefault(f => string.Equals(f, MainViewBackgroundImageFit, StringComparison.OrdinalIgnoreCase)) ?? ImageFitFill;

            if (string.IsNullOrWhiteSpace(FontFamily))
            {
                FontFamily = LegacyDefaultFontFamily;
            }

            if (FontSize < 8 || FontSize > 32)
            {
                FontSize = 12.5;
            }

            if (string.IsNullOrWhiteSpace(AccentColorHex))
            {
                AccentColorHex = "#0078D4";
            }

            if (string.IsNullOrWhiteSpace(BackgroundColorHex))
            {
                BackgroundColorHex = "#FFFFFF";
            }

            if (!string.Equals(Theme, "System", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Theme, "Light", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Theme, "Dark", System.StringComparison.OrdinalIgnoreCase))
            {
                Theme = "System";
            }

            MenuSubmenuStyle = string.Equals(MenuSubmenuStyle, SubmenuVertical, System.StringComparison.OrdinalIgnoreCase)
                ? SubmenuVertical
                : SubmenuHorizontal;

            if (string.Equals(WindowLayout, LegacyLayoutDark, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(WindowLayout, LayoutModern, System.StringComparison.OrdinalIgnoreCase))
            {
                WindowLayout = LayoutModern;
            }
            else if (string.Equals(WindowLayout, LayoutTrove, System.StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(WindowLayout, LegacyLayoutDori, System.StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(WindowLayout, LegacyLayoutNemo, System.StringComparison.OrdinalIgnoreCase))
            {
                WindowLayout = LayoutTrove;
            }
            else if (string.Equals(WindowLayout, LayoutFolio, System.StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(WindowLayout, LegacyLayoutFinder, System.StringComparison.OrdinalIgnoreCase))
            {
                WindowLayout = LayoutFolio;
            }
            else
            {
                WindowLayout = LayoutDefault;
            }
        }
    }
}
