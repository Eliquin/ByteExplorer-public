// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using LibVLCSharp.Shared;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// A native window LibVLC draws video into. Replaces LibVLCSharp.Avalonia's VideoView (same API: construct it, set
    /// <see cref="MediaPlayer"/>), so the app needs only the LibVLCSharp core package.
    /// <para>
    /// LibVLC renders into a platform window it is given: an HWND on Windows, an X11 window id on Linux, an NSView on
    /// macOS. Avalonia's <see cref="NativeControlHost"/> creates that child window; this control passes its handle to the
    /// player whenever either side changes (a new player, or the native window recreated because the control moved to
    /// another window such as full screen), and takes it back before the window is destroyed, so LibVLC never draws into
    /// a dead window. Like any native child window, nothing Avalonia draws can appear on top of it.
    /// </para>
    /// </summary>
    public sealed class VideoView : NativeControlHost
    {
        private MediaPlayer _mediaPlayer;
        private IPlatformHandle _handle;

        /// <summary>The player drawing into this view; null to detach.</summary>
        public MediaPlayer MediaPlayer
        {
            get => _mediaPlayer;
            set
            {
                if (ReferenceEquals(_mediaPlayer, value))
                {
                    return;
                }

                Detach(_mediaPlayer);
                _mediaPlayer = value;
                Attach();
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _handle = base.CreateNativeControlCore(parent);
            Attach();
            return _handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            Detach(_mediaPlayer);
            _handle = null;
            base.DestroyNativeControlCore(control);
        }

        private void Attach()
        {
            MediaPlayer player = _mediaPlayer;
            IPlatformHandle handle = _handle;
            if (player == null || handle == null || handle.Handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    player.Hwnd = handle.Handle;
                }
                else if (OperatingSystem.IsMacOS())
                {
                    player.NsObject = handle.Handle;
                }
                else
                {
                    player.XWindow = (uint)handle.Handle.ToInt64();
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not give the video window to the media player");
            }
        }

        private static void Detach(MediaPlayer player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    player.Hwnd = IntPtr.Zero;
                }
                else if (OperatingSystem.IsMacOS())
                {
                    player.NsObject = IntPtr.Zero;
                }
                else
                {
                    player.XWindow = 0;
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The player is already being torn down; nothing to take back.
            }
        }
    }
}
