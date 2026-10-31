using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Gdi = System.Drawing.Drawing2D;

namespace CodeUsageMonit {
    // Compact native taskbar child (not a system-reserved space). No DLL injection,
    // new AppBar, topmost overlay or moving of Explorer windows.
    public sealed class TaskbarPanel : IDisposable {
        // Win11 composes the taskbar with DirectComposition: a plain child HWND is never shown
        // on screen, while a WS_EX_LAYERED child (Win8+ manifest required) is. A child of
        // DesktopWindowContentBridge shows but gets no hit tests, so it hangs off Shell_TrayWnd.
        public static readonly bool Available = true;
        private HostedButton host;
        private IntPtr shell;
        private readonly DispatcherTimer timer;
        private readonly Func<List<ProviderState>> states;
        private readonly Action<Rect> toggle;
        private readonly Func<bool> expanded;
        private readonly Action menu;
        private readonly Func<string, Drawing.Bitmap> logo;
        private readonly Dispatcher dispatcher;
        private readonly Forms.ToolTip tooltip = new Forms.ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 20000, ShowAlways = true };
        private bool busy, disposed;
        // Consecutive refreshes that could not place the summary for a passing reason (UI Automation
        // busy, taskbar buttons animating): it stays where it was until several fail in a row.
        private int misses;
        private const int MissesBeforeHiding = 3;
        public bool Enabled;
        public string Note { get; private set; }
        // Screen rectangle (physical pixels) of the summary while it is shown, else Rect.Empty.
        public Rect Bounds { get { RECT r; return host != null && host.IsHandleCreated && GetWindowRect(host.Handle, out r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty; } }
        // show: toggles the detail flyout above the given rectangle; isOpen: whether it is open.
        public TaskbarPanel(Func<List<ProviderState>> readings, Action<Rect> show, Func<bool> isOpen, Action contextMenu, Func<string, Drawing.Bitmap> icon) {
            states = readings; toggle = show; expanded = isOpen; menu = contextMenu; logo = icon; dispatcher = Dispatcher.CurrentDispatcher;
            Note = ""; timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async delegate { await Refresh(); }; timer.Start();
        }
        public void Repaint() { if (host != null) { host.Expanded = expanded(); host.Render(); } }
        // The pointer entering (with the summary's screen rectangle) or leaving the summary.
        public event Action<Rect> HoverStart;
        public event Action HoverEnd;
        // What fits in a given width: the focus item (two lines), one progress ring per other
        // platform (at most MaxRings) and a "+N" chip for the rest; fewer rings first, then the
        // focus item alone, then rings only (the focus platform's ring first).
        public sealed class Plan { public bool Primary; public int Rings; public int More; public int Width; }
        public const int RingWidth = 26, ChipWidth = 26, MaxRings = 4;
        public static Plan Fit(int width, int primary, int others) {
            const int pad = 6, gap = 8;
            for (int k = Math.Min(others, MaxRings); k >= 0; k--) {
                int more = others - k, w = pad + primary + (k > 0 || more > 0 ? gap : 0) + k * RingWidth + (more > 0 ? ChipWidth : 0) + pad;
                if (w <= width) return new Plan { Primary = true, Rings = k, More = more, Width = w };
            }
            if (pad + primary + pad <= width) return new Plan { Primary = true, Width = pad + primary + pad };
            int total = others + 1;
            for (int k = Math.Min(total, MaxRings); k >= 1; k--) {
                int more = total - k, w = pad + k * RingWidth + (more > 0 ? ChipWidth : 0) + pad;
                if (w <= width) return new Plan { Rings = k, More = more, Width = w };
            }
            return new Plan { Width = Math.Min(width, pad + RingWidth + pad) };
        }
        // Draws the summary into a bitmap with no window (self-test gallery of edge cases).
        public static Drawing.Bitmap Preview(List<ProviderState> data, int width, int height, Drawing.Color back, out Plan plan) {
            using (var button = new HostedButton(IntPtr.Zero, delegate { }, delegate { })) {
                button.Data = data; button.Back = back; button.Cap = (int)Math.Round(350 * Math.Max(.75, height / 48.0));
                plan = button.Layout(width);
                var bitmap = new Drawing.Bitmap(plan.Width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Drawing.Graphics.FromImage(bitmap)) { g.Clear(back); button.Draw(g, plan.Width, height); }
                return bitmap;
            }
        }
        private sealed class HostedButton : Forms.Button {
            private readonly IntPtr parent;
            private readonly Drawing.Font body = new Drawing.Font("Microsoft YaHei UI", 9f);
            private readonly Drawing.Font bold = new Drawing.Font("Microsoft YaHei UI", 9f, Drawing.FontStyle.Bold);
            private readonly Drawing.Font small = new Drawing.Font("Microsoft YaHei UI", 8.4f);
            private readonly Drawing.Font tiny = new Drawing.Font("Microsoft YaHei UI", 7f, Drawing.FontStyle.Bold);
            private static readonly Drawing.StringFormat Typo = new Drawing.StringFormat(Drawing.StringFormat.GenericTypographic) { Trimming = Drawing.StringTrimming.EllipsisCharacter, LineAlignment = Drawing.StringAlignment.Center, FormatFlags = Drawing.StringFormatFlags.NoWrap | Drawing.StringFormatFlags.MeasureTrailingSpaces };
            private Drawing.Bitmap measureBitmap; private Drawing.Graphics measure; private float dpi = 96;
            private bool hover;
            public List<ProviderState> Data = new List<ProviderState>();
            public bool Expanded;
            // Widest the summary may grow (physical pixels); it never fills the whole free gap.
            public int Cap = 350;
            // The focus item's text never takes more than this, so a long platform name cannot push the rings out.
            private const int TextCap = 170;
            // The taskbar's colour next to the summary, sampled only to pick dark or light ink: the
            // summary itself is transparent and the taskbar's own material shows through.
            public Drawing.Color Back = Drawing.Color.FromArgb(32, 32, 32);
            private bool Light { get { return Luma(Back) > 140; } }
            private Drawing.Color Ink { get { return Light ? Color("#1B1D22") : Color("#EDF0F5"); } }
            private Drawing.Color Dim { get { return Light ? Color("#5B6170") : Color("#AEB5C2"); } }
            private Drawing.Color Faint(int alpha) { return Drawing.Color.FromArgb(alpha, Ink); }
            private static double Luma(Drawing.Color c) { return .299 * c.R + .587 * c.G + .114 * c.B; }
            private Drawing.Color Urgent { get { return Color(Theme.Status(Light, 0)); } }
            private Drawing.Color Warn { get { return Color(Theme.Status(Light, 1)); } }
            private Drawing.Color Good { get { return Color(Theme.Status(Light, 2)); } }
            private Drawing.Color Tone(double? score) { return !score.HasValue ? Dim : score < 20 ? Urgent : score < 40 ? Warn : Good; }
            public readonly Dictionary<string, Drawing.Bitmap> Logos = new Dictionary<string, Drawing.Bitmap>();
            private readonly Action Context;
            public event Action<bool> HoverChanged;
            public HostedButton(IntPtr taskbar, Action click, Action context) {
                parent = taskbar; Context = context; FlatStyle = Forms.FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
                TabStop = false; AccessibleRole = Forms.AccessibleRole.PushButton; AccessibleName = "订阅额度摘要 · 打开详情";
                Click += delegate { click(); };
                MouseEnter += delegate { hover = true; Render(); if (HoverChanged != null) HoverChanged(true); };
                MouseLeave += delegate { hover = false; Render(); if (HoverChanged != null) HoverChanged(false); };
            }
            protected override Forms.CreateParams CreateParams {
                get { var p = base.CreateParams; p.Parent = parent; p.Style = (p.Style | 0x40000000 | 0x04000000) & unchecked((int)~0x80000000); p.ExStyle |= 0x08000000 | 0x00080000; return p; }
            }
            protected override void OnHandleCreated(EventArgs e) {
                base.OnHandleCreated(e);
                using (var g = Drawing.Graphics.FromHwnd(Handle)) dpi = g.DpiX;
            }
            // The window is drawn only through UpdateLayeredWindow (Render); WM_PAINT draws nothing.
            protected override void OnPaint(Forms.PaintEventArgs e) { }
            protected override void OnPaintBackground(Forms.PaintEventArgs e) { }
            protected override void WndProc(ref Forms.Message m) {
                if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
                if (m.Msg == 0x201 || m.Msg == 0x204) { m.Result = IntPtr.Zero; return; }
                if (m.Msg == 0x202 || m.Msg == 0xF5) { OnClick(EventArgs.Empty); m.Result = IntPtr.Zero; return; }
                if (m.Msg == 0x205 || m.Msg == 0x7B) { Context(); m.Result = IntPtr.Zero; return; }
                base.WndProc(ref m);
            }
            // Per-pixel alpha: only text, logos and rings are opaque. Every pixel keeps alpha >= 1,
            // because fully transparent pixels of a layered window let clicks fall through.
            public bool Render() {
                if (!IsHandleCreated || Width <= 0 || Height <= 0) return true;
                using (var bitmap = new Drawing.Bitmap(Width, Height, Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                    bitmap.SetResolution(dpi, dpi);
                    using (var g = Drawing.Graphics.FromImage(bitmap)) { g.Clear(Drawing.Color.FromArgb(1, 0, 0, 0)); Draw(g, Width, Height); }
                    IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), hbitmap = bitmap.GetHbitmap(Drawing.Color.FromArgb(0)), old = SelectObject(memory, hbitmap);
                    try {
                        var size = new SIZE { cx = Width, cy = Height }; var origin = new POINT(); var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                        return UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, 2);
                    } finally { SelectObject(memory, old); DeleteObject(hbitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
                }
            }
            private static Drawing.Color Color(string hex) { return Drawing.ColorTranslator.FromHtml(hex); }
            private void Label(Drawing.Graphics g, string text, Drawing.Font font, Drawing.Color color, Drawing.RectangleF rect) {
                if (String.IsNullOrEmpty(text) || rect.Width <= 0) return;
                using (var brush = new Drawing.SolidBrush(color)) g.DrawString(text, font, brush, rect, Typo);
            }
            private int Measure(string text, Drawing.Font font) {
                if (String.IsNullOrEmpty(text)) return 0;
                if (measure == null || measure.DpiX != dpi) {
                    if (measure != null) { measure.Dispose(); measureBitmap.Dispose(); }
                    measureBitmap = new Drawing.Bitmap(1, 1); measureBitmap.SetResolution(dpi, dpi);
                    measure = Drawing.Graphics.FromImage(measureBitmap); measure.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                }
                return (int)Math.Ceiling(measure.MeasureString(text, font, 2000, Typo).Width);
            }
            private int Px(double points) { return (int)Math.Round(points * dpi / 96); }
            private int PrimaryWidth(Pace.Focus f) {
                if (f == null) return Measure("还没有订阅", body) + 4;
                int title = Measure(f.Title, body) + (f.Value.Length > 0 ? 5 + Measure(f.Value, bold) : 0);
                return Px(24) + Math.Min(Px(TextCap), Math.Max(title, Measure(f.Sub, small))) + 2;
            }
            // The platforms drawn as rings: all but the focus one, tightest first.
            private List<ProviderState> Others(Pace.Focus f) { return Data.Where(s => f == null || s != f.State).OrderBy(s => Pace.Score(s) ?? 999).ToList(); }
            public Plan Layout(int width) {
                Pace.Focus f = Pace.Primary(Data, DateTime.UtcNow);
                return Fit(Math.Min(width, Cap), PrimaryWidth(f), Others(f).Count);
            }
            public void Draw(Drawing.Graphics g, int width, int height) {
                g.SmoothingMode = Gdi.SmoothingMode.AntiAlias; g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = Gdi.InterpolationMode.HighQualityBicubic;
                if (hover || Expanded) using (var path = Rounded(new Drawing.RectangleF(1, 3, width - 2, height - 6), Px(5))) using (var fill = new Drawing.SolidBrush(Faint(Light ? 20 : 26))) g.FillPath(fill, path);
                Pace.Focus f = Pace.Primary(Data, DateTime.UtcNow); Plan plan = Layout(width);
                int mid = height / 2, x = 6, icon = Px(18);
                var rings = Others(f);
                if (plan.Primary) {
                    if (f == null) { Label(g, "还没有订阅", body, Dim, new Drawing.RectangleF(x, 0, width - 12, height)); return; }
                    Logo(g, f.State, x, mid - icon / 2, icon);
                    int tx = x + Px(24), room = PrimaryWidth(f) - Px(24), vw = f.Value.Length > 0 ? Measure(f.Value, bold) : 0;
                    int tw = Math.Max(0, Math.Min(Measure(f.Title, body), room - (vw > 0 ? vw + 5 : 0)));
                    int line = Px(17);
                    Label(g, f.Title, body, Ink, new Drawing.RectangleF(tx, mid - line, tw + 1, line));
                    if (vw > 0) Label(g, f.Value, bold, Tone(f.Score), new Drawing.RectangleF(tx + tw + 5, mid - line, vw + 2, line));
                    Label(g, f.Sub, small, f.Alert ? Tone(Math.Min(f.Score ?? 30, 30)) : Dim, new Drawing.RectangleF(tx, mid + 1, room, line - 1));
                    x += PrimaryWidth(f) + 8;
                    if (plan.Rings > 0 || plan.More > 0) using (var pen = new Drawing.Pen(Faint(48))) g.DrawLine(pen, x - 5, mid - 9, x - 5, mid + 9);
                } else if (f != null) rings.Insert(0, f.State);
                foreach (ProviderState s in rings.Take(plan.Rings)) { Ring(g, s, x + 2, mid - 11); x += RingWidth; }
                if (plan.More > 0) {
                    var chip = new Drawing.RectangleF(x + 2, mid - 9, ChipWidth - 4, 18);
                    using (var path = Rounded(chip, 9)) using (var fill = new Drawing.SolidBrush(Faint(30))) g.FillPath(fill, path);
                    var centered = new Drawing.StringFormat(Typo) { Alignment = Drawing.StringAlignment.Center };
                    using (var brush = new Drawing.SolidBrush(Dim)) g.DrawString("+" + plan.More, tiny, brush, chip, centered);
                }
            }
            private static Gdi.GraphicsPath Rounded(Drawing.RectangleF r, float radius) {
                var path = new Gdi.GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
            }
            // The platform's logo, or its initial when it has none (a user recipe without an icon).
            private void Logo(Drawing.Graphics g, ProviderState s, int x, int y, int size) {
                Drawing.Bitmap image; if (Logos.TryGetValue(s.Id, out image) && image != null) { g.DrawImage(image, x, y, size, size); return; }
                string name = SubscriptionView.Name(s.Id); string initial = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
                var centered = new Drawing.StringFormat(Typo) { Alignment = Drawing.StringAlignment.Center };
                using (var font = new Drawing.Font("Microsoft YaHei UI", size * 72f / dpi * .62f, Drawing.FontStyle.Bold)) using (var brush = new Drawing.SolidBrush(Ink)) g.DrawString(initial, font, brush, new Drawing.RectangleF(x, y, size, size), centered);
            }
            // A platform logo inside a ring whose arc is its tightest remaining share.
            private void Ring(Drawing.Graphics g, ProviderState s, int x, int y) {
                const int d = 22; double? score = Pace.Score(s);
                using (var track = new Drawing.Pen(Faint(46), 2.2f)) g.DrawEllipse(track, x + 1, y + 1, d - 2, d - 2);
                if (score.HasValue && score > 0) using (var arc = new Drawing.Pen(Tone(score), 2.2f) { StartCap = Gdi.LineCap.Round, EndCap = Gdi.LineCap.Round }) g.DrawArc(arc, x + 1, y + 1, d - 2, d - 2, -90, (float)(Math.Min(100, score.Value) / 100 * 360));
                Logo(g, s, x + 5, y + 5, 12);
            }
            protected override void Dispose(bool disposing) {
                if (disposing) { body.Dispose(); bold.Dispose(); small.Dispose(); tiny.Dispose(); if (measure != null) { measure.Dispose(); measureBitmap.Dispose(); } foreach (var image in Logos.Values) if (image != null) image.Dispose(); Logos.Clear(); }
                base.Dispose(disposing);
            }
        }
        public static Rect FreeSpace(Rect bar, IEnumerable<Rect> occupied) {
            if (bar.Width < bar.Height * 3 || bar.Height < 20 || bar.Height > 160) return Rect.Empty;
            double right = bar.Right - 8;
            foreach (Rect item in occupied.Where(r => !r.IsEmpty && r.Width > 0 && r.IntersectsWith(bar)).OrderByDescending(r => r.Right)) {
                double gap = right - item.Right - 8;
                if (gap >= 76) return new Rect(item.Right + 8, bar.Top + 3, gap, bar.Height - 6);
                right = Math.Min(right, item.Left - 8);
            }
            return Rect.Empty;
        }
        // Hide: a definite reason to take the summary off now (a full-screen window, the taskbar
        // hidden); a null slot is a passing failure.
        private sealed class Slot { public IntPtr Shell; public Rect Area; public bool Hide; }
        private static Slot Locate() {
            IntPtr parent = FindWindow("Shell_TrayWnd", null); RECT native;
            if (parent == IntPtr.Zero) return null;
            if (!IsWindowVisible(parent) || !GetWindowRect(parent, out native)) return new Slot { Hide = true };
            var bar = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
            var screen = Forms.Screen.FromHandle(parent).Bounds;
            Rect visible = Rect.Intersect(bar, new Rect(screen.X, screen.Y, screen.Width, screen.Height));
            if (visible.IsEmpty || visible.Height < bar.Height - 2) return new Slot { Hide = true };   // auto-hide taskbar slid away
            if (FullScreen(GetForegroundWindow(), screen)) return new Slot { Hide = true };
            var occupied = new List<Rect>(); IntPtr tray = FindWindowEx(parent, IntPtr.Zero, "TrayNotifyWnd", null); RECT tr;
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out tr)) return null;
            occupied.Add(new Rect(tr.Left, tr.Top, tr.Right - tr.Left, tr.Bottom - tr.Top));
            var elements = AutomationElement.FromHandle(parent).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (elements.Count == 0 || elements.Count > 256) return null;
            int ownPid = Process.GetCurrentProcess().Id, count = 0;
            foreach (AutomationElement item in elements) {
                var info = item.Current; if (info.ProcessId == ownPid || info.IsOffscreen) continue;
                Rect rect = info.BoundingRectangle; if (rect.IsEmpty || rect.Width <= 0 || !rect.IntersectsWith(bar)) continue;
                occupied.Add(rect); count++;
            }
            if (count == 0) return null;
            Rect slot = FreeSpace(bar, occupied);
            // Host directly under Shell_TrayWnd: children of the XAML content bridge paint but
            // never receive hit tests, so clicks would fall through to the taskbar.
            return slot.IsEmpty ? null : new Slot { Shell = parent, Area = slot };
        }
        // A real full-screen window in front (a game, a video, a presentation). The taskbar itself, the
        // desktop and the tray's own pop-ups also span the taskbar but are not: hiding for them made
        // the summary vanish whenever the taskbar or the desktop was clicked.
        private static readonly string[] ShellClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };
        private static bool FullScreen(IntPtr window, Drawing.Rectangle screen) {
            RECT r; if (window == IntPtr.Zero || !GetWindowRect(window, out r)) return false;
            if (r.Left > screen.Left || r.Top > screen.Top || r.Right < screen.Right || r.Bottom < screen.Bottom) return false;
            var name = new System.Text.StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (ShellClasses.Contains(name.ToString())) return false;
            uint pid; GetWindowThreadProcessId(window, out pid);
            return pid != (uint)Process.GetCurrentProcess().Id;
        }
        public async Task Refresh() {
            if (disposed) return;
            if (!Available) { RemoveHost(); Note = "当前系统不支持任务栏摘要，请从托盘打开额度看板。"; return; }
            if (!Enabled) { RemoveHost(); Note = ""; return; }
            if (busy) return; busy = true;
            try {
                Slot slot = await Task.Run(() => { try { return Locate(); } catch { return null; } });
                if (disposed) return;
                if (!Enabled) { RemoveHost(); return; }
                if (slot == null && host != null && ++misses < MissesBeforeHiding) return;
                if (slot == null || slot.Hide) { RemoveHost(); Note = slot == null ? "任务栏空间不足或当前布局不可用，可从托盘打开看板。" : ""; return; }
                misses = 0;
                var readings = states();
                if (host == null || shell != slot.Shell || !host.IsHandleCreated || !IsWindow(host.Handle)) {
                    RemoveHost(); shell = slot.Shell;
                    host = new HostedButton(shell, () => dispatcher.BeginInvoke(new Action(() => toggle(Bounds))), () => {
                        dispatcher.BeginInvoke(menu);
                    });
                    host.HoverChanged += inside => dispatcher.BeginInvoke(new Action(() => {
                        if (inside) { if (HoverStart != null) HoverStart(Bounds); } else if (HoverEnd != null) HoverEnd();
                    }));
                    IntPtr handle = host.Handle; if (GetParent(handle) != shell) throw new InvalidOperationException("Taskbar parenting failed");
                    host.Visible = true;
                }
                foreach (ProviderState state in readings) if (!host.Logos.ContainsKey(state.Id)) host.Logos[state.Id] = logo(state.Id);
                host.Data = readings; host.Expanded = expanded();
                Drawing.Color back; if (SampleTaskbar(slot.Area, out back)) host.Back = back;
                var overview = SubscriptionView.Classify(readings);
                host.Cap = (int)Math.Round(350 * Math.Max(.75, (slot.Area.Height + 6) / 48));
                int width = host.Layout((int)slot.Area.Width).Width;
                Note = "";
                // Hovering shows the themed flyout (TaskbarFlyout); the text stays for screen readers.
                string tip = readings.Count == 0 ? "尚未启用订阅，点击添加。" : String.Join("\n\n", readings.Select(s => SubscriptionView.Tooltip(s, DateTime.UtcNow)));
                host.AccessibleDescription = tip;
                host.Text = overview.Alerts.Count + " 项告警，" + overview.Normal.Count + " 个正常，" + overview.Attention.Count + " 个异常";
                var point = new POINT { X = (int)slot.Area.Right - width, Y = (int)slot.Area.Top };
                if (!ScreenToClient(shell, ref point)) throw new InvalidOperationException("Taskbar coordinates unavailable");
                if (!SetWindowPos(host.Handle, IntPtr.Zero, point.X, point.Y, width, (int)slot.Area.Height, 0x10 | 0x40)) throw new InvalidOperationException("Taskbar positioning failed");
                if (!host.Render()) throw new InvalidOperationException("Layered taskbar window unavailable");
            } catch { RemoveHost(); Note = "任务栏挂接暂不可用，已保留托盘入口。"; } finally { busy = false; }
        }
        // Taskbar colour just left of the free gap (never under the summary itself): the median
        // of a few pixels, so a hovered neighbour or a noisy acrylic pixel does not win.
        private static bool SampleTaskbar(Rect area, out Drawing.Color color) {
            color = Drawing.Color.Empty; IntPtr dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return false;
            try {
                var samples = new List<Drawing.Color>(); int x = (int)area.Left - 3;
                foreach (double f in new[] { .15, .5, .85 }) foreach (int dx in new[] { 0, -2 }) {
                    uint c = GetPixel(dc, x + dx, (int)(area.Top + area.Height * f));
                    if (c != 0xFFFFFFFF) samples.Add(Drawing.Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)));
                }
                if (samples.Count == 0) return false;
                color = samples.OrderBy(s => s.R * 299 + s.G * 587 + s.B * 114).ElementAt(samples.Count / 2); return true;
            } finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        private void RemoveHost() { if (host != null) { tooltip.SetToolTip(host, null); host.Dispose(); host = null; } shell = IntPtr.Zero; }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); RemoveHost(); tooltip.Dispose(); }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, IntPtr dstPoint, ref SIZE size, IntPtr src, ref POINT srcPoint, uint key, ref BLENDFUNCTION blend, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
