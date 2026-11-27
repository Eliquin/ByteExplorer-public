// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ByteExplorer.Infrastructure.Preview;
using Serilog;

namespace ByteExplorer.Views
{
    /// <summary>
    /// Hosts a Windows preview handler (Office, PDF, fonts... whatever File Explorer's preview pane shows) in the details
    /// pane: a native child window (<see cref="NativeControlHost"/>) whose HWND the handler draws into. Created by
    /// <see cref="FilePreviewPanel"/> on Windows only, the first time a document is previewed (as the VideoView is), so
    /// other systems and the headless tests never create a native window.
    /// <para>
    /// The child window is a plain "Static" control (no window class to register); the handler creates its own window
    /// inside it and is told its area in physical pixels (the control's size times the render scaling, which is what
    /// Avalonia sizes the native window to). A size change is passed on at Background priority, after the layout pass.
    /// </para>
    /// <para>
    /// The handler is unloaded and released as soon as the selection moves on (<see cref="Clear"/>), when the native window
    /// is destroyed (the panel leaves the window) and when it fails; a failure is reported through <see cref="Failed"/>,
    /// and the panel then shows "no preview". Like every native child window, nothing Avalonia draws (menus excepted, they
    /// are windows themselves) can cover it.
    /// </para>
    /// </summary>
    public sealed class PreviewHandlerHost : NativeControlHost
    {
        private IntPtr _hwnd;
        private string _path;
        private Guid _handler;
        private int _request;
        private PreviewHandlerSession _session;
        private bool _resizePosted;

        public PreviewHandlerHost()
        {
            Focusable = true;
        }

        /// <summary>The handler could not show this file (raised on the UI thread with the file's path).</summary>
        public event EventHandler<string> Failed;

        /// <summary>Shows <paramref name="path"/> with the preview handler <paramref name="handler"/> (replacing what was shown).</summary>
        public void Show(string path, Guid handler)
        {
            if (string.Equals(path, _path, StringComparison.Ordinal) && handler == _handler && HasSession)
            {
                return;
            }

            CloseSession();
            _path = path;
            _handler = handler;
            int request = ++_request;

            // After this layout pass: the native window exists and has its size by then.
            Dispatcher.UIThread.Post(() =>
            {
                if (request == _request)
                {
                    Load();
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>Unloads the document (the selection moved on, or the pane was hidden).</summary>
        public void Clear()
        {
            _request++;
            _path = null;
            CloseSession();
        }

        private bool HasSession => OperatingSystem.IsWindows() && _session != null;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (!OperatingSystem.IsWindows() || parent == null)
            {
                return base.CreateNativeControlCore(parent);
            }

            const int style = PreviewHandlerNative.WsChild | PreviewHandlerNative.WsVisible |
                              PreviewHandlerNative.WsClipChildren | PreviewHandlerNative.WsClipSiblings;
            _hwnd = PreviewHandlerNative.CreateWindowExW(0, "Static", string.Empty, style, 0, 0, 1, 1,
                parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Logger.Warning("Could not create the document preview window (error {Error})", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return base.CreateNativeControlCore(parent);
            }

            if (_path != null)
            {
                int request = _request;
                Dispatcher.UIThread.Post(() =>
                {
                    if (request == _request)
                    {
                        Load();
                    }
                }, DispatcherPriority.Background);
            }

            return new PlatformHandle(_hwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (OperatingSystem.IsWindows() && control != null && control.Handle == _hwnd && _hwnd != IntPtr.Zero)
            {
                CloseSession();
                PreviewHandlerNative.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return;
            }

            base.DestroyNativeControlCore(control);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty && HasSession && !_resizePosted)
            {
                _resizePosted = true;
                Dispatcher.UIThread.Post(Resize, DispatcherPriority.Background);
            }
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);
            if (OperatingSystem.IsWindows())
            {
                _session?.Focus();
            }
        }

        private void Load()
        {
            if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero || _path == null || HasSession)
            {
                return;
            }

            string path = _path;
            PreviewHandlerNative.RECT rect = ClientRect();
            PreviewHandlerSession session = PreviewHandlerSession.TryOpen(_handler, path, _hwnd, rect,
                ThemeColor("ContentBrush"), ThemeColor("TextPrimary"));
            if (session == null)
            {
                Failed?.Invoke(this, path);
                return;
            }

            if (!string.Equals(path, _path, StringComparison.Ordinal) || _hwnd == IntPtr.Zero)
            {
                session.Dispose(); // replaced or torn down while the handler was starting
                return;
            }

            _session = session;
            Resize(); // the window may have changed size while the handler loaded
        }

        private void Resize()
        {
            _resizePosted = false;
            if (!OperatingSystem.IsWindows() || _session == null || _hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!_session.SetRect(ClientRect()))
            {
                string path = _session.Path;
                CloseSession();
                Failed?.Invoke(this, path);
            }
        }

        /// <summary>
        /// The area the handler fills, in physical pixels: the control's size times the screen scaling (what Avalonia sizes
        /// the native window to), so it is right even before Avalonia has moved the native window in this layout pass; the
        /// window's own client rectangle when the control has no size yet.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private PreviewHandlerNative.RECT ClientRect()
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int width = (int)Math.Ceiling(Bounds.Width * scale);
            int height = (int)Math.Ceiling(Bounds.Height * scale);
            if (width > 0 && height > 0)
            {
                return new PreviewHandlerNative.RECT(0, 0, width, height);
            }

            PreviewHandlerNative.GetClientRect(_hwnd, out PreviewHandlerNative.RECT rect);
            return rect;
        }

        private void CloseSession()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            PreviewHandlerSession session = _session;
            _session = null;
            session?.Dispose();
        }

        /// <summary>A theme color as a Win32 COLORREF (0x00BBGGRR), or null when the resource is not a solid color.</summary>
        private uint? ThemeColor(string key)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out object value) && value is ISolidColorBrush brush)
            {
                Color c = brush.Color;
                return (uint)(c.R | (c.G << 8) | (c.B << 16));
            }

            return null;
        }
    }
}
