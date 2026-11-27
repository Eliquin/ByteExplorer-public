// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Windows Explorer's Folder Options, for every layout (Settings › Folder options). Defaults keep ByteExplorer as it
    /// was before these options existed, which is not always Explorer's default (Explorer hides extensions and empty drives).
    /// </summary>
    public sealed class FolderOptionsSettings
    {
        /// <summary>Opening places for <see cref="OpenAt"/>.</summary>
        public const string OpenAtHome = "Home";
        public const string OpenAtThisPc = "ThisPc";
        public const string OpenAtFolder = "Folder";
        public const string OpenAtLastSession = "LastSession";

        /// <summary>Typing in the file list: jump to the item whose name starts so, or type into the search box.</summary>
        public const string TypeSelects = "Select";
        public const string TypeSearches = "Search";

        // ----- General

        /// <summary>Where a new window opens: Home, This PC, <see cref="StartupFolder"/>, or the folders open at the last close.</summary>
        public string OpenAt { get; set; } = OpenAtHome;

        /// <summary>The folder for <see cref="OpenAtFolder"/>; Home when it's missing.</summary>
        public string StartupFolder { get; set; } = string.Empty;

        /// <summary>One click opens an item (names underlined when pointed at); Ctrl+click and Shift+click still select.</summary>
        public bool OpenWithSingleClick { get; set; }

        /// <summary>Show recently used files on Home and This PC.</summary>
        public bool ShowRecentFiles { get; set; } = true;

        /// <summary>Show frequently used folders on Home and in the left panel.</summary>
        public bool ShowFrequentFolders { get; set; } = true;

        // ----- View

        /// <summary>"report" instead of "report.pdf" for file types the app knows; renaming keeps the extension.</summary>
        public bool HideKnownExtensions { get; set; }

        /// <summary>Leave out drives with no media (a card reader without a card, an empty DVD drive).</summary>
        public bool HideEmptyDrives { get; set; }

        /// <summary>Windows: "Local Disk (C:)"; off: "Local Disk".</summary>
        public bool ShowDriveLetters { get; set; } = true;

        /// <summary>Windows: "(C:) Local Disk" instead of "Local Disk (C:)".</summary>
        public bool DriveLettersFirst { get; set; }

        /// <summary>The window title is the open folder's full path instead of "ByteExplorer".</summary>
        public bool FullPathInTitleBar { get; set; }

        /// <summary>A check box on each item to select it (touch screens, touchpads).</summary>
        public bool UseCheckBoxes { get; set; }

        /// <summary>Hovering an item shows its details.</summary>
        public bool ShowItemTooltips { get; set; } = true;

        /// <summary>A file's size in its hover tip (folder tips: their item count, when known).</summary>
        public bool ShowSizeInTooltips { get; set; } = true;

        /// <summary><see cref="TypeSelects"/> or <see cref="TypeSearches"/>.</summary>
        public string TypingInList { get; set; } = TypeSelects;

        public const string ContextMenuByteExplorer = "ByteExplorer";
        public const string ContextMenuWindows = "Windows";
        public const string ContextMenuHybrid = "Hybrid";

        /// <summary>
        /// The right-click menu of files and folders: <see cref="ContextMenuByteExplorer"/> (the app's own menu),
        /// <see cref="ContextMenuWindows"/> (the Windows shell menu, with every program's entries) or
        /// <see cref="ContextMenuHybrid"/> (the app's menu with "Show more options" for the Windows one, as Windows 11).
        /// Windows and Hybrid apply on Windows only; elsewhere the app's menu is shown.
        /// </summary>
        public string ContextMenu { get; set; } = ContextMenuHybrid;

        // ----- Navigation pane

        /// <summary>The home folder (with its subfolders) above This PC in the tree, as Explorer's "Show all folders".</summary>
        public bool ShowAllFolders { get; set; }

        /// <summary>
        /// Phones and cameras connected by USB (Windows Portable Devices, Linux MTP mounts) in This PC and the tree. Off:
        /// the app never talks to them (a way out if a device's driver misbehaves).
        /// </summary>
        public bool ShowPhones { get; set; } = true;

        // ----- Kept by the app for <see cref="OpenAtLastSession"/>

        public string LastSessionFolder { get; set; } = string.Empty;
        public string LastSessionSecondFolder { get; set; } = string.Empty;
        public bool LastSessionSplitView { get; set; }

        public void Sanitize()
        {
            if (OpenAt != OpenAtHome && OpenAt != OpenAtThisPc && OpenAt != OpenAtFolder && OpenAt != OpenAtLastSession)
            {
                OpenAt = OpenAtHome;
            }

            if (ContextMenu != ContextMenuByteExplorer && ContextMenu != ContextMenuWindows && ContextMenu != ContextMenuHybrid)
            {
                ContextMenu = ContextMenuHybrid;
            }

            if (TypingInList != TypeSelects && TypingInList != TypeSearches)
            {
                TypingInList = TypeSelects;
            }

            StartupFolder ??= string.Empty;
            LastSessionFolder ??= string.Empty;
            LastSessionSecondFolder ??= string.Empty;
        }
    }
}
