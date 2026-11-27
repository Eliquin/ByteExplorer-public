// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Models
{
    /// <summary>One pinned place of the sidebar's Quick access, as saved: the folder, an optional name and color.</summary>
    public sealed class PinnedFolderSettings
    {
        /// <summary>The folder (or ZIP file, or network path), or <see cref="PinColors.TrashKey"/> for the trash.</summary>
        public string Path { get; set; }

        /// <summary>A name shown instead of the folder's own (the folder is not renamed); null for the folder's name.</summary>
        public string Name { get; set; }

        /// <summary>One of <see cref="PinColors.Names"/> for the icon, or null for its usual color.</summary>
        public string Color { get; set; }
    }

    /// <summary>
    /// The colors a pin's icon can take. A fixed set of mid-tone colors that read on both the light and the dark theme
    /// (Finder's tags, Explorer's colored folders), stored by name so a later palette tweak keeps people's choices.
    /// </summary>
    public static class PinColors
    {
        /// <summary>The saved path of the trash pin (the Windows Recycle Bin has no path of its own).</summary>
        public const string TrashKey = "::trash";

        public static readonly IReadOnlyList<(string Name, string Hex)> All = new[]
        {
            ("Red", "#E5484D"),
            ("Orange", "#F76B15"),
            ("Yellow", "#E2A400"),
            ("Green", "#30A46C"),
            ("Teal", "#12A594"),
            ("Blue", "#0090FF"),
            ("Purple", "#8E4EC6"),
            ("Pink", "#D6409F"),
            ("Gray", "#8B8D98")
        };

        public static IEnumerable<string> Names
        {
            get
            {
                foreach ((string name, _) in All)
                {
                    yield return name;
                }
            }
        }

        /// <summary>The hex color of a name (case-insensitive), or null for an unknown one.</summary>
        public static string HexOf(string name)
        {
            foreach ((string colorName, string hex) in All)
            {
                if (string.Equals(colorName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return hex;
                }
            }

            return null;
        }

        /// <summary>The canonical spelling of a known name, or null.</summary>
        public static string Normalize(string name)
        {
            foreach ((string colorName, _) in All)
            {
                if (string.Equals(colorName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return colorName;
                }
            }

            return null;
        }
    }
}
