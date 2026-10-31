using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: AssemblyTitle("余量 Headroom")]
[assembly: AssemblyDescription("A Windows taskbar monitor for AI subscription quotas and local token usage")]
[assembly: AssemblyVersion("2.1.0.0")]
namespace Headroom {
    public static class Program {
        private static Mutex mutex;
        private static EventWaitHandle showEvent;
        private static EventWaitHandle quitEvent;
        // --side-by-side: a second copy (another folder, its own data) beside the installed one,
        // e.g. to try a new build. It never touches the autostart entry.
        public static bool SideBySide, DemoAll;
        // The name people see (windows, tray, notifications); files, folders and registry use "Headroom".
        public const string AppName = "余量 Headroom";
        [STAThread] public static int Main(string[] args) {
            if (args.Contains("--demo") && args.Contains("--probe")) return 2;
            SideBySide = args.Contains("--side-by-side"); DemoAll = args.Contains("--demo-all");
            string instance = @"Local\Headroom" + (args.Contains("--demo") ? ".Preview" : SideBySide ? ".SideBySide" : "");
            if (args.Contains("--quit")) { try { using (var signal = EventWaitHandle.OpenExisting(instance + ".Quit")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } return 0; }
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Contains("--probe")) return Probe().GetAwaiter().GetResult();
            if (args.Contains("--demo")) Store.EnableDemoMode();
            bool created; mutex = new Mutex(true, instance + ".Instance", out created);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Show");
            if (!created) { showEvent.Set(); showEvent.Dispose(); mutex.Dispose(); return 0; }
            if (!SideBySide && !args.Contains("--demo")) MonitorPanel.MigrateStartup();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                try { File.AppendAllText(System.IO.Path.Combine(Store.Data, "errors.log"), DateTime.UtcNow.ToString("o") + " " + e.Exception.GetType().Name + " " + e.Exception.Message + "\n"); } catch { }
                MessageBox.Show(I18n.T("窗口遇到一个错误。请退出后重新打开；已保存的数据不受影响。"), Program.AppName); e.Handled = true;
            };
            var ui = new MonitorPanel(app, args.Contains("--demo"));
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Quit");
            RegisteredWaitHandle listener = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate { app.Dispatcher.BeginInvoke(new Action(ui.ShowDashboard)); }, null, Timeout.Infinite, false);
            RegisteredWaitHandle quitListener = ThreadPool.RegisterWaitForSingleObject(quitEvent, delegate { app.Dispatcher.BeginInvoke(new Action(ui.Quit)); }, null, Timeout.Infinite, false);
            app.Dispatcher.BeginInvoke(new Action(() => ui.Start(args.Contains("--background"))), DispatcherPriority.ApplicationIdle);
            app.Exit += delegate { listener.Unregister(null); quitListener.Unregister(null); quitEvent.Dispose(); ui.Dispose(); showEvent.Dispose(); mutex.ReleaseMutex(); mutex.Dispose(); };
            app.Run(); return 0;
        }
        private static async Task<int> Probe() {
            var states = new List<ProviderState>(); using (var service = new ProviderService(Store.Read<AppConfig>("settings.json"))) { foreach (string id in ProviderCatalog.All) states.Add(await service.Fetch(id)); }
            // Probe reports contain no tokens, raw API responses, or account identifiers.
            foreach (var s in states) s.Account = "";
            Store.Write("probe-results.json", states); return states.Any(s => s.Status == "ready") ? 0 : 1;
        }
    }
    // Tray controller: the taskbar summary (TaskbarPanel), its detail flyout (QuotaDashboard),
    // subscription settings (SimpleSettings.cs) and the refresh cycle (Pacing.cs).
    public sealed partial class MonitorPanel : IDisposable {
        private readonly Application app;
        private readonly bool demo;
        private readonly Forms.NotifyIcon tray;
        private readonly TaskbarPanel taskbarPanel;
        private readonly QuotaDashboard quotaDashboard;
        private readonly DispatcherTimer refreshTimer;
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, string> icons = new Dictionary<string, string>();
        private AppConfig config;
        private Dictionary<string, ProviderState> states = new Dictionary<string, ProviderState>();
        private UsageHistory history;
        // DeepSeek's actual spend, from successive API-key balance readings (BalanceSpend).
        private BalanceLedger ledger = new BalanceLedger();
        // Hourly usage index per agent (tokens by kind + API-equivalent cost), from local logs.
        private Dictionary<string, LogIndex> localIndexes = new Dictionary<string, LogIndex>();
        private bool refreshing, scanning;
        private DateTime lastRefresh = DateTime.MinValue;
        public MonitorPanel(Application application, bool demoMode) {
            app = application; demo = demoMode; Directory.CreateDirectory(Store.Data); config = Store.Read<AppConfig>("settings.json"); I18n.Use(config.Language);
            config.RefreshMinutes = Math.Max(1, Math.Min(60, config.RefreshMinutes)); if (config.Enabled == null) config.Enabled = ProviderCatalog.DefaultEnabled;
            SubscriptionView.BalanceWarn = Math.Max(0, config.BalanceWarn);
            if (!demo) { Migrate(); RegisterCustomProviders(CustomProviders.Load()); }
            foreach (string id in ProviderCatalog.All) states[id] = new ProviderState { Id = id };
            try {
                foreach (var s in Store.Read<List<ProviderState>>("quota-cache.json").Select(Parsers.Normalize)) {
                    if (!states.ContainsKey(s.Id)) continue;
                    // Only entries that actually hold data are shown as the last good reading.
                    if (s.Quotas.Count == 0 && s.Balances.Count == 0) continue;
                    s.Stale = true; s.Status = "cached"; s.Message = "上次成功读取的数据"; states[s.Id] = s;
                }
            } catch { }
            history = Store.Read<UsageHistory>("history.json");
            if (!demo) ledger = Store.Read<BalanceLedger>("balance-ledger.json");
            if (!demo) localIndexes = UsageScanner.Load(UsageScanner.Agents.Select(a => a.Id));
            if (demo) SeedDemo();
            LoadIcons();
            tray = new Forms.NotifyIcon { Text = I18n.T("余量 · 正在读取"), Icon = new Drawing.Icon(System.IO.Path.Combine(Store.Root, "app.ico"), Forms.SystemInformation.SmallIconSize), Visible = true };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) app.Dispatcher.BeginInvoke(new Action(() => quotaDashboard.Toggle())); else if (e.Button == Forms.MouseButtons.Right) app.Dispatcher.BeginInvoke(new Action(ShowQuickMenu)); };
            tray.BalloonTipClicked += delegate { if (alertBalloon) { alertBalloon = false; app.Dispatcher.BeginInvoke(new Action(ShowDashboard)); } };
            quotaDashboard = new QuotaDashboard(SubscriptionStates, () => PlatformUsage.Combine(history, states.Values), Icon, BuildSettings, async () => { await Refresh(); while (refreshing) await Task.Delay(200); RenderSubscriptionRows(); }, demo);
            alertCard = new AlertCard(Icon, ShowDashboard, () => { snoozeUntil = DateTime.Now.AddHours(1); });
            quotaDashboard.VisibilityChanged += () => { if (quotaDashboard.IsOpen) alertCard.Hide(); };
            quotaDashboard.GuideNote = GuideNote; quotaDashboard.OpenGuide = OpenConsoleLogin;
            quotaDashboard.HourProfile = days => demo ? DemoHours(days) : UsageCharts.Hours(localIndexes.Values.Where(i => i != null).SelectMany(i => i.Entries()), DateTime.Today, days, TimeZoneInfo.Local);
            quotaDashboard.SpendNote = s => s.Id != "deepseek" ? null : BalanceSpend.Summary(ledger, s.Id, DateTime.Today) == null ? null : new[] { BalanceSpend.Summary(ledger, s.Id, DateTime.Today), BalanceSpend.Note(ledger, DateTime.Today) };
            taskbarPanel = new TaskbarPanel(SubscriptionStates, area => quotaDashboard.ToggleAt(area), () => quotaDashboard.IsOpen, ShowQuickMenu, TaskbarLogo) { Enabled = config.TaskbarPanel };
            quotaDashboard.VisibilityChanged += taskbarPanel.Repaint;
            flyout = new TaskbarFlyout(SubscriptionStates, Icon);
            taskbarPanel.HoverStart += flyout.Hover; quotaDashboard.VisibilityChanged += flyout.Dismiss;
            // Every 30 s: the platforms that are due (Pacing.cs).
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; refreshTimer.Tick += async delegate { await RefreshDue(); };
        }
        private IEnumerable<string> EnabledIds() { return ProviderCatalog.All.Where(config.Enabled.Contains); }
        private List<ProviderState> SubscriptionStates() {
            string[] order = config.SubscriptionOrder ?? new string[0];
            return EnabledIds().Where(id => states.ContainsKey(id))
                .OrderBy(id => { int at = Array.IndexOf(order, id); return at < 0 ? Int32.MaxValue : at; }).Select(id => states[id]).ToList();
        }
        // From the taskbar summary when it is shown (the flyout opens above it), else bottom-right.
        public void ShowDashboard() { quotaDashboard.ShowAt(taskbarPanel.Bounds); }
        // New readings: repaint the summary now (not on its 2-second tick) and an open flyout.
        private void Render() { var ignored = taskbarPanel.Refresh(); quotaDashboard.Update(); }
        // An alert when a window or balance first drops to urgent, or a window will run out within
        // the hour (once until it recovers); shown as the alert card or a tray balloon (settings).
        private readonly AlertWatch alertWatch = new AlertWatch();
        private AlertCard alertCard;
        private DateTime snoozeUntil;
        private bool alertBalloon;
        private void NotifyAlerts() {
            // The watch always updates, so snoozed or muted alerts are not replayed later.
            var fresh = alertWatch.Update(SubscriptionStates());
            if (fresh.Count == 0 || config.AlertStyle == "off" || DateTime.Now < snoozeUntil) return;
            ShowAlerts(fresh);
        }
        private void ShowAlerts(List<SubscriptionView.Alert> fresh) {
            if (config.AlertStyle != "system") { alertCard.Show(fresh); return; }
            string text = String.Join("；", fresh.Take(4).Select(a => SubscriptionView.Name(a.State.Id) + " · " + a.Window + " 剩 " + a.Value)) + (fresh.Count > 4 ? " 等 " + fresh.Count + " 项" : "");
            alertBalloon = true;
            try { tray.ShowBalloonTip(8000, fresh.Any(a => !a.Soon) ? "额度告急" : "额度快用完", text, Forms.ToolTipIcon.Warning); } catch { }
        }
        // Settings "预览": the two tightest items as they would be shown.
        private void PreviewAlerts() {
            DateTime now = DateTime.UtcNow;
            var sample = SubscriptionStates().Where(s => !SubscriptionView.Old(s)).SelectMany(s => SubscriptionView.Items(s)).OrderBy(a => a.Score).Take(2).ToList();
            foreach (var a in sample) a.Sub = AlertWatch.Describe(a, now);
            if (sample.Count > 0) ShowAlerts(sample);
        }
        private Drawing.Bitmap TaskbarLogo(string id) {
            var element = Icon(id, 20); element.Measure(new Size(20, 20)); element.Arrange(new Rect(0, 0, 20, 20));
            var bitmap = new RenderTargetBitmap(20, 20, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream()) { encoder.Save(stream); stream.Position = 0; using (var image = Drawing.Image.FromStream(stream)) return new Drawing.Bitmap(image); }
        }
        private void MoveSubscription(string id, int delta) {
            var ids = SubscriptionStates().Select(s => s.Id).ToList(); int from = ids.IndexOf(id), to = from + delta;
            if (from < 0 || to < 0 || to >= ids.Count) return;
            ids.RemoveAt(from); ids.Insert(to, id); config.SubscriptionOrder = ids.ToArray(); SaveConfig(); Render();
        }
        // The right-click panel of the taskbar summary and the tray icon: status, each subscription
        // (click to open it), then the actions with what each one does.
        private QuickMenu quickMenu;
        private TaskbarFlyout flyout;
        private void ShowQuickMenu() {
            if (flyout != null) flyout.Dismiss();
            if (quickMenu == null) quickMenu = new QuickMenu();
            if (quickMenu.IsOpen) { quickMenu.Hide(); return; }
            if (quickMenu.JustClosed) return;
            List<ProviderState> list = SubscriptionStates(); var overview = SubscriptionView.Classify(list);
            var root = new StackPanel();
            var head = new Grid { Margin = new Thickness(8, 5, 8, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition());
            try { head.Children.Add(new Image { Source = BitmapFrame.Create(new Uri(System.IO.Path.Combine(Store.Root, "app.ico"))), Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }); } catch { }
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = Program.AppName, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            string when = lastRefresh == DateTime.MinValue ? "尚未刷新" : lastRefresh.ToString("HH:mm") + " 更新";
            title.Children.Add(new TextBlock { Text = (list.Count == 0 ? "还没有订阅" : overview.Headline) + " · " + when, FontSize = 10.5, Foreground = list.Count == 0 ? Theme.Dim : Theme.Paint(overview.Tone), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(title, 1); head.Children.Add(title); root.Children.Add(head);
            if (list.Count > 0) {
                root.Children.Add(QuickMenu.Divider()); root.Children.Add(QuickMenu.Caption("订阅"));
                foreach (ProviderState s in list) {
                    string id = s.Id; var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var logo = Icon(id, 14); logo.HorizontalAlignment = HorizontalAlignment.Left; logo.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(logo);
                    var name = new TextBlock { Text = SubscriptionView.Name(id), FontSize = 12, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(name, 1); row.Children.Add(name);
                    var value = new TextBlock { Text = SubscriptionView.Summary(s), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Paint(SubscriptionView.Tone(s)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(value, 2); row.Children.Add(value);
                    var line = quickMenu.Item(row, SubscriptionView.Tooltip(s, DateTime.UtcNow), () => quotaDashboard.Show(id)); line.Padding = new Thickness(8, 4, 8, 4); root.Children.Add(line);
                }
            }
            root.Children.Add(QuickMenu.Divider());
            root.Children.Add(quickMenu.Action("", "打开详情", "额度、消耗趋势和各平台明细", null, ShowDashboard));
            root.Children.Add(quickMenu.Action("", "立即刷新", "重新读取全部平台的额度", "F5", () => { var ignored = Refresh(); }));
            root.Children.Add(quickMenu.Action("", "设置", "订阅、token、提醒与开机启动", null, OpenSubscriptionSettings));
            root.Children.Add(quickMenu.Switch("任务栏摘要", taskbarPanel.Note.Length > 0 ? taskbarPanel.Note : "在托盘左侧显示最紧的额度和各平台圆环", TaskbarPanel.Available && config.TaskbarPanel, TaskbarPanel.Available, on => {
                config.TaskbarPanel = on; taskbarPanel.Enabled = on; SaveConfig(); Render();
            }));
            root.Children.Add(QuickMenu.Divider());
            root.Children.Add(quickMenu.Action("", "退出", "关闭程序，停止读取和提醒", null, Quit));
            quickMenu.Show(root);
        }
        private void SeedDemo() {
            history = new UsageHistory { Zone = TimeZoneInfo.Local.Id, Updated = DateTime.UtcNow.ToString("o") };
            double[] shape = { .5, .8, 1.1, .3, 0, .9, 1.4, .6, .2, 0, 1.2, 1.8, .7, .4, 1, 1.5, .5, 0, .8, 1.3, 1.9, .6, .3, 1.1, 1.6, .9, .2, 1.2, 1.7, 1.4 };
            for (int i = 0; i < 30; i++) {
                string day = HistoryService.DayKey(DateTime.Today.AddDays(i - 29));
                if (shape[i] > 0) history.Days.Add(DemoDay(day, "codex", shape[i] * 9000000, 2.05e-6));
                if (i % 3 != 1) history.Days.Add(DemoDay(day, "claude", (1.2 - shape[i] / 2) * 1500000, 1.1e-6));
            }
            foreach (string id in new[] { "codex", "claude", "deepseek" }) {
                var state = new ProviderState { Id = id, Status = "ready", Message = "演示数据", Plan = id == "deepseek" ? "账户余额" : "演示账户", Account = "demo." + id + "@example.com", LastSuccess = DateTime.UtcNow.ToString("o") };
                if (id == "deepseek") {
                    state.Balances.Add(new Balance { Currency = "CNY", Amount = 8.62 });
                    ledger = new BalanceLedger { Since = HistoryService.DayKey(DateTime.Today.AddDays(-12)) };
                    ledger.Last.Add(new BalanceReading { Id = id, Currency = "CNY", Amount = 8.62 });
                    double[] spent = { .62, 1.4, 0, 2.05, .3, 0, 0, 3.1, .88, 0, 1.2, .45 };
                    for (int i = 0; i < spent.Length; i++) ledger.Days.Add(new SpendDay { Day = HistoryService.DayKey(DateTime.Today.AddDays(-i)), Id = id, Currency = "CNY", Spent = spent[i] });
                }
                else {
                    state.Quotas.Add(new Quota { Label = "每周", Used = id == "codex" ? 38 : id == "claude" ? 3 : 20, ResetUtc = DateTime.UtcNow.AddDays(3).AddHours(5).ToString("o"), WindowSeconds = 604800 });
                    if (id == "claude") state.Quotas.Insert(0, new Quota { Label = "5 小时", Used = 18, ResetUtc = DateTime.UtcNow.AddHours(1.5).ToString("o"), WindowSeconds = 18000 });
                }
                states[id] = state;
            }
            states["codex"].Quotas.Insert(0, new Quota { Label = "5 小时", Used = 83, ResetUtc = DateTime.UtcNow.AddMinutes(29).ToString("o"), WindowSeconds = 18000 });
            states["codex"].Plan = "Pro 20x";
            // Demo-only custom providers (kept in memory, never written to data/).
            CustomProvider sample = CustomProviders.Parse("{\"name\":\"示例网关\",\"id\":\"demo-gateway\",\"color\":\"#8FD1E8\",\"url\":\"https://gateway.example.com/v1/usage\",\"auth\":\"bearer\",\"windows\":[{\"label\":\"每日\",\"used\":\"daily.used\",\"limit\":\"daily.limit\"}],\"balance\":{\"amount\":\"balance\",\"currency\":\"CNY\"}}");
            RegisterCustomProviders(new[] { sample });
            CustomProvider agnesDemo; if (!ProviderCatalog.Custom.TryGetValue("agnes", out agnesDemo)) agnesDemo = sample;
            states[agnesDemo.Id] = CustomProviders.Map(agnesDemo, J.Parse("{\"data\":{\"plan_name\":\"Token Plan · 演示\",\"usage\":{\"text_generation\":{\"windowed\":{\"used\":35,\"limit\":100,\"reset_in_seconds\":5400},\"weekly\":{\"used\":72,\"limit\":100,\"reset_in_seconds\":259200}},\"image_generation\":{\"daily\":{\"used\":3,\"limit\":20,\"reset_in_seconds\":21600}},\"video_generation\":{\"daily\":{\"used\":2,\"limit\":5,\"reset_in_seconds\":21600}}}}}"), DateTime.UtcNow);
            states[agnesDemo.Id].Status = "ready"; states[agnesDemo.Id].LastSuccess = DateTime.UtcNow.ToString("o");
            states[sample.Id] = CustomProviders.Map(sample, J.Parse("{\"daily\":{\"used\":320,\"limit\":1000},\"balance\":46.8}"), DateTime.UtcNow);
            states[sample.Id].Status = "ready"; states[sample.Id].LastSuccess = DateTime.UtcNow.ToString("o"); states[sample.Id].Account = "demo@example.com";
            config.TaskbarPanel = true;
            config.Enabled = Program.DemoAll ? ProviderCatalog.All.ToArray() : new[] { "claude", "codex", agnesDemo.Id, "deepseek" };
            config.SubscriptionOrder = config.Enabled;
        }
        private static DayUsage DemoDay(string day, string agent, double tokens, double rate) {
            double cached = Math.Round(tokens * .972), input = Math.Round(tokens * .021), output = tokens - cached - input;
            var d = new DayUsage { Day = day, Agent = agent, Tokens = tokens, CachedTokens = cached, InputTokens = input, OutputTokens = output, Cost = tokens * rate, Requests = Math.Round(tokens / 52000), TimedRequests = Math.Round(tokens / 60000), TimedOutput = output * .8, TimedSeconds = output * .8 / (agent == "codex" ? 64 : 48) };
            string[] models = agent == "codex" ? new[] { "gpt-5.5", "gpt-5.5-mini" } : new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
            double[] shares = agent == "codex" ? new[] { .74, .26 } : new[] { .58, .34, .08 };
            for (int i = 0; i < models.Length; i++) d.Models.Add(new ModelUsage { Model = models[i], Tokens = tokens * shares[i], Cost = tokens * rate * shares[i] });
            return d;
        }
        // Demo hour-of-day profile: the period's tokens spread over a working day.
        private double[] DemoHours(int days) {
            double[] shape = { .2, .1, 0, 0, 0, 0, 0, .1, .6, 2.4, 4.1, 4.6, 2.2, 2.0, 4.8, 5.6, 5.1, 3.9, 2.0, 1.4, 2.6, 3.3, 2.1, .9 };
            double total = UsageCharts.Sum(history, DateTime.Today, days).Tokens, weight = shape.Sum();
            return shape.Select(v => v / weight * total).ToArray();
        }
        // One-time moves from the earlier access methods: the Agnes preset (a custom "agnes-plan"
        // entry) became the built-in "agnes" recipe, and DeepSeek reads everything with the web
        // console token, which was optional before. An old DeepSeek API key is set aside, not deleted.
        private void Migrate() {
            try {
                var file = Store.Read<CustomProviderFile>("custom-providers.json"); bool changed = false;
                foreach (string text in file.Definitions.ToList()) {
                    object root; try { root = J.Parse(text); } catch { continue; }
                    if (J.Str(root, "type") != "agnes-plan") continue;
                    string slug = J.Str(root, "id").Trim().ToLowerInvariant(); if (slug.Length == 0) slug = System.Text.RegularExpressions.Regex.Replace(J.Str(root, "name").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                    string old = "custom-" + slug;
                    if (slug.Length > 0 && Store.HasSavedKey(old)) { if (!Store.HasSavedKey("agnes")) Store.SetProviderKey("agnes", Store.SavedKey(old)); Store.SetProviderKey(old, ""); }
                    Func<string[], string[]> rename = ids => ids == null ? null : ids.Select(id => id == old ? "agnes" : id).Distinct().ToArray();
                    config.Enabled = rename(config.Enabled); config.SubscriptionOrder = rename(config.SubscriptionOrder);
                    file.Definitions.Remove(text); changed = true;
                }
                if (changed) Store.Write("custom-providers.json", file);
                string console = Store.SavedKey("deepseek-platform"), key = Store.SavedKey("deepseek");
                if (console.Length > 0) { if (key.StartsWith("sk-")) Store.SetProviderKey("deepseek-apikey", key); Store.SetProviderKey("deepseek", console); Store.SetProviderKey("deepseek-platform", ""); changed = true; }
                else if (key.StartsWith("sk-")) { Store.SetProviderKey("deepseek-apikey", key); Store.SetProviderKey("deepseek", ""); changed = true; }
                if (changed) SaveConfig();
            } catch { }
        }
        private void RegisterCustomProviders(IEnumerable<CustomProvider> providers) {
            ProviderCatalog.Use(providers);
            foreach (string id in ProviderCatalog.All) if (!states.ContainsKey(id)) states[id] = new ProviderState { Id = id };
        }
        public void Start(bool background) {
            Render();
            if (!background) app.Dispatcher.BeginInvoke(new Action(ShowDashboard), DispatcherPriority.Background);
            if (demo) { NotifyAlerts(); return; }
            WatchActivity(); refreshTimer.Start(); var a = Refresh(); var b = ScanHistory();
        }
        private void SaveConfig() { try { Store.Write("settings.json", config); } catch { } }
        private async Task Refresh() {
            if (demo) { Render(); return; }
            if (refreshing) return; refreshing = true;
            try {
                using (var service = new ProviderService(config)) {
                    var jobs = config.Enabled.Where(ProviderCatalog.IsKnown).Select(async id => {
                        Fetched(id); int serial = BeginFetch(id); ProviderState result = await Task.Run(() => service.Fetch(id));
                        if (Latest(id, serial)) Accept(id, result); Render();
                    }).ToArray();
                    await Task.WhenAll(jobs);
                }
                lastRefresh = DateTime.Now; Store.Write("quota-cache.json", states.Values.ToList()); UpdateTray(); NotifyAlerts();
            } catch { }
            finally { refreshing = false; Render(); }
            // At most once a day; newer prices re-price the history in the next scan.
            bool repriced = false; try { repriced = await Pricing.SyncAsync(config); } catch { }
            DateTime stamp; if (!scanning && (repriced || !DateTime.TryParse(history.Updated, out stamp) || DateTime.UtcNow - stamp.ToUniversalTime() > TimeSpan.FromMinutes(15))) { var ignored = ScanHistory(); }
        }
        // A failed read keeps the last good values (marked stale) instead of blanking them.
        private void Accept(string id, ProviderState incoming) {
            ProviderState old; if (!states.TryGetValue(id, out old)) old = new ProviderState { Id = id };
            Parsers.RetainLastGood(incoming, old);
            if (!demo && id == "deepseek" && incoming.Status == "ready" && !incoming.Stale && BalanceSpend.Record(ledger, id, incoming.Balances, DateTime.Now)) { try { Store.Write("balance-ledger.json", ledger); } catch { } }
            // A failed fetch keeps the last platform days; a successful one without them (console token
            // missing or expired) falls back to the local log estimate.
            if ((incoming.Status == "error" || incoming.Status == "expired" || incoming.PlatformAuth == "error") && old.PlatformDays != null && old.PlatformDays.Count > 0) incoming.PlatformDays = old.PlatformDays;
            states[id] = incoming;
        }
        // A full refresh and a single-provider refresh can overlap; only the newest fetch
        // of a provider is applied, so an older result never overwrites a newer one.
        private readonly Dictionary<string, int> fetchSerial = new Dictionary<string, int>();
        private int BeginFetch(string id) { int n; fetchSerial.TryGetValue(id, out n); fetchSerial[id] = ++n; return n; }
        private bool Latest(string id, int serial) { int n; return fetchSerial.TryGetValue(id, out n) && n == serial; }
        // Refreshes one provider (after saving a key or signing in from settings).
        private async Task RefreshOne(string id) {
            if (demo || !ProviderCatalog.IsKnown(id)) return;
            try {
                Fetched(id); int serial = BeginFetch(id);
                using (var service = new ProviderService(config)) { ProviderState result = await Task.Run(() => service.Fetch(id)); if (Latest(id, serial)) Accept(id, result); }
                Store.Write("quota-cache.json", states.Values.ToList()); UpdateTray(); NotifyAlerts();
            } catch { }
            finally { Render(); }
        }
        private async Task ScanHistory() {
            if (demo || scanning) return;
            scanning = true;
            try {
                // Every enabled agent's local logs are read incrementally into hourly indexes;
                // the days (tokens and API-equivalent cost) derive from them.
                var ids = UsageScanner.Agents.Select(a => a.Id).Where(id => config.Enabled.Contains(id)).ToList();
                Dictionary<string, LogIndex> scanned = await Task.Run(() => UsageScanner.ScanAll(ids, DateTime.UtcNow, true));
                foreach (var pair in scanned) localIndexes[pair.Key] = pair.Value;
                UsageHistory next = HistoryService.FromIndexes(localIndexes, TimeZoneInfo.Local, DateTime.Today, 30);
                history = HistoryService.Merge(history, next, HistoryService.DayKey(DateTime.Today.AddDays(-29)), HistoryService.DayKey(DateTime.Today));
                Store.Write("history.json", history);
            }
            catch { history.Error = "本地统计暂时不可用"; }
            finally { scanning = false; Render(); }
        }
        private void UpdateTray() {
            var overview = SubscriptionView.Classify(SubscriptionStates());
            string text = "余量 · " + overview.Headline;
            foreach (var a in overview.Alerts.Take(3)) text += "\n" + SubscriptionView.Name(a.State.Id) + " · " + a.Window + " " + a.Value;
            tray.Text = text.Length <= 63 ? text : text.Substring(0, 63);
        }
        private void LoadIcons() {
            foreach (string id in ProviderCatalog.All) {
                try {
                    // A recipe may name one of the shipped icons; otherwise the file named after its id.
                    CustomProvider recipe; string file = ProviderCatalog.Custom.TryGetValue(id, out recipe) && recipe.Icon.Length > 0 ? recipe.Icon : id;
                    if (!System.IO.File.Exists(System.IO.Path.Combine(Store.Root, "icons", file + ".svg"))) continue;
                    var xml = new XmlDocument(); xml.Load(System.IO.Path.Combine(Store.Root, "icons", file + ".svg")); var paths = xml.GetElementsByTagName("path");
                    if (paths.Count == 0) continue;
                    // WPF path markup: "F0" selects the even-odd fill rule used by some logos.
                    bool evenOdd = xml.OuterXml.Contains("fill-rule=\"evenodd\"") || xml.OuterXml.Contains("fill-rule:evenodd");
                    string all = evenOdd ? "F0 " : ""; foreach (XmlNode p in paths) { if (p.Attributes["d"] != null) all += p.Attributes["d"].Value + " "; }
                    icons[id] = all;
                } catch { }
            }
        }
        private static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color); brush.Freeze(); return brush; }
        private FrameworkElement Icon(string id, double size) {
            string data; if (icons.TryGetValue(id, out data)) { try { return new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Fill = Brush(ProviderCatalog.Color(id)), Stretch = Stretch.Uniform, Width = size, Height = size }; } catch { } }
            return new TextBlock { Text = ProviderCatalog.Name(id).Substring(0, 1), FontSize = size - 2, Width = size, Height = size, TextAlignment = TextAlignment.Center, Foreground = Brush(ProviderCatalog.Color(id)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "Headroom", LegacyRunValue = "codeusagemonit";
        private static string StartupCommand() { return "\"" + System.IO.Path.Combine(Store.Root, "headroom.exe") + "\" --background"; }
        private bool StartupEnabled() { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(RunValue) != null; }
        private void SetStartup(bool enabled) { if (Program.SideBySide) return; using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)) { if (enabled) key.SetValue(RunValue, StartupCommand()); else key.DeleteValue(RunValue, false); } }
        // Versions up to 2.0.1 were codeusagemonit.exe with a "codeusagemonit" autostart entry:
        // replace that entry with one for this exe, so starting with Windows carries over.
        internal static void MigrateStartup() {
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)) {
                    if (key == null || key.GetValue(LegacyRunValue) == null) return;
                    key.DeleteValue(LegacyRunValue, false);
                    if (key.GetValue(RunValue) == null) key.SetValue(RunValue, StartupCommand());
                }
            } catch { }
        }
        public void Quit() { SaveConfig(); taskbarPanel.Dispose(); quotaDashboard.Dispose(); tray.Visible = false; app.Shutdown(); }
        public void Dispose() { quotaDashboard.Dispose(); taskbarPanel.Dispose(); refreshTimer.Stop(); foreach (var watcher in watchers) watcher.Dispose(); tray.Visible = false; tray.Dispose(); }
    }
}
