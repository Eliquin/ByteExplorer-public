// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>The Win32 and COM declarations the preview handler host needs (shobjidl.h, propsys.h, winuser.h).</summary>
    [SupportedOSPlatform("windows")]
    internal static class PreviewHandlerNative
    {
        public const uint ClsctxInprocServer = 0x1;
        public const uint ClsctxLocalServer = 0x4;
        public const uint StgmRead = 0x0;
        public const int RpcECallCanceled = unchecked((int)0x80010002);

        public const int WsChild = 0x40000000;
        public const int WsVisible = 0x10000000;
        public const int WsClipChildren = 0x02000000;
        public const int WsClipSiblings = 0x04000000;

        public static readonly Guid IidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        public static readonly Guid IidIShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public RECT(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("ole32.dll")]
        public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [DllImport("ole32.dll")]
        public static extern int CoEnableCallCancellation(IntPtr reserved);

        [DllImport("ole32.dll")]
        public static extern int CoDisableCallCancellation(IntPtr reserved);

        [DllImport("ole32.dll")]
        public static extern int CoCancelCall(uint threadId, uint timeoutMs);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object item);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hwnd);
    }

    /// <summary>IPreviewHandler (shobjidl.h): what File Explorer's preview pane drives.</summary>
    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref PreviewHandlerNative.RECT rect);

        void SetRect(ref PreviewHandlerNative.RECT rect);

        void DoPreview();

        void Unload();

        void SetFocus();

        void QueryFocus(out IntPtr hwnd);

        [PreserveSig]
        int TranslateAccelerator(ref PreviewHandlerNative.MSG message);
    }

    /// <summary>IPreviewHandlerVisuals: lets the host pass its background and text colors (dark theme).</summary>
    [ComImport]
    [Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandlerVisuals
    {
        void SetBackgroundColor(uint colorRef);

        void SetFont(IntPtr logFont);

        void SetTextColor(uint colorRef);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport]
    [Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithItem
    {
        void Initialize([MarshalAs(UnmanagedType.IUnknown)] object shellItem, uint mode);
    }

    /// <summary>
    /// A read-only COM IStream over a <see cref="FileStream"/>, for handlers initialized with IInitializeWithStream (the
    /// kind Microsoft recommends, used by most modern handlers, e.g. the PDF one). The handler may call it from its own
    /// threads (and, out of process, through RPC), so every operation takes a lock. Writing is refused
    /// (STG_E_ACCESSDENIED): a preview never changes the file.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [ComVisible(true)]
    internal sealed class ManagedIStream : IStream, IDisposable
    {
        private const int StgEAccessDenied = unchecked((int)0x80030005);
        private const int StgEInvalidFunction = unchecked((int)0x80030001);
        private const int StgEReverted = unchecked((int)0x80030102);

        private readonly object _gate = new object();
        private readonly string _name;
        private FileStream _stream;

        public ManagedIStream(string path)
        {
            _name = Path.GetFileName(path);
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
        }

        private FileStream Stream => _stream ?? throw new COMException("The preview was closed.", StgEReverted);

        public void Read(byte[] buffer, int count, IntPtr bytesRead)
        {
            lock (_gate)
            {
                int total = 0;
                count = Math.Min(count, buffer?.Length ?? 0);
                while (total < count)
                {
                    int read = Stream.Read(buffer, total, count - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                if (bytesRead != IntPtr.Zero)
                {
                    Marshal.WriteInt32(bytesRead, total);
                }
            }
        }

        public void Write(byte[] buffer, int count, IntPtr bytesWritten) => throw new COMException("Read-only stream.", StgEAccessDenied);

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            lock (_gate)
            {
                if (origin < 0 || origin > 2)
                {
                    throw new COMException("Invalid seek origin.", StgEInvalidFunction);
                }

                long position = Stream.Seek(offset, (SeekOrigin)origin); // STREAM_SEEK_SET/CUR/END match SeekOrigin
                if (newPosition != IntPtr.Zero)
                {
                    Marshal.WriteInt64(newPosition, position);
                }
            }
        }

        public void SetSize(long newSize) => throw new COMException("Read-only stream.", StgEAccessDenied);

        public void CopyTo(IStream target, long count, IntPtr bytesRead, IntPtr bytesWritten)
        {
            byte[] buffer = new byte[65536];
            long done = 0;
            while (done < count)
            {
                int read;
                lock (_gate)
                {
                    read = Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count - done));
                }

                if (read == 0)
                {
                    break;
                }

                target.Write(read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray(), read, IntPtr.Zero);
                done += read;
            }

            if (bytesRead != IntPtr.Zero)
            {
                Marshal.WriteInt64(bytesRead, done);
            }

            if (bytesWritten != IntPtr.Zero)
            {
                Marshal.WriteInt64(bytesWritten, done);
            }
        }

        public void Commit(int flags)
        {
        }

        public void Revert()
        {
        }

        public void LockRegion(long offset, long count, int lockType) => throw new COMException("Locking is not supported.", StgEInvalidFunction);

        public void UnlockRegion(long offset, long count, int lockType) => throw new COMException("Locking is not supported.", StgEInvalidFunction);

        public void Stat(out STATSTG stat, int flags)
        {
            lock (_gate)
            {
                stat = new STATSTG
                {
                    type = 2, // STGTY_STREAM
                    cbSize = Stream.Length,
                    grfMode = 0, // STGM_READ
                    pwcsName = (flags & 1) != 0 ? null : _name, // STATFLAG_NONAME
                };
            }
        }

        public void Clone(out IStream clone) => throw new COMException("Cloning is not supported.", StgEInvalidFunction);

        public void Dispose()
        {
            lock (_gate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
