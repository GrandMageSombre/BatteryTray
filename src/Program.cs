using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
                try { Native.SetProcessDPIAware(); }
                catch { }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\BatteryTray2.4G", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("2.4G 电量程序已在运行，请查看任务栏通知区域。", "2.4G 电量",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const int PollIntervalMilliseconds = 1000;
        private const string SettingsKeyPath = @"Software\2.4GBattery";

        private readonly SynchronizationContext _ui;
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private ToolStripMenuItem _panelItem;
        private ToolStripMenuItem _autoStartItem;
        private readonly DevicePanelForm _panel;
        private readonly DeviceChangeWindow _deviceWindow;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _watchdog;
        private BatterySnapshot _snapshot;
        private DateTime _lastSuccess = DateTime.MinValue;
        private Icon _trayIcon;
        private int _lastIconPercent = int.MinValue;
        private bool _lastIconHasBattery;
        private bool _showPanel;
        private bool _exiting;
        private bool _suppressAutoStartEvent;
        private int _polling;
        private int _refreshPending;
        private int _pollGeneration;
        private int _pollStartedTicks;
        private int _cachedPercent;
        private int _cachedCategory;
        private static string _lastSnapshotSignature;

        public TrayContext()
        {
            _ui = SynchronizationContext.Current;
            if (_ui == null) _ui = new WindowsFormsSynchronizationContext();

            _showPanel = ReadSetting("ShowBar", 1) == 1;
            _cachedPercent = ReadSetting("LastPercent", -1);
            _cachedCategory = ReadSetting("LastCategory", -1);
            bool firstRun = !SettingExists("AutoStart");
            bool autoStart = firstRun || AutoStart.IsEnabled();

            _panel = new DevicePanelForm();
            _menu = BuildMenu(autoStart);

            _trayIcon = TrayIconFactory.Create(-1, false);
            _notify = new NotifyIcon();
            _notify.Icon = _trayIcon;
            _notify.Text = "设备电量";
            _notify.ContextMenuStrip = _menu;
            _notify.DoubleClick += delegate { ShowStatus(); };
            _notify.Visible = true;

            _panel.PanelEnabled = _showPanel;

            if (firstRun)
            {
                AutoStart.Set(true);
                WriteSetting("AutoStart", 1);
            }
            else if (autoStart)
            {
                AutoStart.Set(true);
            }

            if (_cachedPercent >= 0 && _cachedCategory >= 0 && _cachedCategory <= (int)DeviceCategory.Headset)
            {
                DeviceCategory category = (DeviceCategory)_cachedCategory;
                _snapshot = new BatterySnapshot();
                _snapshot.Devices.Add(new DeviceBattery
                {
                    Key = "cache",
                    Label = DeviceClassifier.CategoryName(category) + "（上次读取）",
                    ShortLabel = DeviceClassifier.CategoryName(category),
                    Category = category,
                    Connection = DeviceConnection.Unknown,
                    Supported = true,
                    Percent = _cachedPercent,
                    Stale = true,
                    Detail = "等待设备唤醒后更新"
                });
                UpdateDisplay();
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = PollIntervalMilliseconds;
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            _watchdog = new System.Windows.Forms.Timer();
            _watchdog.Interval = 1000;
            _watchdog.Tick += delegate { WatchdogTick(); };
            _watchdog.Start();

            _deviceWindow = new DeviceChangeWindow();
            _deviceWindow.DeviceChanged += delegate { RefreshNow(); };

            RefreshNow();
        }

        private ContextMenuStrip BuildMenu(bool autoStart)
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshNow(); };
            menu.Items.Add(refresh);
            menu.Items.Add(new ToolStripSeparator());

            _panelItem = new ToolStripMenuItem("显示在时间栏上方");
            _panelItem.CheckOnClick = true;
            _panelItem.Checked = _showPanel;
            _panelItem.CheckedChanged += PanelItemCheckedChanged;
            menu.Items.Add(_panelItem);

            _autoStartItem = new ToolStripMenuItem("开机自启");
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = autoStart;
            _autoStartItem.CheckedChanged += AutoStartItemCheckedChanged;
            menu.Items.Add(_autoStartItem);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem status = new ToolStripMenuItem("设备状态...");
            status.Click += delegate { ShowStatus(); };
            menu.Items.Add(status);

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { _ui.Post(delegate { ExitThread(); }, null); };
            menu.Items.Add(exit);

            return menu;
        }

        private void PanelItemCheckedChanged(object sender, EventArgs e)
        {
            _showPanel = _panelItem.Checked;
            WriteSetting("ShowBar", _showPanel ? 1 : 0);
            _panel.PanelEnabled = _showPanel;
        }

        private void AutoStartItemCheckedChanged(object sender, EventArgs e)
        {
            if (_suppressAutoStartEvent) return;

            bool enabled = _autoStartItem.Checked;
            if (!AutoStart.Set(enabled))
            {
                _suppressAutoStartEvent = true;
                _autoStartItem.Checked = !enabled;
                _suppressAutoStartEvent = false;
                MessageBox.Show("无法写入开机自启项，请检查当前用户的注册表权限。", "2.4G 电量",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            WriteSetting("AutoStart", enabled ? 1 : 0);
        }

        private void RefreshNow()
        {
            if (_exiting) return;
            if (Interlocked.Exchange(ref _polling, 1) == 1)
            {
                Interlocked.Exchange(ref _refreshPending, 1);
                return;
            }

            int generation = Volatile.Read(ref _pollGeneration);
            _pollStartedTicks = Environment.TickCount;

            ThreadPool.QueueUserWorkItem(delegate
            {
                BatterySnapshot snapshot;
                try
                {
                    snapshot = BatteryReader.ReadAll();
                }
                catch (Exception ex)
                {
                    snapshot = new BatterySnapshot();
                    snapshot.Devices.Add(new DeviceBattery
                    {
                        Key = "error",
                        Label = "2.4G 电量",
                        Supported = false,
                        Detail = "读取失败：" + ex.Message
                    });
                }

                bool current = generation == Volatile.Read(ref _pollGeneration);
                if (!current) return;

                Interlocked.Exchange(ref _polling, 0);
                try
                {
                    _ui.Post(delegate
                    {
                        if (generation == Volatile.Read(ref _pollGeneration))
                            ApplySnapshot(snapshot);
                    }, null);
                }
                catch
                {
                }

                if (Interlocked.Exchange(ref _refreshPending, 0) == 1)
                    RefreshNow();
            });
        }

        private void WatchdogTick()
        {
            if (_exiting) return;
            if (Interlocked.CompareExchange(ref _polling, 0, 0) != 1) return;
            if (unchecked(Environment.TickCount - _pollStartedTicks) <= 3000) return;

            // 读取线程可能卡在 HID 驱动取消等待中：废弃旧代际，允许新线程重新枚举。
            Interlocked.Increment(ref _pollGeneration);
            Interlocked.Exchange(ref _polling, 0);
            Interlocked.Exchange(ref _refreshPending, 0);
            RefreshNow();
        }

        private void ApplySnapshot(BatterySnapshot fresh)
        {
            if (_exiting || fresh == null) return;

            LogSnapshot(fresh);

            // 以最新枚举结果为准：设备/接收器一旦消失或不再上报，旧电量立即清除。
            _snapshot = fresh;

            if (fresh.HasSupported)
            {
                _lastSuccess = DateTime.Now;

                DeviceBattery cacheDevice = null;
                for (int i = 0; i < fresh.Devices.Count; i++)
                {
                    DeviceBattery candidate = fresh.Devices[i];
                    if (candidate.Supported && candidate.DisplayPercent >= 0 && candidate.Category != DeviceCategory.Other)
                    {
                        cacheDevice = candidate;
                        break;
                    }
                }
                if (cacheDevice != null &&
                    (cacheDevice.DisplayPercent != _cachedPercent || (int)cacheDevice.Category != _cachedCategory))
                {
                    _cachedPercent = cacheDevice.DisplayPercent;
                    _cachedCategory = (int)cacheDevice.Category;
                    WriteSetting("LastPercent", _cachedPercent);
                    WriteSetting("LastCategory", _cachedCategory);
                }
            }

            UpdateDisplay();
        }

        private static void LogSnapshot(BatterySnapshot snapshot)
        {
            StringBuilder signature = new StringBuilder();
            signature.Append("count=").Append(snapshot.Devices.Count)
                .Append(" supported=").Append(snapshot.HasSupported).Append(' ');
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceBattery device = snapshot.Devices[i];
                signature.Append('[').Append(device.Key).Append('/')
                    .Append(device.Category).Append('/')
                    .Append(device.Connection).Append('/')
                    .Append(device.Supported).Append('/')
                    .Append(device.Percent).Append('/')
                    .Append(device.Level).Append(']');
            }

            string text = signature.ToString();
            if (text == _lastSnapshotSignature) return;
            _lastSnapshotSignature = text;

            try
            {
                string path = Path.Combine(Path.GetTempPath(), "BatteryTray.snapshot.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch
            {
            }
        }

        private void UpdateDisplay()
        {
            int percent = -1;
            bool hasBattery = false;
            if (_snapshot != null)
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceBattery device = _snapshot.Devices[i];
                    if (!device.Supported || device.DisplayPercent < 0) continue;
                    hasBattery = true;
                    if (percent < 0 || device.DisplayPercent < percent) percent = device.DisplayPercent;
                }
            }

            UpdateTrayIcon(percent, hasBattery);
            UpdateTooltip();
            _panel.SetDevices(_snapshot != null ? _snapshot.Devices : new List<DeviceBattery>());
        }

        private void UpdateTrayIcon(int percent, bool hasBattery)
        {
            if (_lastIconPercent == percent && _lastIconHasBattery == hasBattery) return;

            _lastIconPercent = percent;
            _lastIconHasBattery = hasBattery;
            Icon old = _trayIcon;
            _trayIcon = TrayIconFactory.Create(percent, hasBattery);
            _notify.Icon = _trayIcon;
            if (old != null) old.Dispose();
        }

        private void UpdateTooltip()
        {
            DeviceCategory[] categories = new DeviceCategory[]
            {
                DeviceCategory.Gamepad,
                DeviceCategory.Mouse,
                DeviceCategory.Keyboard,
                DeviceCategory.Headset
            };

            StringBuilder builder = new StringBuilder();
            bool first = true;
            for (int i = 0; i < categories.Length; i++)
            {
                DeviceBattery device = DeviceClassifier.BestForCategory(
                    _snapshot != null ? _snapshot.Devices : null, categories[i]);
                if (device == null) continue;

                if (!first) builder.Append(" · ");
                builder.Append(DeviceClassifier.CategoryName(categories[i]));
                if (device.Supported && device.Level != BatteryLevel.Unknown)
                    builder.Append(' ').Append(DeviceBattery.LevelName(device.Level)).Append("（粗）");
                else if (device.Supported && device.Percent >= 0)
                    builder.Append(' ').Append(device.Percent.ToString(CultureInfo.InvariantCulture)).Append('%');
                else if (device.Connection == DeviceConnection.Bluetooth)
                    builder.Append(" 蓝牙");
                else if (device.Connection == DeviceConnection.UsbWired)
                    builder.Append(" 有线");
                else
                    builder.Append(" 无电量");
                first = false;
            }

            if (builder.Length == 0) builder.Append("设备电量未知");
            string text = builder.ToString();
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            _notify.Text = text;
        }

        private void ShowStatus()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("设备电量");
            builder.AppendLine("刷新间隔：2 秒");
            if (_lastSuccess != DateTime.MinValue)
                builder.AppendLine("最近成功读取：" + _lastSuccess.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine();

            if (_snapshot == null || _snapshot.Devices.Count == 0)
            {
                builder.AppendLine("正在读取设备电量...");
            }
            else
            {
                DeviceCategory[] categories = new DeviceCategory[]
                {
                    DeviceCategory.Gamepad,
                    DeviceCategory.Mouse,
                    DeviceCategory.Keyboard,
                    DeviceCategory.Headset
                };

                for (int i = 0; i < categories.Length; i++)
                {
                    for (int j = 0; j < _snapshot.Devices.Count; j++)
                    {
                        DeviceBattery device = _snapshot.Devices[j];
                        if (device.Category != categories[i]) continue;

                        builder.Append("· ").Append(DeviceClassifier.CategoryName(categories[i]))
                            .Append("：").Append(device.Label)
                            .Append("（").Append(DeviceClassifier.ConnectionName(device.Connection)).Append("）");
                        if (device.Supported && device.Level != BatteryLevel.Unknown)
                        {
                            builder.Append(" ").Append(DeviceBattery.LevelName(device.Level));
                            if (device.Percent >= 0)
                                builder.Append("（固件原始 ").Append(device.Percent.ToString(CultureInfo.InvariantCulture)).Append("%）");
                            else
                                builder.Append("（设备只上报档位）");
                        }
                        else if (device.Supported && device.Percent >= 0)
                            builder.Append(" ").Append(device.Percent.ToString(CultureInfo.InvariantCulture)).Append('%');
                        else
                            builder.Append(" 无可读电量");
                        if (device.Stale) builder.Append("（本次读取超时，显示上次值）");
                        builder.AppendLine();
                        if (!string.IsNullOrEmpty(device.Detail))
                            builder.AppendLine("  " + device.Detail);
                    }
                }

                bool otherHeader = false;
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceBattery device = _snapshot.Devices[i];
                    if (device.Category != DeviceCategory.Other) continue;
                    if (!otherHeader)
                    {
                        builder.AppendLine();
                        builder.AppendLine("接收器/其他：");
                        otherHeader = true;
                    }
                    builder.Append("· ").Append(device.Label).Append("：").Append(device.Detail).AppendLine();
                }
            }

            builder.AppendLine();
            builder.AppendLine("说明：支持标准 HID 电池字段（Feature/Input 报告）、罗技 HID++ 2.0、黑爵 2.4G 协议和已识别的蓝牙设备；其余私有协议需按品牌单独适配。");
            builder.AppendLine("设备提供档位（0x1004）时按档位显示；只报原始百分比时按百分比显示。如罗技 K270 固件只返回 5% 或 30%，30% 对应“良好”。");
            builder.AppendLine("若设备显示“USB 有线”但实际工作在 2.4G：说明接收器没有暴露可识别的无线特征，可运行仓库内 tools/HidDump 采集该设备的 HID 信息。");
            MessageBox.Show(builder.ToString(), "设备状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static int ReadSetting(string name, int defaultValue)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath))
                {
                    if (key != null)
                    {
                        object value = key.GetValue(name);
                        if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
            }
            return defaultValue;
        }

        private static bool SettingExists(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath))
                {
                    return key != null && key.GetValue(name) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WriteSetting(string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (key != null) key.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }

        protected override void ExitThreadCore()
        {
            _exiting = true;
            _timer.Stop();
            _timer.Dispose();
            _watchdog.Stop();
            _watchdog.Dispose();

            _notify.ContextMenuStrip = null;
            _notify.Visible = false;
            _notify.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _panel.Close();
            _panel.Dispose();
            if (_deviceWindow != null) _deviceWindow.DestroyHandle();
            _menu.Dispose();
            base.ExitThreadCore();
        }
    }

    internal sealed class DeviceChangeWindow : NativeWindow
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private IntPtr _notification;

        public event EventHandler DeviceChanged;

        public DeviceChangeWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "BatteryTrayDeviceChange";
            CreateHandle(cp);

            Native.DEV_BROADCAST_DEVICEINTERFACE filter = new Native.DEV_BROADCAST_DEVICEINTERFACE();
            filter.dbcc_size = Marshal.SizeOf(typeof(Native.DEV_BROADCAST_DEVICEINTERFACE));
            filter.dbcc_devicetype = Native.DBT_DEVTYP_DEVICEINTERFACE;
            filter.dbcc_classguid = Native.HidInterfaceGuid;
            _notification = Native.RegisterDeviceNotification(Handle, ref filter, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                EventHandler handler = DeviceChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public override void DestroyHandle()
        {
            if (_notification != IntPtr.Zero)
            {
                Native.UnregisterDeviceNotification(_notification);
                _notification = IntPtr.Zero;
            }
            base.DestroyHandle();
        }
    }

    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "2.4GBatteryTray";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(ValueName, false);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class TrayIconFactory
    {
        public static Icon Create(int percent, bool hasBattery)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    Color outline = IsLightTheme()
                        ? Color.FromArgb(155, 45, 45, 45)
                        : Color.FromArgb(190, 245, 245, 245);

                    using (Pen pen = new Pen(outline, 2f))
                    {
                        graphics.DrawRectangle(pen, 6, 9, 18, 14);
                        using (SolidBrush nub = new SolidBrush(outline))
                            graphics.FillRectangle(nub, 24, 13, 3, 6);
                    }

                    if (hasBattery && percent >= 0)
                    {
                        Color fill;
                        if (percent <= 20) fill = Color.FromArgb(200, 214, 69, 69);
                        else if (percent <= 60) fill = Color.FromArgb(200, 217, 146, 45);
                        else fill = Color.FromArgb(200, 46, 160, 96);

                        int width = (int)Math.Round(14 * (percent / 100.0));
                        if (percent == 0) width = 2;
                        if (width > 0)
                        {
                            using (SolidBrush brush = new SolidBrush(fill))
                                graphics.FillRectangle(brush, 8, 11, width, 10);
                        }
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        private static bool IsLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("SystemUsesLightTheme");
                        if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
