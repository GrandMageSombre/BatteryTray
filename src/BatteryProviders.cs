using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace BatteryTray
{
    internal interface IBatteryProvider
    {
        string Name { get; }
        void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot);
    }

    internal static class VendorCatalog
    {
        public static string GetVendorName(ushort vid)
        {
            switch (vid)
            {
                case 0x046D: return "罗技";
                case 0x26CE: return "SONiX";
                case 0x1532: return "雷蛇";
                case 0x1038: return "赛睿";
                case 0x1B1C: return "海盗船";
                case 0x1E7D: return "ROCCAT";
                case 0x24AE: return "雷柏";
                case 0x09DA: return "双飞燕";
                case 0x045E: return "微软";
                default: return null;
            }
        }
    }

    internal static class DeviceClassifier
    {
        public static string CategoryName(DeviceCategory category)
        {
            switch (category)
            {
                case DeviceCategory.Gamepad: return "手柄";
                case DeviceCategory.Mouse: return "鼠标";
                case DeviceCategory.Keyboard: return "键盘";
                case DeviceCategory.Headset: return "耳机";
                default: return "设备";
            }
        }

        public static string ConnectionName(DeviceConnection connection)
        {
            switch (connection)
            {
                case DeviceConnection.UsbReceiver: return "2.4G";
                case DeviceConnection.Bluetooth: return "蓝牙";
                case DeviceConnection.UsbWired: return "USB 有线";
                default: return "未知连接";
            }
        }

        public static string ReceiverKey(HidInterfaceInfo info)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:X4}:{1:X4}", info.Vid, info.Pid);
        }

        public static DeviceCategory FromHidUsage(ushort usagePage, ushort usage)
        {
            if (usagePage == 0x01)
            {
                if (usage == 0x02) return DeviceCategory.Mouse;
                if (usage == 0x06 || usage == 0x07) return DeviceCategory.Keyboard;
                if (usage == 0x04 || usage == 0x05 || usage == 0x08) return DeviceCategory.Gamepad;
            }
            if (usagePage == 0x0B && usage == 0x05) return DeviceCategory.Headset;
            return DeviceCategory.Other;
        }

        public static DeviceCategory FromProductName(string name)
        {
            if (string.IsNullOrEmpty(name)) return DeviceCategory.Other;
            string value = name.ToLowerInvariant();

            if (ContainsAny(value, "headset", "headphone", "earbud", "earphone", "buds", "airpods", "耳机", "耳麦", "qcy", "soundcore", "freebuds", "jabra", "turtle beach", "arctis"))
                return DeviceCategory.Headset;
            if (ContainsAny(value, "gamepad", "game controller", "joystick", "手柄", "xbox", "dualsense", "dualshock", "8bitdo", "北通", "飞智", "betop"))
                return DeviceCategory.Gamepad;
            if (ContainsAny(value, "keyboard", "键盘"))
                return DeviceCategory.Keyboard;
            if (ContainsAny(value, "mouse", "鼠标"))
                return DeviceCategory.Mouse;
            return DeviceCategory.Other;
        }

        public static bool IsVirtualDevice(HidInterfaceInfo info)
        {
            if (info == null) return false;
            string path = info.Path ?? "";
            if (path.IndexOf("vhf", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string product = info.Product ?? "";
            if (product.IndexOf("虚拟", StringComparison.Ordinal) >= 0) return true;
            if (product.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static DeviceConnection ClassifyConnection(HidInterfaceInfo info, List<HidInterfaceInfo> group)
        {
            string path = info.Path ?? "";
            if (path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("bth", StringComparison.OrdinalIgnoreCase) >= 0)
                return DeviceConnection.Bluetooth;

            if (HasVendorCollection(group))
                return DeviceConnection.UsbReceiver;

            return DeviceConnection.UsbWired;
        }

        public static bool HasVendorCollection(List<HidInterfaceInfo> group)
        {
            if (group == null) return false;
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].UsagePage == 0xFF00) return true;
            }
            return false;
        }

        public static DeviceBattery BestForCategory(List<DeviceBattery> devices, DeviceCategory category)
        {
            DeviceBattery best = null;
            if (devices == null) return null;
            for (int i = 0; i < devices.Count; i++)
            {
                DeviceBattery device = devices[i];
                if (device.Category != category) continue;
                if (best == null)
                {
                    best = device;
                    continue;
                }
                if (device.Supported && device.Percent >= 0 &&
                    (!best.Supported || best.Percent < 0 || device.Percent < best.Percent))
                    best = device;
            }
            return best;
        }

        private static bool ContainsAny(string value, params string[] keywords)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                if (value.IndexOf(keywords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }

    internal sealed class GenericHidBatteryProvider : IBatteryProvider
    {
        private const int HidpFeature = 2;
        private const int HidpStatusSuccess = 0x00110000;
        private const ushort GenericDeviceControlsPage = 0x06;
        private const ushort BatteryStrengthUsage = 0x20;

        public string Name
        {
            get { return "标准 HID 电池"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, List<HidInterfaceInfo>> groups = BatteryReader.GroupReceivers(interfaces);
            HashSet<string> seenReceivers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                if (info.FeatureReportLength <= 0) continue;
                if (info.Path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string receiverKey = DeviceClassifier.ReceiverKey(info);
                if (seenReceivers.Contains(receiverKey)) continue;

                List<HidInterfaceInfo> group;
                groups.TryGetValue(receiverKey, out group);
                if (info.Vid == 0x046D && DeviceClassifier.HasVendorCollection(group)) continue;

                int percent;
                string detail;
                if (!TryReadBattery(info, out percent, out detail)) continue;

                DeviceCategory category = DeviceClassifier.FromHidUsage(info.UsagePage, info.Usage);
                if (category == DeviceCategory.Other && !string.IsNullOrEmpty(info.Product))
                    category = DeviceClassifier.FromProductName(info.Product);

                string vendor = VendorCatalog.GetVendorName(info.Vid);
                string vendorLabel = vendor != null ? vendor : "未知品牌";
                string categoryName = DeviceClassifier.CategoryName(category);

                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = receiverKey + ":hidbattery",
                    Label = vendorLabel + " " + categoryName,
                    ShortLabel = vendorLabel + categoryName,
                    Category = category,
                    Connection = DeviceClassifier.ClassifyConnection(info, group),
                    Supported = true,
                    Percent = percent,
                    Detail = detail
                });
                seenReceivers.Add(receiverKey);
            }
        }

        private static bool TryReadBattery(HidInterfaceInfo info, out int percent, out string detail)
        {
            percent = -1;
            detail = "";

            IntPtr handle = Native.CreateFile(info.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (!Native.IsValidHandle(handle)) return false;

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!Native.HidD_GetPreparsedData(handle, out preparsed)) return false;

                Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                caps.Reserved = new ushort[17];
                caps.Reserved2 = new ushort[10];
                if (Native.HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess) return false;
                if (caps.FeatureReportByteLength == 0) return false;

                int capSize = Marshal.SizeOf(typeof(Native.HIDP_VALUE_CAPS));
                ushort capCount = 64;
                IntPtr capsBuffer = Marshal.AllocHGlobal(capSize * capCount);
                try
                {
                    int status = Native.HidP_GetValueCaps(HidpFeature, capsBuffer, ref capCount, preparsed);
                    if (status != HidpStatusSuccess) return false;

                    for (int i = 0; i < capCount; i++)
                    {
                        Native.HIDP_VALUE_CAPS cap = (Native.HIDP_VALUE_CAPS)Marshal.PtrToStructure(
                            new IntPtr(capsBuffer.ToInt64() + i * capSize), typeof(Native.HIDP_VALUE_CAPS));
                        if (cap.UsagePage != GenericDeviceControlsPage || cap.BitSize == 0 || cap.ReportCount == 0)
                            continue;

                        bool match;
                        if (cap.IsRange != 0)
                        {
                            match = cap.Union != null && cap.Union.Length >= 2 &&
                                cap.Union[0] <= BatteryStrengthUsage && cap.Union[1] >= BatteryStrengthUsage;
                        }
                        else
                        {
                            match = cap.Union != null && cap.Union.Length >= 1 &&
                                cap.Union[0] == BatteryStrengthUsage;
                        }
                        if (!match) continue;

                        byte[] report = new byte[caps.FeatureReportByteLength];
                        report[0] = cap.ReportID;
                        if (!Native.HidD_GetFeature(handle, report, report.Length)) continue;

                        uint value;
                        int valueStatus = Native.HidP_GetUsageValue(HidpFeature, GenericDeviceControlsPage,
                            cap.LinkCollection, BatteryStrengthUsage, out value, preparsed, report, (uint)report.Length);
                        if (valueStatus != HidpStatusSuccess) continue;

                        percent = NormalizePercent(value, cap.LogicalMin, cap.LogicalMax);
                        detail = "标准 HID 电池字段（Usage 0x06/0x20，报告 ID " +
                            cap.ReportID.ToString(CultureInfo.InvariantCulture) + "）";
                        return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(capsBuffer);
                }
            }
            finally
            {
                if (preparsed != IntPtr.Zero) Native.HidD_FreePreparsedData(preparsed);
                Native.CloseHandle(handle);
            }

            return false;
        }

        private static int NormalizePercent(uint value, int logicalMin, int logicalMax)
        {
            int percent;
            if (logicalMax > logicalMin)
            {
                long range = (long)logicalMax - logicalMin;
                long scaled = ((long)value - logicalMin) * 100L;
                percent = (int)(scaled / range);
            }
            else
            {
                percent = (int)value;
            }

            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return percent;
        }
    }

    internal sealed class DevicePresenceProvider : IBatteryProvider
    {
        public string Name
        {
            get { return "HID 设备识别"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, List<HidInterfaceInfo>> groups = BatteryReader.GroupReceivers(interfaces);
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                if (DeviceClassifier.IsVirtualDevice(info)) continue;

                DeviceCategory category = DeviceClassifier.FromHidUsage(info.UsagePage, info.Usage);
                if (category == DeviceCategory.Other && !string.IsNullOrEmpty(info.Product))
                    category = DeviceClassifier.FromProductName(info.Product);
                if (category == DeviceCategory.Other) continue;

                string receiverKey = DeviceClassifier.ReceiverKey(info);
                if (HasCategory(snapshot, receiverKey, category)) continue;

                List<HidInterfaceInfo> group;
                groups.TryGetValue(receiverKey, out group);
                DeviceConnection connection = DeviceClassifier.ClassifyConnection(info, group);
                string vendor = VendorCatalog.GetVendorName(info.Vid);
                string vendorLabel = vendor != null ? vendor : "未知品牌";
                string categoryName = DeviceClassifier.CategoryName(category);

                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = receiverKey + ":presence:" + category,
                    Label = vendorLabel + " " + categoryName,
                    ShortLabel = vendorLabel + categoryName,
                    Category = category,
                    Connection = connection,
                    Supported = false,
                    Percent = -1,
                    Detail = DeviceClassifier.ConnectionName(connection) + "；未读取到电量"
                });
            }
        }

        private static bool HasCategory(BatterySnapshot snapshot, string receiverKey, DeviceCategory category)
        {
            string prefix = receiverKey + ":";
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (snapshot.Devices[i].Category == category &&
                    snapshot.Devices[i].Key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    internal sealed class XInputGamepadProvider : IBatteryProvider
    {
        public string Name
        {
            get { return "XInput 手柄"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (snapshot.Devices[i].Category == DeviceCategory.Gamepad) return;
            }

            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    XINPUT_STATE state;
                    if (XInputGetState(i, out state) != 0) continue;
                    snapshot.Devices.Add(new DeviceBattery
                    {
                        Key = "xinput:" + i.ToString(CultureInfo.InvariantCulture),
                        Label = "Xbox 手柄",
                        ShortLabel = "Xbox手柄",
                        Category = DeviceCategory.Gamepad,
                        Connection = DeviceConnection.Unknown,
                        Supported = false,
                        Percent = -1,
                        Detail = "XInput 已连接；未提供电量"
                    });
                    return;
                }
            }
            catch
            {
            }
        }

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(uint index, out XINPUT_STATE state);

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LeftThumbX;
            public short LeftThumbY;
            public short RightThumbX;
            public short RightThumbY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint PacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }
    }

    internal sealed class BluetoothDeviceProvider : IBatteryProvider
    {
        private static readonly object CacheLock = new object();
        private static List<BluetoothEntry> _cache;
        private static DateTime _cacheAt = DateTime.MinValue;

        private sealed class BluetoothEntry
        {
            public string Name;
            public string Id;
        }

        public string Name
        {
            get { return "蓝牙设备"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            List<BluetoothEntry> entries = GetDevices();
            for (int i = 0; i < entries.Count; i++)
            {
                BluetoothEntry entry = entries[i];
                DeviceCategory category = DeviceClassifier.FromProductName(entry.Name);
                if (category == DeviceCategory.Other) continue;
                if (HasBluetoothCategory(snapshot, category)) continue;

                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = "bth:" + entry.Id + ":" + category,
                    Label = entry.Name,
                    ShortLabel = "蓝牙" + DeviceClassifier.CategoryName(category),
                    Category = category,
                    Connection = DeviceConnection.Bluetooth,
                    Supported = false,
                    Percent = -1,
                    Detail = "蓝牙设备已识别；当前工具链未接入 BLE GATT 电量"
                });
            }
        }

        private static bool HasBluetoothCategory(BatterySnapshot snapshot, DeviceCategory category)
        {
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (snapshot.Devices[i].Connection == DeviceConnection.Bluetooth &&
                    snapshot.Devices[i].Category == category)
                    return true;
            }
            return false;
        }

        private static List<BluetoothEntry> GetDevices()
        {
            lock (CacheLock)
            {
                if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalMilliseconds < 30000)
                    return _cache;

                List<BluetoothEntry> entries = new List<BluetoothEntry>();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass='Bluetooth' AND ConfigManagerErrorCode=0"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                string name = item["Name"] as string;
                                string id = item["DeviceID"] as string;
                                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
                                if (IsServiceName(name)) continue;
                                if (!names.Add(name)) continue;
                                entries.Add(new BluetoothEntry { Name = name, Id = id });
                            }
                        }
                    }
                }
                catch
                {
                }

                _cache = entries;
                _cacheAt = DateTime.UtcNow;
                return entries;
            }
        }

        private static bool IsServiceName(string name)
        {
            string value = name.ToLowerInvariant();
            if (value.IndexOf("服务", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("service", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("gatt", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("协议", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("profile", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("enumerator", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("rfcomm", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("avrcp", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("hfp", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("a2dp", StringComparison.Ordinal) >= 0) return true;
            if (value.IndexOf("standard serial", StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
