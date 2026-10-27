using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

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
                case 0x2DC8: return "八位堂";
                case 0x249A: return "黑爵";
                case 0x248A: return "黑爵";
                default: return null;
            }
        }

        /// <summary>先查已知设备档案，再查 VID 表，最后退回产品名关键词。</summary>
        public static string GetVendorName(HidInterfaceInfo info)
        {
            if (info == null) return null;
            KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
            if (known != null) return known.Vendor;
            string name = GetVendorName(info.Vid);
            if (name != null) return name;
            return KnownDevices.VendorFromProduct(info.Product);
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
            // 先判断明确的键盘/鼠标词，再看手柄词：否则 "8BitDo Retro Keyboard"
            // 会因为品牌词命中手柄，被误判成游戏手柄。
            if (ContainsAny(value, "keyboard", "键盘", "keypad"))
                return DeviceCategory.Keyboard;
            if (ContainsAny(value, "mouse", "鼠标"))
                return DeviceCategory.Mouse;
            if (ContainsAny(value, "gamepad", "game controller", "joystick", "手柄", "xbox", "dualsense", "dualshock", "8bitdo", "北通", "飞智", "betop"))
                return DeviceCategory.Gamepad;
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

            // 有公开档案的 VID/PID 直接按档案判定：接收器和键盘本体通常 PID 不同，
            // 例如八位堂 Retro Keyboard 有线是 5200、2.4G 接收器是 5201。
            KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
            if (known != null) return known.Connection;

            if (IsWiredProductName(info.Product))
                return DeviceConnection.UsbWired;

            if (HasWirelessProductName(info, group))
                return DeviceConnection.UsbReceiver;

            // 罗技有线设备同样带 0xFF00 厂商集合（HID++ 配置通道），
            // 不能据此判成 2.4G，只能靠接收器 PID 白名单区分。
            if (info.Vid == LogitechVendorId)
                return IsLogitechReceiver(info.Pid) ? DeviceConnection.UsbReceiver : DeviceConnection.UsbWired;

            if (HasVendorCollection(group))
                return DeviceConnection.UsbReceiver;

            return DeviceConnection.UsbWired;
        }

        private const ushort LogitechVendorId = 0x046D;

        /// <summary>罗技接收器的已知 PID：Unifying（C52B/C532/C534/C539）、Nano（C52F）、Bolt（C548）。</summary>
        private static bool IsLogitechReceiver(ushort pid)
        {
            switch (pid)
            {
                case 0xC52B:
                case 0xC532:
                case 0xC534:
                case 0xC539:
                case 0xC52F:
                case 0xC548:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWiredProductName(string product)
        {
            if (string.IsNullOrEmpty(product)) return false;
            string value = product.ToLowerInvariant();
            return ContainsAny(value, "wired", "有线");
        }

        private static bool HasWirelessProductName(HidInterfaceInfo info, List<HidInterfaceInfo> group)
        {
            if (IsWirelessName(info.Product)) return true;
            if (group != null)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (IsWirelessName(group[i].Product)) return true;
                }
            }
            return false;
        }

        private static bool IsWirelessName(string product)
        {
            if (string.IsNullOrEmpty(product)) return false;
            string value = product.ToLowerInvariant();
            return ContainsAny(value, "2.4g", "2.4ghz", "receiver", "dongle", "wireless",
                "unifying", "lightspeed", "接收器", "无线");
        }

        public static bool HasVendorCollection(List<HidInterfaceInfo> group)
        {
            if (group == null) return false;
            for (int i = 0; i < group.Count; i++)
            {
                // 厂商自定义页是整个 0xFF00–0xFFFF 区间，不只是罗技常用的 0xFF00。
                if (group[i].UsagePage >= 0xFF00) return true;
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
                if (device.Supported && device.DisplayPercent >= 0 &&
                    (!best.Supported || best.DisplayPercent < 0 || device.DisplayPercent < best.DisplayPercent))
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
        private const int HidpInput = 0;
        private const int HidpFeature = 2;
        private const int HidpStatusSuccess = 0x00110000;
        private const ushort GenericDeviceControlsPage = 0x06;
        private const ushort BatteryStrengthUsage = 0x20;

        // HidD_GetInputReport 是同步调用，设备不响应时会卡住整轮轮询。
        // 失败后 5 分钟内不再对该接口重试，避免每秒阻塞一次。
        private static readonly object InputSkipLock = new object();
        private static readonly Dictionary<string, DateTime> InputSkipUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public string Name
        {
            get { return "标准 HID 电池"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, List<HidInterfaceInfo>> groups = BatteryReader.GroupReceivers(interfaces);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                // 电量既可能在 Feature 报告，也可能在 Input 报告，两者都不能漏。
                if (info.FeatureReportLength <= 0 && info.InputReportLength <= 0) continue;
                if (info.Path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string receiverKey = DeviceClassifier.ReceiverKey(info);
                List<HidInterfaceInfo> group;
                groups.TryGetValue(receiverKey, out group);
                if (info.Vid == 0x046D && DeviceClassifier.HasVendorCollection(group)) continue;

                // 已适配私有协议的设备不重复走通用通道。
                KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
                if (known != null && known.HasBatteryProtocol) continue;

                int percent;
                string detail;
                if (!TryReadBattery(info, out percent, out detail)) continue;

                DeviceCategory category = DeviceClassifier.FromHidUsage(info.UsagePage, info.Usage);
                if (category == DeviceCategory.Other && !string.IsNullOrEmpty(info.Product))
                    category = DeviceClassifier.FromProductName(info.Product);

                // 按"接收器 + 类别"去重：同一个接收器下的键盘和鼠标都要保留。
                if (!seen.Add(receiverKey + ":" + category)) continue;
                if (HasSupportedDevice(snapshot, receiverKey, category)) continue;

                string vendor = VendorCatalog.GetVendorName(info);
                string vendorLabel = vendor != null ? vendor : "未知品牌";
                string categoryName = DeviceClassifier.CategoryName(category);

                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = receiverKey + ":hidbattery:" + category,
                    Label = vendorLabel + " " + categoryName,
                    ShortLabel = vendorLabel + categoryName,
                    Category = category,
                    Connection = DeviceClassifier.ClassifyConnection(info, group),
                    Supported = true,
                    Percent = percent,
                    Detail = detail
                });
            }
        }

        private static bool HasSupportedDevice(BatterySnapshot snapshot, string receiverKey, DeviceCategory category)
        {
            string prefix = receiverKey + ":";
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceBattery device = snapshot.Devices[i];
                if (device.Supported && device.Category == category &&
                    device.Key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
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

                if (caps.FeatureReportByteLength > 0 &&
                    TryReadFromReports(handle, preparsed, HidpFeature, caps.FeatureReportByteLength, out percent, out detail))
                    return true;

                if (caps.InputReportByteLength > 0 && ShouldTryInput(info.Path))
                {
                    if (TryReadFromReports(handle, preparsed, HidpInput, caps.InputReportByteLength, out percent, out detail))
                        return true;
                    MarkInputUnavailable(info.Path);
                }

                return false;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) Native.HidD_FreePreparsedData(preparsed);
                Native.CloseHandle(handle);
            }
        }

        private static bool ShouldTryInput(string path)
        {
            lock (InputSkipLock)
            {
                DateTime until;
                if (InputSkipUntil.TryGetValue(path, out until) && DateTime.UtcNow < until)
                    return false;
                return true;
            }
        }

        private static void MarkInputUnavailable(string path)
        {
            lock (InputSkipLock)
            {
                InputSkipUntil[path] = DateTime.UtcNow.AddMinutes(5);
            }
        }

        private static bool TryReadFromReports(IntPtr handle, IntPtr preparsed, int reportType,
            int reportLength, out int percent, out string detail)
        {
            percent = -1;
            detail = "";

            int capSize = Marshal.SizeOf(typeof(Native.HIDP_VALUE_CAPS));
            ushort capCount = 64;
            IntPtr capsBuffer = Marshal.AllocHGlobal(capSize * capCount);
            try
            {
                int status = Native.HidP_GetValueCaps(reportType, capsBuffer, ref capCount, preparsed);
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

                    byte[] report = new byte[reportLength];
                    report[0] = cap.ReportID;
                    bool read = reportType == HidpFeature
                        ? Native.HidD_GetFeature(handle, report, report.Length)
                        : Native.HidD_GetInputReport(handle, report, report.Length);
                    if (!read) continue;

                    uint value;
                    int valueStatus = Native.HidP_GetUsageValue(reportType, GenericDeviceControlsPage,
                        cap.LinkCollection, BatteryStrengthUsage, out value, preparsed, report, (uint)report.Length);
                    if (valueStatus != HidpStatusSuccess) continue;

                    percent = NormalizePercent(value, cap.LogicalMin, cap.LogicalMax);
                    detail = "标准 HID 电池字段（Usage 0x06/0x20，" +
                        (reportType == HidpFeature ? "Feature" : "Input") + " 报告 ID " +
                        cap.ReportID.ToString(CultureInfo.InvariantCulture) + "）";
                    return true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(capsBuffer);
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
                string vendor = VendorCatalog.GetVendorName(info);
                string vendorLabel = vendor != null ? vendor : "未知品牌";
                string categoryName = DeviceClassifier.CategoryName(category);
                string detail = DeviceClassifier.ConnectionName(connection) + "；未读取到电量";
                KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
                if (known != null && !string.IsNullOrEmpty(known.BatteryNote))
                    detail = DeviceClassifier.ConnectionName(connection) + "；" + known.BatteryNote;
                else if (vendor == "罗技" && category == DeviceCategory.Mouse)
                    detail = DeviceClassifier.ConnectionName(connection) + "；该鼠标未提供电量上报（HID++ 2.0 特性表与 1.0 寄存器均不可用）";

                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = receiverKey + ":presence:" + category,
                    Label = vendorLabel + " " + categoryName,
                    ShortLabel = vendorLabel + categoryName,
                    Category = category,
                    Connection = connection,
                    Supported = false,
                    Percent = -1,
                    Detail = detail
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

    internal sealed class BleGattBatteryProvider : IBatteryProvider
    {
        private static readonly object CacheLock = new object();
        private static Dictionary<string, int> _cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _cacheAt = DateTime.MinValue;
        private static int _scanning;

        public string Name
        {
            get { return "BLE GATT 电池"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, int> cache;
            lock (CacheLock)
            {
                if ((DateTime.UtcNow - _cacheAt).TotalSeconds > 120)
                    _cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                cache = _cache;
            }

            foreach (KeyValuePair<string, int> pair in cache)
            {
                DeviceBattery existing = null;
                for (int i = 0; i < snapshot.Devices.Count; i++)
                {
                    DeviceBattery device = snapshot.Devices[i];
                    if (device.Connection == DeviceConnection.Bluetooth &&
                        (string.Equals(device.Label, pair.Key, StringComparison.OrdinalIgnoreCase) ||
                         device.Label.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        existing = device;
                        break;
                    }
                }

                if (existing != null)
                {
                    existing.Supported = true;
                    existing.Percent = pair.Value;
                    existing.Detail = "BLE GATT 电池服务（0x2A19）";
                    continue;
                }

                DeviceCategory category = DeviceClassifier.FromProductName(pair.Key);
                if (category == DeviceCategory.Other) continue;
                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = "ble:" + pair.Key + ":" + category,
                    Label = pair.Key,
                    ShortLabel = "蓝牙" + DeviceClassifier.CategoryName(category),
                    Category = category,
                    Connection = DeviceConnection.Bluetooth,
                    Supported = true,
                    Percent = pair.Value,
                    Detail = "BLE GATT 电池服务（0x2A19）"
                });
            }

            EnsureScan();
        }

        private static void EnsureScan()
        {
            lock (CacheLock)
            {
                if (_scanning == 1 || (DateTime.UtcNow - _cacheAt).TotalSeconds < 30) return;
                _scanning = 1;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, int> results;
                try
                {
                    results = Scan();
                }
                catch
                {
                    results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                lock (CacheLock)
                {
                    _cache = results;
                    _cacheAt = DateTime.UtcNow;
                    _scanning = 0;
                }
            });
        }

        private static Dictionary<string, int> Scan()
        {
            Dictionary<string, int> results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            DeviceInformationCollection devices = DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelector()).AsTask().Result;

            foreach (DeviceInformation info in devices)
            {
                BluetoothLEDevice device = null;
                try
                {
                    Task<BluetoothLEDevice> connect = BluetoothLEDevice.FromIdAsync(info.Id).AsTask();
                    if (!connect.Wait(3000) || connect.Result == null) continue;
                    device = connect.Result;

                    Task<GattDeviceServicesResult> servicesTask = device.GetGattServicesAsync().AsTask();
                    if (!servicesTask.Wait(4000) || servicesTask.Result.Status != GattCommunicationStatus.Success)
                        continue;

                    int percent = -1;
                    foreach (GattDeviceService service in servicesTask.Result.Services)
                    {
                        Task<GattCharacteristicsResult> characteristicsTask = service.GetCharacteristicsAsync().AsTask();
                        if (!characteristicsTask.Wait(3000) ||
                            characteristicsTask.Result.Status != GattCommunicationStatus.Success)
                            continue;

                        foreach (GattCharacteristic characteristic in characteristicsTask.Result.Characteristics)
                        {
                            if (characteristic.Uuid != GattCharacteristicUuids.BatteryLevel) continue;
                            Task<GattReadResult> readTask = characteristic.ReadValueAsync().AsTask();
                            if (!readTask.Wait(3000) ||
                                readTask.Result.Status != GattCommunicationStatus.Success)
                                continue;

                            using (DataReader reader = DataReader.FromBuffer(readTask.Result.Value))
                                percent = reader.ReadByte();
                            break;
                        }
                        if (percent >= 0) break;
                    }

                    if (percent >= 0 && !string.IsNullOrEmpty(info.Name))
                        results[info.Name] = percent;
                }
                catch
                {
                }
                finally
                {
                    if (device != null) device.Dispose();
                }
            }
            return results;
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
                    Detail = "蓝牙设备已识别；未找到标准 GATT 电量服务"
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
