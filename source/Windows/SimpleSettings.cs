using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel {
        private StackPanel subscriptionRows;
        // Turned off in this session: still listed so the switch can turn them back on.
        private readonly HashSet<string> switchedOff = new HashSet<string>();
        private void OpenSubscriptionSettings() { quotaDashboard.ShowTab("settings"); }
        // The "设置" page of the detail window, rebuilt whenever the page is shown.
        private FrameworkElement BuildSettings() {
            var root = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
            if (demo) root.Children.Add(Notice("演示模式：这里的改动只在内存中生效，不读取也不保存真实凭据。", Theme.WarnHex));
            subscriptionRows = new StackPanel(); addArea = new StackPanel();
            root.Children.Add(Card("订阅", "任务栏和概览按这个顺序显示；关掉开关就停用。", subscriptionRows, addArea));

            var warn = new ComboBox { Width = 170 };
            foreach (double amount in new[] { 0.0, 1, 2, 5, 10, 20, 50 }) warn.Items.Add(new ComboBoxItem { Content = amount == 0 ? "不告警" : "低于 " + amount.ToString("0", CultureInfo.InvariantCulture), Tag = amount, IsSelected = Math.Abs(config.BalanceWarn - amount) < 1e-9 });
            warn.SelectionChanged += delegate { var item = warn.SelectedItem as ComboBoxItem; if (item != null) { config.BalanceWarn = (double)item.Tag; SubscriptionView.BalanceWarn = config.BalanceWarn; PersistSubscriptions(); } };
            var style = new ComboBox { Width = 120 };
            foreach (var option in new[] { Tuple.Create("card", "提醒卡片"), Tuple.Create("system", "系统通知"), Tuple.Create("off", "不提醒") }) style.Items.Add(new ComboBoxItem { Content = option.Item2, Tag = option.Item1, IsSelected = (config.AlertStyle ?? "card") == option.Item1 });
            style.SelectionChanged += delegate { var item = style.SelectedItem as ComboBoxItem; if (item != null) { config.AlertStyle = (string)item.Tag; SaveConfig(); } };
            var preview = Link("预览", PreviewAlerts); preview.Margin = new Thickness(0, 0, 8, 0);
            var alertControls = new StackPanel { Orientation = Orientation.Horizontal }; alertControls.Children.Add(preview); alertControls.Children.Add(style);
            root.Children.Add(Card("提醒", null,
                Row("告急提醒", "某个窗口或余额刚变成告急，或按当前速度 1 小时内会用完时，提醒一次；恢复后才会再提醒。", alertControls),
                Row("余额告警线", "余额低于它算告急，低于两倍算偏紧（按账户币种）。", warn),
                Row("颜色含义", "额度窗口按剩余比例，余额按告警线。", Legend())));

            var taskbar = new CheckBox { IsChecked = TaskbarPanel.Available && config.TaskbarPanel, IsEnabled = TaskbarPanel.Available };
            taskbar.Click += delegate { config.TaskbarPanel = taskbar.IsChecked == true; taskbarPanel.Enabled = config.TaskbarPanel; PersistSubscriptions(); };
            var interval = new ComboBox { Width = 170 };
            foreach (int minutes in new[] { 1, 2, 5, 10, 15, 30, 60 }) interval.Items.Add(new ComboBoxItem { Content = "每 " + minutes + " 分钟", Tag = minutes, IsSelected = config.RefreshMinutes == minutes });
            interval.SelectionChanged += delegate { var item = interval.SelectedItem as ComboBoxItem; if (item != null) { config.RefreshMinutes = (int)item.Tag; SaveConfig(); } };
            root.Children.Add(Card("显示与刷新", null,
                Row("任务栏摘要", TaskbarPanel.Available ? "在托盘左边的空白处显示告警和最紧的额度，点击打开本窗口。" : "当前任务栏不支持（需要主屏底部的横向任务栏）。", taskbar),
                Row("自动刷新", "多久读取一次各平台额度；正在使用的平台会更频繁。", interval)));

            var autoStart = new CheckBox { IsChecked = !demo && StartupEnabled(), IsEnabled = !demo && !Program.SideBySide };
            autoStart.Click += delegate { SetStartup(autoStart.IsChecked == true); };
            var prices = new CheckBox { IsChecked = config.PriceSync };
            prices.Click += delegate { config.PriceSync = prices.IsChecked == true; SaveConfig(); };
            root.Children.Add(Card("系统", null,
                Row("开机启动", "登录 Windows 后在后台运行。", autoStart),
                Row("同步官方价格表", "每天更新一次各模型的 API 价格，只用来折算费用。", prices)));
            var foot = Hint("Claude 和 Codex 的 token 与费用来自本机日志，费用按官方 API 价格折算，不是实际账单；DeepSeek、Agnes 在内置网页里登录后，程序读取网页的登录 token 来查额度和每日用量。所有凭据用 Windows 加密保存在本机，只发给对应平台的地址。");
            foot.Margin = new Thickness(4, 2, 4, 0); root.Children.Add(foot);
            RenderSubscriptionRows(); return root;
        }
        private void PersistSubscriptions() { SaveConfig(); RenderSubscriptionRows(); var ignored = taskbarPanel.Refresh(); }
        private void RenderSubscriptionRows() {
            if (subscriptionRows == null) return; subscriptionRows.Children.Clear();
            // User recipes stay listed when off; built-in platforms come back through the add row.
            var shown = SubscriptionStates().Select(s => s.Id).Concat(ProviderCatalog.Custom.Values.Where(p => !p.Builtin).Select(p => p.Id)).Concat(switchedOff).Distinct().ToList();
            if (shown.Count == 0) subscriptionRows.Children.Add(Hint("还没有订阅，从下方添加。"));
            for (int i = 0; i < shown.Count; i++) subscriptionRows.Children.Add(Line(SubscriptionRow(shown[i]), i > 0));
            RenderAddArea();
        }
        private FrameworkElement SubscriptionRow(string id) {
            bool on = config.Enabled.Contains(id); ProviderState state; states.TryGetValue(id, out state);
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var order = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            order.Children.Add(Tiny("▲", "上移", () => { MoveSubscription(id, -1); RenderSubscriptionRows(); }));
            order.Children.Add(Tiny("▼", "下移", () => { MoveSubscription(id, 1); RenderSubscriptionRows(); }));
            row.Children.Add(order);
            var logo = Icon(id, 22); logo.Margin = new Thickness(0, 0, 10, 0); logo.VerticalAlignment = VerticalAlignment.Center; logo.Opacity = on ? 1 : .45; Grid.SetColumn(logo, 1); row.Children.Add(logo);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = SubscriptionView.Name(id), FontSize = 13, Foreground = on ? Theme.Ink : Theme.Dim });
            string statusHex; string status = Status(id, on, state, out statusHex);
            text.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = Theme.Paint(statusHex), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = state != null ? SubscriptionView.Tooltip(state, DateTime.UtcNow) : null });
            Grid.SetColumn(text, 2); row.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            CustomProvider recipe; ProviderCatalog.Custom.TryGetValue(id, out recipe);
            if (recipe != null && recipe.ConsoleToken) {
                bool needed = on && (state == null || state.Status == "setup" || state.Status == "expired");
                bool signedIn = Store.HasSavedKey(id);
                actions.Children.Add(needed ? Primary(signedIn ? "重新登录" : "登录", () => OpenConsoleLogin(id)) : Link("重新登录", () => OpenConsoleLogin(id)));
            }
            if (recipe == null || !recipe.Builtin) { var edit = Link(recipe != null ? "编辑方案" : "配置", () => EditSubscription(id, null)); edit.IsEnabled = !demo; actions.Children.Add(edit); }
            Grid.SetColumn(actions, 3); row.Children.Add(actions);
            var toggle = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = on ? "停用" : "启用" };
            toggle.Click += delegate {
                if (toggle.IsChecked == true) { config.Enabled = config.Enabled.Concat(new[] { id }).Distinct().ToArray(); switchedOff.Remove(id); }
                else { config.Enabled = config.Enabled.Where(p => p != id).ToArray(); switchedOff.Add(id); }
                PersistSubscriptions();
            };
            Grid.SetColumn(toggle, 4); row.Children.Add(toggle);
            return row;
        }
        private static string Status(string id, bool on, ProviderState s, out string hex) {
            hex = Theme.DimHex;
            if (!on) return "已停用";
            if (s == null || s.Status == "loading") return "等待首次读取";
            CustomProvider recipe; bool console = ProviderCatalog.Custom.TryGetValue(id, out recipe) && recipe.ConsoleToken;
            if (s.Status == "expired") { hex = Theme.UrgentHex; return console ? "登录已过期，点“重新登录”" : "登录已过期 · " + s.Message; }
            if (s.Status == "setup" && console) { hex = Theme.WarnHex; return "还没有登录"; }
            if (s.Status == "error" || s.Status == "setup") { hex = Theme.WarnHex; return s.Message; }
            hex = Theme.GoodHex; string text = "已连接";
            DateTime at; if (DateTime.TryParse(s.LastSuccess, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) text += " · " + Ago(DateTime.UtcNow - at.ToUniversalTime()) + "更新";
            if (s.Plan.Length > 0) text += " · " + s.Plan;
            return text;
        }
        private static string Ago(TimeSpan span) { return span.TotalMinutes < 1 ? "刚刚" : span.TotalMinutes < 60 ? (int)span.TotalMinutes + " 分钟前" : span.TotalHours < 24 ? (int)span.TotalHours + " 小时前" : (int)span.TotalDays + " 天前"; }
        // Platforms not added yet, as tiles: how each one is read and one button. A recipe with a console
        // token signs in in the embedded browser; the others read the local client's sign-in or a key.
        private StackPanel addArea;
        private bool showAdvanced;
        private void RenderAddArea() {
            if (addArea == null) return; addArea.Children.Clear(); addArea.Margin = new Thickness(0, 10, 0, 2);
            var offered = ProviderCatalog.Ids.Concat(ProviderCatalog.Custom.Values.Where(p => p.Builtin).Select(p => p.Id)).Where(id => !ProviderCatalog.LocalOnly(id) && !config.Enabled.Contains(id) && !switchedOff.Contains(id)).Distinct().ToList();
            if (offered.Count > 0) {
                addArea.Children.Add(new TextBlock { Text = "添加订阅", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 4, 0, 8) });
                var tiles = new WrapPanel(); foreach (string id in offered) tiles.Children.Add(AddTile(id)); addArea.Children.Add(tiles);
            }
            var toggle = Link((showAdvanced ? "▾" : "▸") + " 高级：自定义接入方案", () => { showAdvanced = !showAdvanced; RenderAddArea(); });
            toggle.Foreground = Theme.Dim; toggle.HorizontalAlignment = HorizontalAlignment.Left; toggle.Margin = new Thickness(-10, 2, 0, 0); addArea.Children.Add(toggle);
            if (showAdvanced) {
                var body = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };
                body.Children.Add(Hint("给开发者：平台不在上面时，可以用一段 JSON 描述它的额度接口和 token 的读取方式，字段说明见 docs/platform-recipes.md。"));
                var custom = Ghost("编写接入方案…", () => EditSubscription(null, CustomProviders.Template)); custom.IsEnabled = !demo; custom.HorizontalAlignment = HorizontalAlignment.Left; custom.Margin = new Thickness(0, 6, 0, 0);
                body.Children.Add(custom); addArea.Children.Add(body);
            }
        }
        private FrameworkElement AddTile(string id) {
            CustomProvider recipe; ProviderCatalog.Custom.TryGetValue(id, out recipe);
            bool console = recipe != null && recipe.ConsoleToken, keyed = !console && ProviderCatalog.KeyEnv(id, config).Length > 0;
            var body = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = Icon(id, 18); logo.Margin = new Thickness(0, 0, 8, 0); logo.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(logo);
            top.Children.Add(new TextBlock { Text = ProviderCatalog.Name(id), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(top);
            body.Children.Add(new TextBlock { Text = console ? "在网页登录，自动读取" : keyed ? "需要填写 API 密钥" : "读取本机客户端的登录", FontSize = 11, Foreground = Theme.Dim, Margin = new Thickness(0, 4, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(console ? Primary("登录添加", () => AddPlatform(id)) : Ghost("添加", () => AddPlatform(id)));
            if (id == "deepseek") { var key = Link("用 API Key", DeepSeekApiKey); key.ToolTip = "只看余额：粘贴 DeepSeek 的 API Key，读官方余额接口，没有每日用量。"; buttons.Children.Add(key); }
            body.Children.Add(buttons);
            return new Border { Width = 200, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), Child = body };
        }
        private void AddPlatform(string id) {
            config.Enabled = config.Enabled.Concat(new[] { id }).Distinct().ToArray(); switchedOff.Remove(id); PersistSubscriptions();
            CustomProvider recipe;
            if (ProviderCatalog.Custom.TryGetValue(id, out recipe) && recipe.ConsoleToken && !Store.HasSavedKey(id)) OpenConsoleLogin(id);
            else if (ProviderCatalog.KeyEnv(id, config).Length > 0 && String.IsNullOrEmpty(Store.ProviderKey(id, config))) EditSubscription(id, null);
            else { var ignored = RefreshOne(id); }
        }
        // DeepSeek without the console sign-in: an API key and the official balance endpoint.
        private void DeepSeekApiKey() {
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "DeepSeek：只看余额", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            root.Children.Add(Hint("在 DeepSeek 开放平台的 API keys 页面新建一个 key，粘贴到下面。它只用来读官方余额接口（api.deepseek.com/user/balance），看不到每日用量；想看用量请用“登录添加”。"));
            var open = Link("打开 API keys 页面", () => { try { Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys") { UseShellExecute = true }); } catch { } });
            open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(-10, 4, 0, 0); root.Children.Add(open);
            root.Children.Add(Label("API Key"));
            var box = new PasswordBox { Height = 32, Padding = new Thickness(9, 6, 9, 0) }; root.Children.Add(box);
            var result = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Theme.Dim };
            var save = Primary("保存并验证", async () => {
                string key = box.Password.Trim();
                if (!key.StartsWith("sk-", StringComparison.Ordinal) || key.Length < 20) { result.Foreground = Theme.Paint(Theme.WarnHex); result.Text = "这不像 DeepSeek 的 API Key（以 sk- 开头）。"; return; }
                if (demo) { result.Foreground = Theme.Paint(Theme.WarnHex); result.Text = "演示模式不保存凭据。"; return; }
                Store.SetProviderKey("deepseek-apikey", key);
                if (!config.Enabled.Contains("deepseek")) { config.Enabled = config.Enabled.Concat(new[] { "deepseek" }).ToArray(); switchedOff.Remove("deepseek"); }
                PersistSubscriptions();
                if (Store.HasSavedKey("deepseek")) { result.Foreground = Theme.Dim; result.Text = "已保存。已经用网页登录过，会优先用登录读取（含每日用量）。"; return; }
                result.Foreground = Theme.Dim; result.Text = "已保存，正在验证…";
                await RefreshOne("deepseek");
                ProviderState s; states.TryGetValue("deepseek", out s);
                if (s != null && s.Status == "ready") { result.Foreground = Theme.Paint(Theme.GoodHex); result.Text = "已连接：余额 " + String.Join(" / ", s.Balances.Select(SubscriptionView.Money)) + "。"; }
                else { result.Foreground = Theme.Paint(Theme.UrgentHex); result.Text = "没读到余额" + (s != null && s.Message.Length > 0 ? "：" + s.Message : "") + "。"; }
                RenderSubscriptionRows(); quotaDashboard.Update();
            });
            save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new Thickness(0, 12, 0, 0); root.Children.Add(save); root.Children.Add(result);
            var note = Hint("key 用 Windows 加密只保存在本机，只发给 api.deepseek.com。"); note.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(note);
            OwnedWindow("DeepSeek API Key", root, 500).Show();
        }

        // ---- console tokens (recipes with token.kind = console): card hint and the guide ----
        // The call to action on the quota card.
        private string GuideNote(ProviderState s) {
            CustomProvider recipe; if (s == null || !ProviderCatalog.Custom.TryGetValue(s.Id, out recipe) || !recipe.ConsoleToken) return null;
            return s.Status == "setup" ? "登录 ›" : s.Status == "expired" ? "登录已过期 · 重新登录 ›" : null;
        }
        private void OpenPlatformGuide(string id) {
            CustomProvider recipe; if (!ProviderCatalog.Custom.TryGetValue(id, out recipe) || !recipe.ConsoleToken) { EditSubscription(id, null); return; }
            string name = recipe.Name, url = recipe.TokenConsole, command = recipe.TokenCopy, host = recipe.Host;
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "手动设置 " + name + " token", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            root.Children.Add(Hint((recipe.TokenHint.Length > 0 ? recipe.TokenHint + " " : "") + "大约需要 1 分钟。"));
            var result = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            root.Children.Add(Step(1, "登录控制台", "在浏览器里打开并登录（已经登录可以跳过）。", Ghost("打开控制台", () => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } })));
            root.Children.Add(Step(2, "打开浏览器控制台", "在这个网页上按 F12，切到 Console（控制台）。第一次粘贴时浏览器会拦截，按提示输入 allow pasting 回车即可。", null));
            var code = new TextBox { Text = command, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Height = 32, Padding = new Thickness(9, 6, 9, 0), Margin = new Thickness(0, 0, 8, 0) };
            var copyRow = new DockPanel(); var copy = Ghost("复制命令", () => { try { Clipboard.SetText(command); result.Foreground = Theme.Dim; result.Text = "命令已复制，粘贴到网页的 Console 里回车。"; } catch { } });
            DockPanel.SetDock(copy, Dock.Right); copyRow.Children.Add(copy); copyRow.Children.Add(code);
            root.Children.Add(Step(3, "复制 token", "把下面这行命令粘贴到 Console 回车，显示“token 已复制”就成功了（命令只读本网页里你自己的登录信息）。之后别再点“复制命令”，否则剪贴板会被覆盖。", copyRow));
            Func<string, Task> save = async raw => {
                string token = (raw ?? "").Trim().Trim('"', '\'');
                if (token.Length < 20 || token.Contains(" ") || token.Contains("\n")) { result.Foreground = Theme.Paint(Theme.WarnHex); result.Text = "剪贴板里不像是 token。请先完成第 3 步，再点这里。"; return; }
                if (demo) { result.Foreground = Theme.Paint(Theme.WarnHex); result.Text = "演示模式不保存凭据。"; return; }
                Store.SetProviderKey(id, token);
                if (!config.Enabled.Contains(id)) { config.Enabled = config.Enabled.Concat(new[] { id }).ToArray(); SaveConfig(); }
                result.Foreground = Theme.Dim; result.Text = "已保存，正在验证…";
                await RefreshOne(id);
                ProviderState s; states.TryGetValue(id, out s);
                if (s != null && s.Status == "ready") {
                    var parts = new List<string>();
                    if (s.Quotas.Count > 0) parts.Add(s.Quotas.Count + " 个额度窗口");
                    if (s.Balances.Count > 0) parts.Add("余额 " + String.Join(" / ", s.Balances.Select(SubscriptionView.Money)));
                    if (recipe.Usage != null) parts.Add(s.PlatformAuth == "ok" ? "最近 30 天里 " + s.PlatformDays.Count(d => d.Tokens > 0) + " 天有用量" : "每日用量暂时没读到");
                    result.Foreground = Theme.Paint(Theme.GoodHex); result.Text = "已连接：" + String.Join("，", parts) + "。";
                } else {
                    bool bad = s != null && s.Status == "expired";
                    result.Foreground = Theme.Paint(Theme.UrgentHex);
                    result.Text = bad ? "token 无效或已过期：确认网页处于登录状态，重新做第 3 步。" : "暂时没读到数据" + (s != null && s.Message.Length > 0 ? "：" + s.Message : "") + "。稍后会自动重试。";
                }
                RenderSubscriptionRows(); quotaDashboard.Update();
            };
            root.Children.Add(Step(4, "粘贴到这里", "回到本窗口点下面的按钮，程序会保存并马上验证。", Primary("从剪贴板粘贴并验证", async () => { string text = ""; try { text = Clipboard.GetText(); } catch { } await save(text); })));
            var manual = new PasswordBox { Height = 32, Padding = new Thickness(9, 6, 9, 0), Margin = new Thickness(0, 0, 8, 0) }; var manualRow = new DockPanel { Margin = new Thickness(36, 8, 0, 0) };
            var manualSave = Ghost("保存", async () => await save(manual.Password)); DockPanel.SetDock(manualSave, Dock.Right); manualRow.Children.Add(manualSave); manualRow.Children.Add(manual);
            root.Children.Add(new TextBlock { Text = "也可以手动粘贴：", FontSize = 11, Foreground = Theme.Dim, Margin = new Thickness(36, 6, 0, 0) }); root.Children.Add(manualRow);
            root.Children.Add(result);
            var note = Hint("token 用 Windows 加密只保存在本机，只发给 " + host + "。在网页上退出登录或过期后它会失效，到时卡片和设置里会提示，按同样步骤再取一次即可。");
            note.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(note);
            OwnedWindow("设置 token", root, 560).Show();
        }

        private void EditSubscription(string id, string template) {
            CustomProvider existing = null; if (id != null) ProviderCatalog.Custom.TryGetValue(id, out existing);
            bool isCustom = (existing != null && !existing.Builtin) || template != null;
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = id == null ? "添加接入方案" : SubscriptionView.Name(id), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            TextBox json = null;
            if (isCustom) {
                root.Children.Add(Hint("接入方案是一段 JSON：读额度的地址和字段路径、可选的每日用量接口、token 怎么获取（设置引导按它生成）。字段说明和 DeepSeek、Agnes 两个内置方案见 docs/platform-recipes.md。token 不要写进 JSON。"));
                json = new TextBox { Text = existing != null ? existing.Source : template, Height = 300, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 10, 0, 10) }; root.Children.Add(json);
            } else root.Children.Add(Hint(ProviderCatalog.Help(id)));
            bool supportsKey = isCustom || existing != null || ProviderCatalog.KeyEnv(id, config).Length > 0;
            if (supportsKey) {
                root.Children.Add(Label(existing != null && existing.ConsoleToken ? "token（也可以保存后点“登录”自动获取）" : "密钥"));
                var keyBox = new PasswordBox(); keyBox.Name = "key"; root.Children.Add(keyBox);
                root.Children.Add(Hint("留空保留已保存的凭据。"));
            }
            var result = Hint(""); root.Children.Add(result);
            Window editor = null;
            var key = root.Children.OfType<PasswordBox>().FirstOrDefault() ?? new PasswordBox();
            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(Primary("保存", () => {
                try {
                    string target = id;
                    if (isCustom) {
                        var parsed = CustomProviders.Parse(json.Text);
                        if (ProviderCatalog.Custom.ContainsKey(parsed.Id) && (existing == null || existing.Id != parsed.Id)) throw new ArgumentException("已有同名 id，请换一个 id。");
                        // Avoid implicit secret migration; changing ids is an explicit new entry.
                        if (existing != null && existing.Id != parsed.Id) throw new ArgumentException("编辑时请保留 id；新账户请另加订阅。");
                        var list = ProviderCatalog.Custom.Values.Where(p => p.Id != parsed.Id).Concat(new[] { parsed }).ToList();
                        CustomProviders.Save(list); RegisterCustomProviders(list); target = parsed.Id;
                    }
                    if (key.Password.Length > 0) Store.SetProviderKey(target, key.Password);
                    if (!config.Enabled.Contains(target)) config.Enabled = config.Enabled.Concat(new[] { target }).ToArray();
                    PersistSubscriptions(); editor.Close(); var ignored = Refresh();
                    // A new recipe read with a console token goes straight on to the guide.
                    CustomProvider saved; if (existing == null && key.Password.Length == 0 && ProviderCatalog.Custom.TryGetValue(target, out saved) && saved.ConsoleToken) OpenConsoleLogin(target);
                } catch (Exception e) { result.Text = e is ArgumentException ? e.Message : "保存失败，请检查定义或数据目录权限。"; }
            }));
            if (id != null && supportsKey) {
                buttons.Children.Add(Ghost("清除已保存密钥", () => {
                    if (MessageBox.Show("确认清除此平台在本程序保存的密钥？", "清除凭据", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    Store.SetProviderKey(id, ""); result.Text = "已清除；环境变量不受影响。";
                }));
                if (existing != null && !existing.Builtin) buttons.Children.Add(Ghost("删除接入方案", () => {
                    if (MessageBox.Show("确认删除此自定义订阅及本程序保存的密钥？", "删除订阅", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    var list = ProviderCatalog.Custom.Values.Where(p => p.Id != id).ToList(); CustomProviders.Save(list); Store.SetProviderKey(id, ""); RegisterCustomProviders(list); states.Remove(id); config.Enabled = config.Enabled.Where(p => p != id).ToArray(); PersistSubscriptions(); editor.Close();
                }));
            }
            root.Children.Add(buttons);
            if (id == "copilot") {
                var code = new TextBox { IsReadOnly = true, FontSize = 20, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed };
                var login = Primary("使用 GitHub 登录", null); login.IsEnabled = !demo; login.Margin = new Thickness(0, 14, 0, 0);
                login.Click += async delegate { login.IsEnabled = false; await CopilotLogin(code, result); login.IsEnabled = !demo; };
                root.Children.Add(login); root.Children.Add(code);
                root.Children.Add(Hint("OAuth 设备码登录，只申请 read:user 权限，用于读取 Copilot 额度。"));
            } else if (!isCustom && !supportsKey) root.Children.Add(Hint("按上方说明使用平台客户端登录后刷新。"));
            editor = OwnedWindow("订阅设置", root, 540); editor.Show();
        }

        // ---- building blocks, all in the shared theme ----
        private static Border Card(string title, string subtitle, params FrameworkElement[] children) {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
            if (subtitle != null) body.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Theme.Dim, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) }; body.Children.Add(rows);
            for (int i = 0; i < children.Length; i++) rows.Children.Add(children[i] is StackPanel || children[i] is WrapPanel ? children[i] : Line(children[i], i > 0));
            return new Border { Background = Theme.Surface, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 13, 16, 10), Margin = new Thickness(0, 0, 0, 10), Child = body };
        }
        private static Border Line(FrameworkElement child, bool divider) { return new Border { BorderBrush = Theme.Edge, BorderThickness = new Thickness(0, divider ? 1 : 0, 0, 0), Child = child }; }
        // A setting: title and a one-line explanation on the left, its control on the right.
        private static FrameworkElement Row(string title, string description, FrameworkElement control) {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Theme.Ink });
            text.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(text); control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); grid.Children.Add(control);
            return grid;
        }
        private static FrameworkElement Legend() {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in new[] { Tuple.Create(Theme.GoodHex, "充足 ≥40%"), Tuple.Create(Theme.WarnHex, "偏紧 20–40%"), Tuple.Create(Theme.UrgentHex, "告急 <20%") })
                panel.Children.Add(new Border { Background = Theme.Soft(item.Item1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), Child = new TextBlock { Text = "● " + item.Item2, FontSize = 11, Foreground = Theme.Paint(item.Item1) } });
            return panel;
        }
        private static Border Notice(string text, string hex) {
            return new Border { Background = Theme.Soft(hex), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), Child = new TextBlock { Text = text, FontSize = 11, Foreground = Theme.Paint(hex), TextWrapping = TextWrapping.Wrap } };
        }
        private static FrameworkElement Step(int number, string title, string text, FrameworkElement action) {
            var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Accent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.AccentInk, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 3, 0, 0) });
            body.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6), LineHeight = 17 });
            if (action != null) { if (action is Button) action.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(action); }
            Grid.SetColumn(body, 1); grid.Children.Add(body); return grid;
        }
        private static TextBlock Label(string text) { return new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 14, 0, 6) }; }
        private static TextBlock Hint(string text) { return new TextBlock { Text = text, FontSize = 11, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2), LineHeight = 17 }; }
        private static Button Ghost(string text, Action click) { var b = QuotaDashboard.Button(text, text, click ?? delegate { }); b.Height = 30; b.Padding = new Thickness(12, 0, 12, 0); b.Margin = new Thickness(0, 0, 6, 0); return b; }
        // Row actions that are not the next step: accent text, no fill until hovered.
        private static Button Link(string text, Action click) { var b = QuotaDashboard.Quiet(text, text, click ?? delegate { }, Theme.Accent); b.Height = 30; b.Padding = new Thickness(10, 0, 10, 0); b.Margin = new Thickness(0, 0, 4, 0); return b; }
        private static Button Primary(string text, Action click) { var b = Ghost(text, click); b.Background = Theme.Accent; b.Foreground = Theme.AccentInk; return b; }
        private static Button Tiny(string text, string name, Action click) { var b = QuotaDashboard.Button(text, name, click); b.Height = 15; b.MinWidth = 18; b.FontSize = 7; b.Padding = new Thickness(0); b.Margin = new Thickness(0, 1, 0, 1); b.Background = Brushes.Transparent; b.Foreground = Theme.Dim; return b; }
        // GitHub OAuth device flow: show the code, open the browser, poll until authorized.
        private async Task CopilotLogin(TextBox code, TextBlock note) {
            try {
                using (var service = new ProviderService(config)) {
                    note.Text = "正在向 GitHub 申请设备码…";
                    var pending = await service.StartCopilotLogin();
                    code.Text = pending.UserCode; code.Visibility = Visibility.Visible;
                    try { Clipboard.SetText(pending.UserCode); } catch { }
                    try { Process.Start(new ProcessStartInfo(pending.VerificationUri) { UseShellExecute = true }); } catch { }
                    note.Text = "代码已复制，在打开的 GitHub 页面输入并授权，完成后自动连接。";
                    DateTime until = DateTime.UtcNow.AddSeconds(pending.ExpiresIn);
                    while (DateTime.UtcNow < until) {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, pending.Interval)));
                        string token = await service.PollCopilotLogin(pending);
                        if (token == null) continue;
                        Store.SetProviderKey("copilot", token);
                        if (!config.Enabled.Contains("copilot")) config.Enabled = config.Enabled.Concat(new[] { "copilot" }).ToArray();
                        PersistSubscriptions(); note.Text = "已连接 GitHub Copilot。"; code.Visibility = Visibility.Collapsed;
                        await RefreshOne("copilot"); return;
                    }
                    note.Text = "设备码已过期，请重新登录。"; code.Visibility = Visibility.Collapsed;
                }
            } catch (Exception e) { note.Text = e is ProviderException ? e.Message : "无法连接 GitHub，请检查网络或代理设置。"; }
        }
        private Window OwnedWindow(string title, FrameworkElement root, double width) {
            var window = ThemedWindow(title, root, width);
            if (quotaDashboard.IsOpen) { window.Owner = quotaDashboard.View; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            return window;
        }
        private static Window ThemedWindow(string title, FrameworkElement root, double width) {
            var window = new Window { Title = title, Width = width, MinWidth = 380, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60), Background = Theme.Window, Foreground = Theme.Ink, FontFamily = new FontFamily(Theme.Font), WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResize, Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            window.Resources = Theme.Styles(); Theme.Frame(window);
            return window;
        }
    }
}
