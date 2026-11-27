// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>
    /// One document shown by a Windows preview handler: creates the handler, initializes it with the file, gives it the
    /// host window and starts the preview; resizes it; unloads and releases it. Always on the UI thread (an STA thread, as
    /// COM wants for these objects).
    /// <para>
    /// Crash safety. The handler is created out of process first (CLSCTX_LOCAL_SERVER): every handler registered the
    /// standard way names prevhost.exe as its surrogate (its AppID), the same isolation File Explorer uses, so a handler
    /// that crashes takes prevhost.exe down, not ByteExplorer; the next selection starts a new one. Only a class that cannot
    /// be activated out of process at all is loaded in process (as File Explorer does for handlers that opt out of
    /// isolation); a native crash in such a handler is the one case no managed code can catch.
    /// </para>
    /// <para>
    /// Hang safety. A handler that never returns would freeze the window, since these calls are synchronous on the UI
    /// thread. Every call into it runs with COM call cancellation enabled and a watchdog that cancels the pending call
    /// (CoCancelCall) after <see cref="CallTimeout"/>; the call then fails with RPC_E_CALL_CANCELED, the session is dropped
    /// and the pane shows "no preview". (Cancellation only applies to out-of-process calls, another reason to prefer them.)
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class PreviewHandlerSession : IDisposable
    {
        /// <summary>How long one call into a handler (initialize, show, resize, unload) may take before it is cancelled.</summary>
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

        private object _handlerObject;
        private IPreviewHandler _handler;
        private ManagedIStream _stream;

        private PreviewHandlerSession()
        {
        }

        public string Path { get; private set; }

        public bool IsOutOfProcess { get; private set; }

        /// <summary>
        /// Shows <paramref name="path"/> with the handler <paramref name="clsid"/> in <paramref name="hwnd"/> (which fills
        /// <paramref name="rect"/>). Returns null when it can't (the error is logged); never throws.
        /// </summary>
        public static PreviewHandlerSession TryOpen(Guid clsid, string path, IntPtr hwnd, PreviewHandlerNative.RECT rect,
            uint? backgroundColor, uint? textColor)
        {
            var session = new PreviewHandlerSession { Path = path };
            try
            {
                session.Create(clsid);
                session.Initialize(path);
                session.ApplyVisuals(backgroundColor, textColor);
                Guarded(() =>
                {
                    session._handler.SetWindow(hwnd, ref rect);
                    session._handler.DoPreview();
                });
                return session;
            }
            catch (Exception ex)
            {
                Log.Logger.Information(ex, "The preview handler {Clsid} could not show {Path}", clsid, path);
                session.Dispose();
                return null;
            }
        }

        /// <summary>The host window was resized. False when the handler failed (it is then dropped by the caller).</summary>
        public bool SetRect(PreviewHandlerNative.RECT rect)
        {
            if (_handler == null)
            {
                return false;
            }

            try
            {
                Guarded(() => _handler.SetRect(ref rect));
                return true;
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "The preview handler failed to resize for {Path}", Path);
                return false;
            }
        }

        /// <summary>Gives the handler the keyboard focus (the pane was clicked or tabbed into).</summary>
        public void Focus()
        {
            if (_handler == null)
            {
                return;
            }

            try
            {
                Guarded(() => _handler.SetFocus());
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "The preview handler did not take the focus");
            }
        }

        /// <summary>Unloads the document and releases the handler (and the file) at once, not at some later GC.</summary>
        public void Dispose()
        {
            IPreviewHandler handler = _handler;
            object handlerObject = _handlerObject;
            _handler = null;
            _handlerObject = null;
            if (handler != null)
            {
                try
                {
                    Guarded(handler.Unload);
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "The preview handler failed to unload {Path}", Path);
                }
            }

            if (handlerObject != null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(handlerObject);
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "Releasing the preview handler failed");
                }
            }

            _stream?.Dispose();
            _stream = null;
        }

        private void Create(Guid clsid)
        {
            Guid iid = PreviewHandlerNative.IidIUnknown;
            int hr = PreviewHandlerNative.CoCreateInstance(ref clsid, IntPtr.Zero, PreviewHandlerNative.ClsctxLocalServer, ref iid, out object instance);
            IsOutOfProcess = hr >= 0;
            if (hr < 0)
            {
                Log.Logger.Debug("Preview handler {Clsid} can't run out of process (0x{Hr:X8}); loading it in process", clsid, hr);
                hr = PreviewHandlerNative.CoCreateInstance(ref clsid, IntPtr.Zero, PreviewHandlerNative.ClsctxInprocServer, ref iid, out instance);
            }

            if (hr < 0 || instance == null)
            {
                throw new COMException("The preview handler could not be created.", hr);
            }

            _handlerObject = instance;
            _handler = instance as IPreviewHandler ?? throw new InvalidCastException("The object is not a preview handler.");
        }

        /// <summary>A stream first (the recommended way), then a path, then a shell item: whichever the handler supports.</summary>
        private void Initialize(string path)
        {
            if (_handlerObject is IInitializeWithStream withStream)
            {
                _stream = new ManagedIStream(path);
                ManagedIStream stream = _stream;
                Guarded(() => withStream.Initialize(stream, PreviewHandlerNative.StgmRead));
                return;
            }

            if (_handlerObject is IInitializeWithFile withFile)
            {
                Guarded(() => withFile.Initialize(path, PreviewHandlerNative.StgmRead));
                return;
            }

            if (_handlerObject is IInitializeWithItem withItem)
            {
                Guid iid = PreviewHandlerNative.IidIShellItem;
                int hr = PreviewHandlerNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object item);
                if (hr < 0 || item == null)
                {
                    throw new COMException("No shell item for the file.", hr);
                }

                try
                {
                    Guarded(() => withItem.Initialize(item, PreviewHandlerNative.StgmRead));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }

                return;
            }

            throw new NotSupportedException("The preview handler supports no way of being given a file.");
        }

        /// <summary>The theme's colors, for handlers that draw their own background and text (optional for handlers).</summary>
        private void ApplyVisuals(uint? background, uint? text)
        {
            if (_handlerObject is not IPreviewHandlerVisuals visuals)
            {
                return;
            }

            try
            {
                Guarded(() =>
                {
                    if (background is uint b)
                    {
                        visuals.SetBackgroundColor(b);
                    }

                    if (text is uint t)
                    {
                        visuals.SetTextColor(t);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "The preview handler refused the theme colors");
            }
        }

        /// <summary>
        /// Runs one call into the handler under a watchdog: if it has not returned after <see cref="CallTimeout"/>, the
        /// pending COM call of this thread is cancelled, so it returns with RPC_E_CALL_CANCELED instead of hanging forever.
        /// </summary>
        private static void Guarded(Action call)
        {
            uint threadId = PreviewHandlerNative.GetCurrentThreadId();
            bool enabled = PreviewHandlerNative.CoEnableCallCancellation(IntPtr.Zero) >= 0;
            int finished = 0;
            using var watchdog = new Timer(_ =>
            {
                if (Volatile.Read(ref finished) == 0)
                {
                    Log.Logger.Warning("A preview handler call took longer than {Timeout}; cancelling it", CallTimeout);
                    PreviewHandlerNative.CoCancelCall(threadId, 0);
                }
            }, null, enabled ? CallTimeout : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                call();
            }
            finally
            {
                Volatile.Write(ref finished, 1);
                if (enabled)
                {
                    PreviewHandlerNative.CoDisableCallCancellation(IntPtr.Zero);
                }
            }
        }
    }
}
