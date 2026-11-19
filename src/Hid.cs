using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryPatch
{
    /// <summary>One HID collection as Windows sees it (a device can expose several).</summary>
    sealed class HidInfo
    {
        public string Path, Product;
        public ushort Vid, Pid, UsagePage, Usage;
        public int InputLen, OutputLen;
    }

    static class Hid
    {
        public static List<HidInfo> Enumerate()
        {
            var result = new List<HidInfo>();
            Native.HidD_GetHidGuid(out Guid guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA)) };
                for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    string path = InterfacePath(set, ref data);
                    HidInfo info = path == null ? null : Describe(path);
                    if (info != null) result.Add(info);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        static string InterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data)
        {
            Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, mem, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(mem + 4);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        static HidInfo Describe(string path)
        {
            // Access 0 is enough to read the description.
            using (SafeFileHandle h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES)) };
                if (!Native.HidD_GetAttributes(h, ref attr)) return null;
                if (!Native.HidD_GetPreparsedData(h, out IntPtr pre)) return null;
                try
                {
                    if (Native.HidP_GetCaps(pre, out Native.HIDP_CAPS caps) != Native.HIDP_STATUS_SUCCESS) return null;
                    var sb = new StringBuilder(128);
                    string product = Native.HidD_GetProductString(h, sb, sb.Capacity * 2) ? sb.ToString().Trim() : "";
                    return new HidInfo
                    {
                        Path = path, Product = product, Vid = attr.VendorID, Pid = attr.ProductID,
                        UsagePage = caps.UsagePage, Usage = caps.Usage,
                        InputLen = caps.InputReportByteLength, OutputLen = caps.OutputReportByteLength,
                    };
                }
                finally { Native.HidD_FreePreparsedData(pre); }
            }
        }
    }

    /// <summary>An open HID collection, with time-limited reads and writes.</summary>
    sealed class HidDevice : IDisposable
    {
        public readonly HidInfo Info;
        readonly SafeFileHandle handle;

        HidDevice(HidInfo info, SafeFileHandle h) { Info = info; handle = h; }

        public static HidDevice Open(HidInfo info)
        {
            SafeFileHandle h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidDevice(info, h);
        }

        public void Flush() => Native.HidD_FlushQueue(handle);
        public bool Write(byte[] data, int timeoutMs = 300) => Transfer(true, data, timeoutMs) == data.Length;
        public int Read(byte[] buffer, int timeoutMs) => Transfer(false, buffer, timeoutMs);

        int Transfer(bool write, byte[] buffer, int timeoutMs)
        {
            using (var done = new ManualResetEvent(false))
            {
                IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, ov, false);
                    IntPtr p = pin.AddrOfPinnedObject();
                    bool ok = write ? Native.WriteFile(handle, p, buffer.Length, out _, ov) : Native.ReadFile(handle, p, buffer.Length, out _, ov);
                    if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;
                    if (!done.WaitOne(timeoutMs))
                    {
                        Native.CancelIoEx(handle, ov);
                        Native.GetOverlappedResult(handle, ov, out _, true); // wait for the cancel before freeing the buffer
                        return 0;
                    }
                    return Native.GetOverlappedResult(handle, ov, out int n, false) ? n : -1;
                }
                finally { pin.Free(); Marshal.FreeHGlobal(ov); }
            }
        }

        public void Dispose() => handle.Dispose();
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997, HIDP_STATUS_SUCCESS = 0x00110000, DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int size, out int read, IntPtr overlapped);
        [DllImport("kernel32", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int size, out int written, IntPtr overlapped);
        [DllImport("kernel32", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32", SetLastError = true)] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder s, int bytes);
        [DllImport("hid")] public static extern bool HidD_FlushQueue(SafeFileHandle h);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid cls, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
