using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
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
        private const int PollIntervalMilliseconds = 10000;
        private const string SettingsKeyPath = @"Software\2.4GBattery";

        private readonly SynchronizationContext _ui;
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private ToolStripMenuItem _barItem;
        private ToolStripMenuItem _autoStartItem;
        private readonly ClockBarForm _bar;
        private readonly System.Windows.Forms.Timer _timer;
        private BatterySnapshot _snapshot;
        private DateTime _lastSuccess = DateTime.MinValue;
        private Icon _trayIcon;
        private int _lastIconPercent = int.MinValue;
        private bool _showBar;
        private bool _exiting;
        private bool _suppressAutoStartEvent;
        private int _polling;
        private int _failedPolls;
        private int _cachedPercent;

        public TrayContext()
        {
            _ui = SynchronizationContext.Current;
            if (_ui == null) _ui = new WindowsFormsSynchronizationContext();

            _showBar = ReadSetting("ShowBar", 1) == 1;
            _cachedPercent = ReadSetting("LastPercent", -1);
            bool firstRun = !SettingExists("AutoStart");
            bool autoStart = firstRun || AutoStart.IsEnabled();

            _bar = new ClockBarForm();
            _menu = BuildMenu(autoStart);

            _trayIcon = TrayIconFactory.Create(-1);
            _notify = new NotifyIcon();
            _notify.Icon = _trayIcon;
            _notify.Text = "2.4G 电量";
            _notify.ContextMenuStrip = _menu;
            _notify.DoubleClick += delegate { ShowStatus(); };
            _notify.Visible = true;

            _bar.BarEnabled = _showBar;

            if (firstRun)
            {
                AutoStart.Set(true);
                WriteSetting("AutoStart", 1);
            }
            else if (autoStart)
            {
                AutoStart.Set(true);
            }

            if (_cachedPercent >= 0)
            {
                _snapshot = new BatterySnapshot();
                _snapshot.Devices.Add(new DeviceBattery
                {
                    Key = "cache",
                    Label = "上次读取",
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

            RefreshNow();
        }

        private ContextMenuStrip BuildMenu(bool autoStart)
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshNow(); };
            menu.Items.Add(refresh);
            menu.Items.Add(new ToolStripSeparator());

            _barItem = new ToolStripMenuItem("显示在时间栏旁");
            _barItem.CheckOnClick = true;
            _barItem.Checked = _showBar;
            _barItem.CheckedChanged += BarItemCheckedChanged;
            menu.Items.Add(_barItem);

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

        private void BarItemCheckedChanged(object sender, EventArgs e)
        {
            _showBar = _barItem.Checked;
            WriteSetting("ShowBar", _showBar ? 1 : 0);
            _bar.BarEnabled = _showBar;
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
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;

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
                finally
                {
                    Interlocked.Exchange(ref _polling, 0);
                }

                try
                {
                    _ui.Post(delegate { ApplySnapshot(snapshot); }, null);
                }
                catch
                {
                }
            });
        }

        private void ApplySnapshot(BatterySnapshot fresh)
        {
            if (_exiting || fresh == null) return;

            bool receiverAbsent = false;
            for (int i = 0; i < fresh.Devices.Count; i++)
            {
                if (fresh.Devices[i].Key == "none") receiverAbsent = true;
            }

            if (fresh.HasSupported)
            {
                _snapshot = fresh;
                _lastSuccess = DateTime.Now;
                _failedPolls = 0;

                DeviceBattery primary = fresh.Primary;
                if (primary != null && primary.Percent >= 0 && primary.Percent != _cachedPercent)
                {
                    _cachedPercent = primary.Percent;
                    WriteSetting("LastPercent", _cachedPercent);
                }
            }
            else if (receiverAbsent)
            {
                _snapshot = fresh;
                _failedPolls = 0;
            }
            else if (_snapshot != null && _snapshot.HasSupported)
            {
                _failedPolls++;
                if (_failedPolls >= 2)
                {
                    for (int i = 0; i < _snapshot.Devices.Count; i++)
                    {
                        if (_snapshot.Devices[i].Supported)
                            _snapshot.Devices[i].Stale = true;
                    }
                }
            }
            else
            {
                _snapshot = fresh;
            }

            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            DeviceBattery primary = _snapshot != null ? _snapshot.Primary : null;
            int percent = primary != null ? primary.Percent : -1;
            bool stale = primary != null && primary.Stale;

            UpdateTrayIcon(percent);
            UpdateTooltip(primary, stale);

            string barText = BuildBarText();
            _bar.SetStatus(barText, percent, percent < 0);
        }

        private string BuildBarText()
        {
            List<DeviceBattery> supported = SupportedDevices();
            if (supported.Count == 0) return "2.4G --";

            StringBuilder builder = new StringBuilder("2.4G ");
            for (int i = 0; i < supported.Count; i++)
            {
                if (i > 0) builder.Append(" / ");
                if (supported[i].Percent >= 0)
                    builder.Append(supported[i].Percent.ToString(CultureInfo.InvariantCulture)).Append('%');
                else
                    builder.Append("--");
            }
            return builder.ToString();
        }

        private List<DeviceBattery> SupportedDevices()
        {
            List<DeviceBattery> list = new List<DeviceBattery>();
            if (_snapshot == null) return list;
            for (int i = 0; i < _snapshot.Devices.Count; i++)
            {
                if (_snapshot.Devices[i].Supported) list.Add(_snapshot.Devices[i]);
            }
            return list;
        }

        private void UpdateTrayIcon(int percent)
        {
            if (_lastIconPercent == percent) return;

            _lastIconPercent = percent;
            Icon old = _trayIcon;
            _trayIcon = TrayIconFactory.Create(percent);
            _notify.Icon = _trayIcon;
            if (old != null) old.Dispose();
        }

        private void UpdateTooltip(DeviceBattery primary, bool stale)
        {
            StringBuilder builder = new StringBuilder();
            if (primary != null && primary.Percent >= 0)
                builder.Append("2.4G ").Append(primary.Percent.ToString(CultureInfo.InvariantCulture)).Append('%');
            else
                builder.Append("2.4G 电量未知");
            if (stale) builder.Append("（上次读取）");

            List<string> parts = new List<string>();
            List<DeviceBattery> supported = SupportedDevices();
            for (int i = 0; i < supported.Count; i++)
            {
                DeviceBattery device = supported[i];
                if (device.Percent >= 0)
                    parts.Add("罗技 " + device.Percent.ToString(CultureInfo.InvariantCulture) + "%");
            }
            if (_snapshot != null)
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceBattery device = _snapshot.Devices[i];
                    if (!device.Supported && device.Label.StartsWith("SONiX", StringComparison.Ordinal))
                        parts.Add("SONiX 不支持");
                }
            }
            if (parts.Count > 0)
            {
                builder.Append(" · ");
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) builder.Append(" · ");
                    builder.Append(parts[i]);
                }
            }

            string text = builder.ToString();
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            _notify.Text = text;
        }

        private void ShowStatus()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("2.4G 设备电量");
            builder.AppendLine("刷新间隔：10 秒");
            if (_lastSuccess != DateTime.MinValue)
                builder.AppendLine("最近成功读取：" + _lastSuccess.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine();

            if (_snapshot == null || _snapshot.Devices.Count == 0)
            {
                builder.AppendLine("正在读取设备电量...");
            }
            else
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceBattery device = _snapshot.Devices[i];
                    if (device.Supported)
                    {
                        builder.Append("· ").Append(device.Label).Append("：");
                        builder.Append(device.Percent >= 0 ? device.Percent.ToString(CultureInfo.InvariantCulture) + "%" : "未知");
                        if (device.Stale) builder.Append("（本次读取超时，显示上次值）");
                        builder.AppendLine();
                        if (!string.IsNullOrEmpty(device.Detail))
                            builder.AppendLine("  " + device.Detail);
                    }
                    else
                    {
                        builder.Append("· ").Append(device.Label).Append("：").Append(device.Detail).AppendLine();
                    }
                }
            }

            builder.AppendLine();
            builder.AppendLine("说明：罗技设备通过 HID++ 2.0 电池状态特性读取；未提供标准电量特性的接收器无法读取。");
            MessageBox.Show(builder.ToString(), "2.4G 设备状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            _notify.ContextMenuStrip = null;
            _notify.Visible = false;
            _notify.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _bar.Close();
            _bar.Dispose();
            _menu.Dispose();
            base.ExitThreadCore();
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
        public static Icon Create(int percent)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.Clear(Color.Transparent);

                    Color background;
                    if (percent < 0) background = Color.FromArgb(105, 105, 105);
                    else if (percent <= 20) background = Color.FromArgb(214, 69, 69);
                    else if (percent <= 60) background = Color.FromArgb(217, 146, 45);
                    else background = Color.FromArgb(46, 160, 96);

                    using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, 30, 30), 8))
                    using (SolidBrush brush = new SolidBrush(background))
                        graphics.FillPath(brush, path);

                    string text = percent >= 0 ? percent.ToString(CultureInfo.InvariantCulture) : "?";
                    float fontSize = text.Length >= 3 ? 9.5f : 14f;
                    using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        SizeF measured = graphics.MeasureString(text, font);
                        graphics.DrawString(text, font, textBrush, (32f - measured.Width) / 2f, (32f - measured.Height) / 2f);
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

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
