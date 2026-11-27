// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Icons
{
    /// <summary>
    /// The icons the app uses, from the bundled Fluent UI System Icons font (Assets/Fonts/SeagullFluentIcons.ttf). Names
    /// match the FluentIcons library the app used before, which this replaces so the app builds from local packages only.
    /// To add an icon: add its name here and its two codepoints in <see cref="SymbolGlyphs"/> (same order).
    /// </summary>
    public enum Symbol
    {
        Add,
        Apps,
        AppsList,
        Archive,
        ArrowClockwise,
        ArrowDownload,
        ArrowLeft,
        ArrowRight,
        ArrowSort,
        ArrowUp,
        ChevronDown,
        ChevronRight,
        ChevronUp,
        ClipboardPaste,
        Code,
        Copy,
        Cut,
        Delete,
        DeleteDismiss,
        Desktop,
        Dismiss,
        Document,
        DocumentAdd,
        DocumentCopy,
        DocumentPdf,
        DocumentText,
        ErrorCircle,
        Filter,
        Folder,
        FolderAdd,
        FolderOpen,
        FolderZip,
        FullScreenMaximize,
        FullScreenMinimize,
        Globe,
        Grid,
        HardDrive,
        History,
        Home,
        Image,
        Info,
        Link,
        Maximize,
        MoreHorizontal,
        MusicNote2,
        Open,
        PanelLeft,
        PanelRight,
        Pause,
        Pin,
        Play,
        Record,
        Rename,
        Search,
        SelectAllOn,
        Speaker2,
        SpeakerMute,
        SplitVertical,
        SquareMultiple,
        Stop,
        Storage,
        Subtract,
        Table,
        UsbStick,
        Video,
        VideoClip,
        WindowConsole,
        ArrowUndo,
        Bookmark,
        GlobeDesktop,
        Server,
        Star,
        StarAdd,
        Settings,
        PaintBrush,
        Shield,
        Wrench,
        Window,
        DocumentSettings,
        Navigation,
        Gauge,
        ArrowReset,
        ArrowImport,
        ArrowExport,
        Eye,
        ArrowSync,
        TextFont,
        DarkTheme,
        ChevronLeft,
        LayoutColumnThree,
        ImageMultiple,
        TextBulletList,
        Tag,
        ArrowRedo,
        Phone,
        TabAdd,
        TabDesktop,
        TabDesktopCopy,
        TabDesktopArrowClockwise,
        TabDesktopMultiple,
        PinOff,
        PanelLeftContract,
        PanelLeftExpand,
        PanelRightContract,
        PanelRightExpand,
        PanelTopExpand,
        ArrowDown,
        AddCircle,
        SelectAllOff,
        LockClosed,
        LockOpen,
        Password,
        EyeOff,
        DocumentLock,
        KeyReset,
        Warning,
        CalendarWorkWeek,
        CalendarMonth,
        Sparkle,
        Timeline,
        Heart,
        DrinkCoffee
    }

    /// <summary>Outline or solid style of an icon.</summary>
    public enum IconVariant
    {
        Regular,
        Filled
    }

    /// <summary>
    /// Codepoints of each <see cref="Symbol"/> in the bundled font (Seagull Fluent Icons, the WinUI-metric variant of
    /// Microsoft's fluentui-system-icons from the FluentIcons project; MIT, see THIRD-PARTY-NOTICES.md). Indexed by the
    /// enum value.
    /// </summary>
    public static class SymbolGlyphs
    {
        private static readonly int[] Regular =
        {
            0xF0008, // Add
            0xF0030, // Apps
            0xF0038, // AppsList
            0xF003C, // Archive
            0xF06B4, // ArrowClockwise
            0xF06C0, // ArrowDownload
            0xF0048, // ArrowLeft
            0xF0054, // ArrowRight
            0xF06C4, // ArrowSort
            0xF0554, // ArrowUp
            0xF014C, // ChevronDown
            0xF0154, // ChevronRight
            0xF0158, // ChevronUp
            0xF0710, // ClipboardPaste
            0xF0178, // Code
            0xF0198, // Copy
            0xF0718, // Cut
            0xF01A4, // Delete
            0xF10AC, // DeleteDismiss
            0xF01A8, // Desktop
            0xF01C0, // Dismiss
            0xF01D8, // Document
            0xF0E10, // DocumentAdd
            0xF01DC, // DocumentCopy
            0xF07C0, // DocumentPdf
            0xF1A18, // DocumentText
            0xF06A4, // ErrorCircle
            0xF021C, // Filter
            0xF0234, // Folder
            0xF0238, // FolderAdd
            0xF058C, // FolderOpen
            0xF0870, // FolderZip
            0xF10C8, // FullScreenMaximize
            0xF10CC, // FullScreenMinimize
            0xF0254, // Globe
            0xF0258, // Grid
            0xF1D40, // HardDrive
            0xF0270, // History
            0xF0274, // Home
            0xF0278, // Image
            0xF0288, // Info
            0xF02AC, // Link
            0xF0A20, // Maximize
            0xF10F8, // MoreHorizontal
            0xF1100, // MusicNote2
            0xF032C, // Open
            0xF184C, // PanelLeft
            0xF1850, // PanelRight
            0xF0340, // Pause
            0xF03A4, // Pin
            0xF03A8, // Play
            0xF0898, // Record
            0xF03D0, // Rename
            0xF03F4, // Search
            0xF1140, // SelectAllOn
            0xF1154, // Speaker2
            0xF1158, // SpeakerMute
            0xF1300, // SplitVertical
            0xF101C, // SquareMultiple
            0xF08AC, // Stop
            0xF0460, // Storage
            0xF1160, // Subtract
            0xF0478, // Table
            0xF062C, // UsbStick
            0xF04D4, // Video
            0xF04D8, // VideoClip
            0xF1930, // WindowConsole
            0xF0704, // ArrowUndo (stored as arrow_redo_rtl: the same glyph)
            0xF00B0, // Bookmark
            0xF06CC, // GlobeDesktop
            0xF15E0, // Server
            0xF0450, // Star
            0xF05FC, // StarAdd
            0xF0404, // Settings
            0xF0888, // PaintBrush
            0xF041C, // Shield
            0xF0CB8, // Wrench
            0xF0908, // Window
            0xF14F0, // DocumentSettings
            0xF02FC, // Navigation
            0xF1520, // Gauge
            0xF144C, // ArrowReset
            0xF0768, // ArrowImport
            0xF0790, // ArrowExport
            0xF1C80, // Eye
            0xF06FC, // ArrowSync
            0xF07F0, // TextFont
            0xF019C, // DarkTheme
            0xF0150, // ChevronLeft
            0xF2678, // LayoutColumnThree
            0xF10D0, // ImageMultiple
            0xF0494, // TextBulletList
            0xF0484, // Tag
            0xF06F4, // ArrowRedo
            0xF037C, // Phone
            0xF1164, // TabAdd
            0xF08EC, // TabDesktop
            0xF0C88, // TabDesktopCopy
            0xF0A40, // TabDesktopArrowClockwise
            0xF0C8C, // TabDesktopMultiple
            0x0E77A, // PinOff
            0xF1C8C, // PanelLeftContract
            0xF1900, // PanelLeftExpand
            0xF1854, // PanelRightContract
            0xF1858, // PanelRightExpand
            0xF190C, // PanelTopExpand
            0xF0040, // ArrowDown
            0xF06A0, // AddCircle
            0xF03F8, // SelectAllOff
            0xF10E0, // LockClosed
            0xF10E4, // LockOpen
            0xF0AE0, // Password
            0xF1768, // EyeOff
            0xF1C70, // DocumentLock
            0xF1BBC, // KeyReset
            0xF04E8, // Warning
            0xF0C10, // CalendarWorkWeek
            0xF00E4, // CalendarMonth
            0xF1A48, // Sparkle
            0xF04C0, // Timeline
            0xF0268, // Heart
            0xF0A80, // DrinkCoffee
        };

        private static readonly int[] Filled =
        {
            0xF0009, // Add
            0xF0031, // Apps
            0xF0039, // AppsList
            0xF003D, // Archive
            0xF06B5, // ArrowClockwise
            0xF06C1, // ArrowDownload
            0xF0049, // ArrowLeft
            0xF0055, // ArrowRight
            0xF06C5, // ArrowSort
            0xF0555, // ArrowUp
            0xF014D, // ChevronDown
            0xF0155, // ChevronRight
            0xF0159, // ChevronUp
            0xF0711, // ClipboardPaste
            0xF0179, // Code
            0xF0199, // Copy
            0xF0719, // Cut
            0xF01A5, // Delete
            0xF10AD, // DeleteDismiss
            0xF01A9, // Desktop
            0xF01C1, // Dismiss
            0xF01D9, // Document
            0xF0E11, // DocumentAdd
            0xF01DD, // DocumentCopy
            0xF07C1, // DocumentPdf
            0xF1A19, // DocumentText
            0xF06A5, // ErrorCircle
            0xF021D, // Filter
            0xF0235, // Folder
            0xF0239, // FolderAdd
            0xF058D, // FolderOpen
            0xF0871, // FolderZip
            0xF10C9, // FullScreenMaximize
            0xF10CD, // FullScreenMinimize
            0xF0255, // Globe
            0xF0259, // Grid
            0xF1D41, // HardDrive
            0xF0271, // History
            0xF0275, // Home
            0xF0279, // Image
            0xF0289, // Info
            0xF02AD, // Link
            0xF0A21, // Maximize
            0xF10F9, // MoreHorizontal
            0xF1101, // MusicNote2
            0xF032D, // Open
            0xF184D, // PanelLeft
            0xF1851, // PanelRight
            0xF0341, // Pause
            0xF03A5, // Pin
            0xF03A9, // Play
            0xF0899, // Record
            0xF03D1, // Rename
            0xF03F5, // Search
            0xF1141, // SelectAllOn
            0xF1155, // Speaker2
            0xF1159, // SpeakerMute
            0xF1301, // SplitVertical
            0xF101D, // SquareMultiple
            0xF08AD, // Stop
            0xF0461, // Storage
            0xF1161, // Subtract
            0xF0479, // Table
            0xF062D, // UsbStick
            0xF04D5, // Video
            0xF04D9, // VideoClip
            0xF1931, // WindowConsole
            0xF0705, // ArrowUndo
            0xF00B1, // Bookmark
            0xF06CD, // GlobeDesktop
            0xF15E1, // Server
            0xF0451, // Star
            0xF05FD, // StarAdd
            0xF0405, // Settings
            0xF0889, // PaintBrush
            0xF041D, // Shield
            0xF0CB9, // Wrench
            0xF0909, // Window
            0xF14F1, // DocumentSettings
            0xF02FD, // Navigation
            0xF1521, // Gauge
            0xF144D, // ArrowReset
            0xF0769, // ArrowImport
            0xF0791, // ArrowExport
            0xF1C81, // Eye
            0xF06FD, // ArrowSync
            0xF07F1, // TextFont
            0xF019D, // DarkTheme
            0xF0151, // ChevronLeft
            0xF2679, // LayoutColumnThree
            0xF10D1, // ImageMultiple
            0xF0495, // TextBulletList
            0xF0485, // Tag
            0xF06F5, // ArrowRedo
            0xF037D, // Phone
            0xF1165, // TabAdd
            0xF08ED, // TabDesktop
            0xF0C89, // TabDesktopCopy
            0xF0A41, // TabDesktopArrowClockwise
            0xF0C8D, // TabDesktopMultiple
            0xF06D9, // PinOff
            0xF1C8D, // PanelLeftContract
            0xF1901, // PanelLeftExpand
            0xF1855, // PanelRightContract
            0xF1859, // PanelRightExpand
            0xF190D, // PanelTopExpand
            0xF0041, // ArrowDown
            0xF06A1, // AddCircle
            0xF03F9, // SelectAllOff
            0xF10E1, // LockClosed
            0xF10E5, // LockOpen
            0xF0AE1, // Password
            0xF1769, // EyeOff
            0xF1C71, // DocumentLock
            0xF1BBD, // KeyReset
            0xF04E9, // Warning
            0xF0C11, // CalendarWorkWeek
            0xF00E5, // CalendarMonth
            0xF1A49, // Sparkle
            0xF04C1, // Timeline
            0xF0269, // Heart
            0xF0A81, // DrinkCoffee
        };

        private static readonly string[] RegularText = Build(Regular);
        private static readonly string[] FilledText = Build(Filled);

        /// <summary>The character(s) to draw for the symbol (cached strings, no allocation per call).</summary>
        public static string Get(Symbol symbol, IconVariant variant)
        {
            int index = (int)symbol;
            string[] table = variant == IconVariant.Filled ? FilledText : RegularText;
            return (uint)index < (uint)table.Length ? table[index] : string.Empty;
        }

        private static string[] Build(int[] codepoints)
        {
            var result = new string[codepoints.Length];
            for (int i = 0; i < codepoints.Length; i++)
            {
                result[i] = char.ConvertFromUtf32(codepoints[i]);
            }

            return result;
        }
    }
}
