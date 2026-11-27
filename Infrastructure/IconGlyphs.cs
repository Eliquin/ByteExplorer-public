// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using ByteExplorer.Core.Models;
using ByteExplorer.Icons;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Maps node types to Segoe MDL2 Assets glyphs. Segoe MDL2 Assets
    /// ships with Windows 10 1809+ and Windows 11, so this costs zero
    /// extra dependencies and stays fully portable. (Windows 11 also
    /// offers Segoe Fluent Icons with a slightly different glyph set —
    /// worth revisiting as a polish pass, but MDL2 Assets covers both
    /// OS versions from one glyph table, which matters more right now.)
    /// </summary>
    public static class IconGlyphs
    {
        public const Symbol Computer = Symbol.Desktop;
        public const Symbol DriveFixed = Symbol.HardDrive;
        public const Symbol DriveRemovable = Symbol.UsbStick;
        public const Symbol DriveNetwork = Symbol.Globe;
        public const Symbol Phone = Symbol.Phone;
        public const Symbol DriveOptical = Symbol.Record;
        public const Symbol DriveUnknown = Symbol.Storage;
        public const Symbol FolderClosed = Symbol.Folder;
        public const Symbol FolderOpen = Symbol.FolderOpen;

        public static Symbol For(NavigationNodeType nodeType, bool isExpanded)
        {
            switch (nodeType)
            {
                case NavigationNodeType.Computer:
                    return Computer;
                case NavigationNodeType.DriveFixed:
                    return DriveFixed;
                case NavigationNodeType.DriveRemovable:
                    return DriveRemovable;
                case NavigationNodeType.DriveNetwork:
                    return DriveNetwork;
                case NavigationNodeType.Phone:
                    return Phone;
                case NavigationNodeType.DriveOptical:
                    return DriveOptical;
                case NavigationNodeType.Folder:
                    return isExpanded ? FolderOpen : FolderClosed;
                default:
                    return DriveUnknown;
            }
        }
    }
}
