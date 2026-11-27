// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core
{
    /// <summary>
    /// The display name of a drive or volume, shared by the navigation tree and the This PC view. Windows: "Label (C:)",
    /// or "Local Disk (C:)" when unlabelled, as Explorer shows it. Linux/macOS: the volume's name alone ("File System",
    /// "Macintosh HD", "My USB"), as GNOME Files, Dolphin and Finder show it; the mount path is noise there.
    /// </summary>
    public static class DriveLabels
    {
        public static string Format(DriveNodeInfo drive) => Format(drive, showLetter: true, letterFirst: false);

        /// <summary>
        /// The label with Folder options' drive-letter choices (Windows): "Local Disk (C:)", "(C:) Local Disk", or just
        /// "Local Disk". A drive with no label still shows its letter: there would be nothing else to tell it by.
        /// </summary>
        public static string Format(DriveNodeInfo drive, bool showLetter, bool letterFirst)
        {
            if (drive == null)
            {
                return string.Empty;
            }

            if (drive.Phone != null)
            {
                return drive.Phone.Name;
            }

            string root = drive.RootPath ?? string.Empty;
            if (!OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                {
                    return drive.VolumeLabel;
                }

                return root == "/" ? "File System" : PathUtil.GetName(root);
            }

            string letter = root.TrimEnd('\\');
            if (!drive.IsReady)
            {
                return string.IsNullOrEmpty(letter) ? "Drive" : letter;
            }

            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? (drive.DriveType == DriveType.Fixed ? "Local Disk" : "Drive")
                : drive.VolumeLabel;
            if (!showLetter)
            {
                return label;
            }

            return letterFirst ? $"({letter}) {label}" : $"{label} ({letter})";
        }
    }
}
