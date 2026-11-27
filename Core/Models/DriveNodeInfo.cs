// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Immutable snapshot of a drive, captured off the UI thread. Kept as
    /// a plain data holder — no WPF types, no dispatcher — so the drive
    /// enumeration subsystem stays usable outside a UI context entirely.
    /// </summary>
    public sealed class DriveNodeInfo
    {
        public DriveNodeInfo(
            string rootPath,
            string volumeLabel,
            System.IO.DriveType driveType,
            bool isReady,
            long totalSize,
            long availableFreeSpace,
            bool probeTimedOut = false)
        {
            RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
            VolumeLabel = volumeLabel;
            DriveType = driveType;
            IsReady = isReady;
            TotalSize = totalSize;
            AvailableFreeSpace = availableFreeSpace;
            ProbeTimedOut = probeTimedOut;
        }

        public string RootPath { get; }
        public string VolumeLabel { get; }
        public System.IO.DriveType DriveType { get; }
        public bool IsReady { get; }
        public long TotalSize { get; }
        public long AvailableFreeSpace { get; }

        /// <summary>
        /// True when this drive did not respond to its readiness/capacity
        /// probe within the configured probe window and was reported as
        /// not-ready purely because of that — distinct from a drive that
        /// responded quickly and legitimately isn't ready (an empty card
        /// reader, an ejected optical drive). Only ever set for
        /// <see cref="System.IO.DriveType.Network"/> drives; local media
        /// is never probed with a timeout since it was never observed to
        /// hang. The navigation tree uses this to show a distinct
        /// "unreachable" state instead of silently looking empty/unready.
        /// </summary>
        public bool ProbeTimedOut { get; }

        /// <summary>Set when this "drive" is a connected phone (This PC and the tree show it with the drives).</summary>
        public Platform.Phones.PhoneDevice Phone { get; init; }
    }
}
