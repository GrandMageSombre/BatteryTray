using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryTray
{
    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint ULW_ALPHA = 0x00000002;

        public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public static readonly Guid HidInterfaceGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public ushort[] Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public ushort[] Union;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int CX;
            public int CY;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, int detailDataSize, ref int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteFile")]
        public static extern bool WriteFile(IntPtr handle, IntPtr buffer, int bytesToWrite, IntPtr bytesWritten, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
        public static extern bool ReadFile(IntPtr handle, IntPtr buffer, int bytesToRead, IntPtr bytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped, out int bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIo(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterDeviceNotification(IntPtr recipient,
            ref DEV_BROADCAST_DEVICEINTERFACE filter, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterDeviceNotification(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref POINT destination,
            ref SIZE size, IntPtr sourceDc, ref POINT source, IntPtr colorKey, ref BLENDFUNCTION blend, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr gdiObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static bool IsValidHandle(IntPtr handle)
        {
            return handle != IntPtr.Zero && handle != new IntPtr(-1);
        }
    }
}
