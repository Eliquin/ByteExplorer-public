// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO;
using System.Globalization;

namespace ByteExplorer.ViewModels
{
    /// <summary>
    /// One drive row on the This PC home surface (local, removable, network, optical).
    /// </summary>
    public sealed class ThisPcDriveItem
    {
        public ThisPcDriveItem(
            string rootPath,
            string displayName,
            DriveType driveType,
            bool isReady,
            long totalSize,
            long freeSpace,
            bool isUnreachable = false,
            Core.Platform.Phones.PhoneDevice phone = null)
        {
            Phone = phone;
            RootPath = rootPath;
            DisplayName = displayName;
            DriveType = driveType;
            IsReady = isReady;
            TotalSize = totalSize;
            FreeSpace = freeSpace;
            IsUnreachable = isUnreachable;
        }

        public string RootPath { get; }
        public string DisplayName { get; }
        public DriveType DriveType { get; }
        public bool IsReady { get; }
        public long TotalSize { get; }
        public long FreeSpace { get; }

        /// <summary>
        /// True when this network drive did not respond to its
        /// readiness probe within the configured timeout — distinct
        /// from a drive that responded quickly and legitimately isn't
        /// ready. Drives that were probed no differently before (local,
        /// removable, optical) are never marked unreachable. Sourced
        /// from DriveNodeInfo.ProbeTimedOut via
        /// NavigationViewModel.DrivesRefreshed — see
        /// FileListViewModel_ThisPc.MapToThisPcDriveItems.
        /// </summary>
        public bool IsUnreachable { get; }

        /// <summary>Set for a connected phone (shown with the drives).</summary>
        public Core.Platform.Phones.PhoneDevice Phone { get; }

        public bool IsPhone => Phone != null;

        /// <summary>Right column: the drive's path, or a phone's storages ("Internal shared storage, SD card").</summary>
        public string LocationDisplay => Phone == null
            ? RootPath
            : Phone.Storages.Count > 0 ? string.Join(", ", System.Linq.Enumerable.Select(Phone.Storages, s => s.Name)) : null;

        /// <summary>Tooltip: an unreachable network drive, or the phone's maker and model (null: no tooltip).</summary>
        public string StatusTooltip => IsUnreachable
            ? "This network location isn't responding right now."
            : Phone?.NeedsMount == true
                ? "Opening it connects to the phone"
                : Phone?.Description;

        /// <summary>True once usage crosses a "getting full" threshold — mirrors Explorer's own red low-disk-space capacity bar.</summary>
        public bool IsNearFull => IsReady && !IsUnreachable && UsedPercent >= 90.0;

        public string TypeDisplay
        {
            get
            {
                if (Phone != null)
                {
                    return Phone.Description ?? "Phone";
                }

                switch (DriveType)
                {
                    case DriveType.Fixed: return "Local Disk";
                    case DriveType.Removable: return "Removable Disk";
                    case DriveType.Network: return "Network Drive";
                    case DriveType.CDRom: return "Optical Drive";
                    case DriveType.Ram: return "RAM Disk";
                    default: return "Drive";
                }
            }
        }

        /// <summary>
        /// The drive's icon, the same symbols the navigation tree uses for drives (Fluent UI System Icons; the WPF
        /// build's Segoe MDL2 glyphs exist only on Windows).
        /// </summary>
        public ByteExplorer.Icons.Symbol Glyph
        {
            get
            {
                if (Phone != null)
                {
                    return Infrastructure.IconGlyphs.Phone;
                }

                switch (DriveType)
                {
                    case DriveType.Fixed: return Infrastructure.IconGlyphs.DriveFixed;
                    case DriveType.Removable: return Infrastructure.IconGlyphs.DriveRemovable;
                    case DriveType.Network: return Infrastructure.IconGlyphs.DriveNetwork;
                    case DriveType.CDRom: return Infrastructure.IconGlyphs.DriveOptical;
                    default: return Infrastructure.IconGlyphs.DriveUnknown;
                }
            }
        }

        public double UsedPercent
        {
            get
            {
                if (!IsReady || TotalSize <= 0) return 0;
                long used = TotalSize - FreeSpace;
                if (used < 0) used = 0;
                return 100.0 * used / TotalSize;
            }
        }

        public string CapacityDisplay
        {
            get
            {
                if (IsUnreachable)
                {
                    return "Not responding";
                }

                if (!IsReady || TotalSize <= 0)
                {
                    if (Phone != null)
                    {
                        return Phone.NeedsMount ? "Not opened yet"
                            : Phone.Storages.Count == 0 ? "Unlock the phone to see its storage" : string.Empty;
                    }

                    return IsReady ? string.Empty : "Not ready";
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} free of {1}",
                    FormatBytes(FreeSpace),
                    FormatBytes(TotalSize));
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0
                ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", bytes, units[0])
                : string.Format(CultureInfo.CurrentCulture, "{0:N1} {1}", size, units[unit]);
        }
    }
}
