// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Serilog;

namespace ByteExplorer.Core.Platform.Phones
{
    /// <summary>
    /// Where the app asks about phones: which paths are on one, which provider serves a path, and which phones are
    /// connected. Windows phones (MTP, through Windows Portable Devices) live under "phone://" paths
    /// (<see cref="PhonePaths"/>); Linux phones are folders the desktop mounted (<see cref="MountedPhoneProvider"/>).
    /// macOS has no phone support (it has no MTP of its own).
    /// </summary>
    public static class Phones
    {
        private static readonly MountedPhoneProvider Mounted = new MountedPhoneProvider();
        private static readonly object Gate = new object();
        private static IPhoneProvider _portable;
        private static bool _portableTried;
        private static IPhoneProvider _forTests;

        /// <summary>
        /// Whether real phones are used at all (Folder options › "Show phones and cameras"; the window's view model attaches
        /// the setting). Off: no device is asked anything, and none is listed. A provider set by tests ignores it.
        /// </summary>
        public static Func<bool> IsEnabled { get; set; } = () => true;

        /// <summary>Whether phones are listed now (the setting, or a provider set by tests).</summary>
        public static bool ShowsPhones => _forTests != null || Enabled;

        private static bool Enabled
        {
            get
            {
                try
                {
                    return IsEnabled?.Invoke() ?? true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        /// <summary>The provider of "phone://" paths: Windows Portable Devices on Windows, none elsewhere (tests set one).</summary>
        public static IPhoneProvider PortableDevices
        {
            get
            {
                if (_forTests != null)
                {
                    return _forTests;
                }

                if (!OperatingSystem.IsWindows() || (!_portableTried && !Enabled))
                {
                    return null; // turned off before it was ever made: Windows Portable Devices aren't even loaded
                }

                lock (Gate)
                {
                    if (!_portableTried)
                    {
                        _portableTried = true; // missing Windows Portable Devices are reported once, not at every poll
                        // Created on a multithreaded-apartment thread, never the UI thread (single-threaded apartment):
                        // COM objects made on the UI thread would have every later call from the workers marshalled
                        // back onto it, freezing the window while a phone answers.
                        _portable = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
                            ? System.Threading.Tasks.Task.Run(CreateWindowsProvider).GetAwaiter().GetResult()
                            : CreateWindowsProvider();
                    }

                    return _portable;
                }
            }
        }

        private static IPhoneProvider CreateWindowsProvider()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return new Wpd.WpdPhoneProvider();
            }
            catch (Exception ex)
            {
                // No Windows Portable Devices (Windows N editions without the Media Feature Pack, a stripped image).
                Log.Logger.Warning(ex, "Windows Portable Devices are unavailable; phones won't be shown");
                return null;
            }
        }

        /// <summary>Cheap, no I/O for ordinary paths: is the path on a phone?</summary>
        public static bool IsPhonePath(string path) =>
            PhonePaths.IsPhonePath(path) || MountedPhoneProvider.IsMountedPhonePath(path);

        /// <summary>
        /// Whether two paths are on the same phone (a drag between them moves by default, as within a drive). False when
        /// either isn't on a phone.
        /// </summary>
        public static bool AreOnSamePhone(string a, string b)
        {
            if (PhonePaths.IsPhonePath(a) && PhonePaths.IsPhonePath(b))
            {
                return string.Equals(PhonePaths.DeviceName(a), PhonePaths.DeviceName(b), PhonePaths.Comparison);
            }

            if (MountedPhoneProvider.IsMountedPhonePath(a) && MountedPhoneProvider.IsMountedPhonePath(b))
            {
                string rootA = MountedPhoneProvider.DeviceRootOf(a);
                return rootA != null && string.Equals(rootA, MountedPhoneProvider.DeviceRootOf(b), StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>The provider for a phone path, or null (not a phone path, or phones unavailable).</summary>
        public static IPhoneProvider For(string path)
        {
            if (PhonePaths.IsPhonePath(path))
            {
                return PortableDevices;
            }

            return MountedPhoneProvider.IsMountedPhonePath(path) ? Mounted : null;
        }

        /// <summary>For a phone path, the provider or an IOException saying phones can't be reached.</summary>
        public static IPhoneProvider Require(string path) =>
            For(path) ?? throw new IOException("Phones can't be reached on this computer.");

        /// <summary>Every connected phone. Blocks (asks each phone): call off the UI thread. Never throws.</summary>
        public static IReadOnlyList<PhoneDevice> GetDevices()
        {
            var devices = new List<PhoneDevice>();
            if (_forTests == null && !Enabled)
            {
                return devices;
            }

            try
            {
                IPhoneProvider portable = PortableDevices;
                if (portable != null)
                {
                    devices.AddRange(portable.GetDevices());
                }

                if (OperatingSystem.IsLinux() && _forTests == null)
                {
                    devices.AddRange(Mounted.GetDevices());
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Listing phones failed");
            }

            return devices;
        }

        /// <summary>Uses a provider for "phone://" paths (and no real phones) until disposed. Tests only.</summary>
        internal static IDisposable UseProviderForTests(IPhoneProvider provider)
        {
            _forTests = provider;
            return new Restore();
        }

        private sealed class Restore : IDisposable
        {
            public void Dispose() => _forTests = null;
        }
    }

    /// <summary>Messages for what goes wrong with phones, in the person's terms.</summary>
    public static class PhoneErrors
    {
        public const string LockedHint = "Unlock the phone and check that its USB setting is \"File transfer\".";

        public static string ListingMessage(Exception ex) => ex is UnauthorizedAccessException
            ? "The phone doesn't allow access. " + LockedHint
            : "Couldn't read this folder of the phone. " + LockedHint + "\n\n" + ex.Message;

        /// <summary>The phone was unplugged, or stopped answering.</summary>
        public static IOException Disconnected(string name, Exception inner = null) =>
            new IOException($"\"{name}\" isn't connected any more, or stopped answering. Plug it in again and unlock it.", inner);
    }
}
