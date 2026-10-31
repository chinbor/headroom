using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CodeUsageMonit {
    // The right-click panel of the taskbar summary and the tray icon, in the app's own theme:
    // a status header, one line per subscription and the actions, each with what it does.
    // Like a system menu it never takes the focus, so the tray's overflow flyout it was opened
    // from stays open; a click anywhere else (watched with a low-level mouse hook) or Esc closes it.
    public sealed class QuickMenu {
        private readonly Window view;
        private readonly Border frame;
        private IntPtr hook = IntPtr.Zero, keyHook = IntPtr.Zero;
        private readonly HookProc mouseProc, keyProc;   // kept alive while hooked
        public QuickMenu() {
            frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(12), Width = 232,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = .3, Color = Colors.Black } };
            view = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, FontFamily = new FontFamily(Theme.Font), Content = frame };
            view.SourceInitialized += delegate {
                IntPtr handle = new WindowInteropHelper(view).Handle;
                SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80);   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            };
            mouseProc = OnMouse; keyProc = OnKey;
        }
        public bool IsOpen { get { return view.IsVisible; } }
        // The click that closed the panel from outside also reaches what was clicked: when that was
        // the summary or the tray icon again, it should stay closed rather than open once more.
        private DateTime closedOutside = DateTime.MinValue;
        public bool JustClosed { get { return (DateTime.UtcNow - closedOutside).TotalMilliseconds < 500; } }
        public void Hide() { Unhook(); view.Hide(); }
        // Opens like a system context menu: a corner of the panel at the pointer (physical pixels from
        // Cursor.Position), growing up and to the right, flipped left or down where the screen ends.
        public void Show(FrameworkElement content) {
            view.Resources = Theme.Styles(); frame.Background = Theme.Window; frame.BorderBrush = Theme.Edge; frame.Child = content;
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var point = Forms.Cursor.Position; var screen = Forms.Screen.FromPoint(point); var area = screen.WorkingArea;
            double scale = Dpi(), m = frame.Margin.Left, w = frame.DesiredSize.Width + 2 * m, h = frame.DesiredSize.Height + 2 * m;
            double px = point.X / scale, py = point.Y / scale;
            double x = px + 2 - m; if (x + w - m > area.Right / scale) x = px - 2 - w + m;   // the frame's edge, not the shadow margin, at the pointer
            double y = py - 2 - h + m; if (y + m < area.Top / scale) y = py + 2 - m;
            view.Left = Math.Max(area.Left / scale - m, Math.Min(x, area.Right / scale - w + m));
            view.Top = Math.Max(area.Top / scale - m, Math.Min(y, area.Bottom / scale - h + m));
            view.Show();
            if (hook == IntPtr.Zero) {
                IntPtr module = GetModuleHandle(null);
                hook = SetWindowsHookEx(14, mouseProc, module, 0);     // WH_MOUSE_LL
                keyHook = SetWindowsHookEx(13, keyProc, module, 0);    // WH_KEYBOARD_LL
            }
        }
        private void Unhook() {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        }
        // A button pressed outside the panel closes it; the click still reaches what was clicked.
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data) {
            int m = message.ToInt32();
            if (code >= 0 && (m == 0x201 || m == 0x204 || m == 0x207) && view.IsVisible) {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(MSLLHOOKSTRUCT)); RECT r;
                IntPtr handle = new WindowInteropHelper(view).Handle;
                int inset = (int)(frame.Margin.Left * Dpi());   // the transparent shadow margin is outside
                if (!GetWindowRect(handle, out r) || info.X < r.Left + inset || info.X > r.Right - inset || info.Y < r.Top + inset || info.Y > r.Bottom - inset)
                { closedOutside = DateTime.UtcNow; view.Dispatcher.BeginInvoke(new Action(Hide)); }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private IntPtr OnKey(int code, IntPtr message, IntPtr data) {
            if (code >= 0 && message.ToInt32() == 0x100 && Marshal.ReadInt32(data) == 0x1B && view.IsVisible) view.Dispatcher.BeginInvoke(new Action(Hide));
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private double Dpi() {
            var source = PresentationSource.FromVisual(view);
            if (source != null && source.CompositionTarget != null) return source.CompositionTarget.TransformToDevice.M11;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
        }

        // ---- building blocks ----
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        // A clickable line: hover tint, closes the panel, then runs the action.
        public Border Item(FrameworkElement child, string tip, Action click) {
            var b = new Border { Child = child, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
            b.MouseEnter += delegate { b.Background = Theme.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { Hide(); if (click != null) click(); };
            return b;
        }
        // An action: icon glyph and title on one line (what it does in the tooltip), optional shortcut on the right.
        public Border Action(string glyph, string title, string detail, string hint, Action click, string toneHex = null) {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = toneHex != null ? Theme.Paint(toneHex) : Theme.Ink, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = toneHex != null ? Theme.Paint(toneHex) : Theme.Ink });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            if (!String.IsNullOrEmpty(hint)) { var k = new TextBlock { Text = hint, FontSize = 10.5, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(k, 2); grid.Children.Add(k); }
            return Item(grid, detail, click);
        }
        // A switch line: the whole line toggles; the panel stays open.
        public Border Switch(string title, string detail, bool on, bool enabled, Action<bool> changed) {
            var grid = new Grid { ToolTip = detail }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 12, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = Theme.Ink });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            var box = new CheckBox { IsChecked = on, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), LayoutTransform = new ScaleTransform(.8, .8) };
            box.Click += delegate { changed(box.IsChecked == true); };
            Grid.SetColumn(box, 2); grid.Children.Add(box);
            var line = new Border { Child = grid, Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(6), Opacity = enabled ? 1 : .55 };
            return line;
        }
        public static Border Divider() { return new Border { Height = 1, Background = Theme.Edge, Margin = new Thickness(6, 3, 6, 3) }; }
        public static TextBlock Caption(string text) { return new TextBlock { Text = text, FontSize = 10.5, Foreground = Theme.Dim, Margin = new Thickness(8, 2, 8, 1) }; }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public int X, Y, MouseData, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    }
}
