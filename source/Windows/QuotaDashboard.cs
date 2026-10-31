using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodeUsageMonit {
    // Detail window opened from the taskbar summary or the tray: quota cards, consumption
    // charts and settings. Presentation only; no account reads or requests of its own.
    public sealed class QuotaDashboard : IDisposable {
        private readonly Window view;
        private readonly StackPanel content;
        private readonly Grid bar;
        private readonly Func<List<ProviderState>> readings;
        private readonly Func<string, double, FrameworkElement> icon;
        private readonly Func<UsageHistory> history;
        private readonly Func<FrameworkElement> settingsView;
        private readonly Func<Task> refresh;
        // The refresh button spins while a manual refresh runs, then shows the outcome for a few seconds.
        private bool refreshing; private string refreshNote = "";
        private readonly DispatcherTimer noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        public event Action VisibilityChanged;
        public bool IsOpen { get { return view.IsVisible; } }
        // Owner for dialogs opened from settings, so the topmost dashboard never covers them.
        public Window View { get { return view; } }
        private readonly bool demo;
        private string selectedId = "";
        private readonly DispatcherTimer clock;
        // Tab: quota | usage | settings. Usage metric: tokens | cost, over 7 or 30 days.
        private string tab = "quota", metric = "tokens";
        private int range = 30;
        private double renderedWidth;
        private bool disposed, rendering;
        private readonly Border frame;
        private static Brush Ink { get { return Theme.Ink; } }
        private static Brush Dim { get { return Theme.Dim; } }
        private static Brush Surface { get { return Theme.Surface; } }
        private static Brush Edge { get { return Theme.Edge; } }
        public QuotaDashboard(Func<List<ProviderState>> states, Func<UsageHistory> usage, Func<string, double, FrameworkElement> providerIcon, Func<FrameworkElement> settingsPage, Func<Task> reload, bool demoMode) {
            readings = states; history = usage; icon = providerIcon; settingsView = settingsPage; refresh = reload; demo = demoMode;
            noteTimer.Tick += delegate { noteTimer.Stop(); refreshNote = ""; if (!disposed) RenderBar(); };
            content = new StackPanel { Margin = new Thickness(14, 4, 14, 14) };
            bar = new Grid { Margin = new Thickness(14, 12, 14, 8), Background = Brushes.Transparent };
            // Double-clicking the header fills the work area, or goes back to the previous size.
            bar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && !Interactive(e.OriginalSource as DependencyObject)) { ToggleLarge(); e.Handled = true; } };
            var layout = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); layout.Children.Add(bar);
            layout.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            frame = new Border { Child = layout };
            // An opaque window without a system caption (WindowChrome), not a transparent layered one:
            // Windows draws the rounded corners and the shadow, maximize and minimize animate like any
            // window, and rendering stays on the GPU instead of being redrawn in software.
            view = new Window { Title = "余量 · 额度与消耗", Width = 880, Height = 640, MinWidth = 520, MinHeight = 360, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, ShowInTaskbar = true, FontFamily = new FontFamily(Theme.Font), Content = frame };
            System.Windows.Shell.WindowChrome.SetWindowChrome(view, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            ApplyTheme();
            view.SourceInitialized += delegate {
                IntPtr handle = new WindowInteropHelper(view).Handle; var hwnd = HwndSource.FromHwnd(handle); hwnd.AddHook(MaxToWorkArea);
                PaintBase(hwnd);
                // WS_MINIMIZEBOX, so the taskbar button minimizes and restores it like any window.
                SetWindowLong(handle, -16, GetWindowLong(handle, -16) | 0x20000);
                // Rounded corners on Windows 11 (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND); ignored before.
                int round = 2; try { DwmSetWindowAttribute(handle, 33, ref round, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            };
            view.StateChanged += delegate { stateChanged = true; RenderBar(); };
            // Draggable from any empty spot.
            view.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ClickCount != 1 || e.Handled || view.WindowState == WindowState.Maximized || Interactive(e.OriginalSource as DependencyObject)) return;
                try { view.DragMove(); } catch (InvalidOperationException) { }
            };
            // Windows switching light/dark mode or accent colour repaints everything, settings included.
            Theme.Watch(view.Dispatcher); Theme.Changed += delegate { ApplyTheme(); if (view.IsVisible) Render(); };
            // Rebuilding the page follows a maximize or restore at once, a drag-resize once it pauses.
            resized = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; resized.Tick += delegate { resized.Stop(); if (view.IsVisible) Render(); };
            view.SizeChanged += delegate {
                if (!view.IsVisible) return;
                if (stateChanged) { stateChanged = false; resized.Stop(); Render(); }
                else if (Math.Abs(view.ActualWidth - renderedWidth - 30) > 2) { resized.Stop(); resized.Start(); }
            };
            view.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) view.Hide(); else if (e.Key == Key.F5) { var ignored = RunRefresh(); } };
            view.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!disposed) { e.Cancel = true; view.Hide(); } };
            view.IsVisibleChanged += delegate { if (VisibilityChanged != null) VisibilityChanged(); };
            view.Deactivated += delegate { deactivated = DateTime.UtcNow; };
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; clock.Tick += delegate { if (view.IsVisible && tab != "settings") Render(); }; clock.Start();
        }
        // Whether a press lands on something that handles the mouse itself (so it must not start a drag).
        private static bool Interactive(DependencyObject node) {
            for (; node != null; node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node)) {
                if (node is ButtonBase || node is TextBoxBase || node is PasswordBox || node is ComboBox || node is ScrollBar || node is Thumb || node is Slider) return true;
                var element = node as FrameworkElement; if (element != null && element.Cursor == Cursors.Hand) return true;
            }
            return false;
        }
        // Without a caption, maximize would cover the whole monitor, taskbar included: WM_GETMINMAXINFO
        // limits it to the work area of the monitor the window is on.
        private static IntPtr MaxToWorkArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg != 0x24) return IntPtr.Zero;
            IntPtr monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
            var limits = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
            limits.MaxPosition.X = info.Work.Left - info.Monitor.Left; limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            limits.MaxSize.X = info.Work.Right - info.Work.Left; limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            System.Runtime.InteropServices.Marshal.StructureToPtr(limits, lParam, false);
            return IntPtr.Zero;
        }
        private struct NativePoint { public int X, Y; }
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        private bool stateChanged;
        private DispatcherTimer resized;
        private bool Maximized { get { return view.WindowState == WindowState.Maximized; } }
        private void ToggleLarge() { view.WindowState = Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void ApplyTheme() {
            view.Resources = Theme.Styles(); view.Foreground = Ink; view.Background = Theme.Window; frame.Background = Theme.Window;
            var hwnd = PresentationSource.FromVisual(view) as HwndSource; if (hwnd != null) PaintBase(hwnd);
        }
        // Until WPF draws a frame (opening, or the area a maximize uncovers) Windows fills the window
        // with its class brush, white by default: use the theme's window colour for that and for WPF's clear colour.
        private IntPtr baseBrush;
        private void PaintBase(HwndSource hwnd) {
            var brush = Theme.Window as SolidColorBrush; if (brush == null) return; Color c = brush.Color;
            hwnd.CompositionTarget.BackgroundColor = c;
            IntPtr next = CreateSolidBrush(c.R | (c.G << 8) | (c.B << 16));
            SetClassLongPtr(hwnd.Handle, -10, next);   // GCLP_HBRBACKGROUND
            if (baseBrush != IntPtr.Zero) DeleteObject(baseBrush); baseBrush = next;
        }
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] private static extern IntPtr SetClassLongPtr(IntPtr handle, int index, IntPtr value);
        public static int PageCount(int count) { return Math.Max(1, (Math.Max(0, count) + 1) / 2); }
        public static string Level(double? remaining) { return !remaining.HasValue ? "未知" : remaining < 20 ? "告急" : remaining < 40 ? "偏紧" : "充足"; }
        private static string Tone(double? remaining) { return Theme.Tone(remaining); }
        private static Brush Paint(string hex) { return Theme.Paint(hex); }
        private static TextBlock Text(string text, double size, Brush color) { return new TextBlock { Text = text, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; }
        // Shared with the settings page (SimpleSettings.cs).
        // Extra "actual spend" lines for a provider's card (DeepSeek: balance-change ledger): {value, note}.
        public Func<ProviderState, string[]> SpendNote;
        // A short call to action on a card whose platform data could be fuller (console token
        // missing or expired), and what clicking it opens.
        public Func<ProviderState, string> GuideNote;
        public Action<string> OpenGuide;
        public static Button Button(string text, string name, Action click) {
            var button = new Button { Content = text, Height = 28, MinWidth = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), Background = Theme.Raised, Foreground = Ink, BorderThickness = new Thickness(0), FontSize = 12, ToolTip = name, Cursor = Cursors.Hand };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82)); template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4)); template.Triggers.Add(disabled); button.Template = template;
            System.Windows.Automation.AutomationProperties.SetName(button, name); button.Click += delegate { click(); }; return button;
        }
        // Three weights of control, so actions, navigation and filters read differently:
        // Button (filled, an action), Quiet (no fill until hovered: toolbar and row actions),
        // Tab (text with an underline: pages) and Segmented (one track holding the options: filters).
        public static Button Quiet(string text, string name, Action click, Brush fore = null) {
            var b = Button(text, name, click); Brush idle = fore ?? Dim; b.Background = Brushes.Transparent; b.Foreground = idle;
            b.MouseEnter += delegate { b.Background = Theme.Hover; if (fore == null) b.Foreground = Ink; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; b.Foreground = idle; };
            return b;
        }
        private static Button Tab(string text, bool on, Action click) {
            var label = new TextBlock { Text = text, FontSize = 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel(); stack.Children.Add(label);
            stack.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 5, 0, 0), Background = on ? Theme.Accent : Brushes.Transparent });
            var b = Button("", text, click); b.Content = stack; b.Height = 34; b.Padding = new Thickness(10, 7, 10, 0); b.Margin = new Thickness(0, 0, 2, 0);
            b.Background = Brushes.Transparent; b.Foreground = on ? Ink : Dim;
            if (!on) { b.MouseEnter += delegate { b.Foreground = Ink; }; b.MouseLeave += delegate { b.Foreground = Dim; }; }
            return b;
        }
        private static FrameworkElement Segmented(string label, string[] options, int selected, Action<int> pick) {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            var caption = Text(label, 11, Dim); caption.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(caption);
            var track = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++) {
                int index = i; bool on = i == selected;
                var b = Button(options[i], label + " " + options[i], () => { if (index != selected) pick(index); });
                b.Height = 24; b.Margin = new Thickness(0); b.Padding = new Thickness(11, 0, 11, 0); b.FontSize = 11.5;
                b.Background = on ? (Theme.Dark ? Theme.Hover : Theme.Surface) : Brushes.Transparent; b.Foreground = on ? Ink : Dim; if (on) b.FontWeight = FontWeights.SemiBold;
                track.Children.Add(b);
            }
            row.Children.Add(new Border { Background = Theme.Dark ? Theme.Surface : Theme.Raised, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = track });
            return row;
        }
        // Re-renders an open window after new readings (never the settings page, which may hold input).
        public void Update() { if (view.IsVisible && !disposed && tab != "settings") Render(); }
        // An ordinary window other apps can cover (it has a taskbar button). Clicking the summary or the
        // tray icon closes it when it is in front and brings it back when it is behind something.
        // The tray icon takes the focus before its click arrives, so a window that lost it a moment
        // ago still counts as in front.
        private DateTime deactivated = DateTime.MinValue;
        private bool InFront { get { return view.IsVisible && view.WindowState != WindowState.Minimized && (view.IsActive || (DateTime.UtcNow - deactivated).TotalMilliseconds < 400); } }
        public void Toggle() { if (InFront) Hide(); else Show(); }
        // The taskbar summary passes its rectangle; the window always opens centred on screen.
        public void ToggleAt(Rect source) { Toggle(); }
        public void ShowAt(Rect source) { Show(); }
        public void ShowTab(string name) { tab = name; Show(); }
        public void Hide() { view.Hide(); }
        public void Show(string providerId = null) {
            if (disposed) return;
            if (!String.IsNullOrEmpty(providerId)) { selectedId = providerId; tab = "quota"; }
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            var source = PresentationSource.FromVisual(view);
            double scale = source == null || source.CompositionTarget == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            if (!Maximized) { view.Width = Math.Max(520, Math.Min(view.Width, area.Width / scale - 48)); view.Height = Math.Max(360, Math.Min(view.Height, area.Height / scale - 48)); }
            bool opening = !view.IsVisible;
            Render();
            if (opening && !Maximized) {
                if (tab == "quota") FitHeight(area.Height / scale - 48);
                view.Left = (area.Left + area.Width / 2.0) / scale - view.Width / 2; view.Top = (area.Top + area.Height / 2.0) / scale - view.Height / 2;
            }
            view.Show();
            // Back to the state it was minimized from (maximized stays maximized).
            if (view.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(view);
            // Raise above whatever covers it: briefly topmost, then an ordinary window again.
            view.Topmost = true; view.Topmost = false; view.Activate();
        }
        // Opens as tall as the quota page needs (no empty band under a short list), never taller than the screen.
        private void FitHeight(double most) {
            double inner = view.Width - 2 * frame.Margin.Left - 2;
            bar.Measure(new Size(inner, double.PositiveInfinity)); content.Measure(new Size(inner, double.PositiveInfinity));
            double needed = bar.DesiredSize.Height + content.DesiredSize.Height + 2 * frame.Margin.Top + 4;
            view.Height = Math.Max(view.MinHeight, Math.Min(most, needed));
        }
        private void Render() {
            if (rendering || disposed) return;
            rendering = true;
            try {
                // A wide (maximized) window enlarges everything a step instead of leaving tiny text in empty space.
                double raw = (view.ActualWidth > 0 ? view.ActualWidth : view.Width) - 30 - frame.Margin.Left - frame.Margin.Right;
                double zoom = raw >= 1900 ? 1.25 : raw >= 1500 ? 1.12 : 1;
                content.LayoutTransform = zoom == 1 ? Transform.Identity : new ScaleTransform(zoom, zoom);
                renderedWidth = Math.Max(480, raw / zoom);
                RenderBar(); content.Children.Clear();
                if (tab == "usage") RenderUsage(); else if (tab == "settings") content.Children.Add(settingsView()); else RenderQuota();
            } finally { rendering = false; }
        }
        private void RenderBar() {
            bar.Children.Clear(); bar.ColumnDefinitions.Clear();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // The title gives way to the tabs on a narrow window.
            bool narrow = view.ActualWidth > 0 && view.ActualWidth < 640;
            var title = Text("额度与消耗", 15, Ink); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(2, 0, 14, 0); if (!narrow) bar.Children.Add(title);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var t in new[] { new { Id = "quota", Name = "额度" }, new { Id = "usage", Name = "消耗" }, new { Id = "settings", Name = "设置" } }) {
                string id = t.Id; tabs.Children.Add(Tab(t.Name, tab == id, () => { tab = id; Render(); }));
            }
            Grid.SetColumn(tabs, 1); bar.Children.Add(tabs);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(RefreshButton(narrow));
            // Window buttons in the Windows icon font, like a normal title bar.
            var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var minimize = Quiet("\uE921", "最小化", () => { view.WindowState = WindowState.Minimized; }); minimize.FontFamily = icons; minimize.FontSize = 11; minimize.Width = 32; minimize.Margin = new Thickness(6, 0, 0, 0); minimize.ToolTip = "最小化"; controls.Children.Add(minimize);
            var size = Quiet(Maximized ? "" : "", Maximized ? "还原" : "最大化", ToggleLarge); size.FontFamily = icons; size.FontSize = 11; size.Width = 32; size.ToolTip = Maximized ? "还原（双击标题栏）" : "最大化（双击标题栏）"; controls.Children.Add(size);
            var close = Quiet("", "关闭", Hide); close.FontFamily = icons; close.FontSize = 11; close.Width = 32; controls.Children.Add(close);
            Grid.SetColumn(controls, 2); bar.Children.Add(controls);
        }
        private Button RefreshButton(bool compact) {
            var spin = new RotateTransform();
            var glyph = new TextBlock { Text = "↻", FontSize = 14, RenderTransformOrigin = new Point(.5, .55), RenderTransform = spin, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = refreshing ? "刷新中…" : compact ? "" : "刷新", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(glyph); if (label.Text.Length > 0 || refreshNote.Length > 0) row.Children.Add(label);
            var button = Quiet("", "刷新订阅额度（F5）", () => { var ignored = RunRefresh(); }); button.Content = row;
            if (refreshing) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else if (refreshNote.Length > 0) {
                bool ok = refreshNote == "已更新";
                glyph.Text = ok ? "✓" : "!"; label.Text = refreshNote; button.Foreground = Theme.Paint(ok ? Theme.GoodHex : Theme.WarnHex);
            }
            return button;
        }
        private async Task RunRefresh() {
            if (refreshing) return;
            refreshing = true; refreshNote = ""; noteTimer.Stop(); RenderBar();
            try { await refresh(); } catch { }
            refreshing = false;
            int failed = readings().Count(s => s.Status != "ready" || s.Stale);
            refreshNote = failed == 0 ? "已更新" : failed + " 个未读到";
            if (!disposed) { RenderBar(); noteTimer.Start(); }
        }
        // Consumption page: summary tiles, daily stacked bars with a legend (click to hide a
        // platform), a per-platform table that opens into its models, the hour-of-day profile and
        // DeepSeek's actual spend. Local logs only, except platforms whose console usage is read.
        private readonly HashSet<string> hidden = new HashSet<string>();
        private string openAgent = "";
        // Tokens by local hour of day over the given number of days; null when unknown.
        public Func<int, double[]> HourProfile;
        private void RenderUsage() {
            // Filters: what the chart measures and over which days; the tiles always show both.
            var options = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
            options.Children.Add(Segmented("时间范围", new[] { "近 7 天", "近 30 天" }, range == 7 ? 0 : 1, i => { range = i == 0 ? 7 : 30; Render(); }));
            options.Children.Add(Segmented("图表按", new[] { "Token", "API 等价费用" }, metric == "cost" ? 1 : 0, i => { metric = i == 1 ? "cost" : "tokens"; Render(); }));
            content.Children.Add(options);
            bool cost = metric == "cost";
            UsageHistory h = history(); DateTime today = DateTime.Today;
            UsageCharts.Totals sum = UsageCharts.Sum(h, today, range);

            var stats = new[] {
                Stat("Token", UsageTotals.Tokens(sum.Tokens), "日均 " + UsageTotals.Tokens(sum.Tokens / Math.Max(1, range))),
                Stat("API 等价费用", (sum.Partial ? "≥" : "") + UsageTotals.Usd(sum.Cost), "按官方价格折算，不是账单"),
                Stat("请求数", sum.Requests > 0 ? UsageTotals.Tokens(sum.Requests) : "—", sum.Requests > 0 ? "每次约 " + UsageTotals.Tokens(sum.Tokens / sum.Requests) + " token" : "日志里没有请求数"),
                Stat("缓存命中", sum.CacheRate.HasValue ? (sum.CacheRate.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—", "输入中来自缓存的比例"),
                Stat("输出速度", sum.Speed.HasValue ? sum.Speed.Value.ToString("0", CultureInfo.InvariantCulture) + " tok/s" : "—", sum.Speed.HasValue ? "有计时的请求平均" : "还没有计时数据")
            };
            // Five across on a wide window; otherwise volume (tokens, cost, requests) over quality (cache, speed), so both rows are full.
            foreach (Border stat in stats) stat.Margin = new Thickness(0, 0, 8, 8);
            if (renderedWidth >= 760) { var tiles = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, -8, 4) }; foreach (Border stat in stats) tiles.Children.Add(stat); content.Children.Add(tiles); }
            else {
                var first = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) }; var second = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -8, 4) };
                for (int i = 0; i < stats.Length; i++) (i < 3 ? first : second).Children.Add(stats[i]);
                content.Children.Add(first); content.Children.Add(second);
            }
            if (sum.Tokens <= 0) { content.Children.Add(Text(demo ? "演示数据没有本机用量。" : "这段时间本机日志里没有用量记录。", 12, Dim)); RenderSpend(); return; }

            List<UsageCharts.Day> days = UsageCharts.Daily(h, today, range, cost);
            content.Children.Add(Chart(days, UsageCharts.Ranking(days), cost));
            content.Children.Add(PlatformTable(UsageCharts.ByAgent(h, today, range)));
            FrameworkElement hours = HourStrip(); if (hours != null) content.Children.Add(hours);
            RenderSpend();
        }
        private Border Card(FrameworkElement child, string title, string note) {
            var body = new StackPanel();
            if (title != null) {
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
                var t = Text(title, 12, Ink); t.FontWeight = FontWeights.SemiBold; head.Children.Add(t);
                if (note != null) { var n = Text(note, 11, Dim); DockPanel.SetDock(n, Dock.Right); head.Children.Add(n); }
                body.Children.Add(head);
            }
            body.Children.Add(child);
            return new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 12), Child = body };
        }
        private Border Stat(string label, string value, string note) {
            var body = new StackPanel();
            body.Children.Add(Text(label, 11, Dim));
            var number = Text(value, 19, Ink); number.FontWeight = FontWeights.SemiBold; number.Margin = new Thickness(0, 4, 0, 2); body.Children.Add(number);
            var detail = Text(note, 10.5, Dim); detail.TextWrapping = TextWrapping.Wrap; detail.ToolTip = note; body.Children.Add(detail);
            return new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = body };
        }
        // Daily stacked bars, one colour per platform; hover a bar for its breakdown.
        private FrameworkElement Chart(List<UsageCharts.Day> days, List<KeyValuePair<string, double>> ranking, bool cost) {
            const double height = 170;
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var pair in ranking) {
                string id = pair.Key; bool off = hidden.Contains(id);
                // A legend entry, not a button: swatch and name; a hidden platform keeps only the swatch outline.
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                Brush swatch = Paint(ProviderCatalog.Color(id));
                chip.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = off ? Brushes.Transparent : swatch, BorderBrush = swatch, BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                var label = Text(ProviderCatalog.Name(id), 11.5, off ? Dim : Ink); if (off) label.TextDecorations = TextDecorations.Strikethrough; chip.Children.Add(label);
                chip.Children.Add(Text("  " + UsageCharts.Format(pair.Value, cost), 11, Dim));
                var b = new Border { Child = chip, Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 18, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = off ? "点击显示" : "点击隐藏" };
                b.MouseLeftButtonUp += delegate { if (!hidden.Remove(id)) hidden.Add(id); if (hidden.Count >= ranking.Count) hidden.Clear(); Render(); };
                legend.Children.Add(b);
            }
            Func<UsageCharts.Day, double> shown = d => d.Parts.Where(p => !hidden.Contains(p.Key)).Sum(p => p.Value);
            double max = Math.Max(1e-9, days.Max(shown));
            var grid = new Grid { Height = height + 22 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var axis = new Grid { Margin = new Thickness(0, 0, 8, 22) };
            var topLabel = Text(UsageCharts.Format(max, cost), 10, Dim); topLabel.VerticalAlignment = VerticalAlignment.Top; axis.Children.Add(topLabel);
            var zero = Text("0", 10, Dim); zero.VerticalAlignment = VerticalAlignment.Bottom; zero.HorizontalAlignment = HorizontalAlignment.Right; axis.Children.Add(zero);
            var middle = Text(UsageCharts.Format(max / 2, cost), 10, Dim); middle.VerticalAlignment = VerticalAlignment.Center; middle.HorizontalAlignment = HorizontalAlignment.Right; middle.Margin = new Thickness(0, 6, 0, 0); axis.Children.Add(middle);
            grid.Children.Add(axis);
            var plot = new Grid(); Grid.SetColumn(plot, 1); grid.Children.Add(plot);
            plot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) }); plot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            plot.Children.Add(new Border { BorderBrush = Edge, BorderThickness = new Thickness(0, 0, 0, 1) });
            plot.Children.Add(new Border { BorderBrush = Edge, BorderThickness = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top });
            // Bars are drawn up to height - 6, so the half line sits at half of that.
            plot.Children.Add(new Border { BorderBrush = Edge, BorderThickness = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, (height - 6) / 2), Opacity = .6 });
            for (int i = 0; i < days.Count; i++) {
                plot.ColumnDefinitions.Add(new ColumnDefinition());
                UsageCharts.Day day = days[i];
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(days.Count > 14 ? 1.5 : 5, 0, days.Count > 14 ? 1.5 : 5, 0), Background = Brushes.Transparent };
                foreach (var part in day.Parts.Where(p => !hidden.Contains(p.Key)).OrderBy(p => p.Value)) stack.Children.Add(new Border { Height = Math.Max(1, part.Value / max * (height - 6)), Background = Paint(ProviderCatalog.Color(part.Key)) });
                string tip = day.Date.ToString("M月d日 ddd", CultureInfo.GetCultureInfo("zh-CN")) + "  合计 " + UsageCharts.Format(day.Total, cost);
                foreach (var part in day.Parts.OrderByDescending(p => p.Value)) tip += "\n" + ProviderCatalog.Name(part.Key) + "  " + UsageCharts.Format(part.Value, cost) + (hidden.Contains(part.Key) ? "（已隐藏）" : "");
                if (day.Total <= 0) tip += "\n没有用量";
                var hit = new Border { Background = Brushes.Transparent, ToolTip = tip, Child = stack };
                Grid.SetColumn(hit, i); plot.Children.Add(hit);
                if (i == 0 || i == days.Count - 1 || i == days.Count / 2) {
                    var label = Text(UsageCharts.Short(day.Date), 10, Dim); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(-20, 0, -20, 0);
                    Grid.SetColumn(label, i); Grid.SetRow(label, 1); plot.Children.Add(label);
                }
            }
            for (int i = 0; i < 3; i++) Grid.SetColumnSpan(plot.Children[i], Math.Max(1, days.Count));
            var body = new StackPanel(); body.Children.Add(legend); body.Children.Add(grid);
            return Card(body, "每日" + (cost ? " API 等价费用" : " Token") + " · 近 " + days.Count + " 天", renderedWidth >= 600 ? "悬停柱子看明细 · 点平台名隐藏" : null);
        }
        // One row per platform; clicking a row opens its model breakdown.
        private FrameworkElement PlatformTable(List<UsageCharts.Totals> rows) {
            // A narrow window keeps platform, tokens and cost; the rest is in the row's tooltip and model breakdown.
            bool narrow = renderedWidth < 640;
            int[] keep = narrow ? new[] { 0, 1, 2 } : new[] { 0, 1, 2, 3, 4, 5 };
            var all6 = new[] { new GridLength(narrow ? 1.3 : 0, narrow ? GridUnitType.Star : GridUnitType.Auto), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1.8, GridUnitType.Star) };
            if (!narrow) all6[0] = new GridLength(150);
            Func<Grid> line = () => { var g = new Grid(); foreach (int k in keep) g.ColumnDefinitions.Add(new ColumnDefinition { Width = all6[k] }); return g; };
            var table = new StackPanel();
            var head = line(); head.Margin = new Thickness(0, 0, 0, 6);
            string[] titles = { "平台", "Token", "API 等价费用", "请求", "缓存命中", "常用模型" };
            for (int i = 0; i < keep.Length; i++) { var t = Text(titles[keep[i]], 11, Dim); Grid.SetColumn(t, i); head.Children.Add(t); }
            table.Children.Add(head);
            double all = Math.Max(1e-9, rows.Sum(r => r.Tokens));
            foreach (UsageCharts.Totals r in rows) {
                string agent = r.Agent; bool open = openAgent == agent;
                var row = line();
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(Text(r.Models.Count > 0 ? (open ? "▾" : "▸") : " ", 10, Dim)); name.Children[0].SetValue(FrameworkElement.WidthProperty, 12.0);
                var logo = icon(agent, 16); logo.Margin = new Thickness(0, 0, 7, 0); name.Children.Add(logo); name.Children.Add(Text(ProviderCatalog.Name(agent), 12, Ink)); row.Children.Add(name);
                string[] cells = {
                    UsageTotals.Tokens(r.Tokens) + "  " + (r.Tokens / all * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                    r.Cost > 0 ? (r.Partial ? "≥" : "") + UsageTotals.Usd(r.Cost) : r.Partial ? "无价格" : "—",
                    r.Requests > 0 ? UsageTotals.Tokens(r.Requests) : "—",
                    r.CacheRate.HasValue ? (r.CacheRate.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—",
                    r.TopModel.Length > 0 ? r.TopModel : "—"
                };
                for (int i = 1; i < keep.Length; i++) { int k = keep[i] - 1; var c = Text(cells[k], 12, k == 4 ? Dim : Ink); c.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(c, i); row.Children.Add(c); }
                if (narrow) row.ToolTip = "请求 " + cells[2] + " · 缓存命中 " + cells[3] + " · 常用模型 " + cells[4];
                var hit = new Border { Child = row, Padding = new Thickness(4, 7, 4, 7), Margin = new Thickness(-4, 0, -4, 0), CornerRadius = new CornerRadius(6), Background = open ? Theme.Raised : Brushes.Transparent, Cursor = r.Models.Count > 0 ? Cursors.Hand : null };
                if (r.Models.Count > 0) {
                    hit.MouseEnter += delegate { if (openAgent != agent) hit.Background = Theme.Soft(Theme.InkHex); };
                    hit.MouseLeave += delegate { if (openAgent != agent) hit.Background = Brushes.Transparent; };
                    hit.MouseLeftButtonUp += delegate { openAgent = open ? "" : agent; Render(); };
                }
                table.Children.Add(hit);
                if (open) table.Children.Add(Models(r));
            }
            return Card(table, "按平台", "点一行看模型拆分");
        }
        private FrameworkElement Models(UsageCharts.Totals r) {
            var list = new StackPanel { Margin = new Thickness(renderedWidth < 640 ? 14 : 30, 4, 0, 8) };
            double top = Math.Max(1e-9, r.Models.Values.Max());
            foreach (var m in r.Models.OrderByDescending(m => m.Value).Take(8)) {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                bool narrow = renderedWidth < 640;
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = narrow ? new GridLength(1.4, GridUnitType.Star) : new GridLength(220) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(narrow ? 96 : 130) });
                g.Children.Add(Text(m.Key, 11, Ink));
                var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                track.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(3) });
                track.Children.Add(new Border { Background = Paint(ProviderCatalog.Color(r.Agent)), CornerRadius = new CornerRadius(3) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, m.Value / top), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 1 - m.Value / top), GridUnitType.Star) });
                Grid.SetColumnSpan(track.Children[0], 2);
                Grid.SetColumn(track, 1); g.Children.Add(track);
                var v = Text(UsageTotals.Tokens(m.Value) + "  " + (m.Value / Math.Max(1e-9, r.Tokens) * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%", 11, Dim); v.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(v, 2); g.Children.Add(v);
                list.Children.Add(g);
            }
            if (r.Models.Count > 8) list.Children.Add(Text("另有 " + (r.Models.Count - 8) + " 个模型", 10.5, Dim));
            return list;
        }
        // Which hours of the day carry the most tokens.
        private FrameworkElement HourStrip() {
            double[] hours = HourProfile == null ? null : HourProfile(range);
            if (hours == null || hours.Length != 24 || hours.Sum() <= 0) return null;
            double max = hours.Max(), total = hours.Sum();
            int peak = Array.IndexOf(hours, max);
            var cells = new Grid { Height = 26 }; var labels = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = 0; i < 24; i++) {
                cells.ColumnDefinitions.Add(new ColumnDefinition()); labels.ColumnDefinitions.Add(new ColumnDefinition());
                var cell = new Grid { Margin = new Thickness(1, 0, 1, 0), ToolTip = i + ":00–" + (i + 1) + ":00  " + UsageTotals.Tokens(hours[i]) + " token · " + (hours[i] / total * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" };
                cell.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(3) });
                if (hours[i] > 0) cell.Children.Add(new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(3), Opacity = .15 + .85 * hours[i] / max });
                Grid.SetColumn(cell, i); cells.Children.Add(cell);
                if (i % 3 == 0) { var l = Text(i.ToString(CultureInfo.InvariantCulture), 10, Dim); l.HorizontalAlignment = HorizontalAlignment.Left; l.Margin = new Thickness(2, 0, 0, 0); Grid.SetColumn(l, i); Grid.SetColumnSpan(l, 3); labels.Children.Add(l); }
            }
            var body = new StackPanel(); body.Children.Add(cells); body.Children.Add(labels);
            return Card(body, "几点用得最多", "高峰 " + peak + "–" + (peak + 1) + " 时，占 " + (max / total * 100).ToString("0", CultureInfo.InvariantCulture) + "% · 近 " + range + " 天");
        }
        // DeepSeek's actual spend from balance changes, kept apart from API-equivalent cost.
        private void RenderSpend() {
            if (SpendNote == null) return;
            ProviderState deepseek = readings().FirstOrDefault(s => s.Id == "deepseek");
            string[] spend = deepseek == null ? null : SpendNote(deepseek);
            if (spend == null) return;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = icon("deepseek", 18); logo.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(logo);
            var v = Text(spend[0], 14, Ink); v.FontWeight = FontWeights.SemiBold; row.Children.Add(v);
            var body = new StackPanel(); body.Children.Add(row);
            if (spend.Length > 1) { var n = Text(spend[1], 11, Dim); n.Margin = new Thickness(28, 4, 0, 0); body.Children.Add(n); }
            content.Children.Add(Card(body, "DeepSeek 实际扣费（人民币）", "和上面的 API 等价费用分开计算"));
        }
        private void RenderQuota() {
            List<ProviderState> states = readings(); DateTime now = DateTime.UtcNow;
            var overview = SubscriptionView.Classify(states);
            bool urgent = overview.Alerts.Any(v => v.Urgent);
            string headHex = urgent ? Theme.UrgentHex : overview.Alerts.Count > 0 || overview.Attention.Count > 0 ? Theme.WarnHex : Theme.GoodHex;
            // One sentence first: what needs attention and how soon.
            var head = new DockPanel();
            DateTime latest = DateTime.MinValue; foreach (ProviderState s in states) { DateTime at; if (DateTime.TryParse(s.LastSuccess, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) && at.ToUniversalTime() > latest) latest = at.ToUniversalTime(); }
            if (latest > DateTime.MinValue) { var updated = Text(Ago(now - latest) + "更新", 11, Dim); updated.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(updated, Dock.Right); head.Children.Add(updated); }
            var dot = Text("●", 13, Paint(headHex)); dot.Margin = new Thickness(0, 0, 8, 0); head.Children.Add(dot);
            var sentence = Text(Pace.Headline(states, now), 14, Ink); sentence.FontWeight = FontWeights.SemiBold; sentence.TextWrapping = TextWrapping.Wrap; head.Children.Add(sentence);
            if (urgent) sentence.Foreground = Paint(headHex);
            content.Children.Add(new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = head });
            // Consumption at a glance: today, 7 and 30 days, each with its daily trend.
            UsageHistory h = history(); UsageTotals totals = UsageTotals.From(h, DateTime.Today);
            List<UsageCharts.Day> month = UsageCharts.Daily(h, DateTime.Today, 30, false);
            double before = month.Skip(22).Take(7).Average(d => d.Total);
            string compare = before > 0 ? "比前 7 日均值 " + (totals.TodayTokens >= before ? "+" : "−") + Math.Abs(totals.TodayTokens / before - 1).ToString("0%", CultureInfo.InvariantCulture) : "前 7 日没有用量";
            // Three tiles with their trend on a wide window; on a narrow one a single strip, so the
            // subscriptions (what this page is for) start without scrolling.
            FrameworkElement tiles;
            if (renderedWidth < 720) tiles = Strip(new[] { Tuple.Create("今日", totals.TodayTokens, totals.TodayCost), Tuple.Create("近 7 天", totals.WeekTokens, totals.WeekCost), Tuple.Create("近 30 天", totals.MonthTokens, totals.MonthCost) }, totals.Partial);
            else {
                double[] hours = HourProfile == null ? null : HourProfile(1);
                List<double> today = hours != null && hours.Length == 24 ? hours.Take(DateTime.Now.Hour + 1).ToList() : null;
                var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 6) };
                grid.Children.Add(Tile("今日", totals.TodayTokens, totals.TodayCost, totals.Partial, compare, today));
                grid.Children.Add(Tile("近 7 天", totals.WeekTokens, totals.WeekCost, totals.Partial, null, month.Skip(23).Select(d => d.Total).ToList()));
                grid.Children.Add(Tile("近 30 天", totals.MonthTokens, totals.MonthCost, totals.Partial, null, month.Select(d => d.Total).ToList()));
                tiles = grid;
            }
            tiles.ToolTip = "token 来自本机日志（DeepSeek、Agnes 接入平台 token 后用官方用量）；费用按官方 API 价格折算，不是实际账单" + (totals.Partial ? "。部分用量没有价格，费用为下限" : "");
            content.Children.Add(tiles);
            // Platforms that need attention first (tightest first), then the rest in the user's order.
            var attention = overview.Alerts.Select(a => a.State).Concat(overview.Attention).Distinct().ToList();
            var normal = states.Where(s => !attention.Contains(s)).ToList();
            // One even grid, the ones that need attention first; their colour bar and status mark them.
            if (states.Count > 0) {
                var section = Section("订阅", states.Count, attention.Count > 0 ? attention.Count + " 个需要注意，已排在前面" : "全部正常");
                if (states.Any(s => s.Quotas.Any(q => Pace.Of(q, now) != null))) section = WithPaceKey(section);
                content.Children.Add(section);
                content.Children.Add(Table(attention.Concat(normal).ToList(), now));
            }
            if (states.Count == 0) content.Children.Add(Text("还没有订阅，到「设置」里添加。", 12, Dim));
            var foot = Text((demo ? "演示数据 · " : "") + "百分比是剩余比例：≥40% 充足、20–40% 偏紧、<20% 告急 · 进度条上的竖线：按时间均匀使用，此刻应剩的位置", 10, Dim); foot.Margin = new Thickness(2, 6, 0, 0); foot.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(foot);
        }
        private static string Ago(TimeSpan span) { return span.TotalMinutes < 1 ? "刚刚" : span.TotalMinutes < 60 ? (int)span.TotalMinutes + " 分钟前" : span.TotalHours < 24 ? (int)span.TotalHours + " 小时前" : (int)span.TotalDays + " 天前"; }
        private static FrameworkElement Section(string title, int count, string note) {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 7) };
            var t = Text(title, 12, Ink); t.FontWeight = FontWeights.SemiBold; row.Children.Add(t);
            var n = Text(count.ToString(CultureInfo.InvariantCulture) + (note != null ? " · " + note : ""), 11, Dim); n.Margin = new Thickness(6, 0, 0, 0); row.Children.Add(n); return row;
        }
        // The section title with a key for the pace mark on the right.
        private static FrameworkElement WithPaceKey(FrameworkElement section) {
            var row = new DockPanel { LastChildFill = true };
            var key = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 4, 7), VerticalAlignment = VerticalAlignment.Center, ToolTip = "按时间均匀使用的话，此刻应该还剩到这条线；色条短于它说明用得比均速快" };
            key.Children.Add(new Border { Width = 2, Height = 11, Background = Ink, Opacity = .6, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            key.Children.Add(Text("按均速此刻应剩", 11, Dim));
            DockPanel.SetDock(key, Dock.Right); row.Children.Add(key); row.Children.Add(section);
            return row;
        }
        // Today, 7 and 30 days in one row of figures (narrow window).
        private FrameworkElement Strip(Tuple<string, double, double>[] items, bool partial) {
            var grid = new UniformGrid { Columns = items.Length };
            foreach (var item in items) {
                var cell = new StackPanel();
                cell.Children.Add(Text(item.Item1, 11, Dim));
                var number = Text(UsageTotals.Tokens(item.Item2), 16, Ink); number.FontWeight = FontWeights.SemiBold; number.Margin = new Thickness(0, 2, 0, 1); cell.Children.Add(number);
                cell.Children.Add(Text((partial ? "≥" : "≈") + UsageTotals.Usd(item.Item3), 10.5, Dim));
                grid.Children.Add(cell);
            }
            return new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 8), Child = grid };
        }
        private FrameworkElement Tile(string label, double tokens, double cost, bool partial, string note, List<double> trend) {
            var body = new Grid();
            var text = new StackPanel();
            text.Children.Add(Text(label, 11, Dim));
            var value = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var number = Text(UsageTotals.Tokens(tokens), 20, Ink); number.FontWeight = FontWeights.SemiBold; value.Children.Add(number);
            var unit = Text("tokens", 11, Dim); unit.Margin = new Thickness(5, 6, 0, 0); value.Children.Add(unit); text.Children.Add(value);
            var sub = Text((partial ? "≥" : "≈") + UsageTotals.Usd(cost) + " API 等价", 11, Dim); sub.Margin = new Thickness(0, 3, 0, 0); text.Children.Add(sub);
            if (note != null) { var n = Text(note, 11, Dim); n.Margin = new Thickness(0, 2, 0, 0); text.Children.Add(n); }
            if (trend != null) text.Margin = new Thickness(0, 0, 90, 0);
            body.Children.Add(text);
            if (trend != null && trend.Count > 1 && trend.Max() > 0) {
                double max = trend.Max(), w = 84, hgt = 30; var points = new PointCollection();
                for (int i = 0; i < trend.Count; i++) points.Add(new Point(i * w / (trend.Count - 1), hgt - 2 - trend[i] / max * (hgt - 4)));
                body.Children.Add(new Polyline { Points = points, Stroke = Theme.Accent, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Width = w, Height = hgt, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = .9 });
            }
            return new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 8, 8), Child = body };
        }
        // One table of every subscription: a group per platform (who, status, plan on the left) with one
        // aligned row per window or balance, so bars, figures and notes line up down the whole page.
        private FrameworkElement Table(List<ProviderState> list, DateTime now) {
            var rows = new StackPanel();
            for (int i = 0; i < list.Count; i++) {
                if (i > 0) rows.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(14, 0, 14, 0) });
                rows.Children.Add(Group(list[i], now));
            }
            return new Border { Background = Surface, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = rows };
        }
        // Whether rows have room for the note column; otherwise the note goes under the bar.
        private bool WideRows { get { return renderedWidth >= 900; } }
        private FrameworkElement Group(ProviderState state, DateTime now) {
            double? remaining = state.Quotas.Count == 0 ? (double?)null : state.Quotas.Min(q => q.Remaining);
            // Balance-only platforms take their colour and level from the balance warn line.
            double? score = remaining ?? (state.Balances.Count > 0 && SubscriptionView.BalanceWarn > 0 ? (double?)SubscriptionView.Items(state).Min(i => i.Score) : null);
            string tone = Tone(score); bool stale = SubscriptionView.Old(state); bool side = renderedWidth >= 640;
            var who = new StackPanel { Margin = side ? new Thickness(0, 2, 16, 0) : new Thickness(0, 0, 0, 8) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = icon(state.Id, 18); logo.Margin = new Thickness(0, 0, 8, 0); logo.VerticalAlignment = VerticalAlignment.Center; title.Children.Add(logo);
            var name = Text(ProviderCatalog.Name(state.Id), 14, Ink); name.FontWeight = FontWeights.SemiBold; name.VerticalAlignment = VerticalAlignment.Center; title.Children.Add(name);
            string status = stale ? "旧读数" : remaining.HasValue ? Level(remaining) : state.Balances.Count > 0 ? (score.HasValue ? "余额" + Level(score) : "账户余额") : SubscriptionView.Missing(state);
            var pill = new Border { Background = Theme.Soft(tone), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Text("● " + status, 11, Paint(tone)) };
            title.Children.Add(pill); who.Children.Add(title);
            var meta = new List<string>(); if (state.Plan.Length > 0) meta.Add(state.Plan);
            DateTime at; if (DateTime.TryParse(state.LastSuccess, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) meta.Add((stale ? "上次成功 " : "") + Ago(now - at.ToUniversalTime()) + (stale ? "" : "更新"));
            if (meta.Count > 0) { var m = Text(String.Join(" · ", meta), 11, Dim); m.Margin = new Thickness(26, 4, 0, 0); m.TextTrimming = TextTrimming.CharacterEllipsis; m.ToolTip = String.Join(" · ", meta); who.Children.Add(m); }
            string guide = GuideNote == null ? null : GuideNote(state);
            if (guide != null && OpenGuide != null) {
                string id = state.Id; var link = Text(guide, 11, Theme.Accent); link.Margin = new Thickness(26, 4, 0, 0); link.Cursor = Cursors.Hand; link.TextDecorations = TextDecorations.Underline;
                link.ToolTip = "接入平台控制台，拿到所有设备的每日用量"; link.MouseLeftButtonUp += delegate { OpenGuide(id); }; who.Children.Add(link);
            }
            var lines = new StackPanel();
            foreach (Quota quota in state.Quotas) lines.Children.Add(WindowRow(quota, now));
            foreach (Balance balance in state.Balances)
                lines.Children.Add(ValueRow("余额", SubscriptionView.Money(balance), SubscriptionView.BalanceWarn > 0 ? "告急线 " + SubscriptionView.BalanceWarn.ToString("0.##", CultureInfo.InvariantCulture) + " " + balance.Currency : null, null, true));
            string[] spend = SpendNote == null ? null : SpendNote(state);
            if (spend != null) lines.Children.Add(ValueRow("实际扣费", spend[0], spend[1], spend[1], false));
            if (lines.Children.Count == 0) { var message = Text(state.Message, 11.5, Dim); message.TextWrapping = TextWrapping.Wrap; message.ToolTip = state.Message; message.Margin = new Thickness(0, 4, 0, 4); lines.Children.Add(message); }
            var group = new Grid { Margin = new Thickness(16, 12, 16, 10), Opacity = stale ? .75 : 1, Background = state.Id == selectedId ? Theme.Hover : null };
            if (side) {
                group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(renderedWidth >= 1100 ? 230 : 190) }); group.ColumnDefinitions.Add(new ColumnDefinition());
                group.Children.Add(who); Grid.SetColumn(lines, 1); group.Children.Add(lines);
            } else {
                group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); group.RowDefinitions.Add(new RowDefinition());
                group.Children.Add(who); Grid.SetRow(lines, 1); group.Children.Add(lines);
            }
            return group;
        }
        // Columns shared by every row: name, the bar (or value), the figure, and on a wide window the note.
        private Grid RowGrid() {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 8), Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            if (WideRows) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(renderedWidth >= 1300 ? 380 : 300) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return grid;
        }
        // Puts the note beside the row on a wide window, under the bar otherwise.
        private void PlaceNote(Grid grid, TextBlock note) {
            note.TextTrimming = TextTrimming.CharacterEllipsis; note.VerticalAlignment = VerticalAlignment.Center;
            if (WideRows) { note.Margin = new Thickness(18, 0, 0, 0); Grid.SetColumn(note, 3); }
            else { note.Margin = new Thickness(0, 2, 0, 0); Grid.SetRow(note, 1); Grid.SetColumn(note, 1); Grid.SetColumnSpan(note, 2); }
            grid.Children.Add(note);
        }
        // One window: name, a bar with the even-pace mark, remaining %, then used/limit, reset and the projection.
        private FrameworkElement WindowRow(Quota quota, DateTime now) {
            WindowPace pace = Pace.Of(quota, now); string tone = Tone(quota.Remaining);
            var grid = RowGrid();
            var label = Text(SubscriptionView.ShortWindow(quota.Label), 12, Dim); label.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(label);
            var bar = new Grid { Height = 14, VerticalAlignment = VerticalAlignment.Center };
            var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(4) });
            double fill = Math.Max(0, Math.Min(100, quota.Remaining));
            if (fill > 0) { var fillGrid = new Grid(); fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fill, GridUnitType.Star) }); fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 100 - fill), GridUnitType.Star) }); fillGrid.Children.Add(new Border { Background = Paint(tone), CornerRadius = new CornerRadius(4) }); track.Children.Add(fillGrid); }
            bar.Children.Add(track);
            if (pace != null) {
                var mark = new Grid(); mark.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, pace.Even), GridUnitType.Star) }); mark.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 100 - pace.Even), GridUnitType.Star) });
                var tick = new Border { Width = 2, Background = Ink, Opacity = .6, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-1, 0, 0, 0) }; Grid.SetColumn(tick, 1); mark.Children.Add(tick); bar.Children.Add(mark);
            }
            Grid.SetColumn(bar, 1); grid.Children.Add(bar);
            var percent = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            percent.Inlines.Add(new System.Windows.Documents.Run("剩 ") { FontSize = 11, Foreground = Dim });
            percent.Inlines.Add(new System.Windows.Documents.Run(quota.Remaining.ToString("0.#") + "%") { FontSize = 14, Foreground = Ink, FontWeight = FontWeights.SemiBold });
            percent.HorizontalAlignment = HorizontalAlignment.Right; percent.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(percent, 2); grid.Children.Add(percent);
            string count = quota.UsedCount.HasValue && quota.LimitCount.HasValue ? "已用 " + quota.UsedCount.Value.ToString("0.#") + "/" + quota.LimitCount.Value.ToString("0.#") + " · " : "";
            var detail = new TextBlock { FontSize = 11, Foreground = Dim };
            detail.Inlines.Add(new System.Windows.Documents.Run(count + Reset(quota.ResetUtc)));
            string note = Pace.Note(pace, quota.Remaining);
            if (note != null) detail.Inlines.Add(new System.Windows.Documents.Run(" · " + note) { Foreground = pace.Short || quota.Remaining <= 0 ? Paint(quota.Remaining < 20 ? Theme.UrgentHex : Theme.WarnHex) : Dim });
            PlaceNote(grid, detail);
            DateTime reset; string resetAt = DateTime.TryParse(quota.ResetUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out reset) ? reset.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) : "未知";
            grid.ToolTip = quota.Label + "\n剩余 " + quota.Remaining.ToString("0.#") + "%，已用 " + quota.Used.ToString("0.#") + "%" + (pace != null ? "\n窗口已过去 " + (pace.Elapsed * 100).ToString("0") + "%；竖线是按均速此刻应剩的 " + pace.Even.ToString("0") + "%" : "") + "\n重置于 " + resetAt;
            return grid;
        }
        // A money row: name, the figure in the bar's place, and its note.
        // primary: the figure that matters (the balance), large; a secondary one (spend) in body text.
        private FrameworkElement ValueRow(string label, string value, string note, string tip, bool primary) {
            var grid = RowGrid();
            var l = Text(label, 12, Dim); l.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(l);
            var v = Text(value, primary ? 14 : 12, Ink); if (primary) v.FontWeight = FontWeights.SemiBold; v.TextTrimming = TextTrimming.CharacterEllipsis; v.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(v, 1); Grid.SetColumnSpan(v, 2); grid.Children.Add(v);
            if (note != null) PlaceNote(grid, Text(note, 11, Dim));
            if (tip != null) grid.ToolTip = tip;
            return grid;
        }
        private static string Reset(string utc) {
            DateTime time; if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) return "重置时间未知";
            TimeSpan left = time.ToUniversalTime() - DateTime.UtcNow;
            if (left.TotalSeconds <= 0) return "待刷新";
            return Pace.Compact(left) + "后重置";
        }
        public void Dispose() { if (disposed) return; disposed = true; clock.Stop(); view.Close(); }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    }
}
