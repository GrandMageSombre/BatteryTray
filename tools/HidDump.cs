// BatteryTray HID 诊断工具（单文件，无依赖）
//
// 用途：在无法读到电量的电脑上运行，导出所有 HID 接口的品牌、型号、
//       报告长度和报告描述符里的 Usage，用于判断设备是否暴露了标准电池字段，
//       以及接收器是否有可识别的厂商自定义页。
//
// 编译（在仓库根目录）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 ^
//     /target:exe /out:HidDump.exe tools\HidDump.cs
//
// 运行：
//   HidDump.exe > hid-dump.txt
//
// 输出文件里没有隐私内容，只有设备的 VID/PID、名称和 HID 描述符信息。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal static class HidDump
{
    private static readonly Guid HidInterfaceGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_INPUT = 0;
    private const int HIDP_OUTPUT = 1;
    private const int HIDP_FEATURE = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
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
    private struct HIDP_VALUE_CAPS
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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, ref int required, IntPtr info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr device, StringBuilder buffer, int length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort count, IntPtr preparsed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        List<string> groups = new List<string>();

        Guid guid = HidInterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            Console.WriteLine("无法枚举 HID 设备");
            return;
        }

        try
        {
            SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string path = GetPath(set, ref data);
                if (string.IsNullOrEmpty(path)) continue;

                ushort vid, pid;
                ParseVidPid(path, out vid, out pid);
                DumpInterface(path, vid, pid, groups);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        Console.WriteLine();
        Console.WriteLine("=== 按 VID:PID 分组（BatteryTray 的连接方式判断以组为单位）===");
        foreach (string line in groups) Console.WriteLine(line);
    }

    private static void DumpInterface(string path, ushort vid, ushort pid, List<string> groups)
    {
        IntPtr handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

        try
        {
            HIDP_CAPS caps = new HIDP_CAPS();
            caps.Reserved = new ushort[17];
            caps.Reserved2 = new ushort[10];

            IntPtr preparsed;
            if (!HidD_GetPreparsedData(handle, out preparsed)) return;
            try
            {
                HidP_GetCaps(preparsed, ref caps);

                StringBuilder product = new StringBuilder(256);
                HidD_GetProductString(handle, product, 512);
                StringBuilder manufacturer = new StringBuilder(256);
                HidD_GetManufacturerString(handle, manufacturer, 512);

                Console.WriteLine("------------------------------------------------------------");
                Console.WriteLine(string.Format("VID:PID  {0:X4}:{1:X4}", vid, pid));
                Console.WriteLine("产品名   " + product.ToString().Trim());
                Console.WriteLine("厂商     " + manufacturer.ToString().Trim());
                Console.WriteLine(string.Format("用途页   {0:X4} / Usage {1:X4}", caps.UsagePage, caps.Usage));
                Console.WriteLine(string.Format("报告长度 Input={0} Output={1} Feature={2}",
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                Console.WriteLine("路径     " + path);

                bool battery = false;
                battery |= DumpValueCaps(preparsed, HIDP_FEATURE, "Feature");
                battery |= DumpValueCaps(preparsed, HIDP_INPUT, "Input");
                if (battery)
                    Console.WriteLine("=> 该接口暴露标准 HID 电池字段（UsagePage 0x06 / Usage 0x20）");
                else if (caps.UsagePage >= 0xFF00)
                    Console.WriteLine("=> 厂商自定义页接口（常见于 2.4G 接收器配置通道）");
                else
                    Console.WriteLine("=> 未发现标准电池字段");

                groups.Add(string.Format("{0:X4}:{1:X4}  usagePage={2:X4}  product={3}",
                    vid, pid, caps.UsagePage, product.ToString().Trim()));
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool DumpValueCaps(IntPtr preparsed, int reportType, string label)
    {
        int size = Marshal.SizeOf(typeof(HIDP_VALUE_CAPS));
        ushort count = 64;
        IntPtr buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            if (HidP_GetValueCaps(reportType, buffer, ref count, preparsed) != 0x00110000) return false;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                HIDP_VALUE_CAPS cap = (HIDP_VALUE_CAPS)Marshal.PtrToStructure(
                    new IntPtr(buffer.ToInt64() + i * size), typeof(HIDP_VALUE_CAPS));
                if (cap.UsagePage != 0x06) continue;
                ushort usage = cap.IsRange != 0 && cap.Union != null && cap.Union.Length >= 2
                    ? cap.Union[0] : (cap.Union != null && cap.Union.Length >= 1 ? cap.Union[0] : (ushort)0);
                if (usage != 0x20) continue;
                Console.WriteLine(string.Format("  [{0}] 电池字段 reportId={1} bitSize={2} logical={3}..{4}",
                    label, cap.ReportID, cap.BitSize, cap.LogicalMin, cap.LogicalMax));
                found = true;
            }
            return found;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        int required = 0;
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required <= 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, ref required, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringAuto(new IntPtr(buffer.ToInt64() + 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        string upper = path.ToUpperInvariant();
        int vi = upper.IndexOf("VID_", StringComparison.Ordinal);
        int pi = upper.IndexOf("PID_", StringComparison.Ordinal);
        if (vi >= 0 && vi + 8 <= upper.Length)
            ushort.TryParse(upper.Substring(vi + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
        if (pi >= 0 && pi + 8 <= upper.Length)
            ushort.TryParse(upper.Substring(pi + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
    }
}
