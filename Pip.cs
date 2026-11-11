// Pip: a Steam Controller battery widget for Windows.
// Reads the battery report straight from the controller's HID interface
// (wireless puck, USB or Bluetooth) and shows a small always-on-top widget
// that fades in when a controller connects and fades out when it disconnects.
//
// Build: run build.cmd (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Pip
{
    static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ_WRITE = 3;
        public const uint OPEN_EXISTING = 3;
        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)]
            public ushort[] Rest;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr handle, [Out] byte[] buffer, uint count, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);

        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
        static extern uint CM_Get_Device_Interface_List_Size(out uint len, ref Guid guid, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
        static extern uint CM_Get_Device_Interface_List(ref Guid guid, string deviceId, [Out] char[] buffer, uint len, uint flags);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        /// <summary>
        /// Per-monitor DPI awareness. Without it Windows rescales and repositions
        /// our windows on mixed-DPI multi-monitor setups (tiny, misplaced tray menu).
        /// </summary>
        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; // PER_MONITOR_AWARE_V2
            }
            catch (EntryPointNotFoundException) { } // before Windows 10 1703
            SetProcessDPIAware();
        }

        /// <summary>Scale factor (1.0 = 96 DPI) of the monitor containing a screen point.</summary>
        public static float ScaleAt(Point screenPoint)
        {
            try
            {
                POINT p;
                p.X = screenPoint.X;
                p.Y = screenPoint.Y;
                uint dpiX, dpiY;
                if (GetDpiForMonitor(MonitorFromPoint(p, 2), 0, out dpiX, out dpiY) == 0 && dpiX > 0) return dpiX / 96f;
            }
            catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { return g.DpiX / 96f; }
        }

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Paths of every HID interface currently present.</summary>
        public static List<string> ListHidPaths()
        {
            List<string> result = new List<string>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            uint len;
            if (CM_Get_Device_Interface_List_Size(out len, ref guid, null, 0) != 0 || len <= 1) return result;
            char[] buffer = new char[len];
            if (CM_Get_Device_Interface_List(ref guid, null, buffer, len, 0) != 0) return result;
            result.AddRange(new string(buffer).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
            return result;
        }

        public static bool GetCaps(IntPtr handle, out ushort usagePage, out ushort inputLength)
        {
            usagePage = 0;
            inputLength = 0;
            IntPtr data;
            if (!HidD_GetPreparsedData(handle, out data)) return false;
            try
            {
                HIDP_CAPS caps;
                if (HidP_GetCaps(data, out caps) != 0x00110000) return false; // HIDP_STATUS_SUCCESS
                usagePage = caps.UsagePage;
                inputLength = caps.InputReportByteLength;
                return true;
            }
            finally { HidD_FreePreparsedData(data); }
        }
    }

    class ControllerInfo
    {
        public string Path;
        public int Slot;          // 1-4 on the wireless puck, 0 otherwise
        public int Level = -1;    // percent, -1 until the first battery report
        public int ChargeState;   // 1 discharging, 2 charging, 4 charging done
        public long LastSeen = -1;

        public bool Charging { get { return ChargeState == 2; } }
        public bool Full { get { return ChargeState == 4; } }
    }

    /// <summary>
    /// Watches Valve HID interfaces and tracks which controllers are live.
    /// Report layout is from SDL's Steam Controller ("Triton") driver:
    ///   0x42/0x45/0x47  controller state (sent continuously while connected)
    ///   0x43            battery: [1]=charge state, [2]=level percent
    ///   0x46/0x79       wireless status: [1]=1 disconnected, 2 connected
    /// </summary>
    class HidMonitor
    {
        const long PresenceTimeoutMs = 3000;

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Regex ValveId = new Regex(@"vid[_&](0002|02)?28de", RegexOptions.IgnoreCase);
        static readonly Regex PuckSlot = new Regex(@"pid_130[45]&mi_0([2-5])", RegexOptions.IgnoreCase);

        readonly object sync = new object();
        readonly Dictionary<string, ControllerInfo> devices = new Dictionary<string, ControllerInfo>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Start()
        {
            Thread t = new Thread(ScanLoop);
            t.IsBackground = true;
            t.Name = "HID scan";
            t.Start();
        }

        /// <summary>Copies of the controllers that are connected right now.</summary>
        public List<ControllerInfo> Snapshot()
        {
            List<ControllerInfo> list = new List<ControllerInfo>();
            long now = Clock.ElapsedMilliseconds;
            lock (sync)
            {
                foreach (ControllerInfo d in devices.Values)
                {
                    if (d.LastSeen < 0 || now - d.LastSeen > PresenceTimeoutMs) continue;
                    ControllerInfo c = new ControllerInfo();
                    c.Path = d.Path; c.Slot = d.Slot; c.Level = d.Level; c.ChargeState = d.ChargeState;
                    list.Add(c);
                }
            }
            list.Sort(delegate(ControllerInfo a, ControllerInfo b)
            {
                int bySlot = a.Slot.CompareTo(b.Slot);
                return bySlot != 0 ? bySlot : string.CompareOrdinal(a.Path, b.Path);
            });
            return list;
        }

        void ScanLoop()
        {
            while (true)
            {
                try { Scan(); } catch { }
                Thread.Sleep(2000);
            }
        }

        void Scan()
        {
            List<string> paths = Native.ListHidPaths();
            HashSet<string> present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            lock (sync) { rejected.RemoveWhere(delegate(string p) { return !present.Contains(p); }); }

            foreach (string path in paths)
            {
                if (!ValveId.IsMatch(path)) continue;
                lock (sync) { if (devices.ContainsKey(path) || rejected.Contains(path)) continue; }

                IntPtr handle = Native.CreateFile(path, Native.GENERIC_READ, Native.FILE_SHARE_READ_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                ushort usagePage = 0, inputLength = 0;
                bool usable = handle != Native.INVALID_HANDLE
                    && Native.GetCaps(handle, out usagePage, out inputLength)
                    && usagePage >= 0xFF00      // vendor-defined collection, not the mouse/keyboard ones
                    && inputLength >= 16;
                if (!usable)
                {
                    if (handle != Native.INVALID_HANDLE) Native.CloseHandle(handle);
                    lock (sync) { rejected.Add(path); }
                    continue;
                }

                ControllerInfo info = new ControllerInfo();
                info.Path = path;
                Match m = PuckSlot.Match(path);
                info.Slot = m.Success ? int.Parse(m.Groups[1].Value) - 1 : 0;
                lock (sync) { devices[path] = info; }

                int bufferSize = Math.Max((int)inputLength, 64);
                Thread reader = new Thread(delegate() { ReadLoop(info, handle, bufferSize); });
                reader.IsBackground = true;
                reader.Name = "HID read";
                reader.Start();
            }
        }

        void ReadLoop(ControllerInfo info, IntPtr handle, int bufferSize)
        {
            byte[] buffer = new byte[bufferSize];
            uint read;
            // ReadFile fails once the device is unplugged, which ends the loop.
            while (Native.ReadFile(handle, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
            {
                if (read < 2) continue;
                long now = Clock.ElapsedMilliseconds;
                switch (buffer[0])
                {
                    case 0x42:
                    case 0x45:
                    case 0x47:
                        lock (sync) { info.LastSeen = now; }
                        break;
                    case 0x43:
                        if (read >= 15)
                        {
                            lock (sync)
                            {
                                info.ChargeState = buffer[1];
                                info.Level = Math.Min(100, (int)buffer[2]);
                                info.LastSeen = now;
                            }
                        }
                        break;
                    case 0x46:
                    case 0x79:
                        if (buffer[1] == 1)
                        {
                            lock (sync) { info.LastSeen = -1; info.Level = -1; info.ChargeState = 0; }
                        }
                        break;
                }
            }
            Native.CloseHandle(handle);
            lock (sync) { devices.Remove(info.Path); }
        }
    }

    class Widget : Form
    {
        const int RowWidth = 236;
        const int RowHeight = 48;
        const int Margin16 = 16;
        const double MaxOpacity = 0.95;
        const int FadeIntervalMs = 33;
        const int IdleIntervalMs = 250;

        static readonly Color Background = Color.FromArgb(24, 26, 31);
        static readonly Color Outline = Color.FromArgb(205, 208, 215);
        static readonly Color SubText = Color.FromArgb(150, 155, 165);

        readonly HidMonitor monitor;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer;
        // Scale of the monitor the widget is on; fonts are rebuilt whenever it changes.
        float scale;
        Font bigFont, smallFont;
        readonly string logPath;
        readonly string positionFile;

        List<ControllerInfo> shown = new List<ControllerInfo>();
        string lastSignature = null;
        IntPtr trayIconHandle = IntPtr.Zero;
        bool dragging;
        Point dragOffset;
        int anchorLeft, anchorBottom;

        // When false the widget stays hidden and only the tray icon is used.
        bool widgetEnabled = true;
        bool hovering, closePressed;
        readonly string hiddenFlagFile;
        readonly ToolStripMenuItem showItem;

        public Widget(HidMonitor monitor, string logPath)
        {
            this.monitor = monitor;
            this.logPath = logPath;
            positionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pip", "position.txt");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            DoubleBuffered = true;
            Opacity = 0;
            Text = "Pip";

            LoadPosition();
            hiddenFlagFile = Path.Combine(Path.GetDirectoryName(positionFile), "widget-hidden");
            widgetEnabled = !File.Exists(hiddenFlagFile);

            ContextMenuStrip menu = new ContextMenuStrip();
            showItem = new ToolStripMenuItem("Show widget");
            showItem.Checked = widgetEnabled;
            showItem.Click += delegate { SetWidgetEnabled(!widgetEnabled, false); };
            menu.Items.Add(showItem);
            menu.Items.Add("Steam controller settings", null, delegate { OpenSteamControllerSettings(); });
            ToolStripMenuItem autostart = new ToolStripMenuItem("Start with Windows");
            autostart.Checked = Autostart.IsEnabled();
            autostart.Click += delegate
            {
                Autostart.Set(!autostart.Checked);
                autostart.Checked = Autostart.IsEnabled();
            };
            menu.Items.Add(autostart);
            menu.Items.Add("Reset position", null, delegate { ResetPosition(); SavePosition(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Application.Exit(); });

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { SetWidgetEnabled(!widgetEnabled, false); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = FadeIntervalMs;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        void SetScale(float value)
        {
            if (value == scale && bigFont != null) return;
            scale = value;
            if (bigFont != null) bigFont.Dispose();
            if (smallFont != null) smallFont.Dispose();
            // Pixel-sized fonts (13pt and 8pt at 96 DPI) so text follows the monitor's scale.
            bigFont = new Font("Segoe UI Semibold", 13f * 96f / 72f * scale, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 8f * 96f / 72f * scale, GraphicsUnit.Pixel);
            ApplySize();
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x02E0) // WM_DPICHANGED: moved to a monitor with different scaling
            {
                SetScale((m.WParam.ToInt64() & 0xFFFF) / 96f);
                if (!dragging) Location = new Point(anchorLeft, anchorBottom - Height);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080   // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                            | 0x08000000   // WS_EX_NOACTIVATE: never steal focus from a game
                            | 0x00000008;  // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // DWMWCP_ROUND (Windows 11)
            if (Native.DwmSetWindowAttribute(Handle, 33, ref round, 4) != 0) ApplyRegion();
        }

        void ApplyRegion()
        {
            using (GraphicsPath path = Rounded(new RectangleF(0, 0, Width, Height), S(10)))
            {
                Region = new Region(path);
            }
        }

        // ---- state -------------------------------------------------------

        void Tick()
        {
            List<ControllerInfo> list = monitor.Snapshot();
            string signature = Signature(list);
            if (signature != lastSignature)
            {
                lastSignature = signature;
                Log(signature.Length == 0 ? "no controller connected" : signature);
                UpdateTray(list);
                if (list.Count > 0)
                {
                    shown = list;
                    Relayout();
                }
                Invalidate();
            }

            bool want = list.Count > 0 && widgetEnabled;
            if (want && !Visible)
            {
                Opacity = 0;
                Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
                Visible = true;
            }
            if (!Visible)
            {
                timer.Interval = IdleIntervalMs;
                return;
            }

            double target = want ? MaxOpacity : 0;
            double next = Opacity < target ? Math.Min(target, Opacity + 0.08) : Math.Max(target, Opacity - 0.08);
            if (Math.Abs(next - Opacity) > 0.001) Opacity = next;
            if (!want && next <= 0.001)
            {
                Hide();
                hovering = false;
            }

            // Tick fast only while a fade is running; otherwise poll lazily.
            bool fading = Visible && Math.Abs(next - target) > 0.001;
            timer.Interval = fading ? FadeIntervalMs : IdleIntervalMs;
        }

        /// <summary>Shows or hides the widget itself; the tray icon keeps running either way.</summary>
        void SetWidgetEnabled(bool enabled, bool explainHowToRestore)
        {
            widgetEnabled = enabled;
            showItem.Checked = enabled;
            timer.Interval = FadeIntervalMs; // start the fade right away
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hiddenFlagFile));
                if (enabled) File.Delete(hiddenFlagFile);
                else File.WriteAllText(hiddenFlagFile, "");
            }
            catch { }
            if (!enabled && explainHowToRestore)
            {
                tray.ShowBalloonTip(4000, "Pip is hidden", "Battery level is still in the tray. Double-click the tray icon to bring the widget back.", ToolTipIcon.None);
            }
        }

        /// <summary>Opens Steam's Settings window on the Controller page (starts Steam if needed).</summary>
        void OpenSteamControllerSettings()
        {
            try { Process.Start("steam://open/settings/controller"); }
            catch
            {
                tray.ShowBalloonTip(4000, "Couldn't open Steam", "Steam doesn't appear to be installed on this PC.", ToolTipIcon.None);
            }
        }

        RectangleF CloseRect()
        {
            return new RectangleF(ClientSize.Width - 24 * scale, 4 * scale, 20 * scale, 20 * scale);
        }

        static string Signature(List<ControllerInfo> list)
        {
            string s = "";
            foreach (ControllerInfo c in list)
            {
                if (s.Length > 0) s += "; ";
                s += Label(c, list.Count) + ": " + (c.Level < 0 ? "level pending" : c.Level + "%") + StatusSuffix(c, " (", ")");
            }
            return s;
        }

        static string Label(ControllerInfo c, int count)
        {
            return count > 1 && c.Slot > 0 ? "Controller " + c.Slot : "Steam Controller";
        }

        static string StatusSuffix(ControllerInfo c, string before, string after)
        {
            if (c.Charging) return before + "Charging" + after;
            if (c.Full) return before + "Charged" + after;
            return "";
        }

        void Log(string message)
        {
            if (logPath == null) return;
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine); }
            catch { }
        }

        // ---- position ----------------------------------------------------

        void ApplySize()
        {
            Size size = new Size(S(RowWidth), S(RowHeight) * Math.Max(1, shown.Count));
            if (size != ClientSize)
            {
                ClientSize = size;
                if (Region != null) ApplyRegion();
            }
        }

        void Relayout()
        {
            ApplySize();
            // Don't yank the widget back to its anchor while the user is dragging it.
            if (!dragging) Location = new Point(anchorLeft, anchorBottom - Height);
        }

        void ResetPosition()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            SetScale(Native.ScaleAt(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)));
            anchorLeft = area.Right - Width - S(Margin16);
            anchorBottom = area.Bottom - S(Margin16);
            Location = new Point(anchorLeft, anchorBottom - Height);
        }

        void LoadPosition()
        {
            ResetPosition();
            try
            {
                string[] parts = File.ReadAllText(positionFile).Trim().Split(',');
                int left = int.Parse(parts[0]), bottom = int.Parse(parts[1]);
                // Only restore a position that is still on a connected monitor.
                Point probe = new Point(left + 20, bottom - 20);
                foreach (Screen screen in Screen.AllScreens)
                {
                    if (!screen.WorkingArea.Contains(probe)) continue;
                    SetScale(Native.ScaleAt(probe));
                    anchorLeft = left;
                    anchorBottom = bottom;
                    Location = new Point(left, bottom - Height);
                    break;
                }
            }
            catch { }
        }

        void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(positionFile));
                File.WriteAllText(positionFile, anchorLeft + "," + anchorBottom);
            }
            catch { }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (CloseRect().Contains(e.Location)) closePressed = true;
                else { dragging = true; dragOffset = e.Location; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Point p = Cursor.Position;
                Location = new Point(p.X - dragOffset.X, p.Y - dragOffset.Y);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (closePressed)
            {
                closePressed = false;
                if (CloseRect().Contains(e.Location)) SetWidgetEnabled(false, true);
            }
            if (dragging)
            {
                dragging = false;
                anchorLeft = Left;
                anchorBottom = Bottom;
                SavePosition();
            }
            base.OnMouseUp(e);
        }

        // ---- drawing -----------------------------------------------------

        static Color LevelColor(int level)
        {
            if (level >= 50) return Color.FromArgb(76, 201, 110);
            if (level >= 20) return Color.FromArgb(240, 180, 50);
            return Color.FromArgb(235, 80, 70);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Battery glyph; all sizes derive from the body rectangle.</summary>
        static void DrawBattery(Graphics g, RectangleF body, int level, bool charging, Color outline)
        {
            float u = body.Height / 20f;
            using (GraphicsPath shell = Rounded(body, 4 * u))
            using (Pen pen = new Pen(outline, 1.6f * u))
            {
                g.DrawPath(pen, shell);
            }
            using (GraphicsPath nub = Rounded(new RectangleF(body.Right + 1.5f * u, body.Y + 6 * u, 3 * u, 8 * u), 1.2f * u))
            using (SolidBrush brush = new SolidBrush(outline))
            {
                g.FillPath(brush, nub);
            }
            if (level > 0)
            {
                RectangleF inner = RectangleF.Inflate(body, -3 * u, -3 * u);
                inner.Width = Math.Max(2 * u, inner.Width * level / 100f);
                using (GraphicsPath fill = Rounded(inner, 1.8f * u))
                using (SolidBrush brush = new SolidBrush(LevelColor(level)))
                {
                    g.FillPath(brush, fill);
                }
            }
            if (charging)
            {
                float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2;
                PointF[] bolt = new PointF[]
                {
                    new PointF(cx + 1.5f * u, cy - 7.5f * u), new PointF(cx - 4.5f * u, cy + 1.2f * u),
                    new PointF(cx - 0.6f * u, cy + 1.2f * u), new PointF(cx - 1.5f * u, cy + 7.5f * u),
                    new PointF(cx + 4.5f * u, cy - 1.2f * u), new PointF(cx + 0.6f * u, cy - 1.2f * u)
                };
                using (Pen edge = new Pen(Background, 2.2f * u))
                {
                    edge.LineJoin = LineJoin.Round;
                    g.DrawPolygon(edge, bolt);
                }
                g.FillPolygon(Brushes.White, bolt);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Background);

            for (int i = 0; i < shown.Count; i++)
            {
                ControllerInfo c = shown[i];
                float top = i * RowHeight * scale;
                DrawBattery(g, new RectangleF(15 * scale, top + 14 * scale, 38 * scale, 20 * scale), c.Level, c.Charging, Outline);

                string percent = c.Level < 0 ? "…" : c.Level + "%";
                g.DrawString(percent, bigFont, Brushes.White, 64 * scale, top + 4 * scale);
                using (SolidBrush brush = new SolidBrush(SubText))
                {
                    g.DrawString(Label(c, shown.Count) + StatusSuffix(c, " · ", ""), smallFont, brush, 66 * scale, top + 27 * scale);
                }
            }

            if (hovering)
            {
                // Close button: hides the widget, the tray icon stays.
                RectangleF r = RectangleF.Inflate(CloseRect(), -6 * scale, -6 * scale);
                using (Pen pen = new Pen(Outline, 1.5f * scale))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                    g.DrawLine(pen, r.Right, r.Top, r.Left, r.Bottom);
                }
            }
        }

        // ---- tray --------------------------------------------------------

        void UpdateTray(List<ControllerInfo> list)
        {
            ControllerInfo first = list.Count > 0 ? list[0] : null;
            string text = list.Count == 0 ? "Pip: no controller connected" : Signature(list);
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;

            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Color outline = first == null ? Color.FromArgb(130, 130, 130) : Color.White;
                    DrawBattery(g, new RectangleF(2, 8, 25, 16), first == null ? 0 : first.Level, first != null && first.Charging, outline);
                }
                IntPtr handle = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(handle);
                if (trayIconHandle != IntPtr.Zero) Native.DestroyIcon(trayIconHandle);
                trayIconHandle = handle;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                if (bigFont != null) bigFont.Dispose();
                if (smallFont != null) smallFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Pip";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(ValueName) != null;
            }
        }

        static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }

        public static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>If autostart is on but the exe has been moved, point it at the new location.</summary>
        public static void RefreshPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    string current = key.GetValue(ValueName) as string;
                    if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(ValueName, Command);
                    }
                }
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Pip.SingleInstance", out created))
            {
                if (!created) return;

                string logPath = null;
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (args[i] == "--log") logPath = args[i + 1];
                }

                Native.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Autostart.RefreshPath();

                HidMonitor monitor = new HidMonitor();
                monitor.Start();
                using (Widget widget = new Widget(monitor, logPath))
                {
                    Application.Run(new ApplicationContext());
                }
            }
        }
    }
}
