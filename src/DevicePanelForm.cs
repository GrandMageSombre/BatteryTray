using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    internal sealed class DevicePanelForm : Form
    {
        private const int PanelWidth = 220;
        private const int PanelHeight = 48;
        private const int SlotWidth = 50;

        private readonly Timer _positionTimer;
        private readonly DeviceBattery[] _slots = new DeviceBattery[4];
        private bool _enabled = true;
        private Color _background;
        private Color _foreground;
        private Color _border;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;

        public DevicePanelForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Opacity = 0.88;
            AccessibleName = "设备电量面板";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            ApplyTheme();

            _positionTimer = new Timer();
            _positionTimer.Interval = 2000;
            _positionTimer.Tick += delegate { Reposition(); };
            _positionTimer.Start();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public bool PanelEnabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (!value)
                {
                    _positionTimer.Stop();
                    Hide();
                }
                else
                {
                    _positionTimer.Start();
                    Reposition();
                }
            }
        }

        public void SetDevices(List<DeviceBattery> devices)
        {
            DeviceCategory[] categories = new DeviceCategory[]
            {
                DeviceCategory.Gamepad,
                DeviceCategory.Mouse,
                DeviceCategory.Keyboard,
                DeviceCategory.Headset
            };

            for (int i = 0; i < categories.Length; i++)
                _slots[i] = DeviceClassifier.BestForCategory(devices, categories[i]);

            ApplyTheme();
            Reposition();
            Invalidate();
        }

        public void Reposition()
        {
            if (!_enabled) { Hide(); return; }

            Rectangle clock;
            if (!TryGetClockRect(out clock))
            {
                Hide();
                return;
            }

            int x = clock.Right - PanelWidth;
            int y = clock.Top - PanelHeight - 2;
            if (y < 0) y = clock.Bottom + 2;
            if (x < 0) x = 0;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            Rectangle screen = Native.IsValidHandle(taskbar)
                ? Screen.FromHandle(taskbar).Bounds
                : Screen.PrimaryScreen.Bounds;
            if (x + PanelWidth > screen.Right) x = screen.Right - PanelWidth;

            _lastX = x;
            _lastY = y;

            if (!Visible)
            {
                Bounds = new Rectangle(x, y, PanelWidth, PanelHeight);
                Show();
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, PanelWidth, PanelHeight,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(_background);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Pen border = new Pen(_border))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            DeviceCategory[] categories = new DeviceCategory[]
            {
                DeviceCategory.Gamepad,
                DeviceCategory.Mouse,
                DeviceCategory.Keyboard,
                DeviceCategory.Headset
            };

            for (int i = 0; i < categories.Length; i++)
            {
                int cellX = 10 + i * SlotWidth;
                DeviceBattery device = _slots[i];
                bool connected = device != null;
                bool stale = device != null && device.Stale;
                Color color = connected
                    ? Color.FromArgb(stale ? 95 : 150, _foreground)
                    : Color.FromArgb(45, _foreground);

                DrawCategoryIcon(g, categories[i], cellX + 11, 5, 28, color);
                DrawStatusGraphic(g, device, cellX + 9, 35, connected, stale);
            }
        }

        private void DrawStatusGraphic(Graphics g, DeviceBattery device, int x, int y, bool connected, bool stale)
        {
            if (device != null && device.Connection == DeviceConnection.UsbWired && !device.Supported)
            {
                DrawPlug(g, x + 8, y - 1, connected, _foreground);
                return;
            }
            if (device != null && device.Connection == DeviceConnection.Bluetooth && !device.Supported)
            {
                DrawBluetooth(g, x + 10, y - 4, connected, _foreground);
                return;
            }

            int width = 32;
            int height = 10;
            bool known = device != null && device.Supported && device.Percent >= 0;
            Color outline = connected
                ? Color.FromArgb(stale ? 70 : 105, _foreground)
                : Color.FromArgb(30, _foreground);
            using (Pen pen = new Pen(outline))
            {
                if (!known) pen.DashStyle = DashStyle.Dot;
                g.DrawRectangle(pen, x, y, width - 2, height - 2);
                using (SolidBrush nub = new SolidBrush(outline))
                    g.FillRectangle(nub, x + width - 2, y + 3, 2, 4);
            }

            if (known)
            {
                int fillWidth = (int)Math.Round((width - 5) * (device.Percent / 100.0));
                if (device.Percent == 0) fillWidth = 2;
                if (fillWidth > 0)
                {
                    int alpha = stale ? 90 : 150;
                    using (SolidBrush fill = new SolidBrush(LevelColor(device.Percent, alpha)))
                        g.FillRectangle(fill, x + 2, y + 2, fillWidth, height - 4);
                }
            }
        }

        private static void DrawCategoryIcon(Graphics g, DeviceCategory category, int x, int y, int size, Color color)
        {
            using (Pen pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                switch (category)
                {
                    case DeviceCategory.Gamepad:
                        using (GraphicsPath body = RoundedRect(new Rectangle(x + 1, y + 8, 26, 13), 6))
                        {
                            g.DrawPath(pen, body);
                        }
                        g.DrawLine(pen, x + 7, y + 13, x + 11, y + 13);
                        g.DrawLine(pen, x + 9, y + 11, x + 9, y + 15);
                        g.DrawEllipse(pen, x + 17, y + 11, 3, 3);
                        g.DrawEllipse(pen, x + 21, y + 15, 3, 3);
                        break;
                    case DeviceCategory.Mouse:
                        using (GraphicsPath body = RoundedRect(new Rectangle(x + 7, y + 1, 14, 26), 7))
                        {
                            g.DrawPath(pen, body);
                        }
                        g.DrawLine(pen, x + 14, y + 2, x + 14, y + 9);
                        g.DrawEllipse(pen, x + 12, y + 7, 4, 5);
                        break;
                    case DeviceCategory.Keyboard:
                        using (GraphicsPath body = RoundedRect(new Rectangle(x + 1, y + 8, 26, 15), 3))
                        {
                            g.DrawPath(pen, body);
                        }
                        g.DrawLine(pen, x + 5, y + 13, x + 23, y + 13);
                        g.DrawLine(pen, x + 5, y + 17, x + 23, y + 17);
                        g.DrawLine(pen, x + 9, y + 20, x + 19, y + 20);
                        break;
                    case DeviceCategory.Headset:
                        g.DrawArc(pen, x + 4, y + 3, 20, 20, 180, 180);
                        using (GraphicsPath left = RoundedRect(new Rectangle(x + 1, y + 13, 6, 12), 2))
                        {
                            g.DrawPath(pen, left);
                        }
                        using (GraphicsPath right = RoundedRect(new Rectangle(x + 21, y + 13, 6, 12), 2))
                        {
                            g.DrawPath(pen, right);
                        }
                        g.DrawLine(pen, x + 24, y + 24, x + 18, y + 26);
                        g.DrawEllipse(pen, x + 15, y + 24, 4, 3);
                        break;
                }
            }
        }

        private static void DrawPlug(Graphics g, int x, int y, bool connected, Color foreground)
        {
            Color color = connected ? Color.FromArgb(125, foreground) : Color.FromArgb(35, foreground);
            using (Pen pen = new Pen(color, 1.4f))
            {
                g.DrawRectangle(pen, x + 4, y + 5, 12, 8);
                g.DrawLine(pen, x + 16, y + 7, x + 20, y + 7);
                g.DrawLine(pen, x + 16, y + 11, x + 20, y + 11);
                g.DrawLine(pen, x + 8, y + 2, x + 8, y + 5);
                g.DrawLine(pen, x + 12, y + 2, x + 12, y + 5);
            }
        }

        private static void DrawBluetooth(Graphics g, int x, int y, bool connected, Color foreground)
        {
            Color color = connected ? Color.FromArgb(130, foreground) : Color.FromArgb(35, foreground);
            using (Pen pen = new Pen(color, 1.6f))
            {
                g.DrawLine(pen, x + 7, y + 2, x + 7, y + 16);
                g.DrawLine(pen, x + 7, y + 2, x + 14, y + 7);
                g.DrawLine(pen, x + 14, y + 7, x + 3, y + 13);
                g.DrawLine(pen, x + 3, y + 5, x + 14, y + 11);
                g.DrawLine(pen, x + 14, y + 11, x + 7, y + 16);
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

        private static Color LevelColor(int percent, int alpha)
        {
            if (percent <= 20) return Color.FromArgb(alpha, 214, 69, 69);
            if (percent <= 60) return Color.FromArgb(alpha, 217, 146, 45);
            return Color.FromArgb(alpha, 46, 160, 96);
        }

        private void ApplyTheme()
        {
            bool light = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("SystemUsesLightTheme");
                        if (value != null)
                            light = Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
                    }
                }
            }
            catch
            {
            }

            if (light)
            {
                _background = Color.FromArgb(243, 243, 243);
                _foreground = Color.FromArgb(30, 30, 30);
                _border = Color.FromArgb(205, 205, 205);
            }
            else
            {
                _background = Color.FromArgb(31, 31, 31);
                _foreground = Color.FromArgb(245, 245, 245);
                _border = Color.FromArgb(70, 70, 70);
            }
        }

        private static bool TryGetClockRect(out Rectangle clock)
        {
            clock = Rectangle.Empty;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (!Native.IsValidHandle(taskbar)) return false;

            IntPtr notify = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            IntPtr clockWindow = notify != IntPtr.Zero
                ? Native.FindWindowEx(notify, IntPtr.Zero, "TrayClockWClass", null)
                : IntPtr.Zero;
            IntPtr target = clockWindow != IntPtr.Zero ? clockWindow : notify;
            if (target == IntPtr.Zero) return false;

            Native.RECT rect;
            if (!Native.GetWindowRect(target, out rect)) return false;
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

            Rectangle screen = Screen.FromHandle(taskbar).Bounds;
            if (rect.Top >= screen.Bottom || rect.Bottom <= screen.Top) return false;
            if (rect.Bottom > screen.Bottom) rect.Bottom = screen.Bottom;

            Rectangle result = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (result.Height > result.Width * 2) return false;
            clock = result;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _positionTimer.Stop();
                _positionTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
