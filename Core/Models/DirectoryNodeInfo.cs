// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Immutable snapshot of a directory entry, captured off the UI
    /// thread by <see cref="Services.IFileSystemNavigationService"/>.
    /// </summary>
    public sealed class DirectoryNodeInfo
    {
        public DirectoryNodeInfo(string name, string fullPath, bool isHidden = false, bool isSystem = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
            IsHidden = isHidden;
            IsSystem = isSystem;
        }

        public string Name { get; }
        public string FullPath { get; }

        /// <summary>Hidden attribute (or a dot name on Linux and macOS): the tree draws the folder translucent.</summary>
        public bool IsHidden { get; }

        /// <summary>System attribute: the tree draws the folder's name in the system-item color.</summary>
        public bool IsSystem { get; }
    }
}
