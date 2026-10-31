using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel {
        // Sign-in window for platforms read with a web console token (token.kind = console): the console
        // opens in an embedded Edge (WebView2) and once the user is signed in the token is read from the
        // page (token.read), checked against the platform and saved, the way the DevTools guide would.
        // The browser profile lives in the data directory, so the next sign-in is usually one click.
        private Window loginWindow;
        private static bool? webViewReady;
        public static bool WebViewAvailable {
            get {
                if (webViewReady == null) { try { webViewReady = !String.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); } catch { webViewReady = false; } }
                return webViewReady == true;
            }
        }
        private void OpenConsoleLogin(string id) {
            CustomProvider recipe; if (!ProviderCatalog.Custom.TryGetValue(id, out recipe) || !recipe.ConsoleToken) { EditSubscription(id, null); return; }
            // Without the Edge runtime, the demo, or a recipe with no read expression, the manual guide still works.
            if (demo || !WebViewAvailable || recipe.TokenScript.Length == 0) { OpenPlatformGuide(id); return; }
            if (loginWindow != null) { loginWindow.Close(); loginWindow = null; }

            var status = new TextBlock { FontSize = 12, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var bar = new DockPanel { Margin = new Thickness(16, 10, 12, 10), LastChildFill = true };
            var manual = Link("改用手动方式", null); DockPanel.SetDock(manual, Dock.Right); bar.Children.Add(manual);
            var logo = Icon(id, 20); logo.Margin = new Thickness(0, 0, 10, 0); logo.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(logo, Dock.Left); bar.Children.Add(logo);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "登录 " + recipe.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            text.Children.Add(status); bar.Children.Add(text);
            var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            var root = new DockPanel();
            var head = new Border { Child = bar, BorderBrush = Theme.Edge, BorderThickness = new Thickness(0, 0, 0, 1) }; DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            root.Children.Add(web);
            var window = new Window { Title = "登录 " + recipe.Name, Width = 1040, Height = Math.Min(800, SystemParameters.WorkArea.Height - 40), MinWidth = 520, MinHeight = 420, Background = Theme.Window, Foreground = Theme.Ink, FontFamily = new FontFamily(Theme.Font), WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = root };
            window.Resources = Theme.Styles(); Theme.Frame(window);
            if (quotaDashboard.IsOpen) { window.Owner = quotaDashboard.View; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            loginWindow = window;

            Action<string, string> say = (message, hex) => { status.Text = message; status.Foreground = hex == null ? Theme.Dim : Theme.Paint(hex); };
            say("在下面的页面里登录，登录后会自动读取并保存，不用复制任何东西。", null);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            var rejected = new HashSet<string>();   // tokens the platform already turned down
            bool busy = false, done = false;
            manual.Click += delegate { window.Close(); OpenPlatformGuide(id); };
            window.Closed += delegate { timer.Stop(); if (loginWindow == window) loginWindow = null; try { web.Dispose(); } catch { } TrimWebViewCache(); };
            string script = "(function(){try{var t=(" + recipe.TokenScript + ");return typeof t==='string'?t:null;}catch(e){return null;}})()";
            timer.Tick += async delegate {
                if (busy || done || web.CoreWebView2 == null) return;
                busy = true;
                try {
                    string token = J.Parse(await web.CoreWebView2.ExecuteScriptAsync(script)) as string;
                    token = (token ?? "").Trim().Trim('"', '\'');
                    if (token.Length < 20 || token.Contains(" ") || rejected.Contains(token)) return;
                    say("已登录，正在验证…", null);
                    string result = await TryConsoleToken(id, recipe, token);
                    if (result == null) {
                        done = true; timer.Stop(); say("已连接，窗口即将关闭。", Theme.GoodHex);
                        await Task.Delay(900); window.Close();
                    } else { rejected.Add(token); say(result, Theme.UrgentHex); }
                } catch { } finally { busy = false; }
            };
            window.Loaded += async delegate {
                try {
                    string folder = Path.Combine(Store.Data, "webview2");
                    // The HTTP cache is capped at 10 MB; only the sign-in (cookies, local storage) needs to last.
                    var environment = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions("--disk-cache-size=10485760"));
                    await web.EnsureCoreWebView2Async(environment);
                    web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    web.CoreWebView2.Navigate(recipe.TokenConsole);
                    timer.Start();
                } catch (Exception e) {
                    say("内置浏览器启动失败（" + e.Message + "），请改用手动方式。", Theme.WarnHex);
                }
            };
            window.Show();
        }
        // Saves the token, reads the platform once and keeps it only when the platform accepts it.
        // Returns null when connected, otherwise what the user should do next.
        private async Task<string> TryConsoleToken(string id, CustomProvider recipe, string token) {
            string previous = Store.SavedKey(id);
            Store.SetProviderKey(id, token);
            if (!config.Enabled.Contains(id)) { config.Enabled = config.Enabled.Concat(new[] { id }).ToArray(); switchedOff.Remove(id); SaveConfig(); }
            await RefreshOne(id);
            ProviderState s; states.TryGetValue(id, out s);
            RenderSubscriptionRows(); quotaDashboard.Update();
            if (s != null && s.Status == "ready") return null;
            if (s != null && s.Status == "expired") {
                Store.SetProviderKey(id, previous ?? "");
                return "页面里的登录信息已失效，请在页面上退出后重新登录。";
            }
            // Not a rejection (network, rate limit): keep the token, the next refresh retries it.
            return "暂时没读到数据" + (s != null && s.Message.Length > 0 ? "：" + s.Message : "") + "。token 已保存，稍后会自动重试，可以关掉这个窗口。";
        }
        // Once the login window is gone (its browser processes exit a moment later) the page caches are
        // deleted; cookies and local storage stay, so signing in again is usually one click.
        private static readonly string[] WebViewCaches = {
            @"EBWebView\Default\Cache", @"EBWebView\Default\Code Cache", @"EBWebView\Default\GPUCache", @"EBWebView\Default\DawnCache",
            @"EBWebView\Default\DawnGraphiteCache", @"EBWebView\Default\DawnWebGPUCache", @"EBWebView\Default\Service Worker\CacheStorage",
            @"EBWebView\Default\Service Worker\ScriptCache", @"EBWebView\GrShaderCache", @"EBWebView\ShaderCache", @"EBWebView\GraphiteDawnCache", @"EBWebView\Crashpadeports" };
        private static void TrimWebViewCache() {
            string folder = Path.Combine(Store.Data, "webview2");
            Task.Run(async () => {
                await Task.Delay(4000);
                foreach (string name in WebViewCaches) { try { string dir = Path.Combine(folder, name); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
            });
        }
    }
}
