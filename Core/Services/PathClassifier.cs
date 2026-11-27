// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Small, focused helper: is a path network-backed? Used to decide
    /// whether an enumeration timeout applies (local drives never time
    /// out; network paths can hang indefinitely without one). Windows:
    /// UNC paths and network drive letters; Linux: network file systems
    /// from the mount table; macOS: not detected (see below).
    /// </summary>
    public static class PathClassifier
    {
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return true; // UNC path
            }

            if (!OperatingSystem.IsWindows())
            {
                // On Linux and macOS every path's root is "/", so the drive check below only ever saw the root file
                // system and never flagged an NFS or SMB mount: a share that stopped answering hung the folder load with
                // no timeout. Linux answers from the kernel's mount table without touching the share. macOS has no such
                // non-blocking source available here, so its network volumes keep the local behaviour (no timeout).
                return Platform.MountPoints.IsOnNetworkFileSystemLinux(path);
            }

            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                {
                    return false;
                }

                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Network;
            }
            catch (Exception)
            {
                // Malformed or unreachable path — treat as "not network"
                // and let the normal enumeration error handling deal
                // with the actual failure.
                return false;
            }
        }
    }
}
