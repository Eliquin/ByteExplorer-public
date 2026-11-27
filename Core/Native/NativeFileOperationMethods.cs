// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.InteropServices;

namespace ByteExplorer.Core.Native
{
    /// <summary>
    /// Raw Win32 declarations for CopyFileEx/MoveFileEx. Kept private
    /// to Core.Native, same convention as NativeMethods.cs — nothing
    /// outside NativeFileOperations should call these directly.
    /// </summary>
    internal static class NativeFileOperationMethods
    {
        internal const uint MOVEFILE_WRITE_THROUGH = 0x00000008;
        internal const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
        internal const uint COPY_FILE_NO_BUFFERING = 0x00001000;
        internal const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
        internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_NOT_SAME_DEVICE = 17;
        internal const int ERROR_PRIVILEGE_NOT_HELD = 1314;
        internal const int ERROR_SHARING_VIOLATION = 32;
        internal const int ERROR_LOCK_VIOLATION = 33;
        internal const int ERROR_BAD_NETPATH = 53;
        internal const int ERROR_NETWORK_BUSY = 54;
        internal const int ERROR_NETNAME_DELETED = 64;
        internal const int ERROR_SEM_TIMEOUT = 121;
        internal const int ERROR_REQUEST_ABORTED = 1235;
        internal const int ERROR_CONNECTION_ABORTED = 1236;
        internal const int ERROR_CONNECTION_RESET = 1237;

        internal enum CopyProgressResult : uint
        {
            Continue = 0,
            Cancel = 1,
            Stop = 2,
            Quiet = 3
        }

        /// <summary>
        /// Matches the native LPPROGRESS_ROUTINE signature exactly —
        /// this is a callback invoked BY the OS, on the same thread
        /// that called CopyFileEx, so it's safe to touch managed state
        /// captured by closure without any extra marshaling machinery.
        /// </summary>
        internal delegate CopyProgressResult CopyProgressRoutine(
            long totalFileSize,
            long totalBytesTransferred,
            long streamSize,
            long streamBytesTransferred,
            uint streamNumber,
            uint callbackReason,
            IntPtr sourceFile,
            IntPtr destinationFile,
            IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CopyFileEx(
            string existingFileName,
            string newFileName,
            CopyProgressRoutine progressRoutine,
            IntPtr data,
            IntPtr cancel,
            uint copyFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool DeleteFile(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool RemoveDirectory(string pathName);

        internal const uint FO_DELETE = 0x0003;
        internal const ushort FOF_SILENT = 0x0004;
        internal const ushort FOF_NOCONFIRMATION = 0x0010;
        internal const ushort FOF_ALLOWUNDO = 0x0040;
        internal const ushort FOF_NOERRORUI = 0x0400;
        internal const ushort FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>SHFILEOPSTRUCTW for 64-bit processes (natural alignment).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        /// <summary>SHFILEOPSTRUCTW for 32-bit processes: shellapi.h packs it to 1 byte there.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        internal struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        internal static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        internal static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFileAttributes(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetFileAttributes(string fileName, uint fileAttributes);
    }
}
