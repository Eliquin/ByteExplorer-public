// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace ByteExplorer.Infrastructure.SystemIcons
{
    /// <summary>
    /// Explorer's own icons, from the Windows shell: <c>SHGetFileInfo</c> names an item's slot in the system image list
    /// (the list Explorer draws from), and that list's icon at the wanted size is turned into pixels. Called only on the
    /// icon thread (<see cref="SystemIconCache"/>), which is an STA thread as the shell expects.
    /// <para>
    /// Interop is kept to blittable values and raw pointers: <c>IImageList::GetIcon</c> is called through its vtable slot
    /// rather than a declared COM interface (a marshalling mistake there killed the process silently before, see the
    /// phones' interop), and the image lists are never cast to <c>HIMAGELIST</c> for the <c>ImageList_*</c> functions,
    /// which needs common controls 6 in the process. Items with icons of their own (programs, shortcuts, icon files,
    /// customised folders) are asked for by path; everything else by name only (<c>SHGFI_USEFILEATTRIBUTES</c>), which
    /// touches no disk.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class WindowsShellIconSource : ISystemIconSource
    {
        private const uint ShgfiSysIconIndex = 0x4000;
        private const uint ShgfiUseFileAttributes = 0x10;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileAttributeDirectory = 0x10;
        private const int ShilLarge = 0;       // 32 px
        private const int ShilSmall = 1;       // 16 px
        private const int ShilExtraLarge = 2;  // 48 px
        private const int ShilJumbo = 4;       // 256 px
        private const uint IldTransparent = 0x1;
        private const int GetIconSlot = 10;    // IUnknown (3) + Add, ReplaceIcon, SetOverlayImage, Replace, AddMasked, Draw, Remove, GetIcon

        private static readonly Guid ImageListIid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        private readonly IntPtr[] _lists = new IntPtr[5];

        public bool IsAvailable => true;

        public int IndexOf(string path, bool isDirectory, bool byPath)
        {
            var info = new ShFileInfo();
            uint flags = ShgfiSysIconIndex | (byPath ? 0 : ShgfiUseFileAttributes);
            uint attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            string query = byPath ? path : (isDirectory ? "folder" : System.IO.Path.GetFileName(path));
            IntPtr result = SHGetFileInfoW(query, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero && byPath)
            {
                // A file that can't be read (access denied, offline): its type's icon, as Explorer falls back to.
                return IndexOf(path, isDirectory, byPath: false);
            }

            return result == IntPtr.Zero ? -1 : info.IconIndex;
        }

        public IconPixels Render(int index, int size)
        {
            int list = size <= 16 ? ShilSmall : size <= 32 ? ShilLarge : size <= 48 ? ShilExtraLarge : ShilJumbo;
            IconPixels pixels = RenderFrom(list, index);
            if (list == ShilJumbo && pixels != null && pixels.ContentExtent <= 56)
            {
                // Many older icons have no 256 px image: the jumbo list then holds the 48 px one in a corner of a 256 px
                // square, which would draw tiny. Explorer scales the 48 px image up instead; so does this.
                IconPixels extraLarge = RenderFrom(ShilExtraLarge, index);
                return extraLarge ?? pixels;
            }

            return pixels;
        }

        private IconPixels RenderFrom(int list, int index)
        {
            IntPtr imageList = ImageList(list);
            if (imageList == IntPtr.Zero || index < 0)
            {
                return null;
            }

            IntPtr icon = IntPtr.Zero;
            void** vtable = *(void***)imageList;
            var getIcon = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)vtable[GetIconSlot];
            int hr = getIcon(imageList, index, IldTransparent, &icon);
            if (hr < 0 || icon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return ToPixels(icon);
            }
            finally
            {
                DestroyIcon(icon);
            }
        }

        private IntPtr ImageList(int list)
        {
            if (_lists[list] == IntPtr.Zero)
            {
                Guid iid = ImageListIid;
                int hr = SHGetImageList(list, ref iid, out IntPtr pointer);
                if (hr < 0)
                {
                    Log.Logger.Warning("The system image list {List} isn't available (0x{Hr:X8})", list, hr);
                    return IntPtr.Zero;
                }

                _lists[list] = pointer; // kept for the life of the process, as Explorer's own are
            }

            return _lists[list];
        }

        /// <summary>
        /// The icon's pixels, top-down BGRA with straight alpha. Icons without an alpha channel (old 4- and 8-bit ones)
        /// take their transparency from the mask.
        /// </summary>
        private static IconPixels ToPixels(IntPtr icon)
        {
            if (!GetIconInfo(icon, out IconInfo info))
            {
                return null;
            }

            IntPtr dc = IntPtr.Zero;
            try
            {
                if (info.Color == IntPtr.Zero)
                {
                    return null; // a monochrome icon: none in the system image list since Windows XP
                }

                BitmapHeader bitmap;
                if (GetObjectW(info.Color, sizeof(BitmapHeader), &bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 512 || bitmap.Height > 512)
                {
                    return null;
                }

                int width = bitmap.Width;
                int height = bitmap.Height;
                byte[] data = new byte[width * height * 4];
                var header = new BitmapInfoHeader
                {
                    Size = sizeof(BitmapInfoHeader),
                    Width = width,
                    Height = -height, // top-down
                    Planes = 1,
                    BitCount = 32
                };
                dc = CreateCompatibleDC(IntPtr.Zero);
                fixed (byte* bits = data)
                {
                    if (GetDIBits(dc, info.Color, 0, (uint)height, bits, &header, 0) == 0)
                    {
                        return null;
                    }
                }

                bool hasAlpha = false;
                for (int i = 3; i < data.Length; i += 4)
                {
                    if (data[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha && info.Mask != IntPtr.Zero)
                {
                    byte[] mask = new byte[width * height * 4];
                    var maskHeader = header;
                    fixed (byte* bits = mask)
                    {
                        if (GetDIBits(dc, info.Mask, 0, (uint)height, bits, &maskHeader, 0) != 0)
                        {
                            for (int i = 0; i < data.Length; i += 4)
                            {
                                data[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0; // black in the mask: the icon's own pixel
                            }
                        }
                    }
                }

                return new IconPixels(width, height, data);
            }
            finally
            {
                if (dc != IntPtr.Zero)
                {
                    DeleteDC(dc);
                }

                if (info.Color != IntPtr.Zero)
                {
                    DeleteObject(info.Color);
                }

                if (info.Mask != IntPtr.Zero)
                {
                    DeleteObject(info.Mask);
                }
            }
        }

        // ------------------------------------------------------------------ native

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string TypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public int IsIcon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public int Type;
            public int Width;
            public int Height;
            public int WidthBytes;
            public ushort Planes;
            public ushort BitsPixel;
            public IntPtr Bits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
            // A 32-bit BI_RGB request has no color table; room for one entry in case a driver writes it anyway.
            public uint Palette0;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr SHGetFileInfoW([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

        // Exported by name since Vista and by ordinal 727 since XP; the ordinal works on every version.
        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int list, ref Guid iid, out IntPtr imageList);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int GetObjectW(IntPtr handle, int size, void* buffer);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);
    }
}
