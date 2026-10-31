using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodeUsageMonit {
    // The card shown while the pointer rests on the taskbar summary: the overview sentence and
    // every platform's windows at a glance. It never takes focus or clicks; clicking the
    // summary still opens the detail window.
    public sealed class TaskbarFlyout {
        private readonly Window view;
        private readonly Func<List<ProviderState>> readings;
        private readonly Func<string, double, FrameworkElement> icon;
        // The summary lives in another process's window, where leave notifications are unreliable;
        // the pointer is polled instead: shown after resting 350 ms, hidden once it moves off.
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private double rested;
        private Rect anchor;
        public TaskbarFlyout(Func<List<ProviderState>> states, Func<string, double, FrameworkElement> providerIcon) {
            readings = states; icon = providerIcon;
            view = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, FontFamily = new FontFamily(Theme.Font) };
            // Click-through and never activated.
            view.SourceInitialized += delegate { IntPtr h = new WindowInteropHelper(view).Handle; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x08000000 | 0x80); };
            poll.Tick += delegate {
                POINT p; bool inside = GetCursorPos(out p) && p.X >= anchor.Left - 1 && p.X <= anchor.Right + 1 && p.Y >= anchor.Top - 1 && p.Y <= anchor.Bottom + 1;
                if (!inside) { muted = false; Hide(); return; }
                if (muted) return;
                rested += poll.Interval.TotalMilliseconds; if (rested >= 350 && !view.IsVisible) Show();
            };
        }
        // bounds: the summary's screen rectangle in physical pixels.
        public void Hover(Rect bounds) { if (bounds.IsEmpty || poll.IsEnabled) return; anchor = bounds; rested = 0; poll.Start(); }
        public void Hide() { poll.Stop(); if (view.IsVisible) view.Hide(); }
        // A click on the summary (menu or details) puts the flyout away until the pointer leaves it.
        private bool muted;
        public void Dismiss() { if (poll.IsEnabled) muted = true; if (view.IsVisible) view.Hide(); }
        private void Show() {
            view.Resources = Theme.Styles(); view.Content = Build();
            ((FrameworkElement)view.Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = ((FrameworkElement)view.Content).DesiredSize;
            double scale = 1; using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.Left, (int)anchor.Top)).Bounds;
            view.Left = Math.Max(screen.Left / scale, Math.Min(anchor.Right / scale - size.Width + 12, screen.Right / scale - size.Width));
            view.Top = Math.Max(screen.Top / scale, anchor.Top / scale - size.Height + 4);
            view.Show();
        }
        private FrameworkElement Build() {
            List<ProviderState> states = readings(); DateTime now = DateTime.UtcNow;
            var body = new StackPanel { Width = 340 };
            var head = new TextBlock { Text = Pace.Headline(states, now), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(head);
            var overview = SubscriptionView.Classify(states);
            var ordered = overview.Alerts.Select(a => a.State).Concat(overview.Attention).Distinct().ToList();
            ordered.AddRange(states.Where(s => !ordered.Contains(s)));
            foreach (ProviderState s in ordered) body.Children.Add(Row(s, now));
            if (states.Count == 0) body.Children.Add(Text("还没有订阅，点击打开设置添加。", 11, Theme.Dim));
            return new Border { Margin = new Thickness(12), Padding = new Thickness(14, 12, 14, 10), CornerRadius = new CornerRadius(10), Background = Theme.Surface, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), Child = body, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = .3, Color = Colors.Black } };
        }
        private FrameworkElement Row(ProviderState s, DateTime now) {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var logo = icon(s.Id, 16); logo.VerticalAlignment = VerticalAlignment.Top; logo.Margin = new Thickness(0, 1, 0, 0); row.Children.Add(logo);
            var name = Text(SubscriptionView.Name(s.Id), 12, Theme.Ink); name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(name, 1); row.Children.Add(name);
            var items = new WrapPanel(); Grid.SetColumn(items, 2); row.Children.Add(items);
            bool old = SubscriptionView.Old(s);
            if (s.Quotas.Count == 0 && s.Balances.Count == 0) items.Children.Add(Text(s.Message, 11, Theme.Dim));
            foreach (Quota q in s.Quotas) {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 3) };
                cell.Children.Add(Text(SubscriptionView.AlertWindow(q.Label), 10, Theme.Dim));
                var track = new Grid { Width = 34, Height = 4, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(2) });
                double fill = Math.Max(0, Math.Min(100, q.Remaining));
                if (fill > 0) track.Children.Add(new Border { Background = Theme.Paint(Theme.Tone(q.Remaining)), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 34 * fill / 100 });
                cell.Children.Add(track);
                var value = Text(q.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%", 11, Theme.Paint(old ? Theme.UnknownHex : Theme.Tone(q.Remaining))); value.FontWeight = FontWeights.SemiBold; cell.Children.Add(value);
                WindowPace pace = Pace.Of(q, now);
                if (pace != null && pace.Short && q.Remaining > 0) { var warn = Text(" · " + Pace.Compact(pace.RunOut.Value) + "用完", 10, Theme.Paint(Theme.WarnHex)); cell.Children.Add(warn); }
                items.Children.Add(cell);
            }
            foreach (Balance b in s.Balances) {
                double score = SubscriptionView.BalanceWarn > 0 ? b.Amount / SubscriptionView.BalanceWarn * 20 : 100;
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 3) };
                cell.Children.Add(Text("余额 ", 10, Theme.Dim));
                var value = Text(SubscriptionView.Money(b), 11, Theme.Paint(old ? Theme.UnknownHex : Theme.Tone(score))); value.FontWeight = FontWeights.SemiBold; cell.Children.Add(value);
                items.Children.Add(cell);
            }
            if (old) items.Children.Add(Text("旧读数", 10, Theme.Dim));
            return row;
        }
        private static TextBlock Text(string text, double size, Brush color) { return new TextBlock { Text = text, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center }; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
