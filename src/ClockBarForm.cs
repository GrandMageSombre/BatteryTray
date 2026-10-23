using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    internal sealed class ClockBarForm : Form
    {
        private readonly Timer _positionTimer;
        private readonly Font _font;
        private string _text = "2.4G --";
        private int _percent = -1;
        private bool _stale = true;
        private bool _enabled = true;
        private Color _background;
        private Color _foreground;
        private Color _border;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;
        private int _lastWidth = int.MinValue;
        private int _lastHeight = int.MinValue;

        public ClockBarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
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

        public bool BarEnabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (!value)
                    Hide();
                else
                    Reposition();
            }
        }

        public void SetStatus(string text, int percent, bool stale)
        {
            _text = text;
            _percent = percent;
            _stale = stale;
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

            int width = MeasureBarWidth();
            int height = Math.Max(22, clock.Height - 8);
            int x = clock.Left - width - 6;
            int y = clock.Top + (clock.Height - height) / 2;
            if (x < 0 || width <= 0)
            {
                Hide();
                return;
            }

            if (x == _lastX && y == _lastY && width == _lastWidth && height == _lastHeight && Visible)
                return;

            _lastX = x;
            _lastY = y;
            _lastWidth = width;
            _lastHeight = height;

            if (!Visible)
            {
                Bounds = new Rectangle(x, y, width, height);
                Show();
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, width, height,
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

            int bodyX = 9;
            int bodyY = (Height - 9) / 2;
            Rectangle body = new Rectangle(bodyX, bodyY, 15, 9);
            using (Pen outline = new Pen(_foreground))
                g.DrawRectangle(outline, body);
            using (SolidBrush nub = new SolidBrush(_foreground))
                g.FillRectangle(nub, body.Right, body.Top + 3, 2, 3);

            if (_percent > 0 && !_stale)
            {
                int fillWidth = (int)Math.Round((body.Width - 4) * (_percent / 100.0));
                if (fillWidth > 0)
                {
                    using (SolidBrush fill = new SolidBrush(LevelColor(_percent)))
                        g.FillRectangle(fill, body.X + 2, body.Y + 2, fillWidth, body.Height - 4);
                }
            }

            Rectangle textRect = new Rectangle(body.Right + 8, 0, Width - (body.Right + 8) - 4, Height);
            TextRenderer.DrawText(g, _text, _font, textRect, _foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private int MeasureBarWidth()
        {
            int textWidth = TextRenderer.MeasureText(_text, _font).Width;
            return Math.Max(62, textWidth + 9 + 15 + 8 + 6 + 10);
        }

        private static Color LevelColor(int percent)
        {
            if (percent <= 20) return Color.FromArgb(214, 69, 69);
            if (percent <= 60) return Color.FromArgb(217, 146, 45);
            return Color.FromArgb(46, 160, 96);
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
                _font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
