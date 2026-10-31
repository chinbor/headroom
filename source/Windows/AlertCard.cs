using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodeUsageMonit {
    // The alert card in the bottom-right corner: which windows just turned urgent (or will run
    // out within the hour), with the reason on a second line. It never takes focus; it closes
    // itself after a while, but stays while the pointer is on it.
    public sealed class AlertCard {
        private readonly Window view;
        private readonly Func<string, double, FrameworkElement> icon;
        private readonly Action open, snooze;
        private readonly DispatcherTimer life = new DispatcherTimer();
        public AlertCard(Func<string, double, FrameworkElement> providerIcon, Action openDetails, Action snoozeHour) {
            icon = providerIcon; open = openDetails; snooze = snoozeHour;
            view = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, FontFamily = new FontFamily(Theme.Font) };
            // Clickable but never activated, and kept out of Alt+Tab.
            view.SourceInitialized += delegate { IntPtr h = new WindowInteropHelper(view).Handle; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x80); };
            life.Tick += delegate { Hide(); };
            view.MouseEnter += delegate { life.Stop(); };
            view.MouseLeave += delegate { Restart(5); };
        }
        public bool IsOpen { get { return view.IsVisible; } }
        public void Show(IList<SubscriptionView.Alert> alerts) {
            if (alerts == null || alerts.Count == 0) return;
            view.Resources = Theme.Styles(); view.Content = Build(alerts);
            var content = (FrameworkElement)view.Content; content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = 1; using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
            var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            view.Left = area.Right / scale - content.DesiredSize.Width - 4; view.Top = area.Bottom / scale - content.DesiredSize.Height - 4;
            content.Opacity = 0; content.RenderTransform = new TranslateTransform(0, 12);
            if (!view.IsVisible) view.Show();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            content.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            Restart(alerts.Any(a => !a.Soon) ? 15 : 10);
        }
        public void Hide() { life.Stop(); if (view.IsVisible) view.Hide(); }
        private void Restart(int seconds) { life.Stop(); life.Interval = TimeSpan.FromSeconds(seconds); life.Start(); }
        private FrameworkElement Build(IList<SubscriptionView.Alert> alerts) {
            bool urgent = alerts.Any(a => !a.Soon);
            string tone = urgent ? Theme.UrgentHex : Theme.WarnHex;
            var body = new StackPanel { Width = 320 };

            var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Theme.Soft(tone), Margin = new Thickness(0, 0, 8, 0), Child = new TextBlock { Text = "!", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Theme.Paint(tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            head.Children.Add(mark);
            var title = Text(urgent ? "额度告急" : "额度快用完", 13, Theme.Ink); title.FontWeight = FontWeights.SemiBold; Grid.SetColumn(title, 1); head.Children.Add(title);
            var close = QuotaDashboard.Button("✕", "关闭提醒", Hide); close.Width = 24; close.Height = 22; close.Padding = new Thickness(0); close.FontSize = 10; close.Background = Brushes.Transparent; close.Foreground = Theme.Dim; Grid.SetColumn(close, 2); head.Children.Add(close);
            body.Children.Add(head);

            foreach (var a in alerts.Take(3)) body.Children.Add(Row(a));
            if (alerts.Count > 3) { var more = Text("另有 " + (alerts.Count - 3) + " 项，打开详情查看", 11, Theme.Dim); more.Margin = new Thickness(28, 2, 0, 0); body.Children.Add(more); }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var later = QuotaDashboard.Button("1 小时内不再提醒", "1 小时内不再提醒", () => { snooze(); Hide(); }); later.Height = 28; later.Padding = new Thickness(10, 0, 10, 0); later.Margin = new Thickness(0, 0, 6, 0); later.FontSize = 11;
            var details = QuotaDashboard.Button("查看详情", "查看详情", () => { Hide(); open(); }); details.Height = 28; details.Padding = new Thickness(12, 0, 12, 0); details.FontSize = 11; details.Background = Theme.Accent; details.Foreground = Theme.AccentInk;
            actions.Children.Add(later); actions.Children.Add(details); body.Children.Add(actions);

            // A thin strip on the left carries the card's tone.
            var strip = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Theme.Paint(tone), Margin = new Thickness(0, 2, 12, 2) };
            var inner = new DockPanel(); DockPanel.SetDock(strip, Dock.Left); inner.Children.Add(strip); inner.Children.Add(body);
            var card = new Border { Margin = new Thickness(14), Padding = new Thickness(10, 12, 14, 12), CornerRadius = new CornerRadius(10), Background = Theme.Surface, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), Child = inner, Cursor = Cursors.Hand, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = .32, Color = Colors.Black } };
            // Clicking anywhere but the buttons opens the details too.
            card.MouseLeftButtonUp += (sender, e) => { if (!(e.OriginalSource is DependencyObject) || FindButton((DependencyObject)e.OriginalSource) == null) { Hide(); open(); } };
            return card;
        }
        private FrameworkElement Row(SubscriptionView.Alert a) {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = icon(a.State.Id, 18); logo.VerticalAlignment = VerticalAlignment.Top; logo.Margin = new Thickness(0, 1, 0, 0); row.Children.Add(logo);
            var text = new StackPanel(); Grid.SetColumn(text, 1); row.Children.Add(text);
            text.Children.Add(Text(SubscriptionView.Name(a.State.Id) + " · " + a.Window, 12, Theme.Ink));
            if (a.Sub.Length > 0) { var sub = Text(a.Sub, 11, a.Soon ? Theme.Paint(Theme.WarnHex) : Theme.Dim); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 2, 8, 0); text.Children.Add(sub); }
            var value = Text(a.Value, 13, Theme.Paint(Theme.Tone(a.Score))); value.FontWeight = FontWeights.SemiBold; value.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(value, 2); row.Children.Add(value);
            return row;
        }
        private static Button FindButton(DependencyObject d) { while (d != null && !(d is Button)) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); return d as Button; }
        private static TextBlock Text(string text, double size, Brush color) { return new TextBlock { Text = text, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center }; }
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
