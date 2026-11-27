// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Serilog;

namespace ByteExplorer.Infrastructure.Shell
{
    /// <summary>
    /// The real Windows shell context menu (Settings › Folder options › Right-click menu: "Windows", or "Show more
    /// options" in "Hybrid"): the classic IContextMenu path, so it has every verb and third-party entry File Explorer
    /// shows (7-Zip, "Open with Code", TortoiseGit, antivirus scans, Send to, Open with…). For items (one or more files
    /// and folders of one folder) and for a folder's empty area (its background menu: Paste, New, Properties and the
    /// extensions that add to it).
    /// <para>
    /// Ported from the WPF build's ShellContextMenu (git 3f9bc91^), which worked there: the window's menu messages are
    /// forwarded to IContextMenu2/3 while the menu is open (through Avalonia's WndProc hook instead of WPF's HwndSource),
    /// or Send to, Open with and owner-drawn entries come up empty; the verb of the chosen entry is read as Unicode. The
    /// caller's <c>intercept</c> gets that verb first ("rename", "delete", "cut", "copy", "paste", "open", "refresh"…):
    /// those the app does itself (its own rename, its undoable delete, its paste queue, opening a folder in the pane)
    /// return true and the shell is not asked; the rest are invoked through the shell. Rename in particular only works
    /// inside Explorer's own view, so it must be intercepted.
    /// </para>
    /// <para>
    /// Windows only, UI (STA) thread. Every COM object and PIDL is released; any failure (a broken shell extension) is
    /// logged and reported as false, so the caller can fall back to the app's own menu.
    /// </para>
    /// </summary>
    public static class ShellContextMenu
    {
        private static readonly ILogger Logger = Log.Logger.ForContext(typeof(ShellContextMenu));

        public static bool IsSupported => SupportedForTests ?? OperatingSystem.IsWindows();

        /// <summary>Tests only: lets the menus offer the Windows menu on any system (nothing calls the shell then).</summary>
        internal static bool? SupportedForTests { get; set; }

        /// <summary>
        /// Whether the shell can make one menu for <paramref name="paths"/>: real file-system paths (not inside an archive,
        /// not on a phone, not Home or This PC), all in the same folder (IShellFolder.GetUIObjectOf takes the children of
        /// one parent).
        /// </summary>
        public static bool CanShowFor(IReadOnlyList<string> paths)
        {
            if (!IsSupported || paths == null || paths.Count == 0 || paths.Count > 2000)
            {
                return false;
            }

            string parent = null;
            foreach (string path in paths)
            {
                if (!IsShellPath(path))
                {
                    return false;
                }

                string itemParent = Core.PathUtil.GetParent(path) ?? path; // a drive root's "parent" is This PC
                if (parent == null)
                {
                    parent = itemParent;
                }
                else if (!Core.PathUtil.AreSame(parent, itemParent))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether the shell can make the background menu of <paramref name="folder"/>.</summary>
        public static bool CanShowForBackground(string folder) => IsSupported && IsShellPath(folder);

        internal static bool IsShellPath(string path) =>
            !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) &&
            !Core.Services.ArchiveBrowser.IsInsideArchive(path) &&
            !Core.Platform.Phones.Phones.IsPhonePath(path) &&
            !Core.Security.ProtectedFormat.IsProtectedPath(path);

        /// <summary>Shows the menu for items at <paramref name="screenPoint"/> (physical pixels). True when it was shown.</summary>
        public static bool ShowForItems(TopLevel owner, IReadOnlyList<string> paths, PixelPoint screenPoint, Func<string, bool> intercept)
        {
            if (!CanShowFor(paths) || owner == null || !OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return ShowCore(owner, paths, null, screenPoint, intercept);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Shell context menu failed for {Count} path(s)", paths.Count);
                return false;
            }
        }

        /// <summary>Shows a folder's background menu (its empty area) at <paramref name="screenPoint"/>. True when it was shown.</summary>
        public static bool ShowForBackground(TopLevel owner, string folder, PixelPoint screenPoint, Func<string, bool> intercept)
        {
            if (!CanShowForBackground(folder) || owner == null || !OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return ShowCore(owner, null, folder, screenPoint, intercept);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Shell background menu failed for {Folder}", folder);
                return false;
            }
        }

        /// <summary>The pointer's position on the screen (physical pixels), for a menu opened by the mouse.</summary>
        public static PixelPoint CursorPosition()
        {
            if (OperatingSystem.IsWindows() && GetCursorPos(out POINT point))
            {
                return new PixelPoint(point.x, point.y);
            }

            return default;
        }

        /// <summary>Shift is held now (Shift+right-click opens the Windows menu directly in Hybrid, as Windows 11 does).</summary>
        public static bool IsShiftDown() => OperatingSystem.IsWindows() && (GetKeyState(VK_SHIFT) & 0x8000) != 0;

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool ShowCore(TopLevel owner, IReadOnlyList<string> paths, string backgroundFolder, PixelPoint screenPoint, Func<string, bool> intercept)
        {
            IntPtr hwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var absolutePidls = new List<IntPtr>();
            IShellFolder parentFolder = null;
            IShellFolder backgroundShellFolder = null;
            IContextMenu contextMenu = null;
            IntPtr hmenu = IntPtr.Zero;
            try
            {
                if (backgroundFolder != null)
                {
                    contextMenu = BackgroundMenu(hwnd, backgroundFolder, absolutePidls, out parentFolder, out backgroundShellFolder);
                }
                else
                {
                    contextMenu = ItemsMenu(paths, absolutePidls, out parentFolder);
                }

                if (contextMenu == null)
                {
                    return false;
                }

                hmenu = CreatePopupMenu();
                if (hmenu == IntPtr.Zero)
                {
                    return false;
                }

                const uint CMF_NORMAL = 0x00000000;
                const uint CMF_EXPLORE = 0x00000004;
                const uint CMF_CANRENAME = 0x00000010;
                const uint CMF_EXTENDEDVERBS = 0x00000100; // Shift held: "Copy as path", "Open command window here"…
                uint flags = backgroundFolder != null ? CMF_NORMAL : CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
                if (IsShiftDown())
                {
                    flags |= CMF_EXTENDEDVERBS;
                }

                int hr = contextMenu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags);
                if (hr < 0)
                {
                    return false;
                }

                // Submenus such as Send to and Open with are filled, and owner-drawn items measured and painted, only
                // when the menu's window messages reach IContextMenu2/3 (as Explorer forwards them).
                var menu2 = contextMenu as IContextMenu2;
                var menu3 = contextMenu as IContextMenu3;
                Win32Properties.CustomWndProcHookCallback hook = null;
                if (menu2 != null || menu3 != null)
                {
                    hook = (IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        ForwardMenuMessage(menu2, menu3, msg, wParam, lParam, ref handled);
                    Win32Properties.AddWndProcHookCallback(owner, hook);
                }

                uint cmd;
                try
                {
                    cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON, screenPoint.X, screenPoint.Y, hwnd, IntPtr.Zero);
                }
                finally
                {
                    if (hook != null)
                    {
                        Win32Properties.RemoveWndProcHookCallback(owner, hook);
                    }
                }

                if (cmd < FirstCommand)
                {
                    return true; // shown and dismissed
                }

                uint offset = cmd - FirstCommand;
                string verb = GetVerb(contextMenu, offset);
                if (intercept != null && intercept(verb))
                {
                    return true;
                }

                var invoke = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = hwnd,
                    lpVerb = (IntPtr)offset,
                    lpVerbW = (IntPtr)offset,
                    lpDirectoryW = backgroundFolder != null ? Marshal.StringToHGlobalUni(backgroundFolder) : IntPtr.Zero,
                    nShow = SW_SHOWNORMAL,
                    ptInvoke = new POINT { x = screenPoint.X, y = screenPoint.Y }
                };
                try
                {
                    hr = contextMenu.InvokeCommand(ref invoke);
                }
                finally
                {
                    if (invoke.lpDirectoryW != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(invoke.lpDirectoryW);
                    }
                }

                if (hr < 0)
                {
                    Logger.Information("The shell command {Verb} failed: 0x{Hr:X8}", verb ?? offset.ToString(), hr);
                }

                return true;
            }
            finally
            {
                if (hmenu != IntPtr.Zero)
                {
                    DestroyMenu(hmenu);
                }

                Release(contextMenu);
                Release(backgroundShellFolder);
                Release(parentFolder);
                foreach (IntPtr pidl in absolutePidls)
                {
                    ILFree(pidl);
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static IContextMenu ItemsMenu(IReadOnlyList<string> paths, List<IntPtr> absolutePidls, out IShellFolder parentFolder)
        {
            parentFolder = null;
            foreach (string path in paths)
            {
                IntPtr pidl = ILCreateFromPathW(path);
                if (pidl != IntPtr.Zero)
                {
                    absolutePidls.Add(pidl);
                }
            }

            if (absolutePidls.Count == 0)
            {
                return null;
            }

            Guid iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(absolutePidls[0], ref iidShellFolder, out parentFolder, out _) != 0 || parentFolder == null)
            {
                return null;
            }

            var relativePidls = new IntPtr[absolutePidls.Count];
            for (int i = 0; i < absolutePidls.Count; i++)
            {
                relativePidls[i] = ILFindLastID(absolutePidls[i]);
            }

            Guid iidContextMenu = typeof(IContextMenu).GUID;
            int hr = parentFolder.GetUIObjectOf(IntPtr.Zero, (uint)relativePidls.Length, relativePidls, ref iidContextMenu, IntPtr.Zero, out object menu);
            return hr == 0 ? menu as IContextMenu : null;
        }

        /// <summary>The folder's own background menu: bind to the folder, then IShellFolder.CreateViewObject(IID_IContextMenu).</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static IContextMenu BackgroundMenu(IntPtr hwnd, string folder, List<IntPtr> absolutePidls, out IShellFolder parentFolder, out IShellFolder shellFolder)
        {
            parentFolder = null;
            shellFolder = null;
            IntPtr pidl = ILCreateFromPathW(folder);
            if (pidl == IntPtr.Zero)
            {
                return null;
            }

            absolutePidls.Add(pidl);
            Guid iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidShellFolder, out parentFolder, out IntPtr last) != 0 || parentFolder == null)
            {
                return null;
            }

            if (parentFolder.BindToObject(last, IntPtr.Zero, ref iidShellFolder, out IntPtr folderPtr) != 0 || folderPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                shellFolder = Marshal.GetObjectForIUnknown(folderPtr) as IShellFolder;
            }
            finally
            {
                Marshal.Release(folderPtr);
            }

            if (shellFolder == null)
            {
                return null;
            }

            Guid iidContextMenu = typeof(IContextMenu).GUID;
            if (shellFolder.CreateViewObject(hwnd, ref iidContextMenu, out IntPtr menuPtr) != 0 || menuPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(menuPtr) as IContextMenu;
            }
            finally
            {
                Marshal.Release(menuPtr);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.ReleaseComObject(com);
            }
        }

        private const uint FirstCommand = 1;
        private const uint LastCommand = 0x7FFF;
        private const uint WM_DRAWITEM = 0x002B;
        private const uint WM_MEASUREITEM = 0x002C;
        private const uint WM_INITMENUPOPUP = 0x0117;
        private const uint WM_MENUCHAR = 0x0120;
        private const uint GCS_VERBW = 0x00000004;

        private static IntPtr ForwardMenuMessage(IContextMenu2 menu2, IContextMenu3 menu3, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_INITMENUPOPUP:
                case WM_MENUCHAR:
                    break;
                case WM_DRAWITEM:
                case WM_MEASUREITEM:
                    if (wParam != IntPtr.Zero)
                    {
                        return IntPtr.Zero; // a control's item, not the menu's
                    }

                    break;
                default:
                    return IntPtr.Zero;
            }

            try
            {
                if (menu3 != null)
                {
                    if (menu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                }
                else if (menu2 != null && menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return msg == WM_INITMENUPOPUP ? IntPtr.Zero : new IntPtr(1);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "A shell extension failed to handle menu message {Message}", msg);
            }

            return IntPtr.Zero;
        }

        /// <summary>The canonical verb of a menu command ("rename", "delete"…), or null if it has none.</summary>
        private static string GetVerb(IContextMenu menu, uint commandOffset)
        {
            const int capacity = 256;
            IntPtr buffer = Marshal.AllocHGlobal(capacity * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                int hr = menu.GetCommandString(new UIntPtr(commandOffset), GCS_VERBW, IntPtr.Zero, buffer, capacity);
                return hr >= 0 ? Marshal.PtrToStringUni(buffer) : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        #region Native

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint CMIC_MASK_UNICODE = 0x00004000;
        private const uint CMIC_MASK_PTINVOKE = 0x20000000;
        private const int SW_SHOWNORMAL = 1;
        private const int VK_SHIFT = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
            public IntPtr lpTitle;
            public IntPtr lpVerbW;
            public IntPtr lpParametersW;
            public IntPtr lpDirectoryW;
            public IntPtr lpTitleW;
            public POINT ptInvoke;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        private interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
            void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            [PreserveSig]
            int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
                [In] ref Guid riid, IntPtr rgfReserved,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        }

        // COM interface inheritance: each derived interface redeclares the base methods first, in vtable order.
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F4-0000-0000-C000-000000000046")]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        #endregion
    }
}
