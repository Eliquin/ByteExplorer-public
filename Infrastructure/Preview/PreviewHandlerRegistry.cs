// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace ByteExplorer.Infrastructure.Preview
{
    /// <summary>
    /// Read-only access to HKEY_CLASSES_ROOT (the merged per-user and per-machine view), as the preview handler lookup
    /// uses it. An interface so the lookup is tested with a dictionary on any OS; the registration's
    /// <see cref="DefaultApp.IRegistryStore"/> is HKEY_CURRENT_USER and writable, a different thing.
    /// </summary>
    public interface IClassesRootReader
    {
        /// <summary>A string value of a key under HKEY_CLASSES_ROOT (<paramref name="name"/> empty: the default value); null when missing.</summary>
        string GetValue(string key, string name);

        bool KeyExists(string key);
    }

    /// <summary>
    /// Finds the preview handler Windows uses for a file type: the CLSID under the
    /// <c>ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}</c> (IPreviewHandler) key, looked up where the shell looks for it,
    /// in this order: the extension's own key (<c>.pdf</c>), its ProgID (<c>.pdf</c>'s default value, e.g.
    /// <c>AcroExch.Document.DC</c>), <c>SystemFileAssociations\.pdf</c>, then the type's perceived type
    /// (<c>SystemFileAssociations\text</c>, <c>...\image</c>). A CLSID that is not registered (a handler uninstalled but its
    /// association left behind) is skipped, so the pane never tries to start something that is not there.
    /// <para>
    /// Not consulted: the user's "Open with" choice (FileExts\UserChoice). Explorer resolves handlers from the classes above as
    /// well; a type whose handler only exists under a UserChoice ProgID simply shows no document preview.
    /// </para>
    /// <para>Results are cached per extension (a few registry reads, but at every selection change otherwise).</para>
    /// </summary>
    public sealed class PreviewHandlerRegistry
    {
        /// <summary>IID_IPreviewHandler: the ShellEx subkey whose default value is the handler's CLSID.</summary>
        public const string PreviewHandlerShellEx = @"ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

        private static readonly Lazy<PreviewHandlerRegistry> SystemRegistry = new Lazy<PreviewHandlerRegistry>(CreateSystem);

        private readonly IClassesRootReader _reader;
        private readonly ConcurrentDictionary<string, Guid?> _cache = new ConcurrentDictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        public PreviewHandlerRegistry(IClassesRootReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>This computer's registry.</summary>
        [SupportedOSPlatform("windows")]
        public static PreviewHandlerRegistry System => SystemRegistry.Value;

        /// <summary>The preview handler's CLSID for an extension (".docx"), or null when the type has none.</summary>
        public Guid? FindHandler(string extension)
        {
            if (string.IsNullOrEmpty(extension) || extension[0] != '.' || extension.Length > 64 ||
                extension.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
            {
                return null;
            }

            return _cache.GetOrAdd(extension, Lookup);
        }

        public void ClearCache() => _cache.Clear();

        private Guid? Lookup(string extension)
        {
            try
            {
                if (HandlerUnder(extension) is Guid own)
                {
                    return own;
                }

                string progId = _reader.GetValue(extension, string.Empty);
                if (IsKeyName(progId) && HandlerUnder(progId) is Guid byProgId)
                {
                    return byProgId;
                }

                if (HandlerUnder(@"SystemFileAssociations\" + extension) is Guid bySystem)
                {
                    return bySystem;
                }

                string perceived = _reader.GetValue(extension, "PerceivedType");
                if (IsKeyName(perceived) && HandlerUnder(@"SystemFileAssociations\" + perceived) is Guid byPerceived)
                {
                    return byPerceived;
                }
            }
            catch (Exception ex)
            {
                // A registry read that fails (permissions, a policy) only means: no document preview for this type.
                Log.Logger.Debug(ex, "Could not look up the preview handler of {Extension}", extension);
            }

            return null;
        }

        /// <summary>The registered handler CLSID under <c>key\ShellEx\{IID_IPreviewHandler}</c>, or null.</summary>
        private Guid? HandlerUnder(string key)
        {
            string value = _reader.GetValue(key + @"\" + PreviewHandlerShellEx, string.Empty);
            if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out Guid clsid) || clsid == Guid.Empty)
            {
                return null;
            }

            return _reader.KeyExists(@"CLSID\" + clsid.ToString("B")) ? clsid : null;
        }

        private static bool IsKeyName(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length < 256 && value.IndexOfAny(new[] { '\\', '\0' }) < 0;

        private static PreviewHandlerRegistry CreateSystem() =>
            OperatingSystem.IsWindows()
                ? new PreviewHandlerRegistry(new WindowsClassesRoot())
                : throw new PlatformNotSupportedException("Preview handlers are a Windows feature.");

        /// <summary>HKEY_CLASSES_ROOT through Microsoft.Win32.Registry.</summary>
        [SupportedOSPlatform("windows")]
        private sealed class WindowsClassesRoot : IClassesRootReader
        {
            public string GetValue(string key, string name)
            {
                using RegistryKey opened = Registry.ClassesRoot.OpenSubKey(key);
                return opened?.GetValue(name ?? string.Empty) as string;
            }

            public bool KeyExists(string key)
            {
                using RegistryKey opened = Registry.ClassesRoot.OpenSubKey(key);
                return opened != null;
            }
        }
    }
}
