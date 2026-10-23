using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryTray
{
    internal enum DeviceCategory
    {
        Gamepad = 0,
        Mouse = 1,
        Keyboard = 2,
        Headset = 3,
        Other = 4
    }

    internal enum DeviceConnection
    {
        UsbReceiver = 0,
        Bluetooth = 1,
        UsbWired = 2,
        Unknown = 3
    }

    internal enum BatteryLevel
    {
        Unknown = 0,
        Critical = 1,
        Low = 2,
        Good = 3,
        Full = 4
    }

    internal sealed class DeviceBattery
    {
        public string Key;
        public string Label;
        public string ShortLabel;
        public int Percent = -1;
        public bool Supported;
        public bool Stale;
        public string Detail = "";
        public DeviceCategory Category = DeviceCategory.Other;
        public DeviceConnection Connection = DeviceConnection.Unknown;
        public BatteryLevel Level = BatteryLevel.Unknown;

        public int DisplayPercent
        {
            get
            {
                switch (Level)
                {
                    case BatteryLevel.Full: return 100;
                    case BatteryLevel.Good: return 70;
                    case BatteryLevel.Low: return 30;
                    case BatteryLevel.Critical: return 10;
                    default: return Percent;
                }
            }
        }

        public static string LevelName(BatteryLevel level)
        {
            switch (level)
            {
                case BatteryLevel.Full: return "满";
                case BatteryLevel.Good: return "良好";
                case BatteryLevel.Low: return "低";
                case BatteryLevel.Critical: return "临界";
                default: return "未知";
            }
        }
    }

    internal sealed class BatterySnapshot
    {
        public readonly List<DeviceBattery> Devices = new List<DeviceBattery>();

        public bool HasSupported
        {
            get
            {
                for (int i = 0; i < Devices.Count; i++)
                {
                    if (Devices[i].Supported) return true;
                }
                return false;
            }
        }

        public DeviceBattery Primary
        {
            get
            {
                for (int i = 0; i < Devices.Count; i++)
                {
                    if (Devices[i].Supported) return Devices[i];
                }
                return null;
            }
        }
    }

    internal sealed class HidInterfaceInfo
    {
        public string Path;
        public string Product;
        public ushort Vid;
        public ushort Pid;
        public ushort UsagePage;
        public ushort Usage;
        public int InputReportLength;
        public int OutputReportLength;
        public int FeatureReportLength;
    }

    internal static class BatteryReader
    {
        private const ushort BatteryStatusFeature = 0x1000;
        private const ushort UnifiedBatteryFeature = 0x1004;
        private const byte SoftwareId = 0x0C;

        public static BatterySnapshot ReadAll()
        {
            BatterySnapshot snapshot = new BatterySnapshot();
            List<HidInterfaceInfo> interfaces = EnumerateHidInterfaces();

            IBatteryProvider[] providers = new IBatteryProvider[]
            {
                new LogitechBatteryProvider(),
                new GenericHidBatteryProvider(),
                new DevicePresenceProvider(),
                new BluetoothDeviceProvider(),
                new XInputGamepadProvider(),
                new BleGattBatteryProvider()
            };
            for (int i = 0; i < providers.Length; i++)
            {
                try
                {
                    providers[i].Read(interfaces, snapshot);
                }
                catch
                {
                    // 单个厂商读取失败不应影响其他品牌
                }
            }

            AddUnsupportedReceivers(interfaces, snapshot);
            return snapshot;
        }

        internal static Dictionary<string, List<HidInterfaceInfo>> GroupReceivers(List<HidInterfaceInfo> interfaces)
        {
            Dictionary<string, List<HidInterfaceInfo>> receivers = new Dictionary<string, List<HidInterfaceInfo>>();
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                if (info.Vid == 0 || info.UsagePage == 0) continue;
                string key = string.Format(CultureInfo.InvariantCulture, "{0:X4}:{1:X4}", info.Vid, info.Pid);
                List<HidInterfaceInfo> group;
                if (!receivers.TryGetValue(key, out group))
                {
                    group = new List<HidInterfaceInfo>();
                    receivers.Add(key, group);
                }
                group.Add(info);
            }
            return receivers;
        }

        private static void AddUnsupportedReceivers(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, List<HidInterfaceInfo>> receivers = GroupReceivers(interfaces);
            if (receivers.Count == 0)
            {
                if (snapshot.Devices.Count == 0)
                {
                    snapshot.Devices.Add(new DeviceBattery
                    {
                        Key = "none",
                        Label = "2.4G 接收器",
                        ShortLabel = "2.4G",
                        Supported = false,
                        Percent = -1,
                        Detail = "未检测到 2.4G 接收器"
                    });
                }
                return;
            }

            foreach (KeyValuePair<string, List<HidInterfaceInfo>> pair in receivers)
            {
                if (DeviceClassifier.ClassifyConnection(pair.Value[0], pair.Value) != DeviceConnection.UsbReceiver)
                    continue;

                bool hasSupported = false;
                string prefix = pair.Key + ":";
                for (int i = 0; i < snapshot.Devices.Count; i++)
                {
                    if (snapshot.Devices[i].Supported &&
                        snapshot.Devices[i].Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        hasSupported = true;
                        break;
                    }
                }
                if (!hasSupported)
                    snapshot.Devices.Add(CreateUnsupportedReceiver(pair.Key, pair.Value[0]));
            }
        }

        private static DeviceBattery CreateUnsupportedReceiver(string key, HidInterfaceInfo info)
        {
            string vendor = VendorCatalog.GetVendorName(info.Vid);
            string label;
            string detail;
            if (info.Vid == 0x26CE)
            {
                label = "SONiX 2.4G 接收器";
                detail = "接收器芯片为 SONiX（松翰），未提供标准 HID 电池字段";
            }
            else if (info.Vid == 0x046D)
            {
                label = "罗技 2.4G 接收器";
                detail = "未发现可读取电量的设备（设备可能处于休眠或未配对）";
            }
            else
            {
                label = (vendor != null ? vendor : "未知品牌") + " 2.4G 接收器";
                detail = "未提供可读的标准 HID 电池字段";
                if (!string.IsNullOrEmpty(info.Product))
                    detail += "（" + info.Product + "）";
            }

            return new DeviceBattery
            {
                Key = key + ":receiver",
                Label = label,
                ShortLabel = (vendor != null ? vendor : "2.4G") + "接收器",
                Supported = false,
                Percent = -1,
                Detail = detail,
                Connection = DeviceConnection.UsbReceiver
            };
        }

        internal static void ReadLogitechReceiver(string receiverKey, List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            List<HidInterfaceInfo> candidates = new List<HidInterfaceInfo>();
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                if (info.UsagePage == 0xFF00 && info.OutputReportLength >= 7 && info.InputReportLength >= 7)
                    candidates.Add(info);
            }
            candidates.Sort(delegate(HidInterfaceInfo a, HidInterfaceInfo b)
            {
                return b.OutputReportLength.CompareTo(a.OutputReportLength);
            });

            for (int c = 0; c < candidates.Count; c++)
            {
                using (HidConnection connection = HidConnection.Open(candidates[c].Path))
                {
                    if (connection == null) continue;
                    byte reportId = connection.OutputReportLength >= 20 ? (byte)0x11 : (byte)0x10;
                    List<DeviceBattery> found = ReadLogitechDevices(connection, reportId, receiverKey, interfaces);
                    if (found.Count > 0)
                    {
                        snapshot.Devices.AddRange(found);
                        return;
                    }
                }
            }
        }

        private static List<DeviceBattery> ReadLogitechDevices(HidConnection connection, byte reportId, string receiverKey, List<HidInterfaceInfo> group)
        {
            List<DeviceBattery> result = new List<DeviceBattery>();
            for (int index = 1; index <= 6; index++)
            {
                int timeout = index <= 2 ? 200 : 120;
                int statusFeature = connection.GetFeature(index, BatteryStatusFeature, reportId, 1, timeout);
                int unifiedFeature = -1;
                if (statusFeature > 0 || index <= 2)
                    unifiedFeature = connection.GetFeature(index, UnifiedBatteryFeature, reportId, 1, timeout);
                bool respondedUnsupported = statusFeature == 0 || unifiedFeature == 0;
                if (statusFeature <= 0 && unifiedFeature <= 0)
                {
                    if (respondedUnsupported && index <= 2)
                        AddUnsupportedLogitechDevice(connection, index, reportId, receiverKey, group, result);
                    continue;
                }

                int percent = -1;
                BatteryLevel level = BatteryLevel.Unknown;
                string detail = "";

                // 0x1004 优先：有百分比就显示百分比，只有档位标志就显示档位。
                if (unifiedFeature > 0)
                {
                    byte[] capabilities = connection.Call(index, unifiedFeature, 0, reportId, null, 1, 200);
                    if (capabilities != null && !HidConnection.IsErrorResponse(capabilities) && capabilities.Length > 5)
                    {
                        int reportedLevels = capabilities[4];
                        int capsByte = capabilities[5];
                        if ((capsByte & 0x02) != 0)
                        {
                            byte[] status = connection.Call(index, unifiedFeature, 1, reportId, null, 1, 200);
                            if (status != null && !HidConnection.IsErrorResponse(status) && status.Length > 4)
                            {
                                percent = status[4];
                                if (percent > 100) percent = 100;
                                if (percent >= 0) detail = "HID++ 0x1004 统一电池：设备上报百分比";
                            }
                        }
                        else if ((reportedLevels & 0x0F) != 0)
                        {
                            byte[] status = connection.Call(index, unifiedFeature, 1, reportId, null, 1, 200);
                            if (status != null && !HidConnection.IsErrorResponse(status) && status.Length > 5)
                            {
                                byte levelCode = status[5];
                                level = LogitechLevelFromCode(levelCode);
                                if (level != BatteryLevel.Unknown)
                                {
                                    detail = "HID++ 0x1004 统一电池：设备档位 " + DeviceBattery.LevelName(level) +
                                        "（档位代码 " + levelCode.ToString(CultureInfo.InvariantCulture) + "，仅上报档位）";
                                }
                            }
                        }
                    }
                }

                // 没有百分比也没有档位时，回退到 0x1000 的原始百分比。
                if (percent < 0 && level == BatteryLevel.Unknown && statusFeature > 0)
                {
                    byte[] response = connection.Call(index, statusFeature, 0, reportId, null, 1, 200);
                    if (response != null && !HidConnection.IsErrorResponse(response) && response.Length > 4)
                    {
                        percent = response[4];
                        if (percent == 0) percent = -1;
                        else if (percent > 100) percent = 100;
                        if (percent >= 0)
                            detail = "HID++ 0x1000 电池状态：设备原始值 " +
                                percent.ToString(CultureInfo.InvariantCulture) + "%";
                    }
                }

                if (percent < 0 && level == BatteryLevel.Unknown) continue;

                DeviceCategory category = GetLogitechCategory(connection, index, reportId, group);
                string categoryName = DeviceClassifier.CategoryName(category);
                bool fitsCategory = category != DeviceCategory.Other;

                DeviceBattery device = new DeviceBattery
                {
                    Key = receiverKey + ":" + index.ToString(CultureInfo.InvariantCulture),
                    Label = fitsCategory
                        ? "罗技 " + categoryName
                        : "罗技 2.4G 设备 " + index.ToString(CultureInfo.InvariantCulture),
                    ShortLabel = fitsCategory
                        ? "罗技" + categoryName
                        : "罗技设备 " + index.ToString(CultureInfo.InvariantCulture),
                    Category = category,
                    Connection = DeviceConnection.UsbReceiver,
                    Supported = true,
                    Percent = percent,
                    Level = level,
                    Detail = detail
                };
                result.Add(device);
            }
            return result;
        }

        private static BatteryLevel LogitechLevelFromCode(byte code)
        {
            // 0x1004 档位为位标志：1 临界、2 低、4 良好、8 满。
            switch (code)
            {
                case 1: return BatteryLevel.Critical;
                case 2: return BatteryLevel.Low;
                case 4: return BatteryLevel.Good;
                case 8: return BatteryLevel.Full;
                default: return BatteryLevel.Unknown;
            }
        }

        private static void AddUnsupportedLogitechDevice(HidConnection connection, int index, byte reportId,
            string receiverKey, List<HidInterfaceInfo> group, List<DeviceBattery> result)
        {
            int nameFeature = connection.GetFeature(index, 0x0005, reportId, 1, 150);
            if (nameFeature <= 0) return;

            string name = ReadLogitechName(connection, index, nameFeature, reportId).Trim();
            DeviceCategory category = GetLogitechCategory(connection, index, reportId, group);
            string categoryName = DeviceClassifier.CategoryName(category);
            string label = !string.IsNullOrEmpty(name)
                ? "罗技 " + name
                : "罗技 " + categoryName;

            result.Add(new DeviceBattery
            {
                Key = receiverKey + ":" + index.ToString(CultureInfo.InvariantCulture) + ":nobattery",
                Label = label,
                ShortLabel = "罗技" + categoryName,
                Category = category,
                Connection = DeviceConnection.UsbReceiver,
                Supported = false,
                Percent = -1,
                Detail = "该设备未提供电量上报（HID++ 2.0 特性表与 1.0 寄存器均不可用）"
            });
        }

        private static DeviceCategory GetLogitechCategory(HidConnection connection, int index, byte reportId, List<HidInterfaceInfo> group)
        {
            int featureIndex = connection.GetFeature(index, 0x0005, reportId, 1, 200);
            if (featureIndex > 0)
            {
                byte[] typeResponse = connection.Call(index, featureIndex, 2, reportId, null, 1, 200);
                if (typeResponse != null && !HidConnection.IsErrorResponse(typeResponse))
                {
                    switch (typeResponse[4])
                    {
                        case 0:
                        case 2:
                            return DeviceCategory.Keyboard;
                        case 3:
                        case 4:
                        case 5:
                            return DeviceCategory.Mouse;
                    }
                }

                string name = ReadLogitechName(connection, index, featureIndex, reportId);
                DeviceCategory byName = DeviceClassifier.FromProductName(name);
                if (byName != DeviceCategory.Other) return byName;
            }

            return InferCategoryFromGroup(group);
        }

        private static string ReadLogitechName(HidConnection connection, int index, int featureIndex, byte reportId)
        {
            byte[] lengthResponse = connection.Call(index, featureIndex, 0, reportId, null, 1, 200);
            if (lengthResponse == null || HidConnection.IsErrorResponse(lengthResponse)) return "";

            int length = lengthResponse[4];
            StringBuilder name = new StringBuilder();
            for (int offset = 0; offset < length; offset += 16)
            {
                byte[] chunk = connection.Call(index, featureIndex, 1, reportId, new byte[] { (byte)offset }, 1, 200);
                if (chunk == null || HidConnection.IsErrorResponse(chunk)) break;
                int end = Math.Min(chunk.Length, 20);
                for (int i = 4; i < end; i++)
                {
                    byte value = chunk[i];
                    if (value == 0) return name.ToString();
                    if (value >= 32 && value < 127) name.Append((char)value);
                }
            }
            return name.ToString();
        }

        private static DeviceCategory InferCategoryFromGroup(List<HidInterfaceInfo> group)
        {
            bool keyboard = false;
            bool mouse = false;
            bool gamepad = false;
            bool headset = false;
            for (int i = 0; i < group.Count; i++)
            {
                DeviceCategory category = DeviceClassifier.FromHidUsage(group[i].UsagePage, group[i].Usage);
                if (category == DeviceCategory.Other && !string.IsNullOrEmpty(group[i].Product))
                    category = DeviceClassifier.FromProductName(group[i].Product);
                if (category == DeviceCategory.Keyboard) keyboard = true;
                else if (category == DeviceCategory.Mouse) mouse = true;
                else if (category == DeviceCategory.Gamepad) gamepad = true;
                else if (category == DeviceCategory.Headset) headset = true;
            }

            if (gamepad) return DeviceCategory.Gamepad;
            if (headset) return DeviceCategory.Headset;
            if (keyboard && !mouse) return DeviceCategory.Keyboard;
            if (mouse && !keyboard) return DeviceCategory.Mouse;
            return DeviceCategory.Other;
        }

        private static List<HidInterfaceInfo> EnumerateHidInterfaces()
        {
            List<HidInterfaceInfo> result = new List<HidInterfaceInfo>();
            Guid guid = Native.HidInterfaceGuid;
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (!Native.IsValidHandle(set)) return result;

            try
            {
                Native.SP_DEVICE_INTERFACE_DATA data = new Native.SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; ; i++)
                {
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    string path = GetInterfacePath(set, ref data);
                    if (string.IsNullOrEmpty(path)) continue;

                    HidInterfaceInfo info = new HidInterfaceInfo();
                    info.Path = path;
                    ParseVidPid(path, out info.Vid, out info.Pid);

                    IntPtr handle = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (Native.IsValidHandle(handle))
                    {
                        try
                        {
                            Native.HIDP_CAPS caps = GetCaps(handle);
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            info.FeatureReportLength = caps.FeatureReportByteLength;

                            StringBuilder product = new StringBuilder(256);
                            if (Native.HidD_GetProductString(handle, product, 512))
                                info.Product = product.ToString().Trim();
                        }
                        finally
                        {
                            Native.CloseHandle(handle);
                        }
                    }
                    result.Add(info);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static string GetInterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data)
        {
            int required = 0;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, ref required, IntPtr.Zero);
            if (required <= 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, ref required, IntPtr.Zero))
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
            int vidIndex = upper.IndexOf("VID_", StringComparison.Ordinal);
            int pidIndex = upper.IndexOf("PID_", StringComparison.Ordinal);
            if (vidIndex >= 0 && vidIndex + 8 <= upper.Length)
                ushort.TryParse(upper.Substring(vidIndex + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
            if (pidIndex >= 0 && pidIndex + 8 <= upper.Length)
                ushort.TryParse(upper.Substring(pidIndex + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }

        private static Native.HIDP_CAPS GetCaps(IntPtr handle)
        {
            Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
            caps.Reserved = new ushort[17];
            caps.Reserved2 = new ushort[10];
            IntPtr preparsed;
            if (Native.HidD_GetPreparsedData(handle, out preparsed))
            {
                try
                {
                    Native.HidP_GetCaps(preparsed, ref caps);
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }
            }
            return caps;
        }

        private sealed class HidConnection : IDisposable
        {
            private IntPtr _handle;
            public readonly int InputReportLength;
            public readonly int OutputReportLength;

            private HidConnection(IntPtr handle, int inputLength, int outputLength)
            {
                _handle = handle;
                InputReportLength = inputLength;
                OutputReportLength = outputLength;
            }

            public static HidConnection Open(string path)
            {
                IntPtr handle = Native.CreateFile(path,
                    Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (!Native.IsValidHandle(handle)) return null;

                Native.HIDP_CAPS caps = GetCaps(handle);
                if (caps.InputReportByteLength == 0 || caps.OutputReportByteLength == 0)
                {
                    Native.CloseHandle(handle);
                    return null;
                }
                return new HidConnection(handle, caps.InputReportByteLength, caps.OutputReportByteLength);
            }

            public int GetFeature(int deviceIndex, ushort feature, byte reportId, int attempts, int timeout)
            {
                byte[] parameters = new byte[] { (byte)(feature >> 8), (byte)(feature & 0xFF) };
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    byte[] response = Call(deviceIndex, 0x00, 0, reportId, parameters, 1, timeout);
                    if (response != null)
                    {
                        if (IsErrorResponse(response)) return -1;
                        return response[4];
                    }
                    if (attempt + 1 < attempts) Thread.Sleep(120);
                }
                return -1;
            }

            public static bool IsErrorResponse(byte[] response)
            {
                return response[2] == 0x8F || (response[2] == 0xFF && response[3] == 0x8F);
            }

            public byte[] Call(int deviceIndex, int featureIndex, int function, byte reportId, byte[] parameters, int attempts, int timeout)
            {
                int length = reportId == 0x11 ? 20 : 7;
                if (OutputReportLength > length) length = OutputReportLength;
                byte[] request = new byte[length];
                request[0] = reportId;
                request[1] = (byte)deviceIndex;
                request[2] = (byte)featureIndex;
                request[3] = (byte)((function << 4) | SoftwareId);
                if (parameters != null)
                {
                    int copy = Math.Min(parameters.Length, request.Length - 4);
                    Array.Copy(parameters, 0, request, 4, copy);
                }

                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    byte[] response = Exchange(request, deviceIndex, featureIndex, timeout);
                    if (response != null) return response;
                }
                return null;
            }

            private byte[] Exchange(byte[] request, int deviceIndex, int featureIndex, int timeout)
            {
                GCHandle requestPin = GCHandle.Alloc(request, GCHandleType.Pinned);
                ManualResetEvent ioEvent = new ManualResetEvent(false);
                IntPtr eventHandle = ioEvent.SafeWaitHandle.DangerousGetHandle();
                try
                {
                    NativeOverlapped writeOverlapped = new NativeOverlapped();
                    writeOverlapped.EventHandle = eventHandle;
                    bool ok = Native.WriteFile(_handle, requestPin.AddrOfPinnedObject(), request.Length, IntPtr.Zero, ref writeOverlapped);
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 997) return null;
                        if (!ioEvent.WaitOne(timeout))
                        {
                            CancelAndWait(ref writeOverlapped);
                            return null;
                        }
                    }
                    int transferred;
                    Native.GetOverlappedResult(_handle, ref writeOverlapped, out transferred, false);
                    ioEvent.Reset();

                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        int bufferLength = Math.Max(InputReportLength, 64);
                        IntPtr readBuffer = Marshal.AllocHGlobal(bufferLength);
                        try
                        {
                            NativeOverlapped readOverlapped = new NativeOverlapped();
                            readOverlapped.EventHandle = eventHandle;
                            int readCount;
                            ok = Native.ReadFile(_handle, readBuffer, bufferLength, IntPtr.Zero, ref readOverlapped);
                            if (!ok)
                            {
                                int error = Marshal.GetLastWin32Error();
                                if (error != 997) break;
                                int remaining = (int)Math.Max(10, (deadline - DateTime.UtcNow).TotalMilliseconds);
                                if (!ioEvent.WaitOne(remaining))
                                {
                                    CancelAndWait(ref readOverlapped);
                                    break;
                                }
                            }
                            ioEvent.Reset();
                            if (!Native.GetOverlappedResult(_handle, ref readOverlapped, out readCount, false)) break;

                            if (readCount >= 7)
                            {
                                int copyLength = Math.Min(readCount, 64);
                                byte[] received = new byte[copyLength];
                                Marshal.Copy(readBuffer, received, 0, copyLength);
                                if (IsMatchingResponse(received, request, deviceIndex, featureIndex))
                                    return received;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(readBuffer);
                        }
                    }
                    return null;
                }
                finally
                {
                    requestPin.Free();
                    ioEvent.Close();
                }
            }

            private void CancelAndWait(ref NativeOverlapped overlapped)
            {
                Native.CancelIo(_handle);
                int transferred;
                Native.GetOverlappedResult(_handle, ref overlapped, out transferred, true);
            }

            private static bool IsMatchingResponse(byte[] buffer, byte[] request, int deviceIndex, int featureIndex)
            {
                if (buffer[0] != request[0] || buffer[1] != deviceIndex) return false;
                if (buffer[2] == 0xFF && buffer[3] == 0x8F) return true;
                if (buffer[2] == 0x8F) return true;
                return buffer[2] == featureIndex && (buffer[3] & 0x0F) == SoftwareId;
            }

            public void Dispose()
            {
                if (_handle != IntPtr.Zero)
                {
                    Native.CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }

    internal sealed class LogitechBatteryProvider : IBatteryProvider
    {
        public string Name
        {
            get { return "罗技 HID++ 2.0"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            Dictionary<string, List<HidInterfaceInfo>> receivers = BatteryReader.GroupReceivers(interfaces);
            foreach (KeyValuePair<string, List<HidInterfaceInfo>> pair in receivers)
            {
                if (pair.Value[0].Vid != 0x046D) continue;
                BatteryReader.ReadLogitechReceiver(pair.Key, pair.Value, snapshot);
            }
        }
    }
}
