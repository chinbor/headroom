using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodeUsageMonit {
    // One visual system for the detail window and the settings editors: colour tokens that
    // follow Windows' app light/dark mode and accent colour, and themed control templates
    // (switch, drop-down, text boxes, thin scroll bars, tool tips). Status colours carry
    // meaning only (good / tight / urgent); platform colours stay on logos and chart series.
    public static class Theme {
        public static bool Dark { get; private set; }
        public static Color AccentColor { get; private set; }
        public static event Action Changed;
        public const string Font = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
        private static Dispatcher dispatcher;
        private static readonly Dictionary<string, Brush> cache = new Dictionary<string, Brush>();

        static Theme() { Read(); }
        // Call once on the UI thread; theme changes are then re-read and announced there.
        public static void Watch(Dispatcher ui) {
            if (dispatcher != null) return; dispatcher = ui;
            SystemEvents.UserPreferenceChanged += delegate(object sender, UserPreferenceChangedEventArgs e) {
                if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle) return;
                dispatcher.BeginInvoke(new Action(() => {
                    bool dark = Dark; Color accent = AccentColor; Read();
                    if ((dark != Dark || accent != AccentColor) && Changed != null) Changed();
                }));
            };
        }
        private static void Read() {
            bool dark = true; Color accent = Color.FromRgb(0x4C, 0x8D, 0xFF);
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme"); if (v is int) dark = (int)v == 0;
                }
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM")) {
                    object v = key == null ? null : key.GetValue("AccentColor");
                    if (v is int) { uint abgr = unchecked((uint)(int)v); accent = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF)); }
                }
            } catch { }
            // CODEUSAGEMONIT_THEME=light|dark overrides Windows (for checking both looks).
            string forced = Environment.GetEnvironmentVariable("CODEUSAGEMONIT_THEME");
            if (forced == "light") dark = false; else if (forced == "dark") dark = true;
            // A grey accent (Windows' "automatic" on a grey wallpaper) would make an "on" switch look
            // disabled; fall back to the app blue.
            double max = Math.Max(accent.R, Math.Max(accent.G, accent.B)), min = Math.Min(accent.R, Math.Min(accent.G, accent.B));
            if (max == 0 || (max - min) / max < .25) accent = dark ? Color.FromRgb(0x5B, 0x9B, 0xFF) : Color.FromRgb(0x25, 0x63, 0xEB);
            // Keep the accent readable on the window: lift dark accents in dark mode, deepen pale ones in light mode.
            double l = Luma(accent);
            if (dark && l < 90) accent = Mix(accent, Colors.White, .35); else if (!dark && l > 170) accent = Mix(accent, Colors.Black, .3);
            Dark = dark; AccentColor = accent; cache.Clear();
        }
        private static double Luma(Color c) { return .299 * c.R + .587 * c.G + .114 * c.B; }
        private static Color Mix(Color a, Color b, double t) { return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t)); }
        private static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        // Tokens as hex for the current mode.
        public static string WindowHex { get { return Dark ? "#1E1F23" : "#F3F4F6"; } }
        public static string SurfaceHex { get { return Dark ? "#27292E" : "#FFFFFF"; } }
        public static string RaisedHex { get { return Dark ? "#33353B" : "#EEF0F3"; } }
        public static string HoverHex { get { return Dark ? "#3E4047" : "#E3E6EA"; } }
        public static string EdgeHex { get { return Dark ? "#34363C" : "#E2E5E9"; } }
        public static string TrackHex { get { return Dark ? "#3A3C42" : "#E4E7EB"; } }
        public static string InkHex { get { return Dark ? "#EDF0F5" : "#1B1D22"; } }
        public static string DimHex { get { return Dark ? "#949EAD" : "#5E6977"; } }
        public static string AccentHex { get { return Hex(AccentColor); } }
        public static string AccentInkHex { get { return Luma(AccentColor) > 165 ? "#111317" : "#FFFFFF"; } }
        // Status colours after the Fluent system palette (success, caution, critical), shared by the
        // window, the flyout and the taskbar summary. level: 0 critical, 1 caution, 2 success.
        public static string Status(bool light, int level) {
            return level == 0 ? (light ? "#C42B1C" : "#FF7B72") : level == 1 ? (light ? "#9D5D00" : "#F2C14E") : (light ? "#0F7B0F" : "#6CCB5F");
        }
        public static string GoodHex { get { return Status(!Dark, 2); } }
        public static string WarnHex { get { return Status(!Dark, 1); } }
        public static string UrgentHex { get { return Status(!Dark, 0); } }
        public static string UnknownHex { get { return Dark ? "#818891" : "#7A828C"; } }
        // Status colour for a remaining share (or balance score): <20 urgent, <40 tight.
        public static string Tone(double? remaining) { return !remaining.HasValue ? UnknownHex : remaining < 20 ? UrgentHex : remaining < 40 ? WarnHex : GoodHex; }
        // A tinted background for a status chip of that colour.
        public static Brush Soft(string hex) { return Paint("soft:" + hex, () => Mix(Parse(SurfaceHex), Parse(hex), Dark ? .2 : .13)); }

        public static Brush Window { get { return Paint(WindowHex); } }
        public static Brush Surface { get { return Paint(SurfaceHex); } }
        public static Brush Raised { get { return Paint(RaisedHex); } }
        public static Brush Hover { get { return Paint(HoverHex); } }
        public static Brush Edge { get { return Paint(EdgeHex); } }
        public static Brush Track { get { return Paint(TrackHex); } }
        public static Brush Ink { get { return Paint(InkHex); } }
        public static Brush Dim { get { return Paint(DimHex); } }
        public static Brush Accent { get { return Paint(AccentHex); } }
        public static Brush AccentInk { get { return Paint(AccentInkHex); } }
        public static Brush Paint(string hex) { return Paint(hex, () => Parse(hex)); }
        private static Brush Paint(string key, Func<Color> color) {
            Brush b; if (cache.TryGetValue(key, out b)) return b;
            var brush = new SolidColorBrush(color()); brush.Freeze(); cache[key] = brush; return brush;
        }
        private static Color Parse(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        // Dark title bar for ordinary (framed) windows in dark mode.
        public static void Frame(Window window) {
            window.SourceInitialized += delegate {
                try { int on = Dark ? 1 : 0; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref on, 4); } catch { }
            };
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Implicit control styles in the current colours; reassign after Changed.
        public static ResourceDictionary Styles() {
            string xaml = StylesXaml.Replace("{Window}", WindowHex).Replace("{Surface}", SurfaceHex).Replace("{Raised}", RaisedHex).Replace("{Hover}", HoverHex)
                .Replace("{Edge}", EdgeHex).Replace("{Track}", TrackHex).Replace("{Ink}", InkHex).Replace("{Dim}", DimHex).Replace("{Accent}", AccentHex).Replace("{AccentInk}", AccentInkHex);
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }
        private const string StylesXaml = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='{Ink}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>
      <Border Background='{Surface}' BorderBrush='{Edge}' BorderThickness='1' CornerRadius='6' Padding='9,6'><ContentPresenter TextBlock.FontSize='11'/></Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{Ink}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
      <StackPanel Orientation='Horizontal' Background='Transparent'>
        <Grid Width='40' Height='22' VerticalAlignment='Center'>
          <Border x:Name='T' CornerRadius='11' Background='{Raised}' BorderBrush='{Dim}' BorderThickness='1'/>
          <Ellipse x:Name='K' Width='12' Height='12' Fill='{Dim}' HorizontalAlignment='Left' Margin='5,0,0,0'/>
        </Grid>
        <ContentPresenter x:Name='C' Margin='10,0,0,0' VerticalAlignment='Center'/>
      </StackPanel>
      <ControlTemplate.Triggers>
        <Trigger Property='Content' Value='{x:Null}'><Setter TargetName='C' Property='Margin' Value='0'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='BorderBrush' Value='{Ink}'/><Setter TargetName='K' Property='Fill' Value='{Ink}'/></Trigger>
        <Trigger Property='IsChecked' Value='True'>
          <Setter TargetName='T' Property='Background' Value='{Accent}'/><Setter TargetName='T' Property='BorderBrush' Value='{Accent}'/>
          <Setter TargetName='K' Property='Fill' Value='{AccentInk}'/><Setter TargetName='K' Property='HorizontalAlignment' Value='Right'/>
          <Setter TargetName='K' Property='Margin' Value='0,0,4,0'/><Setter TargetName='K' Property='Width' Value='14'/><Setter TargetName='K' Property='Height' Value='14'/>
        </Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{Ink}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
      <Border x:Name='B' Background='Transparent' CornerRadius='5' Padding='10,6'><ContentPresenter/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='B' Property='Background' Value='{Hover}'/></Trigger>
        <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{Accent}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{Ink}'/>
    <Setter Property='Height' Value='32'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
      <Grid>
        <ToggleButton Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
          <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
            <Border x:Name='B' Background='{Raised}' BorderBrush='{Edge}' BorderThickness='1' CornerRadius='7'>
              <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,11,0' Data='M0,0 L4,4 L8,0' Stroke='{Dim}' StrokeThickness='1.5'/>
            </Border>
            <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='{Hover}'/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <ContentPresenter IsHitTestVisible='False' Margin='11,0,30,0' VerticalAlignment='Center' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>
        <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
          <Border Background='{Surface}' BorderBrush='{Edge}' BorderThickness='1' CornerRadius='8' Padding='4' Margin='0,4,0,0' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
            <ScrollViewer MaxHeight='300' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
          </Border>
        </Popup>
      </Grid>
      <ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{Ink}'/><Setter Property='CaretBrush' Value='{Ink}'/><Setter Property='Padding' Value='9,7'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TextBox'>
      <Border x:Name='B' Background='{Raised}' BorderBrush='{Edge}' BorderThickness='1' CornerRadius='7'><ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='{Accent}'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='PasswordBox'>
    <Setter Property='Foreground' Value='{Ink}'/><Setter Property='CaretBrush' Value='{Ink}'/><Setter Property='Padding' Value='9,7'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='PasswordBox'>
      <Border x:Name='B' Background='{Raised}' BorderBrush='{Edge}' BorderThickness='1' CornerRadius='7'><ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='{Accent}'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <ControlTemplate x:Key='ThumbTemplate' TargetType='Thumb'>
    <Border x:Name='Th' CornerRadius='3' Background='{Track}' Margin='3'/>
    <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Th' Property='Background' Value='{Dim}'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='11'/><Setter Property='MinWidth' Value='11'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
      <Track x:Name='PART_Track' IsDirectionReversed='True'><Track.Thumb><Thumb Template='{StaticResource ThumbTemplate}'/></Track.Thumb></Track>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/><Setter Property='MinWidth' Value='0'/><Setter Property='Height' Value='11'/><Setter Property='MinHeight' Value='11'/>
        <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track'><Track.Thumb><Thumb Template='{StaticResource ThumbTemplate}'/></Track.Thumb></Track>
        </ControlTemplate></Setter.Value></Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='ResizeGrip'><Setter Property='Opacity' Value='.35'/></Style>
</ResourceDictionary>";
    }
}
