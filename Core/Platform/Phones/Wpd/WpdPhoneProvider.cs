// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ByteExplorer.Core.Models;
using Serilog;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace ByteExplorer.Core.Platform.Phones.Wpd
{
    /// <summary>
    /// Phones on Windows through Windows Portable Devices, the API Explorer uses for "This PC &gt; Galaxy S21". A phone
    /// is a tree of objects with ids, not paths: the device object ("DEVICE"), its storages, then folders and files.
    /// This maps "phone://Name/Storage/Folder/file" paths onto that tree, walking it by name and remembering the ids it
    /// has seen.
    /// <para>
    /// Devices that Windows also shows as a drive letter (USB sticks and card readers, which Windows Portable Devices
    /// lists too, protocol "MSC") are left out: they are already drives. Calls run on the caller's thread (never the UI
    /// thread: the app calls these from workers); the device objects are created through the free-threaded class so
    /// any worker can use them.
    /// </para>
    /// <para>
    /// MTP lets two items in one folder share a name; the second is listed as "name (2)" so every item has a path.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WpdPhoneProvider : IPhoneProvider
    {
        private readonly object _gate = new object();
        private readonly IPortableDeviceManager _manager;
        private readonly Dictionary<string, WpdDevice> _byName = new Dictionary<string, WpdDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _nameByDeviceId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _notPhones = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Devices whose connection was written to the log (once per connection, not at every poll).</summary>
        private readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);

        public WpdPhoneProvider()
        {
            // Throws when Windows Portable Devices are missing (Windows N without the Media Feature Pack). Made on the
            // portable-devices thread, like every object of the API (see WpdThread).
            _manager = WpdThread.Run(() => Wpd.Create<IPortableDeviceManager>(Wpd.ClsidPortableDeviceManager));
        }

        // Every entry point runs on the portable-devices thread, one at a time (see WpdThread).
        public IReadOnlyList<PhoneDevice> GetDevices() => WpdThread.Run(GetDevicesCore);

        public DirectoryIndex List(string folderPath, CancellationToken cancellationToken) => WpdThread.Run(() => ListCore(folderPath, cancellationToken));

        public PhoneItemInfo GetInfo(string path) => WpdThread.Run(() => GetInfoCore(path));

        public Stream OpenRead(string path) => WpdThread.Run(() => OpenReadCore(path));

        public PhoneWriteStream CreateFile(string folderPath, string name, long size, DateTime modifiedUtc) =>
            WpdThread.Run(() => CreateFileCore(folderPath, name, size, modifiedUtc));

        public string CreateFolder(string parentPath, string name) => WpdThread.Run(() => CreateFolderCore(parentPath, name));

        public void Delete(string path) => WpdThread.Run(() => DeleteCore(path));

        public string Rename(string path, string newName) => WpdThread.Run(() => RenameCore(path, newName));

        private IReadOnlyList<PhoneDevice> GetDevicesCore()
        {
            var result = new List<PhoneDevice>();
            string[] ids;
            try
            {
                lock (_gate)
                {
                    _manager.RefreshDeviceList();
                    ids = GetDeviceIds();
                }
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is InvalidComObjectException)
            {
                Log.Logger.Warning(ex, "Listing portable devices failed");
                return result;
            }

            ForgetUnplugged(ids);
            foreach (string id in ids)
            {
                WpdDevice device = OpenDevice(id);
                if (device == null)
                {
                    continue;
                }

                try
                {
                    // The storages as read at most StorageCacheSeconds ago: the drive list asks every few seconds, and
                    // each fresh read is several requests to the phone.
                    result.Add(new PhoneDevice(device.Name, PhonePaths.Build(device.Name), device.Description, device.ListStorages(allowCached: true)));
                }
                catch (Exception ex) when (IsDeviceError(ex))
                {
                    // Locked or busy: shown without storages; opening it says what to do.
                    Log.Logger.Information(ex, "Couldn't read the storages of {Device}", device.Name);
                    result.Add(new PhoneDevice(device.Name, PhonePaths.Build(device.Name), device.Description, Array.Empty<PhoneStorage>()));
                }
            }

            return result;
        }

        private DirectoryIndex ListCore(string folderPath, CancellationToken cancellationToken)
        {
            string path = PhonePaths.Normalize(folderPath);
            string[] segments = PhonePaths.Segments(path);
            WpdDevice device = segments.Length == 0 ? null : Find(segments[0]);
            if (device == null)
            {
                return DirectoryIndex.Error(path, PhoneErrors.Disconnected(segments.Length > 0 ? segments[0] : "The phone").Message);
            }

            try
            {
                var entries = new List<DirectoryIndexEntry>();
                if (segments.Length == 1)
                {
                    foreach (PhoneStorage storage in device.ListStorages())
                    {
                        entries.Add(new DirectoryIndexEntry(storage.Name, true, 0, default, FileAttributes.Directory));
                    }

                    return new DirectoryIndex(path, entries, false, null);
                }

                WpdEntry folder = device.Resolve(segments);
                if (folder == null || !folder.IsDirectory)
                {
                    return DirectoryIndex.Error(path, "This folder isn't on the phone any more.");
                }

                foreach (WpdEntry child in device.Children(path, folder.Id, cancellationToken))
                {
                    entries.Add(new DirectoryIndexEntry(child.Name, child.IsDirectory, child.Size, child.ModifiedUtc,
                        (child.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal) | (child.IsHidden ? FileAttributes.Hidden : 0)));
                }

                return new DirectoryIndex(path, entries, false, null);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                Exception translated = Translate(ex, device);
                Log.Logger.Information(ex, "Listing {Path} failed", path);
                return DirectoryIndex.Error(path, translated is UnauthorizedAccessException ? PhoneErrors.ListingMessage(translated) : translated.Message);
            }
        }

        private PhoneItemInfo GetInfoCore(string path)
        {
            path = PhonePaths.Normalize(path);
            string[] segments = PhonePaths.Segments(path);
            WpdDevice device = segments.Length == 0 ? null : Find(segments[0]);
            if (device == null)
            {
                return null;
            }

            if (segments.Length == 1)
            {
                return new PhoneItemInfo(path, true, 0, default);
            }

            try
            {
                WpdEntry entry = device.Resolve(segments);
                return entry == null ? null : new PhoneItemInfo(path, entry.IsDirectory, entry.Size, entry.ModifiedUtc);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }
        }

        private Stream OpenReadCore(string path)
        {
            (WpdDevice device, WpdEntry entry) = Require(path);
            if (entry.IsDirectory)
            {
                throw new IOException($"\"{entry.Name}\" is a folder.");
            }

            try
            {
                PropertyKey key = Wpd.ResourceDefault;
                uint optimal = 0;
                device.Resources.GetStream(entry.Id, ref key, Wpd.StgmRead, ref optimal, out ComIStream stream);
                return new WpdReadStream(stream, device);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }
        }

        private PhoneWriteStream CreateFileCore(string folderPath, string name, long size, DateTime modifiedUtc)
        {
            (WpdDevice device, WpdEntry folder) = Require(folderPath);
            device.ThrowIfReadOnly();
            string safeName = PhonePaths.SanitizeName(name);
            try
            {
                IPortableDeviceValues values = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidPortableDeviceValues);
                SetString(values, Wpd.ObjectParentId, folder.Id);
                SetString(values, Wpd.ObjectName, safeName);
                SetString(values, Wpd.ObjectOriginalFileName, safeName);
                SetULong(values, Wpd.ObjectSize, (ulong)Math.Max(0, size));
                SetGuid(values, Wpd.ObjectContentType, Wpd.ContentTypeGenericFile);
                SetGuid(values, Wpd.ObjectFormat, Wpd.ObjectFormatUnspecified);

                uint optimal = 0;
                string cookie = null;
                device.Content.CreateObjectWithPropertiesAndData(values, out ComIStream stream, ref optimal, ref cookie);
                return new WpdWriteStream(stream, device, PhonePaths.Combine(folderPath, safeName), size);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }
        }

        private string CreateFolderCore(string parentPath, string name)
        {
            (WpdDevice device, WpdEntry parent) = Require(parentPath);
            device.ThrowIfReadOnly();
            string safeName = PhonePaths.SanitizeName(name);
            string path = PhonePaths.Combine(parentPath, safeName);
            if (device.Resolve(PhonePaths.Segments(path)) != null)
            {
                throw new IOException($"\"{safeName}\" already exists on the phone.");
            }

            try
            {
                IPortableDeviceValues values = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidPortableDeviceValues);
                SetString(values, Wpd.ObjectParentId, parent.Id);
                SetString(values, Wpd.ObjectName, safeName);
                SetString(values, Wpd.ObjectOriginalFileName, safeName);
                SetGuid(values, Wpd.ObjectContentType, Wpd.ContentTypeFolder);
                device.Content.CreateObjectWithPropertiesOnly(values, out string id);
                device.Remember(path, new WpdEntry(id, safeName, true, 0, DateTime.UtcNow, false));
                return path;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }
        }

        private void DeleteCore(string path)
        {
            (WpdDevice device, WpdEntry entry) = Require(path);
            device.ThrowIfReadOnly();
            try
            {
                try
                {
                    device.DeleteObject(entry.Id, recursive: true);
                }
                catch (COMException) when (entry.IsDirectory)
                {
                    // Not every phone deletes a folder with its contents in one call: empty it first, deepest items first.
                    foreach (WpdEntry child in device.Children(PhonePaths.Normalize(path), entry.Id, CancellationToken.None))
                    {
                        Delete(PhonePaths.Combine(path, child.Name));
                    }

                    device.DeleteObject(entry.Id, recursive: false);
                }

                device.Forget(path);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                device.Forget(path);
                throw Translate(ex, device);
            }
        }

        private string RenameCore(string path, string newName)
        {
            (WpdDevice device, WpdEntry entry) = Require(path);
            device.ThrowIfReadOnly();
            string safeName = PhonePaths.SanitizeName(newName);
            string newPath = PhonePaths.Combine(PhonePaths.GetParent(path), safeName);
            try
            {
                // The file name is what Android shows; some devices only take the object name.
                if (!device.TrySetName(entry.Id, Wpd.ObjectOriginalFileName, safeName) && !device.TrySetName(entry.Id, Wpd.ObjectName, safeName))
                {
                    throw new IOException("The phone doesn't allow renaming this item.");
                }

                device.Forget(path);
                device.Remember(newPath, new WpdEntry(entry.Id, safeName, entry.IsDirectory, entry.Size, entry.ModifiedUtc, entry.IsHidden));
                return newPath;
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }
        }

        // ------------------------------------------------------------------ devices

        private unsafe string[] GetDeviceIds()
        {
            uint count = 0;
            _manager.GetDevices(IntPtr.Zero, ref count); // how many
            if (count == 0)
            {
                return Array.Empty<string>();
            }

            // The API fills a plain array of string pointers (LPWSTR*), each allocated with CoTaskMemAlloc for the caller to
            // free; it may report fewer than were asked for if a device left in between.
            var pointers = new IntPtr[count];
            fixed (IntPtr* buffer = pointers)
            {
                _manager.GetDevices((IntPtr)buffer, ref count);
            }

            count = Math.Min(count, (uint)pointers.Length);
            var ids = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                if (pointers[i] != IntPtr.Zero)
                {
                    ids.Add(Marshal.PtrToStringUni(pointers[i]));
                    Marshal.FreeCoTaskMem(pointers[i]);
                }
            }

            return ids.ToArray();
        }

        private string ManagerString(Func<IntPtr, uint, uint> call)
        {
            uint size = call(IntPtr.Zero, 0);
            if (size == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocCoTaskMem((int)size * 2);
            try
            {
                call(buffer, size);
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        private WpdDevice OpenDevice(string id)
        {
            lock (_gate)
            {
                if (_notPhones.Contains(id))
                {
                    return null;
                }

                if (_nameByDeviceId.TryGetValue(id, out string known) && _byName.TryGetValue(known, out WpdDevice open))
                {
                    return open;
                }
            }

            // One line per step with a device newly connected (not at every poll): if a phone's driver ever takes the app
            // down, the log's last line says which call it was.
            bool announce;
            lock (_gate)
            {
                announce = _announced.Add(id);
            }

            if (announce)
            {
                Log.Logger.Information("Portable device connected: {Id}; opening it", id);
            }

            WpdDevice device;
            try
            {
                device = WpdDevice.Open(id);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                if (announce)
                {
                    // Locked, or busy: tried again at the next poll, without another line each time.
                    Log.Logger.Information(ex, "Couldn't open portable device {Id} (it is tried again quietly)", id);
                }

                return null;
            }

            Log.Logger.Information("Portable device opened: {Model} (protocol {Protocol}{ReadOnly})", device.FallbackName, device.Protocol,
                device.IsReadOnly ? ", read-only" : string.Empty);
            if (device.IsMassStorage)
            {
                Log.Logger.Information("{Model} is a USB drive (it has a drive letter): not listed as a phone", device.FallbackName);
                device.Close();
                lock (_gate)
                {
                    _notPhones.Add(id); // a USB stick or card reader: it has a drive letter already
                }

                return null;
            }

            string friendly = null;
            try
            {
                lock (_gate)
                {
                    friendly = ManagerString((buffer, size) =>
                    {
                        uint s = size;
                        _manager.GetDeviceFriendlyName(id, buffer, ref s);
                        return s;
                    });
                }
            }
            catch (COMException)
            {
            }

            lock (_gate)
            {
                if (!_nameByDeviceId.TryGetValue(id, out string name))
                {
                    var taken = new HashSet<string>(_byName.Keys, StringComparer.OrdinalIgnoreCase);
                    name = PhonePaths.UniqueDeviceName(friendly ?? device.FallbackName, taken);
                    _nameByDeviceId[id] = name;
                }

                device.Name = name;
                _byName[name] = device;
                return device;
            }
        }

        private void ForgetUnplugged(string[] presentIds)
        {
            var present = new HashSet<string>(presentIds, StringComparer.Ordinal);
            var gone = new List<WpdDevice>();
            lock (_gate)
            {
                foreach (KeyValuePair<string, string> pair in new List<KeyValuePair<string, string>>(_nameByDeviceId))
                {
                    if (!present.Contains(pair.Key))
                    {
                        _nameByDeviceId.Remove(pair.Key);
                        if (_byName.Remove(pair.Value, out WpdDevice device))
                        {
                            gone.Add(device);
                        }
                    }
                }

                _notPhones.IntersectWith(present);
                _announced.IntersectWith(present);
            }

            foreach (WpdDevice device in gone)
            {
                Log.Logger.Information("Portable device disconnected: {Name}", device.Name);
                device.Close();
            }
        }

        private WpdDevice Find(string name)
        {
            lock (_gate)
            {
                if (_byName.TryGetValue(name, out WpdDevice device))
                {
                    return device;
                }
            }

            // Not opened yet in this session (a path from history or the address bar): look the phones up once.
            GetDevices();
            lock (_gate)
            {
                return _byName.TryGetValue(name, out WpdDevice device) ? device : null;
            }
        }

        private (WpdDevice Device, WpdEntry Entry) Require(string path)
        {
            string[] segments = PhonePaths.Segments(path);
            WpdDevice device = segments.Length == 0 ? null : Find(segments[0]);
            if (device == null)
            {
                throw PhoneErrors.Disconnected(segments.Length > 0 ? segments[0] : "The phone");
            }

            if (segments.Length == 1)
            {
                throw new IOException("Choose one of the phone's storages (\"Internal shared storage\") first.");
            }

            WpdEntry entry;
            try
            {
                entry = device.Resolve(segments);
            }
            catch (Exception ex) when (IsDeviceError(ex))
            {
                throw Translate(ex, device);
            }

            return entry != null
                ? (device, entry)
                : throw new FileNotFoundException($"\"{PhonePaths.GetName(path)}\" isn't on the phone any more.", path);
        }

        private void DropDevice(WpdDevice device)
        {
            lock (_gate)
            {
                if (device.Name != null && _byName.TryGetValue(device.Name, out WpdDevice current) && ReferenceEquals(current, device))
                {
                    _byName.Remove(device.Name);
                }
            }

            device.Close();
        }

        internal static bool IsDeviceError(Exception ex) =>
            ex is COMException || ex is InvalidComObjectException || ex is InvalidCastException || ex is IOException || ex is UnauthorizedAccessException;

        /// <summary>A COM failure in the person's terms; an unplugged phone is forgotten so the next look reopens it.</summary>
        private Exception Translate(Exception ex, WpdDevice device)
        {
            if (ex is IOException || ex is UnauthorizedAccessException)
            {
                return ex;
            }

            int hr = ex.HResult;
            if (hr == Wpd.EAccessDenied)
            {
                return new UnauthorizedAccessException("The phone doesn't allow this. " + PhoneErrors.LockedHint, ex);
            }

            // ERROR_GEN_FAILURE, ERROR_DEVICE_NOT_CONNECTED, ERROR_NOT_READY, ERROR_SEM_TIMEOUT, ERROR_BUSY: unplugged or asleep.
            if (hr == unchecked((int)0x8007001F) || hr == unchecked((int)0x8007048F) || hr == unchecked((int)0x80070015) ||
                hr == unchecked((int)0x80070079) || hr == unchecked((int)0x800700AA) || ex is InvalidComObjectException)
            {
                DropDevice(device);
                return PhoneErrors.Disconnected(device.Name, ex);
            }

            return new IOException($"The phone reported an error (0x{hr:X8}). " + PhoneErrors.LockedHint, ex);
        }

        // ------------------------------------------------------------------ value helpers (static readonly keys can't be passed by ref)

        internal static void SetString(IPortableDeviceValues values, PropertyKey key, string value) => values.SetStringValue(ref key, value);
        internal static void SetUInt(IPortableDeviceValues values, PropertyKey key, uint value) => values.SetUnsignedIntegerValue(ref key, value);
        internal static void SetULong(IPortableDeviceValues values, PropertyKey key, ulong value) => values.SetUnsignedLargeIntegerValue(ref key, value);

        internal static void SetGuid(IPortableDeviceValues values, PropertyKey key, Guid value) => values.SetGuidValue(ref key, ref value);

        internal static string GetString(IPortableDeviceValues values, PropertyKey key) =>
            values.GetStringValue(ref key, out string value) == 0 ? value : null;

        internal static ulong? GetULong(IPortableDeviceValues values, PropertyKey key) =>
            values.GetUnsignedLargeIntegerValue(ref key, out ulong value) == 0 ? value : null;

        internal static Guid? GetGuid(IPortableDeviceValues values, PropertyKey key) =>
            values.GetGuidValue(ref key, out Guid value) == 0 ? value : null;

        internal static bool GetBool(IPortableDeviceValues values, PropertyKey key) =>
            values.GetBoolValue(ref key, out int value) == 0 && value != 0;

        /// <summary>A date property (VT_DATE in local time, or VT_FILETIME) as UTC; default when missing.</summary>
        internal static DateTime GetDate(IPortableDeviceValues values, PropertyKey key)
        {
            if (values.GetValue(ref key, out PropVariant variant) != 0)
            {
                return default;
            }

            try
            {
                long bits = IntPtr.Size == 8
                    ? variant.Value.ToInt64()
                    : (uint)variant.Value.ToInt32() | ((long)variant.Value2.ToInt32() << 32);
                if (variant.VarType == Wpd.VtDate)
                {
                    double oaDate = BitConverter.Int64BitsToDouble(bits);
                    return oaDate > 0 && oaDate < 2958466 ? DateTime.SpecifyKind(DateTime.FromOADate(oaDate), DateTimeKind.Local).ToUniversalTime() : default;
                }

                if (variant.VarType == Wpd.VtFiletime && bits > 0)
                {
                    return DateTime.FromFileTimeUtc(bits);
                }

                return default;
            }
            catch (ArgumentException)
            {
                return default;
            }
            finally
            {
                Wpd.PropVariantClear(ref variant);
            }
        }
    }

    /// <summary>An object on a phone, as listed.</summary>
    internal sealed class WpdEntry
    {
        public WpdEntry(string id, string name, bool isDirectory, long size, DateTime modifiedUtc, bool isHidden)
        {
            Id = id;
            Name = name;
            IsDirectory = isDirectory;
            Size = size;
            ModifiedUtc = modifiedUtc;
            IsHidden = isHidden;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public long Size { get; }
        public DateTime ModifiedUtc { get; }
        public bool IsHidden { get; }
    }

    /// <summary>One open device and what is known of its object tree (path → object).</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WpdDevice
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, WpdEntry> _known = new Dictionary<string, WpdEntry>(StringComparer.OrdinalIgnoreCase);
        private IPortableDevice _device;

        private WpdDevice()
        {
        }

        public string Name { get; set; }
        public string FallbackName { get; private set; }
        public string Description { get; private set; }
        public bool IsMassStorage { get; private set; }
        public string Protocol { get; private set; }
        public bool IsReadOnly { get; private set; }
        public IPortableDeviceContent Content { get; private set; }
        public IPortableDeviceProperties Properties { get; private set; }
        public IPortableDeviceResources Resources { get; private set; }

        public static WpdDevice Open(string deviceId)
        {
            var result = new WpdDevice();
            IPortableDeviceValues client = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidPortableDeviceValues);
            WpdPhoneProvider.SetString(client, Wpd.ClientName, "ByteExplorer");
            WpdPhoneProvider.SetUInt(client, Wpd.ClientMajorVersion, 1);
            WpdPhoneProvider.SetUInt(client, Wpd.ClientMinorVersion, 0);
            WpdPhoneProvider.SetUInt(client, Wpd.ClientRevision, 0);
            WpdPhoneProvider.SetUInt(client, Wpd.ClientSecurityQualityOfService, Wpd.SecurityImpersonation);
            WpdPhoneProvider.SetUInt(client, Wpd.ClientDesiredAccess, Wpd.GenericRead | Wpd.GenericWrite);

            IPortableDevice device = Wpd.Create<IPortableDevice>(Wpd.ClsidPortableDeviceFtm);
            try
            {
                device.Open(deviceId, client);
            }
            catch (COMException ex) when (ex.HResult == Wpd.EAccessDenied)
            {
                // Some devices allow reading only (a camera in PTP mode).
                WpdPhoneProvider.SetUInt(client, Wpd.ClientDesiredAccess, Wpd.GenericRead);
                device.Open(deviceId, client);
                result.IsReadOnly = true;
            }

            result._device = device;
            device.Content(out IPortableDeviceContent content);
            result.Content = content;
            content.Properties(out IPortableDeviceProperties properties);
            result.Properties = properties;
            content.Transfer(out IPortableDeviceResources resources);
            result.Resources = resources;
            result.ReadDeviceProperties();
            return result;
        }

        private void ReadDeviceProperties()
        {
            IPortableDeviceKeyCollection keys = Keys(Wpd.DeviceProtocol, Wpd.DeviceManufacturer, Wpd.DeviceModel, Wpd.DeviceFriendlyName);
            Properties.GetValues(Wpd.DeviceObjectId, keys, out IPortableDeviceValues values);
            string protocol = WpdPhoneProvider.GetString(values, Wpd.DeviceProtocol) ?? string.Empty;
            string manufacturer = WpdPhoneProvider.GetString(values, Wpd.DeviceManufacturer);
            string model = WpdPhoneProvider.GetString(values, Wpd.DeviceModel);
            Protocol = protocol;
            IsMassStorage = protocol.StartsWith("MSC", StringComparison.OrdinalIgnoreCase);
            FallbackName = WpdPhoneProvider.GetString(values, Wpd.DeviceFriendlyName) ?? model ?? "Phone";
            Description = string.Join(" · ", new[] { manufacturer, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrEmpty(Description))
            {
                Description = null;
            }
        }

        public void ThrowIfReadOnly()
        {
            if (IsReadOnly)
            {
                throw new UnauthorizedAccessException($"\"{Name}\" only allows reading.");
            }
        }

        /// <summary>How long the drive list may show storages (and their free space) as last read.</summary>
        private const int StorageCacheSeconds = 30;

        private IReadOnlyList<PhoneStorage> _storages;
        private long _storagesReadAt;

        /// <summary>
        /// The phone's storages (internal storage, SD card). <paramref name="allowCached"/>: as read in the last
        /// <see cref="StorageCacheSeconds"/> seconds (the drive list's poll); otherwise read now (opening the phone).
        /// </summary>
        public IReadOnlyList<PhoneStorage> ListStorages(bool allowCached = false)
        {
            if (allowCached && _storages != null && Environment.TickCount64 - _storagesReadAt < StorageCacheSeconds * 1000L)
            {
                return _storages;
            }

            bool first = _storages == null;
            IReadOnlyList<PhoneStorage> read = ReadStorages();
            _storages = read;
            _storagesReadAt = Environment.TickCount64;
            if (first)
            {
                Log.Logger.Information("Portable device {Name}: {Count} storage(s) read", Name, read.Count);
            }

            return read;
        }

        private IReadOnlyList<PhoneStorage> ReadStorages()
        {
            var storages = new List<PhoneStorage>();
            foreach (string id in EnumerateIds(Wpd.DeviceObjectId, CancellationToken.None))
            {
                IPortableDeviceKeyCollection keys = Keys(Wpd.ObjectContentType, Wpd.FunctionalObjectCategory, Wpd.ObjectName,
                    Wpd.StorageDescription, Wpd.StorageCapacity, Wpd.StorageFreeSpaceInBytes);
                Properties.GetValues(id, keys, out IPortableDeviceValues values);
                if (WpdPhoneProvider.GetGuid(values, Wpd.FunctionalObjectCategory) != Wpd.FunctionalCategoryStorage)
                {
                    continue; // other functions of the device (contacts, SMS services)
                }

                string name = PhonePaths.SanitizeName(WpdPhoneProvider.GetString(values, Wpd.ObjectName) ??
                                                      WpdPhoneProvider.GetString(values, Wpd.StorageDescription) ?? "Storage");
                long total = (long?)WpdPhoneProvider.GetULong(values, Wpd.StorageCapacity) ?? -1;
                long free = (long?)WpdPhoneProvider.GetULong(values, Wpd.StorageFreeSpaceInBytes) ?? -1;
                string path = PhonePaths.Build(Name, name);
                Remember(path, new WpdEntry(id, name, true, 0, default, false));
                storages.Add(new PhoneStorage(name, path, total, free));
            }

            return storages;
        }

        /// <summary>The object at a path (segments from the device name on), walking and listing folders as needed; null when missing.</summary>
        public WpdEntry Resolve(string[] segments)
        {
            string path = PhonePaths.Build(segments[0], segments[1..]);
            lock (_gate)
            {
                if (_known.TryGetValue(path, out WpdEntry known))
                {
                    return known;
                }
            }

            if (segments.Length == 2)
            {
                ListStorages(); // storages are remembered as they are listed
            }
            else
            {
                WpdEntry parent = Resolve(segments[..^1]);
                if (parent == null || !parent.IsDirectory)
                {
                    return null;
                }

                Children(PhonePaths.GetParent(path), parent.Id, CancellationToken.None);
            }

            lock (_gate)
            {
                return _known.TryGetValue(path, out WpdEntry found) ? found : null;
            }
        }

        /// <summary>A folder's items, remembered by path. A name used twice in the folder gets "name (2)".</summary>
        public List<WpdEntry> Children(string folderPath, string folderId, CancellationToken cancellationToken)
        {
            var result = new List<WpdEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IPortableDeviceKeyCollection keys = Keys(Wpd.ObjectName, Wpd.ObjectOriginalFileName, Wpd.ObjectContentType,
                Wpd.ObjectSize, Wpd.ObjectDateModified, Wpd.ObjectIsHidden);
            foreach (string id in EnumerateIds(folderId, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Properties.GetValues(id, keys, out IPortableDeviceValues values);
                Guid? type = WpdPhoneProvider.GetGuid(values, Wpd.ObjectContentType);
                bool isDirectory = type == Wpd.ContentTypeFolder || type == Wpd.ContentTypeFunctionalObject;
                string name = WpdPhoneProvider.GetString(values, Wpd.ObjectOriginalFileName);
                if (string.IsNullOrEmpty(name))
                {
                    name = WpdPhoneProvider.GetString(values, Wpd.ObjectName) ?? id;
                }

                name = UniqueIn(names, PhonePaths.SanitizeName(name));
                long size = isDirectory ? 0 : (long)(WpdPhoneProvider.GetULong(values, Wpd.ObjectSize) ?? 0);
                var entry = new WpdEntry(id, name, isDirectory, size, WpdPhoneProvider.GetDate(values, Wpd.ObjectDateModified),
                    WpdPhoneProvider.GetBool(values, Wpd.ObjectIsHidden));
                result.Add(entry);
            }

            lock (_gate)
            {
                foreach (WpdEntry entry in result)
                {
                    _known[PhonePaths.Combine(folderPath, entry.Name)] = entry;
                }
            }

            return result;
        }

        private static string UniqueIn(HashSet<string> names, string name)
        {
            string candidate = name;
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int n = 2; !names.Add(candidate); n++)
            {
                candidate = $"{stem} ({n}){extension}";
            }

            return candidate;
        }

        private IEnumerable<string> EnumerateIds(string parentId, CancellationToken cancellationToken)
        {
            Content.EnumObjects(0, parentId, null, out IEnumPortableDeviceObjectIDs enumerator);
            var batch = new IntPtr[64];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int hr = enumerator.Next((uint)batch.Length, batch, out uint fetched);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                for (int i = 0; i < fetched; i++)
                {
                    string id = Marshal.PtrToStringUni(batch[i]);
                    Marshal.FreeCoTaskMem(batch[i]);
                    batch[i] = IntPtr.Zero;
                    if (!string.IsNullOrEmpty(id))
                    {
                        yield return id;
                    }
                }

                if (hr != 0 || fetched == 0)
                {
                    yield break; // S_FALSE: that was the last batch
                }
            }
        }

        public void Remember(string path, WpdEntry entry)
        {
            lock (_gate)
            {
                _known[PhonePaths.Normalize(path)] = entry;
            }
        }

        /// <summary>Forgets a path and everything under it (deleted or renamed).</summary>
        public void Forget(string path)
        {
            string normalized = PhonePaths.Normalize(path);
            lock (_gate)
            {
                var stale = new List<string>();
                foreach (string key in _known.Keys)
                {
                    if (PhonePaths.AreSame(key, normalized) || PhonePaths.IsInside(key, normalized))
                    {
                        stale.Add(key);
                    }
                }

                foreach (string key in stale)
                {
                    _known.Remove(key);
                }
            }
        }

        public void DeleteObject(string objectId, bool recursive)
        {
            IPortableDevicePropVariantCollection ids = Wpd.Create<IPortableDevicePropVariantCollection>(Wpd.ClsidPortableDevicePropVariantCollection);
            var value = new PropVariant { VarType = Wpd.VtLpwstr, Value = Marshal.StringToCoTaskMemUni(objectId) };
            try
            {
                ids.Add(ref value); // the collection keeps its own copy
            }
            finally
            {
                Wpd.PropVariantClear(ref value);
            }

            Content.Delete(recursive ? Wpd.DeleteWithRecursion : Wpd.DeleteNoRecursion, ids, IntPtr.Zero);
        }

        public bool TrySetName(string objectId, PropertyKey key, string name)
        {
            IPortableDeviceValues values = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidPortableDeviceValues);
            WpdPhoneProvider.SetString(values, key, name);
            try
            {
                Properties.SetValues(objectId, values, out IPortableDeviceValues results);
                return results == null || results.GetErrorValue(ref key, out int error) != 0 || error >= 0;
            }
            catch (COMException ex) when (ex.HResult != Wpd.EAccessDenied)
            {
                return false;
            }
        }

        public void Close()
        {
            try
            {
                _device?.Close();
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidComObjectException)
            {
            }

            _device = null;
            lock (_gate)
            {
                _known.Clear();
            }
        }

        private static IPortableDeviceKeyCollection Keys(params PropertyKey[] keys)
        {
            IPortableDeviceKeyCollection collection = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.ClsidPortableDeviceKeyCollection);
            foreach (PropertyKey key in keys)
            {
                PropertyKey copy = key;
                collection.Add(ref copy);
            }

            return collection;
        }
    }

    /// <summary>A phone file being read (a download).</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WpdReadStream : Stream
    {
        private ComIStream _stream;
        private byte[] _scratch;

        public WpdReadStream(ComIStream stream, WpdDevice device)
        {
            _stream = stream;
            Device = device;
        }

        public WpdDevice Device { get; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => WpdThread.Run(() => ReadCore(buffer, offset, count));

        private unsafe int ReadCore(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_stream == null, this);
            if (count <= 0)
            {
                return 0;
            }

            int read = 0;
            try
            {
                if (offset == 0)
                {
                    _stream.Read(buffer, count, (IntPtr)(&read));
                    return read;
                }

                if (_scratch == null || _scratch.Length < count)
                {
                    _scratch = new byte[count];
                }

                _stream.Read(_scratch, count, (IntPtr)(&read));
                Buffer.BlockCopy(_scratch, 0, buffer, offset, read);
                return read;
            }
            catch (COMException ex)
            {
                throw new IOException("Reading from the phone failed. " + PhoneErrors.LockedHint, ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WpdThread.Run(ReleaseStream); // released where it was made
            base.Dispose(disposing);
        }

        private void ReleaseStream()
        {
            if (_stream != null)
            {
                try
                {
                    Marshal.ReleaseComObject(_stream);
                }
                catch (ArgumentException)
                {
                }

                _stream = null;
            }
        }
    }

    /// <summary>A new phone file being written (an upload): nothing exists on the phone until Commit.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WpdWriteStream : PhoneWriteStream
    {
        private readonly WpdDevice _device;
        private readonly string _path;
        private readonly long _size;
        private ComIStream _stream;
        private byte[] _scratch;
        private long _written;
        private bool _committed;

        public WpdWriteStream(ComIStream stream, WpdDevice device, string path, long size)
        {
            _stream = stream;
            _device = device;
            _path = path;
            _size = size;
        }

        public override void Write(byte[] buffer, int offset, int count) => WpdThread.Run(() => WriteCore(buffer, offset, count));

        private unsafe void WriteCore(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_stream == null, this);
            while (count > 0)
            {
                byte[] source = buffer;
                if (offset != 0)
                {
                    if (_scratch == null || _scratch.Length < count)
                    {
                        _scratch = new byte[count];
                    }

                    Buffer.BlockCopy(buffer, offset, _scratch, 0, count);
                    source = _scratch;
                }

                int written = 0;
                try
                {
                    _stream.Write(source, count, (IntPtr)(&written));
                }
                catch (COMException ex)
                {
                    throw new IOException("Writing to the phone failed. " + PhoneErrors.LockedHint, ex);
                }

                if (written <= 0)
                {
                    throw new IOException("The phone stopped accepting data.");
                }

                _written += written;
                offset += written;
                count -= written;
            }
        }

        public override void Flush()
        {
        }

        public override string Commit() => WpdThread.Run(CommitCore);

        private string CommitCore()
        {
            ObjectDisposedException.ThrowIf(_stream == null, this);
            if (_written != _size)
            {
                throw new IOException($"Only {_written:N0} of {_size:N0} bytes reached the phone.");
            }

            try
            {
                _stream.Commit((int)Wpd.StgcDefault);
            }
            catch (COMException ex)
            {
                throw new IOException("The phone didn't accept the file. " + PhoneErrors.LockedHint, ex);
            }

            _committed = true;
            try
            {
                if (_stream is IPortableDeviceDataStream data)
                {
                    data.GetObjectID(out string id);
                    _device.Remember(_path, new WpdEntry(id, PhonePaths.GetName(_path), false, _size, DateTime.UtcNow, false));
                }
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
            {
                _device.Forget(_path); // found again by listing its folder
            }

            return _path;
        }

        protected override void Dispose(bool disposing)
        {
            WpdThread.Run(ReleaseStream); // released where it was made
            base.Dispose(disposing);
        }

        private void ReleaseStream()
        {
            if (_stream != null)
            {
                if (!_committed)
                {
                    try
                    {
                        // Abandons the new object: nothing is left on the phone.
                        if (_stream is IPortableDeviceDataStream data)
                        {
                            data.Cancel();
                        }
                        else
                        {
                            _stream.Revert();
                        }
                    }
                    catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
                    {
                    }
                }

                try
                {
                    Marshal.ReleaseComObject(_stream);
                }
                catch (ArgumentException)
                {
                }

                _stream = null;
            }
        }
    }
}
