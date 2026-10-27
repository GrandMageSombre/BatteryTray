using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryTray
{
    /// <summary>
    /// 黑爵（AJAZZ）2.4G 接收器电量读取。
    /// 协议出处：Rockeyxx/aj179-linux 的 docs/PROTOCOL.md（官方驱动逆向 + 抓包验证）。
    /// 只发送只读的 0x10 设备信息查询，不写配置、不碰固件命令。
    /// 报文为 32 字节：data[0] 命令，data[1] 状态，data[4..7] 设备 ID，
    /// data[12] 充电，data[13] 电量百分比，data[14] 无线连接状态，data[31] 校验和。
    /// </summary>
    internal sealed class AjazzBatteryProvider : IBatteryProvider
    {
        private const int PayloadLength = 32;
        private const byte CmdDeviceInfo = 0x10;
        private const byte CmdStatusFrame = 0xC0;
        // 轮询周期 1 秒、看门狗 3 秒，单次读取必须远低于该阈值：
        // 写 300ms + 读 500ms，最坏 800ms 放弃，不做额外重试。
        private const int WriteTimeoutMs = 300;
        private const int ReadTimeoutMs = 500;

        public string Name
        {
            get { return "黑爵 2.4G 协议"; }
        }

        public void Read(List<HidInterfaceInfo> interfaces, BatterySnapshot snapshot)
        {
            HashSet<string> finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidInterfaceInfo info = interfaces[i];
                if (!IsConfigInterface(info)) continue;

                string receiverKey = DeviceClassifier.ReceiverKey(info);
                if (finished.Contains(receiverKey)) continue;
                if (TryRead(info, snapshot)) finished.Add(receiverKey);
            }
        }

        private static bool IsConfigInterface(HidInterfaceInfo info)
        {
            KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
            if (known == null || !known.HasBatteryProtocol) return false;

            string path = info.Path ?? "";
            if (path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 标准键盘/鼠标/消费控制接口不承担配置通道
            if (info.UsagePage == 0x01 || info.UsagePage == 0x0C) return false;
            return info.OutputReportLength >= PayloadLength && info.InputReportLength >= PayloadLength;
        }

        private static bool TryRead(HidInterfaceInfo info, BatterySnapshot snapshot)
        {
            IntPtr handle = Native.CreateFile(info.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (!Native.IsValidHandle(handle)) return false;

            try
            {
                Native.HIDP_CAPS caps = GetCaps(handle);
                if (caps.OutputReportByteLength < PayloadLength || caps.InputReportByteLength < PayloadLength)
                    return false;

                byte[] reply = QueryDeviceInfo(handle, caps);
                if (reply == null) return false;

                int offset = caps.InputReportByteLength > PayloadLength ? 1 : 0;
                if (reply.Length < offset + PayloadLength) return false;

                int percent = reply[offset + 13];
                if (percent > 100) return false;

                string deviceId = ReadDeviceId(reply, offset);
                bool charging = reply[offset + 12] != 0;
                bool connected = reply[offset + 14] != 0;

                StringBuilder detail = new StringBuilder();
                detail.Append("黑爵 2.4G 协议（0x10 设备信息）");
                if (deviceId.Length > 0) detail.Append("；设备 ").Append(deviceId);
                detail.Append(charging ? "；充电中" : "；电池供电");
                if (!connected) detail.Append("；鼠标休眠或未连接，电量为接收器保存的最近值");

                KnownDevice known = KnownDevices.Find(info.Vid, info.Pid);
                snapshot.Devices.Add(new DeviceBattery
                {
                    Key = DeviceClassifier.ReceiverKey(info) + ":ajazz",
                    Label = "黑爵 " + (deviceId.Length > 0 ? deviceId : "鼠标"),
                    ShortLabel = "黑爵鼠标",
                    Category = DeviceCategory.Mouse,
                    Connection = known != null ? known.Connection : DeviceConnection.UsbReceiver,
                    Supported = true,
                    Percent = percent,
                    Detail = detail.ToString()
                });
                return true;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static string ReadDeviceId(byte[] reply, int offset)
        {
            StringBuilder id = new StringBuilder();
            for (int i = 4; i < 8; i++)
            {
                byte b = reply[offset + i];
                if (b >= 0x20 && b <= 0x7E) id.Append((char)b);
            }
            return id.ToString();
        }

        private static byte[] QueryDeviceInfo(IntPtr handle, Native.HIDP_CAPS caps)
        {
            int outOffset = caps.OutputReportByteLength > PayloadLength ? 1 : 0;
            int inOffset = caps.InputReportByteLength > PayloadLength ? 1 : 0;

            byte[] request = new byte[caps.OutputReportByteLength];
            request[outOffset] = CmdDeviceInfo;

            using (ManualResetEvent ioEvent = new ManualResetEvent(false))
            {
                IntPtr eventHandle = ioEvent.SafeWaitHandle.DangerousGetHandle();
                if (!WriteReport(handle, request, eventHandle, ioEvent)) return null;

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    int remaining = (int)Math.Max(20, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    byte[] buffer = new byte[caps.InputReportByteLength];
                    int read = ReadReport(handle, buffer, eventHandle, ioEvent, remaining);
                    if (read < inOffset + PayloadLength) continue;

                    byte command = buffer[inOffset];
                    if (command == CmdStatusFrame) continue; // 在线状态帧，不是本次查询的应答
                    if (command != CmdDeviceInfo) continue;
                    if (buffer[inOffset + 1] != 0) return null; // 01 未应答 / 02 休眠或未连接
                    if (!ChecksumValid(buffer, inOffset)) return null;
                    return buffer;
                }
            }
            return null;
        }

        private static bool ChecksumValid(byte[] buffer, int offset)
        {
            if (buffer.Length < offset + PayloadLength) return false;
            int sum = 0;
            for (int i = 4; i <= 30; i++) sum += buffer[offset + i];
            return (sum & 0xFF) == buffer[offset + 31];
        }

        private static bool WriteReport(IntPtr handle, byte[] request, IntPtr eventHandle, ManualResetEvent ioEvent)
        {
            GCHandle pin = GCHandle.Alloc(request, GCHandleType.Pinned);
            try
            {
                NativeOverlapped overlapped = new NativeOverlapped();
                overlapped.EventHandle = eventHandle;
                bool ok = Native.WriteFile(handle, pin.AddrOfPinnedObject(), request.Length, IntPtr.Zero, ref overlapped);
                if (!ok)
                {
                    if (Marshal.GetLastWin32Error() != 997) return false;
                    if (!ioEvent.WaitOne(WriteTimeoutMs))
                    {
                        CancelAndWait(handle, ref overlapped);
                        return false;
                    }
                }
                int transferred;
                return Native.GetOverlappedResult(handle, ref overlapped, out transferred, false);
            }
            finally
            {
                pin.Free();
                ioEvent.Reset();
            }
        }

        private static int ReadReport(IntPtr handle, byte[] buffer, IntPtr eventHandle, ManualResetEvent ioEvent, int timeout)
        {
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                NativeOverlapped overlapped = new NativeOverlapped();
                overlapped.EventHandle = eventHandle;
                bool ok = Native.ReadFile(handle, pin.AddrOfPinnedObject(), buffer.Length, IntPtr.Zero, ref overlapped);
                if (!ok)
                {
                    if (Marshal.GetLastWin32Error() != 997) return 0;
                    if (!ioEvent.WaitOne(timeout))
                    {
                        CancelAndWait(handle, ref overlapped);
                        return 0;
                    }
                }
                int transferred;
                if (!Native.GetOverlappedResult(handle, ref overlapped, out transferred, false)) return 0;
                return transferred;
            }
            finally
            {
                pin.Free();
                ioEvent.Reset();
            }
        }

        private static void CancelAndWait(IntPtr handle, ref NativeOverlapped overlapped)
        {
            Native.CancelIo(handle);
            int transferred;
            Native.GetOverlappedResult(handle, ref overlapped, out transferred, true);
        }

        private static Native.HIDP_CAPS GetCaps(IntPtr handle)
        {
            Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
            caps.Reserved = new ushort[17];
            caps.Reserved2 = new ushort[10];
            IntPtr preparsed;
            if (Native.HidD_GetPreparsedData(handle, out preparsed))
            {
                try { Native.HidP_GetCaps(preparsed, ref caps); }
                finally { Native.HidD_FreePreparsedData(preparsed); }
            }
            return caps;
        }
    }
}
