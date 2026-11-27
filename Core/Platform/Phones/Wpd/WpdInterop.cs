// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace ByteExplorer.Core.Platform.Phones.Wpd
{
    // Windows Portable Devices (PortableDeviceApi.dll, part of Windows), declared by hand so the app needs no interop
    // package. Every interface lists its methods in vtable order exactly as the SDK headers declare them (verified
    // against MinGW-w64's portabledeviceapi.h and portabledevicetypes.h, generated from Microsoft's IDL); a slot out of
    // order would call the wrong method. Methods the app never calls keep their slot with placeholder parameters.
    // Getters that report "no such value" as an error HRESULT are PreserveSig, so a missing property costs a return
    // code, not an exception.

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>PROPVARIANT: the type tag, three reserved words, then a 16-byte (x64) or 8-byte (x86) union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Value2;
    }

    /// <remarks>
    /// The device-id arrays are passed as raw pointers to a caller's pinned buffer (LPWSTR*). Never declare them as
    /// IntPtr[]: in a COM interface an array parameter is marshalled as a SAFEARRAY by default, so the API wrote the ids
    /// into what it took for a plain array and the marshaller then "cleared" a SAFEARRAY that wasn't one, an access
    /// violation that killed the app silently as soon as a phone was connected.
    /// </remarks>
    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceManager
    {
        void GetDevices(IntPtr deviceIds, ref uint deviceIdsCount);
        void RefreshDeviceList();
        void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr friendlyName, ref uint friendlyNameSize);
        void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr description, ref uint descriptionSize);
        void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr manufacturer, ref uint manufacturerSize);
        void GetDeviceProperty([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.LPWStr)] string propertyName, IntPtr data, ref uint dataSize, ref uint type);
        void GetPrivateDevices(IntPtr deviceIds, ref uint deviceIdsCount);
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IPortableDeviceValues clientInfo);
        void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
        void Content(out IPortableDeviceContent content);
        void Capabilities(out IntPtr capabilities);
        void Cancel();
        void Close();
        void Advise(uint flags, IntPtr callback, IPortableDeviceValues parameters, out IntPtr cookie);
        void Unadvise([MarshalAs(UnmanagedType.LPWStr)] string cookie);
        void GetPnPDeviceID(out IntPtr deviceId);
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceContent
    {
        void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IPortableDeviceValues filter, out IEnumPortableDeviceObjectIDs enumObjectIds);
        void Properties(out IPortableDeviceProperties properties);
        void Transfer(out IPortableDeviceResources resources);
        void CreateObjectWithPropertiesOnly(IPortableDeviceValues values, [MarshalAs(UnmanagedType.LPWStr)] out string objectId);
        void CreateObjectWithPropertiesAndData(IPortableDeviceValues values, out ComIStream data, ref uint optimalWriteBufferSize, [MarshalAs(UnmanagedType.LPWStr)] ref string cookie);
        void Delete(uint options, IPortableDevicePropVariantCollection objectIds, IntPtr results);
        void GetObjectIDsFromPersistentUniqueIDs(IPortableDevicePropVariantCollection persistentUniqueIds, out IPortableDevicePropVariantCollection objectIds);
        void Cancel();
        void Move(IPortableDevicePropVariantCollection objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderObjectId, IntPtr results);
        void Copy(IPortableDevicePropVariantCollection objectIds, [MarshalAs(UnmanagedType.LPWStr)] string destinationFolderObjectId, IntPtr results);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceProperties
    {
        void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
        void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
        void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values);
        void SetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceValues values, out IPortableDeviceValues results);
        void Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys);
        void Cancel();
    }

    [ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceResources
    {
        void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
        void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
        void GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out ComIStream stream);
        void Delete([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys);
        void Cancel();
        void CreateResource(IPortableDeviceValues resourceAttributes, out ComIStream data, ref uint optimalWriteBufferSize, IntPtr cookie);
    }

    [ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumPortableDeviceObjectIDs
    {
        /// <summary>S_OK, or S_FALSE (1) when fewer than asked were left; the LPWSTRs are freed by the caller.</summary>
        [PreserveSig]
        int Next(uint objects, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] objectIds, out uint fetched);
        void Skip(uint objects);
        void Reset();
        void Clone(out IEnumPortableDeviceObjectIDs enumObjectIds);
        void Cancel();
    }

    /// <summary>The stream of a new object: IStream's eleven methods, then GetObjectID and Cancel.</summary>
    [ComImport, Guid("88e04db3-1012-4d64-9996-f703a950d3f4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceDataStream
    {
        void Read(IntPtr pv, uint cb, IntPtr pcbRead);
        void Write(IntPtr pv, uint cb, IntPtr pcbWritten);
        void Seek(long dlibMove, uint dwOrigin, IntPtr plibNewPosition);
        void SetSize(ulong libNewSize);
        void CopyTo(IntPtr pstm, ulong cb, IntPtr pcbRead, IntPtr pcbWritten);
        void Commit(uint grfCommitFlags);
        void Revert();
        void LockRegion(ulong libOffset, ulong cb, uint dwLockType);
        void UnlockRegion(ulong libOffset, ulong cb, uint dwLockType);
        void Stat(IntPtr pstatstg, uint grfStatFlag);
        void Clone(out IntPtr ppstm);
        void GetObjectID([MarshalAs(UnmanagedType.LPWStr)] out string objectId);
        void Cancel();
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceValues
    {
        void GetCount(out uint count);
        void GetAt(uint index, IntPtr key, IntPtr value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
        void SetSignedIntegerValue(ref PropertyKey key, int value);
        [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);
        void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
        [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
        void SetFloatValue(ref PropertyKey key, float value);
        [PreserveSig] int GetFloatValue(ref PropertyKey key, out float value);
        void SetErrorValue(ref PropertyKey key, int value);
        [PreserveSig] int GetErrorValue(ref PropertyKey key, out int value);
        void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
        [PreserveSig] int GetKeyValue(ref PropertyKey key, out PropertyKey value);
        void SetBoolValue(ref PropertyKey key, int value);
        [PreserveSig] int GetBoolValue(ref PropertyKey key, out int value);
        void SetIUnknownValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIUnknownValue(ref PropertyKey key, out IntPtr value);
        void SetGuidValue(ref PropertyKey key, ref Guid value);
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
        void SetBufferValue(ref PropertyKey key, IntPtr value, uint size);
        [PreserveSig] int GetBufferValue(ref PropertyKey key, out IntPtr value, out uint size);
        void SetIPortableDeviceValuesValue(ref PropertyKey key, IPortableDeviceValues value);
        [PreserveSig] int GetIPortableDeviceValuesValue(ref PropertyKey key, out IPortableDeviceValues value);
        void SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IPortableDevicePropVariantCollection value);
        [PreserveSig] int GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IPortableDevicePropVariantCollection value);
        void SetIPortableDeviceKeyCollectionValue(ref PropertyKey key, IPortableDeviceKeyCollection value);
        [PreserveSig] int GetIPortableDeviceKeyCollectionValue(ref PropertyKey key, out IPortableDeviceKeyCollection value);
        void SetIPortableDeviceValuesCollectionValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetIPortableDeviceValuesCollectionValue(ref PropertyKey key, out IntPtr value);
        void RemoveValue(ref PropertyKey key);
        void CopyValuesFromPropertyStore(IntPtr store);
        void CopyValuesToPropertyStore(IntPtr store);
        void Clear();
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceKeyCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void Add(ref PropertyKey key);
        void Clear();
        void RemoveAt(uint index);
    }

    [ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDevicePropVariantCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropVariant value);
        void Add(ref PropVariant value);
        void GetType(out ushort varType);
        void ChangeType(ushort varType);
        void Clear();
        void RemoveAt(uint index);
    }

    /// <summary>Class ids, property keys and type ids (portabledeviceapi.h, portabledevicetypes.h, portabledevice.h).</summary>
    [SupportedOSPlatform("windows")]
    internal static class Wpd
    {
        public static readonly Guid ClsidPortableDeviceManager = new Guid(0x0af10cec, 0x2ecd, 0x4b92, 0x95, 0x81, 0x34, 0xf6, 0xae, 0x06, 0x37, 0xf3);
        public static readonly Guid ClsidPortableDeviceFtm = new Guid(0xf7c0039a, 0x4762, 0x488a, 0xb4, 0xb3, 0x76, 0x0e, 0xf9, 0xa1, 0xba, 0x9b);
        public static readonly Guid ClsidPortableDeviceValues = new Guid(0x0c15d503, 0xd017, 0x47ce, 0x90, 0x16, 0x7b, 0x3f, 0x97, 0x87, 0x21, 0xcc);
        public static readonly Guid ClsidPortableDeviceKeyCollection = new Guid(0xde2d022d, 0x2480, 0x43be, 0x97, 0xf0, 0xd1, 0xfa, 0x2c, 0xf9, 0x8f, 0x4f);
        public static readonly Guid ClsidPortableDevicePropVariantCollection = new Guid(0x08a99e2f, 0x6d6d, 0x4b80, 0xaf, 0x5a, 0xba, 0xf2, 0xbc, 0xbe, 0x4c, 0xb9);

        private static readonly Guid ObjectProperties = new Guid(0xef6b490d, 0x5cd8, 0x437a, 0xaf, 0xfc, 0xda, 0x8b, 0x60, 0xee, 0x4a, 0x3c);
        private static readonly Guid DeviceProperties = new Guid(0x26d4979a, 0xe643, 0x4626, 0x9e, 0x2b, 0x73, 0x6d, 0xc0, 0xc9, 0x2f, 0xdc);
        private static readonly Guid StorageProperties = new Guid(0x01a3057a, 0x74d6, 0x4e80, 0xbe, 0xa7, 0xdc, 0x4c, 0x21, 0x2c, 0xe5, 0x0a);
        private static readonly Guid ClientProperties = new Guid(0x204d9f0c, 0x2292, 0x4080, 0x9f, 0x42, 0x40, 0x66, 0x4e, 0x70, 0xf8, 0x59);

        public static readonly PropertyKey ObjectId = new PropertyKey(ObjectProperties, 2);
        public static readonly PropertyKey ObjectParentId = new PropertyKey(ObjectProperties, 3);
        public static readonly PropertyKey ObjectName = new PropertyKey(ObjectProperties, 4);
        public static readonly PropertyKey ObjectFormat = new PropertyKey(ObjectProperties, 6);
        public static readonly PropertyKey ObjectContentType = new PropertyKey(ObjectProperties, 7);
        public static readonly PropertyKey ObjectIsHidden = new PropertyKey(ObjectProperties, 9);
        public static readonly PropertyKey ObjectSize = new PropertyKey(ObjectProperties, 11);
        public static readonly PropertyKey ObjectOriginalFileName = new PropertyKey(ObjectProperties, 12);
        public static readonly PropertyKey ObjectDateModified = new PropertyKey(ObjectProperties, 19);

        public static readonly PropertyKey FunctionalObjectCategory = new PropertyKey(new Guid(0x8f052d93, 0xabca, 0x4fc5, 0xa5, 0xac, 0xb0, 0x1d, 0xf4, 0xdb, 0xe5, 0x98), 2);

        public static readonly PropertyKey StorageCapacity = new PropertyKey(StorageProperties, 4);
        public static readonly PropertyKey StorageFreeSpaceInBytes = new PropertyKey(StorageProperties, 5);
        public static readonly PropertyKey StorageDescription = new PropertyKey(StorageProperties, 7);

        public static readonly PropertyKey DeviceProtocol = new PropertyKey(DeviceProperties, 6);
        public static readonly PropertyKey DeviceManufacturer = new PropertyKey(DeviceProperties, 7);
        public static readonly PropertyKey DeviceModel = new PropertyKey(DeviceProperties, 8);
        public static readonly PropertyKey DeviceFriendlyName = new PropertyKey(DeviceProperties, 12);

        public static readonly PropertyKey ClientName = new PropertyKey(ClientProperties, 2);
        public static readonly PropertyKey ClientMajorVersion = new PropertyKey(ClientProperties, 3);
        public static readonly PropertyKey ClientMinorVersion = new PropertyKey(ClientProperties, 4);
        public static readonly PropertyKey ClientRevision = new PropertyKey(ClientProperties, 5);
        public static readonly PropertyKey ClientSecurityQualityOfService = new PropertyKey(ClientProperties, 8);
        public static readonly PropertyKey ClientDesiredAccess = new PropertyKey(ClientProperties, 9);

        public static readonly PropertyKey ResourceDefault = new PropertyKey(new Guid(0xe81e79be, 0x34f0, 0x41bf, 0xb5, 0x3f, 0xf1, 0xa0, 0x6a, 0xe8, 0x78, 0x42), 0);

        public static readonly Guid ContentTypeFunctionalObject = new Guid(0x99ed0160, 0x17ff, 0x4c44, 0x9d, 0x98, 0x1d, 0x7a, 0x6f, 0x94, 0x19, 0x21);
        public static readonly Guid ContentTypeFolder = new Guid(0x27e2e392, 0xa111, 0x48e0, 0xab, 0x0c, 0xe1, 0x77, 0x05, 0xa0, 0x5f, 0x85);
        public static readonly Guid ContentTypeGenericFile = new Guid(0x0085e0a6, 0x8d34, 0x45d7, 0xbc, 0x5c, 0x44, 0x7e, 0x59, 0xc7, 0x3d, 0x48);
        public static readonly Guid FunctionalCategoryStorage = new Guid(0x23f05bbc, 0x15de, 0x4c2a, 0xa5, 0x5b, 0xa9, 0xaf, 0x5c, 0xe4, 0x12, 0xef);
        public static readonly Guid ObjectFormatUnspecified = new Guid(0x30000000, 0xae6c, 0x4804, 0x98, 0xba, 0xc5, 0x7b, 0x46, 0x96, 0x5f, 0xe7);

        /// <summary>WPD_DEVICE_OBJECT_ID: the root object of every device.</summary>
        public const string DeviceObjectId = "DEVICE";

        public const uint StgmRead = 0x00000000;
        public const uint StgcDefault = 0;
        public const uint DeleteNoRecursion = 0;
        public const uint DeleteWithRecursion = 1;
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint SecurityImpersonation = 0x00020000;

        public const ushort VtLpwstr = 31;
        public const ushort VtDate = 7;
        public const ushort VtFiletime = 64;

        public const int EAccessDenied = unchecked((int)0x80070005);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant value);

        public static T Create<T>(Guid clsid) where T : class =>
            (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true));
    }
}
