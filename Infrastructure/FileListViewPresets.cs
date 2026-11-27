// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Everything that maps a named preset to what it actually means —
    /// kept as one static table so the toolbar, the view model, and
    /// settings persistence all agree on the same values instead of
    /// duplicating magic numbers in three places.
    /// </summary>
    public static class FileListViewPresets
    {
        public const double SmallIconSize = 32;
        public const double MediumIconSize = 64;
        public const double LargeIconSize = 96;
        public const double ExtraLargeIconSize = 160;

        public static readonly IReadOnlyList<FileListViewPreset> All = new[]
        {
            FileListViewPreset.Details,
            FileListViewPreset.List,
            FileListViewPreset.Tiles,
            FileListViewPreset.SmallIcons,
            FileListViewPreset.MediumIcons,
            FileListViewPreset.LargeIcons,
            FileListViewPreset.ExtraLargeIcons,
            FileListViewPreset.Timeline,
            FileListViewPreset.Columns,
            FileListViewPreset.Nexus
        };

        public static string DisplayName(FileListViewPreset preset)
        {
            switch (preset)
            {
                case FileListViewPreset.Details:         return "Details";
                case FileListViewPreset.List:            return "List";
                case FileListViewPreset.Tiles:           return "Tiles";
                case FileListViewPreset.SmallIcons:      return "Small icons";
                case FileListViewPreset.MediumIcons:     return "Medium icons";
                case FileListViewPreset.LargeIcons:      return "Large icons";
                case FileListViewPreset.ExtraLargeIcons: return "Extra large icons";
                case FileListViewPreset.Timeline:        return "Timeline";
                case FileListViewPreset.Columns:         return "Columns";
                case FileListViewPreset.Nexus:           return "Nexus";
                default:                                 return "Details";
            }
        }

        public static FileListViewMode ModeFor(FileListViewPreset preset)
        {
            switch (preset)
            {
                case FileListViewPreset.List:      return FileListViewMode.List;
                case FileListViewPreset.Tiles:     return FileListViewMode.Tiles;
                case FileListViewPreset.Timeline:  return FileListViewMode.Timeline;
                case FileListViewPreset.Columns:   return FileListViewMode.Columns;
                case FileListViewPreset.Nexus:     return FileListViewMode.Nexus;
                case FileListViewPreset.SmallIcons:
                case FileListViewPreset.MediumIcons:
                case FileListViewPreset.LargeIcons:
                case FileListViewPreset.ExtraLargeIcons:
                    return FileListViewMode.Icons;
                default:
                    return FileListViewMode.Details;
            }
        }

        public static double IconSizeFor(FileListViewPreset preset)
        {
            switch (preset)
            {
                case FileListViewPreset.SmallIcons:      return SmallIconSize;
                case FileListViewPreset.MediumIcons:     return MediumIconSize;
                case FileListViewPreset.LargeIcons:      return LargeIconSize;
                case FileListViewPreset.ExtraLargeIcons: return ExtraLargeIconSize;
                default:                                 return MediumIconSize;
            }
        }

        /// <summary>
        /// Given a continuous icon size (from Ctrl+scroll zoom), finds
        /// the nearest named preset — keeps the toolbar dropdown
        /// showing a sensible label mid-zoom, the same way Explorer's
        /// view switcher tracks continuous zoom.
        /// </summary>
        public static FileListViewPreset NearestPresetFor(double iconSize)
        {
            FileListViewPreset closest = FileListViewPreset.MediumIcons;
            double closestDistance = double.MaxValue;

            foreach (FileListViewPreset preset in All)
            {
                if (ModeFor(preset) != FileListViewMode.Icons)
                {
                    continue;
                }

                double distance = Math.Abs(IconSizeFor(preset) - iconSize);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = preset;
                }
            }

            return closest;
        }

        public static string ToSettingsString(FileListViewPreset preset) => preset.ToString();

        public static FileListViewPreset FromSettingsString(string value)
        {
            // Enum.TryParse also accepts numbers ("3", "42"), giving values that are no preset at all; a hand-edited or
            // damaged settings file falls back to Details instead.
            return !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value.Trim()[0]) && value.Trim()[0] != '-' &&
                   Enum.TryParse(value, ignoreCase: true, out FileListViewPreset parsed) && Enum.IsDefined(parsed)
                ? parsed
                : FileListViewPreset.Details;
        }
    }
}
