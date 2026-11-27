// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// How one area of the main window is customized: its text size (a scale of everything in it, so headings and captions
    /// keep their proportions), text color and background color. Empty colors and a scale of 1 mean the theme's look.
    /// </summary>
    public sealed class AreaAppearance
    {
        public const string AddressBar = "AddressBar";
        public const string Toolbar = "Toolbar";
        public const string LeftPanel = "LeftPanel";
        public const string MainView = "MainView";
        public const string DetailsPanel = "DetailsPanel";
        public const string StatusBar = "StatusBar";

        public const double MinScale = 0.75;
        public const double MaxScale = 1.6;

        /// <summary>The six areas, top to bottom and left to right, with the names the settings show.</summary>
        public static IReadOnlyList<(string Key, string Label)> All { get; } = new[]
        {
            (AddressBar, "Address bar"),
            (Toolbar, "Toolbar"),
            (LeftPanel, "Left panel"),
            (MainView, "Main view"),
            (DetailsPanel, "Details panel"),
            (StatusBar, "Status bar"),
        };

        /// <summary>One of the area keys above.</summary>
        public string Area { get; set; } = string.Empty;

        /// <summary>Text size as a scale of the theme's (1 = unchanged, 0.75 to 1.6).</summary>
        public double TextScale { get; set; } = 1.0;

        /// <summary>"#RRGGBB" or "#AARRGGBB"; empty = the theme's text colors.</summary>
        public string TextColorHex { get; set; } = string.Empty;

        /// <summary>"#RRGGBB" or "#AARRGGBB"; empty = the theme's background.</summary>
        public string BackgroundColorHex { get; set; } = string.Empty;

        /// <summary>Nothing is changed from the theme.</summary>
        public bool IsDefault => Math.Abs(TextScale - 1.0) < 0.001 && string.IsNullOrEmpty(TextColorHex) && string.IsNullOrEmpty(BackgroundColorHex);

        public void Sanitize()
        {
            TextScale = double.IsFinite(TextScale) ? Math.Round(Math.Clamp(TextScale, MinScale, MaxScale), 2) : 1.0;
            TextColorHex = NormalizeColor(TextColorHex);
            BackgroundColorHex = NormalizeColor(BackgroundColorHex);
        }

        public static bool IsKnownArea(string area) => All.Any(a => a.Key == area);

        /// <summary>
        /// "#RRGGBB" / "#AARRGGBB" in upper case for a valid color ("#RGB", "#ARGB" and a missing '#' are accepted), or
        /// empty for anything else.
        /// </summary>
        public static string NormalizeColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string hex = text.Trim().TrimStart('#');
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return string.Empty;
            }

            return "#" + hex.ToUpperInvariant();
        }
    }
}
