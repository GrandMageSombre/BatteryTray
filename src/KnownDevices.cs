using System;

namespace BatteryTray
{
    /// <summary>
    /// 已知设备档案。只收录有公开出处的 VID/PID，用于准确判断品牌与连接方式；
    /// 没有档案的设备仍走通用识别，不会被当成未知品牌排除。
    /// </summary>
    internal sealed class KnownDevice
    {
        public ushort Vid;
        public ushort Pid;
        public string Vendor;
        public string Model;
        public DeviceConnection Connection;
        /// <summary>该设备是否存在已实现的私有电量协议。</summary>
        public bool HasBatteryProtocol;
        /// <summary>读不到电量时向用户说明的原因。</summary>
        public string BatteryNote;
    }

    internal static class KnownDevices
    {
        private static readonly KnownDevice[] Entries = new KnownDevice[]
        {
            // 8BitDo Retro Mechanical Keyboard：有线 5200、2.4G 接收器 5201。
            // 出处：goncalor/8bitdo-kbd-mapper 的 udev 规则、m1nl/usb_hid_host 的设备列表。
            // 官网说明 PC 端无电量百分比显示，只有机身 LED 状态；2.4G 电量协议未见公开。
            new KnownDevice
            {
                Vid = 0x2DC8, Pid = 0x5200, Vendor = "八位堂",
                Model = "Retro Keyboard（有线）", Connection = DeviceConnection.UsbWired,
                BatteryNote = "八位堂键盘仅提供机身 LED 电量状态，未公开 PC 端电量协议"
            },
            new KnownDevice
            {
                Vid = 0x2DC8, Pid = 0x5201, Vendor = "八位堂",
                Model = "Retro Keyboard 2.4G 接收器", Connection = DeviceConnection.UsbReceiver,
                BatteryNote = "八位堂键盘仅提供机身 LED 电量状态，未公开 2.4G 电量协议"
            },

            // 黑爵 AJ179：Rockeyxx/aj179-linux docs/PROTOCOL.md（0x10 设备信息含电量）。
            new KnownDevice
            {
                Vid = 0x249A, Pid = 0x5C2F, Vendor = "黑爵",
                Model = "AJ179 2.4G 接收器", Connection = DeviceConnection.UsbReceiver,
                HasBatteryProtocol = true
            },
            new KnownDevice
            {
                Vid = 0x248A, Pid = 0x5C2E, Vendor = "黑爵",
                Model = "AJ179（有线）", Connection = DeviceConnection.UsbWired,
                HasBatteryProtocol = true
            },
            new KnownDevice
            {
                Vid = 0x248A, Pid = 0x5D2E, Vendor = "黑爵",
                Model = "AJ179（有线）", Connection = DeviceConnection.UsbWired,
                HasBatteryProtocol = true
            },
            new KnownDevice
            {
                Vid = 0x248A, Pid = 0x5E2E, Vendor = "黑爵",
                Model = "AJ179（有线）", Connection = DeviceConnection.UsbWired,
                HasBatteryProtocol = true
            },

            // 黑爵 AJ159 V2：f0rd2017/ajazz-mouse docs/PROTOCOL.md。
            // 电量在输入报告 03 <id> <40|41> <充电> <百分比> 中，需要监听状态帧。
            new KnownDevice
            {
                Vid = 0x1D57, Pid = 0xFA60, Vendor = "黑爵",
                Model = "AJ159 V2 2.4G 接收器", Connection = DeviceConnection.UsbReceiver,
                BatteryNote = "该型号电量通过状态帧推送，需监听输入报告，当前版本未实现"
            },
            new KnownDevice
            {
                Vid = 0x1D57, Pid = 0xFA65, Vendor = "黑爵",
                Model = "8K 接收器", Connection = DeviceConnection.UsbReceiver,
                BatteryNote = "8K 接收器使用另一套协议，当前版本未适配"
            }
        };

        public static KnownDevice Find(ushort vid, ushort pid)
        {
            for (int i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Vid == vid && Entries[i].Pid == pid) return Entries[i];
            }
            return null;
        }

        /// <summary>产品名里的品牌关键词，用于 VID 查不到时的补充识别。</summary>
        public static string VendorFromProduct(string product)
        {
            if (string.IsNullOrEmpty(product)) return null;
            string value = product.ToLowerInvariant();
            if (value.Contains("8bitdo") || product.Contains("八位堂")) return "八位堂";
            if (value.Contains("ajazz") || product.Contains("黑爵")) return "黑爵";
            if (value.Contains("dareu") || product.Contains("达尔优")) return "达尔优";
            if (value.Contains("keychron")) return "Keychron";
            if (value.Contains("mchose") || product.Contains("迈从")) return "迈从";
            if (value.Contains("vgn") || value.Contains("vxe")) return "VGN";
            if (value.Contains("logitech")) return "罗技";
            if (value.Contains("razer")) return "雷蛇";
            if (value.Contains("steelseries")) return "赛睿";
            if (value.Contains("corsair")) return "海盗船";
            if (value.Contains("hyperx")) return "HyperX";
            if (value.Contains("rapoo")) return "雷柏";
            if (value.Contains("roccat")) return "ROCCAT";
            return null;
        }
    }
}
