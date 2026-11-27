// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Cut/copy/paste of files through the system clipboard, on Windows, Linux and macOS.
    /// <para>
    /// Avalonia's clipboard is asynchronous and per window (TopLevel.Clipboard), where WPF's was a synchronous static.
    /// The view models still ask synchronous questions (CanExecute of Paste: "are there files?"), so this service keeps
    /// what it last knew: what this app put on the clipboard, and whether the system clipboard held files when last
    /// checked (<see cref="RefreshAsync"/>, run when the window is activated and after every cut/copy). Pasting always
    /// reads the system clipboard itself, so files copied in another file manager paste correctly.
    /// </para>
    /// <para>
    /// Formats written, so other file managers understand a copy or cut from here:
    /// <list type="bullet">
    /// <item>Files (storage items): Windows Explorer's CF_HDROP, Finder's file URLs.</item>
    /// <item>"Preferred DropEffect" (4 bytes, DROPEFFECT_MOVE for a cut): Explorer's cut marker, as the WPF version wrote.</item>
    /// <item>"x-special/gnome-copied-files" ("copy" or "cut", then file:// URIs): GNOME Files, Nemo, Caja, Thunar.</item>
    /// <item>"text/uri-list": KDE Dolphin and everything else on Linux.</item>
    /// <item>Plain text, one path per line, for pasting into a terminal or editor.</item>
    /// </list>
    /// Reading takes the same formats in the reverse order of specificity.
    /// </para>
    /// </summary>
    public static class ClipboardFileService
    {
        private static readonly DataFormat<byte[]> PreferredDropEffectFormat = DataFormat.CreateBytesPlatformFormat("Preferred DropEffect");
        private static readonly DataFormat<byte[]> GnomeCopiedFilesFormat = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
        private static readonly DataFormat<byte[]> UriListFormat = DataFormat.CreateBytesPlatformFormat("text/uri-list");

        /// <summary>
        /// The paths themselves, for ByteExplorer's own windows: the only way to carry items of a Windows phone
        /// ("phone://…"), which no other program can open, through the clipboard or a drag.
        /// </summary>
        internal static readonly DataFormat<string> AppPathsFormat = DataFormat.CreateStringApplicationFormat("ByteExplorer.Paths");
        private const int DropEffectCopy = 1;
        private const int DropEffectMove = 2;

        private static Func<IClipboard> _clipboardProvider;
        private static Func<IStorageProvider> _storageProvider;
        private static volatile bool _systemHasFiles;
        private static IReadOnlyList<string> _ownPaths = Array.Empty<string>();
        private static bool _ownIsCut;

        // The paths of our own pending cut, for the views to draw them dimmed (read at every repaint: a set, swapped whole).
        private static volatile HashSet<string> s_cutPaths = new HashSet<string>();

        /// <summary>The item was cut in ByteExplorer and not pasted yet: the views draw it dimmed, as Explorer does.</summary>
        public static bool IsCut(string path)
        {
            HashSet<string> cut = s_cutPaths;
            return cut.Count > 0 && path != null && cut.Contains(path);
        }
        private static string _ownMarkerText;

        /// <summary>Raised on the UI thread when <see cref="HasFiles"/> may have changed.</summary>
        public static event EventHandler FilesAvailabilityChanged;

        /// <summary>Called once by the main window: where the clipboard and the storage provider come from.</summary>
        public static void Initialize(Func<IClipboard> clipboardProvider, Func<IStorageProvider> storageProvider)
        {
            _clipboardProvider = clipboardProvider;
            _storageProvider = storageProvider;
        }

        public static void SetFilesForCopy(IReadOnlyList<string> paths) => SetFiles(paths, isCut: false);

        public static void SetFilesForCut(IReadOnlyList<string> paths) => SetFiles(paths, isCut: true);

        public static void SetText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            IClipboard clipboard = _clipboardProvider?.Invoke();
            if (clipboard == null)
            {
                return;
            }

            Post(async () =>
            {
                await clipboard.SetTextAsync(text);
                _ownPaths = Array.Empty<string>();
                _ownMarkerText = null;
                await RefreshAsync();
            }, "Failed to set clipboard text data");
        }

        /// <summary>
        /// Synchronous answer for CanExecute: files this app put on the clipboard and still there as far as it knows,
        /// or files another program put there when the clipboard was last checked.
        /// </summary>
        public static bool HasFiles() => _systemHasFiles || _ownPaths.Count > 0;

        /// <summary>Reads the files on the system clipboard and whether they were cut.</summary>
        public static async Task<(bool Ok, IReadOnlyList<string> Paths, bool IsCut)> TryGetFilesAsync()
        {
            IClipboard clipboard = _clipboardProvider?.Invoke();
            if (clipboard == null)
            {
                return _ownPaths.Count > 0 ? (true, _ownPaths, _ownIsCut) : (false, Array.Empty<string>(), false);
            }

            try
            {
                using IAsyncDataTransfer data = await clipboard.TryGetDataAsync();
                if (data == null)
                {
                    return (false, Array.Empty<string>(), false);
                }

                if (data.Contains(AppPathsFormat))
                {
                    List<string> own = SplitPaths(await data.TryGetValueAsync(AppPathsFormat));
                    if (own.Count > 0)
                    {
                        bool ownCut = _ownIsCut && _ownPaths.SequenceEqual(own, StringComparer.Ordinal);
                        if (data.Contains(PreferredDropEffectFormat))
                        {
                            byte[] effect = await data.TryGetValueAsync(PreferredDropEffectFormat);
                            ownCut = effect != null && effect.Length >= 4 && (BitConverter.ToInt32(effect, 0) & DropEffectMove) != 0;
                        }

                        return (true, own, ownCut);
                    }
                }

                if (data.Contains(GnomeCopiedFilesFormat) &&
                    TryParseGnomeCopiedFiles(DecodeText(await data.TryGetValueAsync(GnomeCopiedFilesFormat)), out List<string> gnomePaths, out bool gnomeCut))
                {
                    return (true, gnomePaths, gnomeCut);
                }

                var paths = new List<string>();
                if (data.Contains(DataFormat.File))
                {
                    IStorageItem[] items = await data.TryGetFilesAsync();
                    if (items != null)
                    {
                        foreach (IStorageItem item in items)
                        {
                            string local = item?.TryGetLocalPath();
                            if (!string.IsNullOrEmpty(local))
                            {
                                paths.Add(local);
                            }
                        }
                    }
                }

                if (paths.Count == 0 && data.Contains(UriListFormat))
                {
                    paths = ParseUriList(DecodeText(await data.TryGetValueAsync(UriListFormat)));
                }

                if (paths.Count > 0)
                {
                    bool isCut = false;
                    if (data.Contains(PreferredDropEffectFormat))
                    {
                        byte[] effect = await data.TryGetValueAsync(PreferredDropEffectFormat);
                        isCut = effect != null && effect.Length >= 4 && (BitConverter.ToInt32(effect, 0) & DropEffectMove) != 0;
                    }
                    else if (_ownPaths.Count > 0 && _ownPaths.SequenceEqual(paths, StringComparer.Ordinal))
                    {
                        isCut = _ownIsCut; // our own cut, on a platform that dropped the marker
                    }

                    return (true, paths, isCut);
                }

                // Some X11 setups keep only the plain-text part of what we wrote; recognize our own copy by its text.
                if (_ownPaths.Count > 0 && _ownMarkerText != null &&
                    string.Equals(await data.TryGetTextAsync(), _ownMarkerText, StringComparison.Ordinal))
                {
                    return (true, _ownPaths, _ownIsCut);
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Failed to read clipboard file data");
            }

            return (false, Array.Empty<string>(), false);
        }

        /// <summary>After a cut is pasted the clipboard is emptied, as Explorer does, so the files are not moved twice.</summary>
        public static void ClearFiles()
        {
            s_cutPaths = new HashSet<string>();
            _ownPaths = Array.Empty<string>();
            _ownMarkerText = null;
            _systemHasFiles = false;
            RaiseAvailabilityChanged();
            IClipboard clipboard = _clipboardProvider?.Invoke();
            if (clipboard != null)
            {
                Post(() => clipboard.ClearAsync(), "Failed to clear clipboard after Cut+Paste");
            }
        }

        /// <summary>
        /// After a cut was pasted: empties the clipboard only if it still holds that same cut. During a long move the
        /// person may have copied something else; that must survive the move finishing.
        /// </summary>
        public static async Task ClearCutIfUnchangedAsync(IReadOnlyList<string> pasted)
        {
            (bool ok, IReadOnlyList<string> current, bool isCut) = await TryGetFilesAsync();
            if (ok && isCut && current.Count == pasted.Count &&
                new HashSet<string>(current, ByteExplorer.Core.PathUtil.Comparer).SetEquals(pasted))
            {
                ClearFiles();
            }
        }

        /// <summary>Re-checks whether the system clipboard holds files (window activation, after our own writes).</summary>
        public static async Task RefreshAsync()
        {
            IClipboard clipboard = _clipboardProvider?.Invoke();
            if (clipboard == null)
            {
                return;
            }

            bool hasFiles;
            try
            {
                IReadOnlyList<DataFormat> formats = await clipboard.GetDataFormatsAsync();
                hasFiles = formats != null && formats.Any(f => f.Equals(DataFormat.File) || f.Equals(GnomeCopiedFilesFormat) || f.Equals(UriListFormat));
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Failed to query clipboard formats");
                return;
            }

            if (hasFiles != _systemHasFiles)
            {
                _systemHasFiles = hasFiles;
                RaiseAvailabilityChanged();
            }

            // Another program put something else on the clipboard: our cut is gone, its items are no longer dimmed.
            if (s_cutPaths.Count > 0)
            {
                (bool ok, IReadOnlyList<string> current, bool isCut) = await TryGetFilesAsync();
                if (!ok || !isCut || !new HashSet<string>(current, ByteExplorer.Core.PathUtil.Comparer).SetEquals(s_cutPaths))
                {
                    s_cutPaths = new HashSet<string>();
                    _ownPaths = Array.Empty<string>();
                    RaiseAvailabilityChanged();
                }
            }
        }

        private static void SetFiles(IReadOnlyList<string> paths, bool isCut)
        {
            if (paths == null || paths.Count == 0)
            {
                return;
            }

            var copy = paths.ToArray();
            _ownPaths = copy;
            _ownIsCut = isCut;
            s_cutPaths = isCut ? new HashSet<string>(copy, ByteExplorer.Core.PathUtil.Comparer) : new HashSet<string>();
            _ownMarkerText = string.Join(Environment.NewLine, copy);
            RaiseAvailabilityChanged();

            IClipboard clipboard = _clipboardProvider?.Invoke();
            if (clipboard == null)
            {
                return;
            }

            Post(async () =>
            {
                var transfer = await BuildTransferAsync(copy, isCut, _storageProvider?.Invoke());
                await clipboard.SetDataAsync(transfer);
                await RefreshAsync();
            }, "Failed to set clipboard file data");
        }

        /// <summary>
        /// The data for a file copy/cut or a file drag: one item per file carrying the storage item (the platform's
        /// native file list), and on the first item the cross-desktop formats described on the class.
        /// </summary>
        internal static async Task<DataTransfer> BuildTransferAsync(IReadOnlyList<string> paths, bool isCut, IStorageProvider storage)
        {
            var transfer = new DataTransfer();
            var first = new DataTransferItem();
            first.Set(AppPathsFormat, string.Join("\n", paths));
            first.Set(PreferredDropEffectFormat, BitConverter.GetBytes(isCut ? DropEffectMove : DropEffectCopy));
            first.SetText(string.Join(Environment.NewLine, paths));
            if (paths.Any(ByteExplorer.Core.Platform.Phones.PhonePaths.IsPhonePath))
            {
                // Items of a Windows phone have no file path another program could use: only ByteExplorer's own format.
                transfer.Add(first);
                return transfer;
            }

            string uris = string.Join("\r\n", paths.Select(ToFileUri));
            first.Set(UriListFormat, Encoding.UTF8.GetBytes(uris + "\r\n"));
            first.Set(GnomeCopiedFilesFormat, Encoding.UTF8.GetBytes((isCut ? "cut" : "copy") + "\n" + string.Join("\n", paths.Select(ToFileUri))));

            bool firstUsed = false;
            if (storage != null)
            {
                foreach (string path in paths)
                {
                    IStorageItem item = Directory.Exists(path)
                        ? await storage.TryGetFolderFromPathAsync(path)
                        : await storage.TryGetFileFromPathAsync(path);
                    if (item == null)
                    {
                        continue;
                    }

                    if (!firstUsed)
                    {
                        first.SetFile(item);
                        transfer.Add(first);
                        firstUsed = true;
                    }
                    else
                    {
                        transfer.Add(DataTransferItem.CreateFile(item));
                    }
                }
            }

            if (!firstUsed)
            {
                transfer.Add(first);
            }

            return transfer;
        }

        private static void Post(Func<Task> work, string failureMessage)
        {
            UiThread.Post(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, failureMessage);
                }
            });
        }

        private static void RaiseAvailabilityChanged()
        {
            UiThread.Post(() =>
            {
                FilesAvailabilityChanged?.Invoke(null, EventArgs.Empty);
                CommandManager.InvalidateRequerySuggested();
            });
        }

        private static string ToFileUri(string path) => new Uri(path).AbsoluteUri;

        private static bool TryParseGnomeCopiedFiles(string text, out List<string> paths, out bool isCut)
        {
            paths = new List<string>();
            isCut = false;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                return false;
            }

            isCut = string.Equals(lines[0].Trim(), "cut", StringComparison.OrdinalIgnoreCase);
            paths = ParseUriList(string.Join("\n", lines.Skip(1)));
            return paths.Count > 0;
        }

        /// <summary>RFC 2483 text/uri-list: one URI per line, '#' lines are comments; only file URIs are kept.</summary>
        private static List<string> ParseUriList(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            foreach (string raw in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().TrimEnd('\0');
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (Uri.TryCreate(line, UriKind.Absolute, out Uri uri) && uri.IsFile)
                {
                    result.Add(uri.LocalPath);
                }
                else if (Path.IsPathRooted(line))
                {
                    result.Add(line);
                }
            }

            return result;
        }

        internal static List<string> SplitPaths(string text) =>
            string.IsNullOrEmpty(text)
                ? new List<string>()
                : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private static string DecodeText(byte[] bytes) => bytes == null ? null : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
}
