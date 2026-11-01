using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Headroom {
    public static class SelfTests {
        public static int Run() {
            var passed = new List<string>(); var failed = new List<string>();
            Action<string, Action> test = (name, check) => { try { check(); passed.Add(name); } catch (Exception e) { failed.Add(name + ": " + e.Message); } };
            test("Taskbar warnings count platforms, exclude stale and sort tightest", () => {
                var a = new ProviderState { Id = "a", Status = "ready" }; a.Quotas.Add(new Quota { Label = "日", Used = 85 }); a.Quotas.Add(new Quota { Label = "周", Used = 90 });
                var b = new ProviderState { Id = "b", Status = "ready" }; b.Quotas.Add(new Quota { Label = "周", Used = 92 });
                var c = new ProviderState { Id = "c", Status = "ready" }; c.Quotas.Add(new Quota { Label = "周", Used = 20 });
                var d = new ProviderState { Id = "d", Status = "error", Stale = true }; d.Quotas.Add(new Quota { Label = "日", Used = 99 });
                var e = new ProviderState { Id = "e", Status = "setup" };
                var summary = SubscriptionView.Classify(new[] { a, b, c, d, e });
                Require(summary.Alerts.Count == 2 && summary.Normal.Count == 1 && summary.Attention.Count == 2 && summary.Alerts[0].State == b && summary.Alerts[1].Window == "每周");
                Require(summary.Headline == "▲ 2 项告警" && SubscriptionView.Classify(new[] { c }).Headline == "● 全部充足" && SubscriptionView.Classify(new[] { c, e }).Headline == "○ 1 项异常");
                Require(SubscriptionView.Summary(d).Contains("旧") && SubscriptionView.Missing(e) == "未连接");
            });
            test("Balances alert below the warn line and share the quota scale", () => {
                double saved = SubscriptionView.BalanceWarn;
                try {
                    SubscriptionView.BalanceWarn = 5;
                    var low = new ProviderState { Id = "deepseek", Status = "ready" }; low.Balances.Add(new Balance { Currency = "CNY", Amount = 0.29 });
                    var tight = new ProviderState { Id = "x", Status = "ready" }; tight.Balances.Add(new Balance { Currency = "USD", Amount = 8.62 });
                    var rich = new ProviderState { Id = "y", Status = "ready" }; rich.Balances.Add(new Balance { Currency = "USD", Amount = 40 });
                    var o = SubscriptionView.Classify(new[] { rich, tight, low });
                    Require(o.Alerts.Count == 2 && o.Alerts[0].State == low && o.Alerts[0].Urgent && o.Alerts[0].Value == "¥0.29" && o.Alerts[0].Window == "余额");
                    Require(!o.Alerts[1].Urgent && o.Normal.Count == 1);
                    SubscriptionView.BalanceWarn = 0;
                    Require(SubscriptionView.Classify(new[] { low }).Alerts.Count == 0);
                } finally { SubscriptionView.BalanceWarn = saved; }
            });
            test("Taskbar row degrades: fewer rings, then rings only, then one ring", () => {
                int r = TaskbarPanel.RingWidth;
                var full = TaskbarPanel.Fit(10000, 120, 3);
                Require(full.Primary && full.Rings == 3 && full.Width == 6 + 120 + 8 + 3 * r + 6);
                var two = TaskbarPanel.Fit(full.Width - 1, 120, 3);
                Require(two.Primary && two.More > 0 && two.Rings + two.More == 3 && two.Width < full.Width);
                var focus = TaskbarPanel.Fit(6 + 120 + 6, 120, 3);
                Require(focus.Primary && focus.Rings == 0 && focus.More == 0);
                var rings = TaskbarPanel.Fit(6 + 120 + 5, 120, 3);
                Require(!rings.Primary && rings.Rings == 4 && rings.More == 0 && rings.Width == 6 + 4 * r + 6);
                var tiny = TaskbarPanel.Fit(20, 120, 3);
                Require(!tiny.Primary && tiny.Rings == 0 && tiny.Width <= 20);
            });
            test("Taskbar with many subscriptions: at most four rings plus a +N chip, edge cases drawn", () => {
                int r = TaskbarPanel.RingWidth, c = TaskbarPanel.ChipWidth;
                var many = TaskbarPanel.Fit(10000, 120, 11);
                Require(many.Primary && many.Rings == TaskbarPanel.MaxRings && many.More == 7 && many.Width == 6 + 120 + 8 + 4 * r + c + 6);
                var chipOnly = TaskbarPanel.Fit(6 + 120 + 8 + c + 6, 120, 11);
                Require(chipOnly.Primary && chipOnly.Rings == 0 && chipOnly.More == 11);
                var ringsOnly = TaskbarPanel.Fit(100, 120, 11);
                Require(!ringsOnly.Primary && ringsOnly.Rings == 2 && ringsOnly.More == 10 && ringsOnly.Width <= 100);
                // Every subscription is either drawn or counted in the chip.
                for (int width = 20; width <= 400; width += 7) for (int n = 0; n <= 14; n++) {
                    var plan = TaskbarPanel.Fit(width, 120, n);
                    Require(plan.Width <= Math.Max(width, 38) && (plan.Rings == 0 && plan.More == 0 || plan.Rings + plan.More + (plan.Primary ? 1 : 0) == n + 1));
                }
                // Gallery: verification/taskbar-gallery.png, each case drawn on its taskbar colour.
                var now = DateTime.UtcNow;
                Func<string, double, string, ProviderState> make = (id, used, status) => {
                    var s = new ProviderState { Id = id, Status = status, LastSuccess = now.ToString("o") };
                    s.Quotas.Add(new Quota { Label = "每周", WindowSeconds = 604800, Used = used, ResetUtc = now.AddDays(3).ToString("o") }); return s;
                };
                Func<int, List<ProviderState>> set = n => Enumerable.Range(0, n).Select(i => make(i < 4 ? new[] { "codex", "claude", "deepseek", "agnes" }[i] : "custom-p" + i, (i * 37) % 100, "ready")).ToList();
                var longName = set(4); longName[0] = make("custom-a-very-long-platform-name-for-testing", 62, "ready");
                var failing = set(4).Select(s => { s.Status = "error"; s.Stale = true; return s; }).ToList();
                var dark = System.Drawing.Color.FromArgb(32, 32, 32); var light = System.Drawing.Color.FromArgb(238, 238, 238); var tinted = System.Drawing.Color.FromArgb(60, 20, 56);
                var cases = new[] {
                    new { Name = "0 个订阅", Data = set(0), W = 320, H = 48, Back = dark },
                    new { Name = "1 个", Data = set(1), W = 320, H = 48, Back = dark },
                    new { Name = "4 个", Data = set(4), W = 320, H = 48, Back = tinted },
                    new { Name = "12 个", Data = set(12), W = 400, H = 48, Back = dark },
                    new { Name = "12 个 · 窄 170", Data = set(12), W = 170, H = 48, Back = dark },
                    new { Name = "12 个 · 窄 110", Data = set(12), W = 110, H = 48, Back = dark },
                    new { Name = "超长名称", Data = longName, W = 400, H = 48, Back = dark },
                    new { Name = "全部读取失败", Data = failing, W = 320, H = 48, Back = dark },
                    new { Name = "浅色任务栏", Data = set(6), W = 320, H = 48, Back = light },
                    new { Name = "小任务栏 32", Data = set(6), W = 320, H = 32, Back = dark },
                    new { Name = "大任务栏 72", Data = set(12), W = 480, H = 72, Back = dark },
                };
                using (var gallery = new System.Drawing.Bitmap(660, cases.Sum(k => k.H + 24) + 10))
                using (var g = System.Drawing.Graphics.FromImage(gallery))
                using (var font = new System.Drawing.Font("Microsoft YaHei UI", 9f)) {
                    g.Clear(System.Drawing.Color.FromArgb(18, 18, 20)); int y = 6;
                    foreach (var k in cases) {
                        TaskbarPanel.Plan plan;
                        using (var image = TaskbarPanel.Preview(k.Data, k.W, k.H, k.Back, out plan)) {
                            Require(plan.Width <= Math.Min(k.W, 350 * Math.Max(.75, k.H / 48.0)) + 1 && image.Width == plan.Width);
                            using (var strip = new System.Drawing.SolidBrush(k.Back)) g.FillRectangle(strip, 140, y + 2, 500, k.H);
                            g.DrawImage(image, 140 + 500 - image.Width, y + 2);
                            g.DrawString(k.Name + "\n" + (plan.Primary ? "焦点+" : "") + plan.Rings + " 环" + (plan.More > 0 ? " +" + plan.More : ""), font, System.Drawing.Brushes.Gainsboro, 6, y + 4);
                        }
                        y += k.H + 24;
                    }
                    Directory.CreateDirectory(Path.Combine(Store.Root, "verification"));
                    gallery.Save(Path.Combine(Store.Root, "verification", "taskbar-gallery.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            });
            test("Taskbar focus: soonest run-out wins, then the lowest window or balance", () => {
                var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
                var codex = new ProviderState { Id = "codex", Status = "ready" };
                codex.Quotas.Add(new Quota { Label = "5 小时", WindowSeconds = 18000, Used = 50, ResetUtc = now.AddSeconds(14400).ToString("o") });   // runs out in 1h
                var claude = new ProviderState { Id = "claude", Status = "ready" };
                claude.Quotas.Add(new Quota { Label = "每周", WindowSeconds = 604800, Used = 75, ResetUtc = now.AddDays(1).ToString("o") });
                var f = Pace.Primary(new[] { claude, codex }, now);
                Require(f.State == codex && f.Title == "Codex 5 小时" && f.Value == "50%" && f.Alert && f.Sub == "预计 1时0分后用完");
                var calm = new ProviderState { Id = "claude", Status = "ready" };
                calm.Quotas.Add(new Quota { Label = "5 小时", WindowSeconds = 18000, Used = 30, ResetUtc = now.AddSeconds(3600).ToString("o") });
                var low = new ProviderState { Id = "deepseek", Status = "ready" }; low.Balances.Add(new Balance { Currency = "CNY", Amount = 2 });
                var g = Pace.Primary(new[] { calm, low }, now);
                Require(g.State == low && g.Value == "¥2" && g.Sub == "余额告急" && Pace.Score(low) == 8);
                var h = Pace.Primary(new[] { calm }, now);
                Require(h.Sub == "1时0分后重置" && !h.Alert && Pace.Primary(new ProviderState[0], now) == null);
            });
            test("Urgent alerts notify once until they recover; stale readings never alert", () => {
                var watch = new AlertWatch();
                var s1 = new ProviderState { Id = "codex", Status = "ready" }; s1.Quotas.Add(new Quota { Label = "5 小时", Used = 85 });
                Require(watch.Update(new[] { s1 }).Count == 1 && watch.Update(new[] { s1 }).Count == 0);
                s1.Quotas[0].Used = 50; Require(watch.Update(new[] { s1 }).Count == 0);
                s1.Quotas[0].Used = 90; Require(watch.Update(new[] { s1 }).Count == 1);
                var old = new ProviderState { Id = "claude", Status = "error", Stale = true }; old.Quotas.Add(new Quota { Label = "周", Used = 99 });
                Require(new AlertWatch().Update(new[] { old }).Count == 0);
            });
            test("Windows that run out within the hour alert early, then again once urgent", () => {
                var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc); var watch = new AlertWatch();
                // 5h window, 4h left, 70% used in the first hour: 30% lasts ~26 minutes.
                var s = new ProviderState { Id = "codex", Status = "ready" }; s.Quotas.Add(new Quota { Label = "5 小时", Used = 70, WindowSeconds = 18000, ResetUtc = now.AddHours(4).ToString("o") });
                var first = watch.Update(new[] { s }, now);
                Require(first.Count == 1 && first[0].Soon && first[0].Sub.StartsWith("预计 ") && watch.Update(new[] { s }, now).Count == 0);
                s.Quotas[0].Used = 85; var urgent = watch.Update(new[] { s }, now);
                Require(urgent.Count == 1 && !urgent[0].Soon);
                var calm = new ProviderState { Id = "claude", Status = "ready" }; calm.Quotas.Add(new Quota { Label = "5 小时", Used = 10, WindowSeconds = 18000, ResetUtc = now.AddHours(4).ToString("o") });
                Require(new AlertWatch().Update(new[] { calm }, now).Count == 0);
            });
            test("Usage totals give cache rate, speed, top model and the hour-of-day profile", () => {
                var h = new UsageHistory(); var today = new DateTime(2026, 10, 31);
                var a = new DayUsage { Day = "2026-10-31", Agent = "codex", Tokens = 1000, Cost = 2, Requests = 4, CachedTokens = 600, InputTokens = 300, CacheCreationTokens = 100, TimedOutput = 600, TimedSeconds = 10 };
                a.Models.Add(new ModelUsage { Model = "gpt-5.5", Tokens = 700 }); a.Models.Add(new ModelUsage { Model = "gpt-5.5-mini", Tokens = 300 });
                h.Days.Add(a);
                h.Days.Add(new DayUsage { Day = "2026-10-30", Agent = "claude", Tokens = 400, Cost = 1, CostKnown = false });
                h.Days.Add(new DayUsage { Day = "2026-10-01", Agent = "claude", Tokens = 9999 });   // outside 7 days
                var sum = UsageCharts.Sum(h, today, 7);
                Require(sum.Tokens == 1400 && sum.Cost == 3 && sum.Partial && Math.Abs(sum.CacheRate.Value - .6) < 1e-9 && Math.Abs(sum.Speed.Value - 60) < 1e-9);
                var rows = UsageCharts.ByAgent(h, today, 7);
                Require(rows.Count == 2 && rows[0].Agent == "codex" && rows[0].TopModel == "gpt-5.5" && rows[1].CacheRate == null);
                var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
                var hours = UsageCharts.Hours(new[] { new HourUsage { Hour = new DateTime(2026, 10, 31, 6, 0, 0), Tokens = 50 }, new HourUsage { Hour = new DateTime(2026, 10, 20, 6, 0, 0), Tokens = 70 } }, today, 7, zone);
                Require(hours[14] == 50 && hours.Sum() == 50);
            });
            test("Usage charts keep empty days and rank platforms by total", () => {
                var h = new UsageHistory();
                h.Days.Add(new DayUsage { Day = "2026-10-31", Agent = "codex", Tokens = 300, Cost = 3 });
                h.Days.Add(new DayUsage { Day = "2026-10-31", Agent = "claude", Tokens = 100, Cost = 5 });
                h.Days.Add(new DayUsage { Day = "2026-10-29", Agent = "claude", Tokens = 50, Cost = 1 });
                h.Days.Add(new DayUsage { Day = "2026-10-01", Agent = "codex", Tokens = 999, Cost = 9 });
                var days = UsageCharts.Daily(h, new DateTime(2026, 10, 31), 7, false);
                Require(days.Count == 7 && days[6].Total == 400 && days[4].Total == 50 && days[5].Total == 0 && days[0].Date == new DateTime(2026, 10, 25));
                var tokens = UsageCharts.Ranking(days); var money = UsageCharts.Ranking(UsageCharts.Daily(h, new DateTime(2026, 10, 31), 7, true));
                Require(tokens[0].Key == "codex" && tokens[1].Value == 150 && money[0].Key == "claude" && money[0].Value == 6);
            });
            test("Platform recipes: built-in DeepSeek and Agnes read console data and replace local logs", () => {
                CustomProvider ds = ProviderCatalog.Custom["deepseek"], ag = ProviderCatalog.Custom["agnes"];
                Require(ds.Builtin && ds.ConsoleToken && ag.Builtin && ag.Headers["X-User-Language"] == "zh-CN" && !ProviderCatalog.Ids.Contains("deepseek") && ProviderCatalog.Name("agnes") == "Agnes");
                var tz = TimeSpan.FromHours(8); var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", tz, "UTC+8", "UTC+8");
                Require(CustomProviders.UsageUrl(ds.Usage.Url, new DateTime(2026, 10, 31), tz).EndsWith("amount?start=1790870400&end=1793462400&tz=28800"));
                object series = J.Parse("{\"code\":0,\"data\":{\"biz_code\":0,\"biz_data\":{\"series\":[" +
                    "{\"model\":\"deepseek-flash\",\"buckets\":[{\"time\":1793289600,\"usage\":{\"RESPONSE_TOKEN\":100,\"REQUEST\":2,\"PROMPT_CACHE_HIT_TOKEN\":1000,\"PROMPT_CACHE_MISS_TOKEN\":50}},{\"time\":1793376000,\"usage\":{\"RESPONSE_TOKEN\":0,\"REQUEST\":0,\"PROMPT_CACHE_HIT_TOKEN\":0,\"PROMPT_CACHE_MISS_TOKEN\":0}}]}," +
                    "{\"model\":\"deepseek-v4-pro\",\"buckets\":[{\"time\":1793289600,\"usage\":{\"RESPONSE_TOKEN\":10,\"REQUEST\":1,\"PROMPT_CACHE_HIT_TOKEN\":0,\"PROMPT_CACHE_MISS_TOKEN\":40}}]}]}}}");
                CustomProviders.Check(ds, series);
                var days = CustomProviders.MapUsage(ds, series, zone);
                Require(days.Count == 1 && days[0].Day == "2026-10-30" && days[0].Tokens == 1200 && days[0].CachedTokens == 1000 && days[0].OutputTokens == 110 && days[0].Requests == 3 && days[0].Models.Count == 2);
                bool expired = false; try { CustomProviders.Check(ds, J.Parse("{\"code\":40003,\"msg\":\"Authorization Failed\",\"data\":null}")); } catch (ProviderException e) { expired = e.State == "expired"; }
                Require(expired);
                var wallet = CustomProviders.Map(ds, J.Parse("{\"code\":0,\"data\":{\"biz_code\":0,\"biz_data\":{\"normal_wallets\":[{\"currency\":\"CNY\",\"balance\":\"12.5\"}],\"bonus_wallets\":[{\"currency\":\"CNY\",\"balance\":\"0.5\"}]}}}"), DateTime.UtcNow);
                Require(wallet.Balances.Count == 1 && wallet.Balances[0].Currency == "CNY" && wallet.Balances[0].Amount == 13 && wallet.Plan == "账户余额");
                var agDays = CustomProviders.MapUsage(ag, J.Parse("{\"code\":200,\"data\":{\"range\":\"custom\",\"items\":[{\"bucket\":\"2026-10-30\",\"request_count\":711,\"text_tokens\":27926409,\"image_count\":17,\"video_seconds\":137},{\"bucket\":\"2026-10-31\",\"request_count\":0,\"text_tokens\":0}]}}"), zone);
                Require(agDays.Count == 1 && agDays[0].Tokens == 27926409 && agDays[0].Requests == 711 && !agDays[0].CostKnown && CustomProviders.UsageUrl(ag.Usage.Url, new DateTime(2026, 10, 31), tz).EndsWith("start_date=2026-10-02&end_date=2026-10-31"));
                var local = new UsageHistory();
                local.Days.Add(new DayUsage { Day = "2026-10-30", Agent = "deepseek", Tokens = 5 });
                local.Days.Add(new DayUsage { Day = "2026-10-30", Agent = "codex", Tokens = 7 });
                local.Days.Add(new DayUsage { Day = "2026-10-30", Agent = "gemini", Tokens = 9 });   // not offered: left out
                var dsState = new ProviderState { Id = "deepseek" }; dsState.PlatformDays = days;
                var merged = PlatformUsage.Combine(local, new[] { dsState, new ProviderState { Id = "codex" } });
                Require(merged.Days.Count == 2 && merged.Days.Single(d => d.Agent == "deepseek").Tokens == 1200 && merged.Days.Single(d => d.Agent == "codex").Tokens == 7 && local.Days.Count == 3);
            });
            test("Pace: even-use mark, run-out projection and the overview sentence", () => {
                var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
                var q = new Quota { Label = "5 小时", WindowSeconds = 18000, Used = 80, ResetUtc = now.AddSeconds(9000).ToString("o") };
                WindowPace p = Pace.Of(q, now);
                Require(p != null && Math.Abs(p.Elapsed - .5) < 1e-9 && Math.Abs(p.Even - 50) < 1e-9 && p.Short && Math.Abs(p.RunOut.Value.TotalSeconds - 2250) < 1e-6);
                Require(Pace.Note(p, q.Remaining) == "预计 38 分钟后用完");
                var slow = new Quota { Label = "每周", WindowSeconds = 604800, Used = 10, ResetUtc = now.AddDays(3.5).ToString("o") };
                Require(!Pace.Of(slow, now).Short && Pace.Note(Pace.Of(slow, now), slow.Remaining) == "用得比均速慢，够用");
                Require(Pace.Of(new Quota { WindowSeconds = 0, Used = 5, ResetUtc = now.AddHours(1).ToString("o") }, now) == null);
                var codex = new ProviderState { Id = "codex", Status = "ready" }; codex.Quotas.Add(q); codex.Quotas.Add(slow);
                var calm = new ProviderState { Id = "claude", Status = "ready" }; calm.Quotas.Add(slow);
                Require(Pace.Headline(new[] { codex, calm }, now).StartsWith("1 项需要注意 · Codex 5 小时 预计 38 分钟后用完"));
                Require(Pace.Headline(new[] { calm }, now) == "全部充足，照现在的速度都撑得到重置");
                // Before the first read: loading, not an error.
                var fresh = new ProviderState { Id = "deepseek" };
                Pace.Focus waiting = Pace.Primary(new[] { fresh, new ProviderState { Id = "agnes" } }, now);
                Require(waiting.Loading && !waiting.Alert && waiting.Sub == "正在读取额度…");
                var overview = SubscriptionView.Classify(new[] { fresh });
                Require(overview.OnlyLoading && overview.Attention.Count == 0 && Pace.Headline(new[] { fresh }, now) == "正在读取额度…");
                Require(!Pace.Primary(new[] { fresh, calm }, now).Loading && Pace.Headline(new[] { fresh, calm }, now).StartsWith("全部充足"));
            });
            test("Balance ledger: drops are spend on that day, rises are top-ups, first reading is a baseline", () => {
                var l = new BalanceLedger(); var day1 = new DateTime(2026, 10, 30, 9, 0, 0); var day2 = new DateTime(2026, 10, 31, 9, 0, 0);
                Func<double, Balance[]> cny = v => new[] { new Balance { Currency = "CNY", Amount = v } };
                Require(BalanceSpend.Record(l, "deepseek", cny(20), day1) && l.Days.Count == 0 && l.Since == "2026-10-30");
                Require(!BalanceSpend.Record(l, "deepseek", cny(20), day1));
                BalanceSpend.Record(l, "deepseek", cny(18.5), day1);
                BalanceSpend.Record(l, "deepseek", cny(48.5), day2);   // top-up of 30
                BalanceSpend.Record(l, "deepseek", cny(47), day2);
                var today = BalanceSpend.Spent(l, "deepseek", day2, 1); var week = BalanceSpend.Spent(l, "deepseek", day2, 7);
                Require(Math.Abs(today["CNY"] - 1.5) < 1e-9 && Math.Abs(week["CNY"] - 3) < 1e-9 && l.Days.Single(d => d.Day == "2026-10-31").TopUp == 30);
                Require(BalanceSpend.Summary(l, "deepseek", day2) == "今日 ¥1.5 · 7 天 ¥3 · 30 天 ¥3" && BalanceSpend.Summary(l, "other", day2) == null);
                BalanceSpend.Record(l, "deepseek", cny(47), day2.AddDays(45));
                Require(l.Days.Count == 0 && BalanceSpend.Note(l, day2).StartsWith("自 10/30 起记录"));
            });
            test("Usage totals sum today, 7 and 30 days and flag unpriced tokens", () => {
                var today = new DateTime(2026, 10, 31);
                var h = new UsageHistory();
                h.Days.Add(new DayUsage { Day = "2026-10-31", Agent = "codex", Tokens = 1000, Cost = 1 });
                h.Days.Add(new DayUsage { Day = "2026-10-31", Agent = "claude", Tokens = 500, Cost = 2 });
                h.Days.Add(new DayUsage { Day = "2026-10-25", Agent = "codex", Tokens = 100, Cost = .5 });
                h.Days.Add(new DayUsage { Day = "2026-10-24", Agent = "codex", Tokens = 10, Cost = .1, UnpricedTokens = 3 });
                h.Days.Add(new DayUsage { Day = "2026-09-01", Agent = "codex", Tokens = 99999, Cost = 99 });
                var t = UsageTotals.From(h, today);
                Require(t.TodayTokens == 1500 && t.TodayCost == 3 && t.WeekTokens == 1600 && t.MonthTokens == 1610 && Math.Abs(t.MonthCost - 3.6) < 1e-9 && t.Partial);
                Require(UsageTotals.Tokens(1234567) == "1.23M" && UsageTotals.Usd(3.6) == "$3.60" && !UsageTotals.From(null, today).Partial);
            });
            test("Third-party usage follows the switch timeline; history before tracking stays unattributed", () => {
                var log = new EndpointLog();
                log.Marks.Add(new EndpointMark { App = "claude", Official = true, Since = "2026-09-20T00:00:00Z" });
                log.Marks.Add(new EndpointMark { App = "claude", Host = "relay.example.com", Key = "abc123", Name = "Relay", Since = "2026-09-25T00:00:00Z" });
                var claude = new LogIndex(); claude.Files.Add(new LogFile { Hours = { { "2026091810||m", new Bucket { I = 7, R = 1 } }, { "2026092210||m", new Bucket { I = 50, R = 1 } }, { "2026092610||m", new Bucket { I = 300, R = 2, D = 3 } }, { "2026092310|" + ClaudeLogs.NoRequestId + "|m", new Bucket { I = 20, R = 1 } }, { "2026091010|" + ClaudeLogs.NoRequestId + "|m", new Bucket { I = 5, R = 1 } } } });
                var codex = new LogIndex(); codex.Files.Add(new LogFile { Hours = { { "2026092610|custom|g", new Bucket { I = 40, R = 1, U = 40 } }, { "2026092610|openai|g", new Bucket { I = 900, R = 3 } } } });
                var report = ThirdPartyReport.Build(codex, claude, log, new UsageHistory(), new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
                var relay = report.Endpoints.First(e => e.App == "claude" && e.Host.Length > 0); var unknown = report.Endpoints.First(e => e.App == "codex");
                var bare = report.Endpoints.First(e => e.Key == ClaudeLogs.NoRequestId);
                Require(report.Endpoints.Count == 3 && relay.Current && relay.Month == 300 && relay.Requests == 2 && Math.Abs(relay.CostMonth - 3) < 1e-9 && relay.Week == 300 && relay.Today == 0);
                // No request-id: a relay even while settings.json looked official, and before tracking.
                Require(bare.Month == 25 && !bare.Current && unknown.Host == "" && unknown.Month == 40 && unknown.CostPartial && report.ClaudeUnattributed == 7 && report.AppMonth("claude") == 325);
            });
            test("Switching relay inside an hour attributes each request to the endpoint in effect then", () => {
                var log = new EndpointLog();
                log.Marks.Add(new EndpointMark { App = "claude", Host = "a.example.com", Key = "aaaaaa", Name = "A", Since = "2026-09-30T09:00:00Z" });
                log.Marks.Add(new EndpointMark { App = "claude", Host = "b.example.com", Key = "bbbbbb", Name = "B", Since = "2026-09-30T10:30:00Z" });
                EndpointAttribution.Use(log);
                try {
                    // Switched A → B at 10:30; 900 tokens used on B at 10:40.
                    string line = "{\"type\":\"assistant\",\"timestamp\":\"2026-09-30T10:40:00Z\",\"requestId\":\"req_x\",\"message\":{\"id\":\"msg_x\",\"model\":\"demo-claude\",\"usage\":{\"input_tokens\":800,\"output_tokens\":100}}}";
                    var claude = new LogIndex(); var state = new LogFile { Name = "s.jsonl" }; claude.Files.Add(state);
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line + "\n"))) ClaudeLogs.Read(claude, state, "s.jsonl", stream, 0);
                    var report = ThirdPartyReport.Build(null, claude, log, new UsageHistory(), new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
                    var a = report.Endpoints.FirstOrDefault(e => e.Name == "A"); var b = report.Endpoints.First(e => e.Name == "B");
                    Require(b.Month == 900 && b.Current && (a == null || a.Month == 0));
                } finally { EndpointAttribution.Use(null); }
            });
            test("Third-party dashboard ranks endpoints over a period and stacks bars by endpoint", () => {
                var log = new EndpointLog();
                log.Marks.Add(new EndpointMark { App = "claude", Host = "a.example.com", Key = "aaaaaa", Name = "A", Since = "2026-09-30T09:00:00Z" });
                log.Marks.Add(new EndpointMark { App = "claude", Host = "b.example.com", Key = "bbbbbb", Name = "B", Since = "2026-09-30T10:30:00Z" });
                var claude = new LogIndex(); claude.Files.Add(new LogFile { Name = "s.jsonl", Hours = {
                    { "2026093009|anthropic|m1|a.example.com/aaaaaa", new Bucket { I = 1000, R = 2 } },
                    { "2026093010|anthropic|m1|b.example.com/bbbbbb", new Bucket { I = 3000, R = 3, TN = 3, TO = 900, TS = 9 } },
                    { "2026093010|anthropic|m2|", new Bucket { I = 5000, R = 1 } } } }); // official: not a relay
                var codex = new LogIndex(); codex.Files.Add(new LogFile { Name = "r.jsonl", Hours = { { "2026093011|myrelay|gpt", new Bucket { I = 500, R = 1 } } } });
                var period = ThirdPartyReport.Period(codex, claude, log, new DateTime(2026, 9, 30, 0, 0, 0), new DateTime(2026, 9, 30, 12, 0, 0), new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
                Require(period.Hourly && period.Bars.Count == 12 && Math.Abs(period.Total.Tokens() - 4500) < 1e-9);
                Require(period.Endpoints.Select(p => p.Label).SequenceEqual(new[] { "B", "A", "地址未记录的第三方接口" }));
                EndpointPeriod top = period.Endpoints[0];
                Require(top.Current && Math.Abs(top.Total.Speed().Value - 100) < 1e-9 && top.Color == ThirdPartyReport.Palette[0]);
                ChartBar ten = period.Bars[10];
                Require(ten.Parts.Count == 1 && ten.Parts[0].Name == "B" && Math.Abs(ten.Tokens - 3000) < 1e-9);
                // A 4-hour window from 08:00 leaves B's 10:00 bucket in and the 11:00 Codex one out.
                var narrow = ThirdPartyReport.Period(codex, claude, log, new DateTime(2026, 9, 30, 8, 0, 0), new DateTime(2026, 9, 30, 11, 0, 0), new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
                Require(Math.Abs(narrow.Total.Tokens() - 4000) < 1e-9 && narrow.Endpoints.Last().Total.Tokens() == 0);
            });
            test("Dashboard pages keep two platforms and distinguish unknown from healthy", () => {
                Require(QuotaDashboard.PageCount(0) == 1 && QuotaDashboard.PageCount(2) == 1 && QuotaDashboard.PageCount(3) == 2 && QuotaDashboard.PageCount(14) == 7);
                Require(QuotaDashboard.Level(null) == "未知" && QuotaDashboard.Level(0) == "告急" && QuotaDashboard.Level(26) == "偏紧" && QuotaDashboard.Level(100) == "充足");
            });
            test("Recipes keep the token on their own host and reject unsafe headers", () => {
                Func<string, bool> rejects = json => { try { CustomProviders.Parse(json); return false; } catch (ArgumentException) { return true; } };
                Require(rejects("{\"name\":\"X\",\"url\":\"https://a.example.com/q\",\"auth\":\"bearer\",\"balance\":{\"amount\":\"b\"},\"usage\":{\"url\":\"https://evil.example.com/u\",\"items\":\"d\",\"day\":\"t\",\"tokens\":[\"n\"]}}"));
                Require(rejects("{\"name\":\"X\",\"url\":\"https://a.example.com/q\",\"auth\":\"bearer\",\"headers\":{\"Authorization\":\"Bearer leak\"},\"balance\":{\"amount\":\"b\"}}"));
                Require(rejects("{\"type\":\"agnes-plan\",\"name\":\"Agnes\"}"));
                Require(rejects("{\"name\":\"X\",\"url\":\"https://a.example.com/q\",\"auth\":\"bearer\",\"token\":{\"kind\":\"console\"},\"balance\":{\"amount\":\"b\"}}"));
                var user = CustomProviders.Parse(CustomProviders.Template);
                Require(user.Id.StartsWith("custom-") && !user.Builtin && user.ConsoleToken && user.Usage != null && user.Usage.Items == "data.days");
                var named = CustomProviders.Parse("{\"id\":\"deepseek\",\"name\":\"Mine\",\"url\":\"https://a.example.com/q\",\"balance\":{\"amount\":\"b\"}}");
                Require(named.Id == "custom-deepseek");
            });
            test("Agnes maps four independent quota windows and relative resets", () => {
                var now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
                var state = CustomProviders.Map(ProviderCatalog.Custom["agnes"], J.Parse("{\"data\":{\"plan_name\":\"Pro\",\"usage\":{\"text_generation\":{\"windowed\":{\"used\":25,\"limit\":100,\"reset_in_seconds\":3600},\"weekly\":{\"used\":40,\"limit\":200,\"usage_pct\":30}},\"image_generation\":{\"daily\":{\"used\":0,\"limit\":20}},\"video_generation\":{\"daily\":{\"limit\":0}}}}}"), now);
                Require(state.Plan == "Pro" && state.Quotas.Count == 4);
                Require(state.Quotas[0].Remaining == 75 && state.Quotas[0].ResetUtc == now.AddHours(1).ToString("o"));
                Require(state.Quotas[1].Remaining == 70 && state.Quotas[2].Remaining == 100 && state.Quotas[3].Remaining == 0);
                Require(state.Quotas[0].UsedCount == 25 && state.Quotas[0].LimitCount == 100);
            });
            test("Agnes missing usage never invents a full allocation", () => {
                bool rejected = false;
                try { CustomProviders.Map(ProviderCatalog.Custom["agnes"], J.Parse("{\"data\":{\"usage\":{\"text_generation\":{\"windowed\":{\"limit\":100}}}}}"), DateTime.UtcNow); } catch (ProviderException) { rejected = true; }
                Require(rejected);
                rejected = false;
                try { CustomProviders.Map(ProviderCatalog.Custom["agnes"], J.Parse("{}"), DateTime.UtcNow); } catch (ProviderException) { rejected = true; }
                Require(rejected);
            });
            test("Release check accepts stable versions and safe official links", () => {
                var release = AppUpdate.Parse(J.Parse("{\"tag_name\":\"v2.10.1\",\"body\":\"## 本版更新\\n- 修复\\n\\n## 下载\\n链接\",\"draft\":false,\"prerelease\":false}"));
                Require(release.Version == "2.10.1" && release.Notes == "- 修复");
                Require(AppUpdate.IsNewer(release.Version, "2.9.9") && !AppUpdate.IsNewer("2.1.1", "2.1.1") && !AppUpdate.IsNewer("bad", "2.1.1"));
                Require(AppUpdate.ReleaseUrl(release) == "https://github.com/chinbor/headroom/releases/tag/v2.10.1");
                byte[] payload = System.Text.Encoding.UTF8.GetBytes("abc");
                string sum = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  headroom-setup-2.10.1.exe\n";
                Require(AppUpdate.ValidChecksum(payload, sum, "headroom-setup-2.10.1.exe"));
                Require(!AppUpdate.ValidChecksum(System.Text.Encoding.UTF8.GetBytes("bad"), sum, "headroom-setup-2.10.1.exe"));
                Require(!AppUpdate.ValidChecksum(payload, sum, "headroom-setup-2.10.2.exe"));
                foreach (string tag in new[] { "v2.1.1-rc1", "v2.1.1/evil", "v2.1", "v99999999999999999999.1.1" }) {
                    bool rejected = false; try { AppUpdate.Parse(J.Parse("{\"tag_name\":\"" + tag + "\"}")); } catch (FormatException) { rejected = true; } Require(rejected);
                }
                bool draft = false; try { AppUpdate.Parse(J.Parse("{\"tag_name\":\"v2.1.2\",\"prerelease\":true}")); } catch (FormatException) { draft = true; } Require(draft);
            });
            test("Taskbar gap shrinks or hides instead of overlapping occupied controls", () => {
                var bar = new System.Windows.Rect(0, 1000, 1920, 48);
                var occupied = new[] { new System.Windows.Rect(0, 1000, 1000, 48), new System.Windows.Rect(1620, 1000, 300, 48) };
                var slot = TaskbarPanel.FreeSpace(bar, occupied);
                Require(!slot.IsEmpty && slot.Width > 280 && !occupied.Any(r => r.IntersectsWith(slot)));
                var crowded = TaskbarPanel.FreeSpace(bar, new[] { new System.Windows.Rect(0, 1000, 1550, 48), occupied[1] });
                Require(crowded.IsEmpty);
                var narrow = TaskbarPanel.FreeSpace(bar, new[] { new System.Windows.Rect(0, 1000, 1500, 48), occupied[1] });
                Require(!narrow.IsEmpty && narrow.Width < 140);
                Require(TaskbarPanel.FreeSpace(new System.Windows.Rect(0, 0, 48, 1000), occupied).IsEmpty);
            });
            test("Codex weekly-only response does not invent a 5h window", () => {
                var s = Parsers.Codex(J.Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":38,\"limit_window_seconds\":604800,\"reset_at\":1900000000},\"secondary_window\":null}}"));
                Require(s.Quotas.Count == 1 && s.Quotas[0].Label == "每周" && s.Quotas[0].Remaining == 62);
            });
            test("Window classification uses duration, not primary/secondary position", () => {
                var s = Parsers.Codex(J.Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":10,\"limit_window_seconds\":604800},\"secondary_window\":{\"used_percent\":70,\"limit_window_seconds\":18000}}}"));
                Require(s.Quotas[0].Label == "每周" && s.Quotas[1].Label == "5 小时" && s.Quotas[1].Remaining == 30);
            });
            test("Absent utilization stays unknown instead of becoming zero", () => {
                Require(Parsers.Claude(J.Parse("{\"five_hour\":{\"resets_at\":null},\"seven_day\":null}")).Quotas.Count == 0);
                Require(Parsers.Codex(J.Parse("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000}}}")).Quotas.Count == 0);
            });
            test("Explicit zero usage is valid data", () => {
                var s = Parsers.Claude(J.Parse("{\"five_hour\":{\"utilization\":0},\"seven_day\":{\"utilization\":3}}"));
                Require(s.Quotas.Count == 2 && s.Quotas[0].Remaining == 100 && s.Quotas[1].Remaining == 97);
            });
            test("Malformed optional Codex limits do not discard primary usage", () => {
                var s = Parsers.Codex(J.Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"limit_window_seconds\":18000}},\"additional_rate_limits\":[null,{}, {\"limit_name\":\"test-model\",\"rate_limit\":{\"primary_window\":{\"used_percent\":50,\"limit_window_seconds\":604800}}}]}"));
                Require(s.Quotas.Count == 2 && s.Quotas[0].Remaining == 75);
            });
            test("DeepSeek currencies are never summed or treated as percentages", () => {
                var s = Parsers.DeepSeek(J.Parse("{\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"12.34\"},{\"currency\":\"USD\",\"total_balance\":\"2.50\"}]}"));
                Require(s.Quotas.Count == 0 && s.Balances.Count == 2 && s.Balances[0].Amount == 12.34);
            });
            RecoveryTests.Run(test);
            test("Percentages are bounded and nonnumeric values ignored", () => {
                var s = new ProviderState(); Parsers.Add(s, "one", -10, null); Parsers.Add(s, "two", 150, null); Parsers.Add(s, "bad", "NaN", null); Require(s.Quotas.Count == 2 && s.Quotas[0].Remaining == 100 && s.Quotas[1].Remaining == 0);
            });
            test("Daily history groups hourly buckets by local day and model", () => {
                var idx = new LogIndex(); idx.Files.Add(new LogFile { Hours = { { "2026010110||m1", new Bucket { I = 100, C = 900, O = 10, R = 1, D = 2 } }, { "2026010118||m2", new Bucket { I = 50, O = 5, R = 1, D = 1 } } } });
                // 10:00Z is 18:00 on Jan 1 in UTC+8; 18:00Z is 02:00 on Jan 2.
                var h = HistoryService.FromIndexes(new Dictionary<string, LogIndex> { { "codex", idx } }, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"), new DateTime(2026, 1, 2), 30);
                Require(h.Days.Count == 2 && h.Days[0].Day == "2026-01-01" && h.Days[0].Tokens == 1010 && h.Days[0].CachedTokens == 900 && h.Days[0].Cost == 2 && h.Days[1].Day == "2026-01-02" && h.Days[1].Models[0].Model == "m2" && h.Engine == HistoryService.Engine);
            });
            test("JWT display parsing fails closed on malformed tokens", () => { Require(J.Jwt("bad") == null && J.Jwt("a.!.c") == null); });
            test("Direct proxy selection does not inherit ambient SOCKS settings", () => { Require(ProviderService.ResolveProxy("direct") == ""); });
            test("Explicit HTTP proxy is preserved and unsupported schemes rejected", () => {
                Require(ProviderService.ResolveProxy("http://127.0.0.1:7890") == "http://127.0.0.1:7890"); bool rejected = false; try { ProviderService.ResolveProxy("socks5://127.0.0.1:7890"); } catch (ArgumentException) { rejected = true; } Require(rejected);
            });
            test("Plan formatting matches the upstream Pro tiers", () => {
                Require(UsageDetails.PlanName("pro") == "Pro 20x" && UsageDetails.PlanName("pro_lite") == "Pro 5x" && UsageDetails.PlanName("custom-plan") == "custom-plan");
            });
            test("Pace reserve and headroom are derived from elapsed time", () => {
                DateTime now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
                var q = new Quota { Used = 66, WindowSeconds = 604800, ResetUtc = now.AddHours(12).ToString("o") };
                var p = UsageDetails.Pace(q, now);
                Require(p != null && Math.Abs(p.Reserve - 26.857142857) < .0001 && p.Lasts && p.SpeedMultiplier > 1.5);
            });
            test("Pace predicts depletion for a deficit and rejects invalid windows", () => {
                DateTime now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
                var q = new Quota { Used = 70, WindowSeconds = 604800, ResetUtc = now.AddDays(5).ToString("o") };
                var p = UsageDetails.Pace(q, now); Require(p != null && p.Reserve < 0 && !p.Lasts && p.SecondsUntilEmpty.HasValue);
                q.WindowSeconds = 0; Require(UsageDetails.Pace(q, now) == null);
                q.WindowSeconds = 604800; q.ResetUtc = now.AddDays(8).ToString("o"); Require(UsageDetails.Pace(q, now) == null);
            });
            test("Reset credit inventory excludes expired and redeemed expiry entries", () => {
                var state = new ProviderState(); var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
                UsageDetails.ParseResetCredits(state, J.Parse("{\"available_count\":2,\"credits\":[{\"status\":\"available\",\"expires_at\":\"2026-01-20T00:00:00Z\"},{\"status\":\"redeemed\",\"expires_at\":\"2026-01-25T00:00:00Z\"},{\"status\":\"available\",\"expires_at\":\"2026-01-01T00:00:00Z\"}]}"), now);
                Require(state.ResetCreditsAvailable == 2 && state.ResetCreditExpiries.Count == 1);
            });
            test("No reset credits is different from an unavailable reset endpoint", () => {
                var state = new ProviderState(); UsageDetails.ParseResetCredits(state, J.Parse("{}"), DateTime.UtcNow); Require(!state.ResetCreditsAvailable.HasValue);
                UsageDetails.ParseResetCredits(state, J.Parse("{\"available_count\":0,\"credits\":[]}"), DateTime.UtcNow); Require(state.ResetCreditsAvailable == 0);
            });
            test("Current-window estimates exclude the partial opening day and future rows", () => {
                DateTime now = new DateTime(2026, 1, 17, 4, 0, 0, DateTimeKind.Utc);
                var q = new Quota { WindowSeconds = 604800, ResetUtc = new DateTime(2026, 1, 20, 4, 0, 0, DateTimeKind.Utc).ToString("o") };
                var days = new[] { new DayUsage { Day = "2026-01-13", Tokens = 100 }, new DayUsage { Day = "2026-01-14", Tokens = 200 }, new DayUsage { Day = "2026-01-17", Tokens = 300 }, new DayUsage { Day = "2026-01-19", Tokens = 500 } };
                var usage = UsageDetails.Window(days, q, now, 0, null, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")); Require(!usage.Exact && usage.Days.Count == 2 && usage.Tokens == 500);
            });
            test("Price sync accepts only a dated, complete catalogue", () => {
                string date; var many = String.Join(",", Enumerable.Range(0, 120).Select(i => "\"m" + i + "\":{\"in\":1,\"out\":2}"));
                Require(Pricing.Valid("{\"updated\":\"2026-10-01\",\"models\":{" + many + "}}", out date) && date == "2026-10-01");
                Require(!Pricing.Valid("{\"updated\":\"2026-10-01\",\"models\":{\"a\":{\"in\":1,\"out\":2}}}", out date));
                Require(!Pricing.Valid("{\"updated\":\"yesterday\",\"models\":{" + many + "}}", out date));
                Require(Pricing.Valid("{\"updated\":\"2026-10-01T02:17Z\",\"models\":{" + many + "}}", out date) && String.CompareOrdinal(date, "2026-10-01") > 0);
                Require(!Pricing.Valid("<html>rate limited</html>", out date));
                Require(Pricing.Valid(File.ReadAllText(Path.Combine(Store.Root, "pricing.json")), out date));
            });
            test("Pricing: cache writes, 1-hour writes, whole-request and marginal long-context tiers", () => {
                Pricing.LoadJson("{\"updated\":\"t\",\"models\":{\"t-model\":{\"in\":10,\"out\":50,\"cr\":1,\"cw\":12.5,\"long\":{\"at\":272000,\"in\":20,\"out\":75,\"cr\":2},\"fast\":2},\"c-model\":{\"in\":3,\"out\":15},\"m-model\":{\"in\":1,\"out\":2,\"long\":{\"at\":200000,\"marginal\":true,\"in\":2,\"out\":4}},\"g-5\":{\"in\":1,\"out\":1}}}");
                try {
                    ModelPrice t = Pricing.Find("t-model"), c = Pricing.Find("c-model"), m = Pricing.Find("m-model");
                    Require(Math.Abs(Pricing.CodexCost(t, 300000, 200000, 0, 1000) - 2.475) < 1e-9 && Math.Abs(Pricing.CodexCost(t, 1000, 900, 0, 10) - 0.0024) < 1e-12 && t.Fast == 2);
                    Require(Math.Abs(Pricing.Cost(c, new TokenUse { Input = 1000, Output = 100, CacheRead = 10000, CacheWrite5m = 2000, CacheWrite1h = 1000 }) - 0.021) < 1e-12);
                    Require(Math.Abs(Pricing.Cost(m, new TokenUse { Input = 300000 }) - 0.4) < 1e-12);
                    Require(Math.Abs(Pricing.Cost(t, new TokenUse { Input = 100000, CacheRead = 200000 }) - (100000 * 20e-6 + 200000 * 2e-6)) < 1e-9);
                    Require(Pricing.Find("t-model(xhigh)") == t && Pricing.Find("provider/t-model-20260101") == t && Pricing.Find("g-5-4") == null && Pricing.Find("g-5.4") == null && Pricing.Find("g-5-20260101") != null);
                } finally { Pricing.Load(Path.Combine(Store.Root, "pricing.json")); }
            });
            test("History merge never shrinks a past day and always replaces today", () => {
                var old = new UsageHistory { Zone = "Z", Days = { new DayUsage { Day = "2026-09-27", Agent = "codex", Tokens = 12000 }, new DayUsage { Day = "2026-09-28", Agent = "codex", Tokens = 10 }, new DayUsage { Day = "2026-09-29", Agent = "codex", Tokens = 999 }, new DayUsage { Day = "2026-08-01", Agent = "codex", Tokens = 5 } } };
                var next = new UsageHistory { Zone = "Z", Days = { new DayUsage { Day = "2026-09-27", Agent = "codex", Tokens = 11000 }, new DayUsage { Day = "2026-09-28", Agent = "codex", Tokens = 20 }, new DayUsage { Day = "2026-09-29", Agent = "codex", Tokens = 50 } } };
                var merged = HistoryService.Merge(old, next, "2026-08-31", "2026-09-29");
                Require(merged.Days.Count == 3 && merged.Days.First(d => d.Day == "2026-09-27").Tokens == 12000 && merged.Days.First(d => d.Day == "2026-09-28").Tokens == 20 && merged.Days.First(d => d.Day == "2026-09-29").Tokens == 50);
                var otherZone = new UsageHistory { Zone = "Y", Days = { new DayUsage { Day = "2026-09-27", Agent = "codex", Tokens = 1 } } };
                Require(HistoryService.Merge(old, otherZone, "2026-08-31", "2026-09-29").Days.Count == 1);
            });
            test("Codex log reader counts token_count once, skips repeats, keeps an unfinished tail", () => {
                string a = "{\"timestamp\":\"2026-09-27T02:10:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":900,\"output_tokens\":50,\"reasoning_output_tokens\":10,\"total_tokens\":1050},\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":900,\"output_tokens\":50,\"total_tokens\":1050}}}}";
                string repeat = a.Replace("02:10:00", "02:11:00");
                string noise = "{\"timestamp\":\"2026-09-27T02:12:00.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"text\":\"" + new string('x', 300000) + "\"}}";
                string nullInfo = "{\"timestamp\":\"2026-09-27T02:13:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}";
                string b = "{\"timestamp\":\"2026-09-27T03:05:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":3000,\"cached_input_tokens\":2700,\"output_tokens\":80,\"reasoning_output_tokens\":10,\"total_tokens\":3080},\"last_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":1800,\"output_tokens\":30,\"total_tokens\":2030}}}}";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(a + "\n" + repeat + "\n" + noise + "\n" + nullInfo + "\n" + b.Substring(0, 40));
                var index = new LogIndex(); var state = new LogFile();
                long offset; using (var stream = new MemoryStream(bytes)) offset = CodexLogs.Read(index, state, "none", stream, 0);
                // No turn_context yet: the model falls back to gpt-5, as Codex itself does.
                Bucket first = state.Hours["2026092702|openai|gpt-5"];
                Require(state.Hours.Count == 1 && first.Tokens() == 1050 && first.I == 100 && first.C == 900 && first.R == 1 && offset == bytes.Length - 40);
                byte[] rest = System.Text.Encoding.UTF8.GetBytes(b + "\n");
                using (var stream = new MemoryStream(rest)) CodexLogs.Read(index, state, "none", stream, offset);
                Require(state.Hours["2026092703|openai|gpt-5"].Tokens() == 2030);
            });
            test("Codex buckets carry the session's provider and model; subscription windows skip relays", () => {
                string meta = "{\"timestamp\":\"2026-09-27T02:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"model_provider\":\"custom\"}}";
                string turn = "{\"timestamp\":\"2026-09-27T02:00:01.000Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"demo-model\"}}";
                string tier = "{\"timestamp\":\"2026-09-27T02:00:02.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_settings\":{\"service_tier\":\"priority\"}}}";
                string count = "{\"timestamp\":\"2026-09-27T02:05:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":500,\"output_tokens\":20},\"last_token_usage\":{\"input_tokens\":500,\"output_tokens\":20}}}}";
                var state = new LogFile();
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(meta + "\n" + turn + "\n" + tier + "\n" + count + "\n"))) CodexLogs.Read(new LogIndex(), state, "none", stream, 0);
                Require(state.Hours.ContainsKey("2026092702|custom|demo-model") && state.Hours["2026092702|custom|demo-model"].Tokens() == 520 && state.Tier == "fast");
                var index = new LogIndex { CoveredFrom = "2026-09-01T00:00:00Z" }; index.Files.Add(state);
                index.Files.Add(new LogFile { Hours = { { "2026092703|openai|demo-model", new Bucket { I = 100, R = 1 } } } });
                var q = new Quota { WindowSeconds = 604800, ResetUtc = "2026-10-01T00:00:00Z" };
                var usage = UsageDetails.Window(new DayUsage[0], q, new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), 0, index, TimeZoneInfo.Utc, CodexLogs.IsOfficial);
                Require(usage.Exact && Math.Abs(usage.Tokens - 100) < 1e-9);
            });
            test("Claude log entries are de-duplicated by message id + request id; the larger copy wins", () => {
                Func<string, string, string, int, string> line = (id, model, stamp, output) => "{\"type\":\"assistant\",\"timestamp\":\"" + stamp + "\",\"requestId\":\"req_" + id + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":10,\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":1000}}}";
                string text = line("msg_a", "demo-claude", "2026-09-27T02:00:00Z", 5) + "\n" + line("msg_a", "demo-claude", "2026-09-27T02:00:01Z", 5) + "\n" + line("msg_b", "<synthetic>", "2026-09-27T02:01:00Z", 5) + "\n" + line("msg_c", "demo-claude", "2026-09-27T03:00:00Z", 5) + "\n" + line("msg_c", "demo-claude", "2026-09-27T03:00:02Z", 50) + "\n";
                var index = new LogIndex(); var state = new LogFile { Name = "s.jsonl" }; index.Files.Add(state);
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))) ClaudeLogs.Read(index, state, "s.jsonl", stream, 0);
                Require(state.Hours["2026092702||demo-claude"].Tokens() == 1115 && state.Hours["2026092702||demo-claude"].R == 1 && state.Hours["2026092702||"].Tokens() == 1115 && state.Hours["2026092703||demo-claude"].O == 50 && state.Hours["2026092703||demo-claude"].R == 1);
                // Without requestId: same id + same timestamp is a copy, a new timestamp is not.
                string bare = "{\"type\":\"assistant\",\"timestamp\":\"2026-09-27T04:00:00Z\",\"message\":{\"id\":\"msg_d\",\"model\":\"demo-claude\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}";
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bare + "\n" + bare + "\n" + bare.Replace("04:00:00", "04:00:09") + "\n"))) ClaudeLogs.Read(index, state, "s.jsonl", stream, 0);
                Require(state.Hours["2026092704|" + ClaudeLogs.NoRequestId + "|demo-claude"].Tokens() == 4 && state.Hours["2026092704|" + ClaudeLogs.NoRequestId + "|demo-claude"].R == 2);
                // A null id is malformed and skipped; sub-agent progress lines count.
                string malformed = "{\"type\":\"assistant\",\"timestamp\":\"2026-09-27T05:00:00Z\",\"message\":{\"id\":null,\"model\":\"demo-claude\",\"usage\":{\"input_tokens\":7,\"output_tokens\":7}}}";
                string progress = "{\"type\":\"progress\",\"sessionId\":\"s1\",\"data\":{\"message\":{\"timestamp\":\"2026-09-27T05:10:00Z\",\"requestId\":\"req_p\",\"message\":{\"id\":\"msg_p\",\"model\":\"demo-claude\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}}}";
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(malformed + "\n" + progress + "\n"))) ClaudeLogs.Read(index, state, "s.jsonl", stream, 0);
                Require(state.Hours["2026092705||demo-claude"].Tokens() == 5);
            });
            test("Output speed (Codex): last tool output / prompt → last model item, across reads; short replies skipped", () => {
                Func<string, string, string> item = (stamp, payload) => "{\"timestamp\":\"2026-09-27T" + stamp + "Z\",\"type\":\"response_item\",\"payload\":{" + payload + "}}";
                Func<string, int, int, string> count = (stamp, total, last) => "{\"timestamp\":\"2026-09-27T" + stamp + "Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + (total * 10) + ",\"output_tokens\":" + total + "},\"last_token_usage\":{\"input_tokens\":" + (last * 10) + ",\"output_tokens\":" + last + "}}}}";
                string first = String.Join("\n", new[] {
                    item("10:00:00.000", "\"type\":\"message\",\"role\":\"user\",\"content\":[]"),
                    "{\"timestamp\":\"2026-09-27T10:00:00.500Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"demo-model\"}}",
                    item("10:00:12.000", "\"type\":\"reasoning\",\"summary\":[]") }) + "\n";
                string rest = String.Join("\n", new[] {
                    item("10:00:20.000", "\"type\":\"custom_tool_call\",\"name\":\"apply_patch\""),
                    item("10:00:22.000", "\"type\":\"custom_tool_call_output\",\"output\":\"ok\""),
                    count("10:00:22.010", 1000, 1000),
                    item("10:00:40.000", "\"type\":\"reasoning\",\"summary\":[]"),
                    item("10:00:47.000", "\"type\":\"message\",\"role\":\"assistant\",\"content\":[]"),
                    count("10:00:47.100", 1500, 500),
                    item("10:05:00.000", "\"type\":\"message\",\"role\":\"user\",\"content\":[]"),
                    item("10:05:03.000", "\"type\":\"message\",\"role\":\"assistant\",\"content\":[]"),
                    count("10:05:03.100", 1520, 20) }) + "\n";
                var index = new LogIndex(); var state = new LogFile();
                long offset; using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(first))) offset = CodexLogs.Read(index, state, "none", stream, 0);
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rest))) CodexLogs.Read(index, state, "none", stream, offset);
                Bucket b = state.Hours["2026092710|openai|demo-model"];
                // 20 s (10:00:00 → 10:00:20) + 25 s (10:00:22 → 10:00:47); the 20-token reply is not timed.
                Require(b.R == 3 && b.TN == 2 && b.TO == 1500 && Math.Abs(b.TS - 45) < 1e-9 && Math.Abs(b.Speed().Value - 1500 / 45.0) < 1e-9);
            });
            test("Output speed (Claude): parent entry → last block, across reads and files; quoted keys in text ignored", () => {
                Func<string, string, string, string, int, string> block = (id, uuid, parent, stamp, output) => "{\"parentUuid\":\"" + parent + "\",\"type\":\"assistant\",\"requestId\":\"req_" + id + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"demo-claude\",\"usage\":{\"input_tokens\":10,\"output_tokens\":" + output + "}},\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-09-27T" + stamp + "Z\"}";
                string user = "{\"parentUuid\":null,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"},\"uuid\":\"u1\",\"timestamp\":\"2026-09-27T11:00:00.000Z\"}";
                string toolResult = "{\"parentUuid\":\"a2\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"x \\\"uuid\\\":\\\"a1\\\",\\\"timestamp\\\":\\\"2026-09-27T09:00:00Z\\\"\"}]},\"uuid\":\"u2\",\"timestamp\":\"2026-09-27T11:00:30.000Z\"}";
                string system = "{\"parentUuid\":\"a3\",\"type\":\"system\",\"timestamp\":\"2026-09-27T11:01:00.000Z\",\"uuid\":\"s1\"}";
                string first = user + "\n" + block("m1", "a1", "u1", "11:00:06.000", 800) + "\n";
                string rest = String.Join("\n", new[] { block("m1", "a2", "a1", "11:00:10.000", 800), toolResult, block("m2", "a3", "u2", "11:00:45.000", 30), system, block("m3", "a4", "s1", "11:01:20.000", 400) }) + "\n";
                var index = new LogIndex(); var state = new LogFile { Name = "s.jsonl" }; index.Files.Add(state);
                long offset; using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(first))) offset = ClaudeLogs.Read(index, state, "s.jsonl", stream, 0);
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rest))) ClaudeLogs.Read(index, state, "s.jsonl", stream, offset);
                Bucket b = state.Hours["2026092711||demo-claude"];
                // m1: 11:00:00 → 11:00:10 = 10 s; m2 has 30 output tokens (skipped); m3: 11:01:00 → 11:01:20 = 20 s.
                Require(b.R == 3 && b.TN == 2 && b.TO == 1200 && Math.Abs(b.TS - 30) < 1e-9);
                // The same responses copied into another file are not timed again.
                var copy = new LogFile { Name = "t.jsonl" }; index.Files.Add(copy);
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(first + rest))) ClaudeLogs.Read(index, copy, "t.jsonl", stream, 0);
                Require(copy.Hours.Values.Sum(x => x.TN) == 0 && state.Hours["2026092711||demo-claude"].TN == 2);
            });
            test("Codex endpoint is read from model_provider, profiles and provider tables", () => {
                string toml = "model = \"m\"\nmodel_provider = \"custom\"\n[projects.'D:\\x.y']\ntrust_level = \"trusted\"\n[model_providers.\"custom\"]\nname = \"custom\"\nbase_url = \"https://Relay.Example.com/v1\" # comment\n";
                Require(Endpoints.ActiveCodexProvider(toml) == "custom" && Endpoints.Host(Endpoints.Toml(toml, "model_providers.custom", "base_url")) == "relay.example.com");
                Require(Endpoints.ActiveCodexProvider("profile = \"p\"\n[profiles.p]\nmodel_provider = \"alt\"\n") == "alt" && Endpoints.ActiveCodexProvider("model = \"m\"\n") == "openai");
                Require(Endpoints.Host("http://127.0.0.1:15721") == "127.0.0.1:15721" && Endpoints.Host("not a url") == "");
                string fingerprint = Endpoints.Fingerprint("sk-test-123456");
                Require(fingerprint.Length == 6 && fingerprint == Endpoints.Fingerprint(" sk-test-123456 ") && Endpoints.Fingerprint("") == "");
            });
            test("The running hour counts in full up to now; past range edges still split by overlap", () => {
                DateTime hour = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), now = hour.AddMinutes(15);
                var index = new LogIndex(); index.Files.Add(new LogFile { Name = "a.jsonl", Hours = { { "2026093010|openai|m", new Bucket { I = 1000, D = 4, R = 4 } }, { "2026093008|openai|m", new Bucket { I = 1000, D = 4, R = 4 } } } });
                // "Today until now" at 10:15: the 10:00 bucket already holds everything up to now.
                Bucket today = index.Usage(hour.AddHours(-10), now, null, null, now);
                Require(Math.Abs(today.Tokens() - 2000) < 1e-9 && Math.Abs(today.D - 8) < 1e-9 && Math.Abs(today.R - 8) < 1e-9);
                // A range ending at 08:15, asked at 10:15, takes a quarter of the 08:00 bucket.
                Require(Math.Abs(index.Usage(hour.AddHours(-3), hour.AddHours(-2).AddMinutes(15), null, null, now).Tokens() - 250) < 1e-9);
                Require(LogIndex.Weight(hour, hour.AddMinutes(-30), now, now) == 1 && LogIndex.Weight(hour, now, hour.AddHours(1), now) == 0 && LogIndex.Weight(hour.AddHours(-2), hour.AddHours(-2).AddMinutes(30), hour, now) == .5);
            });
            test("DeepSeek Harness sessions: zstd frames, model, cache and speed, read incrementally", () => {
                // Synthetic session: header + message (2 s, 100 output) in two frames, then a
                // flash request whose answer arrives in a third frame (20 output: not timed).
                byte[] a = Convert.FromBase64String("KLUv/WDgAG0IAAaQMSFgh5sDtR+JyOJyAyo7jtw+QIUKluVVDdHfy/8UEAAWxAkoACkAJQCEGJP8k9dW5mhBKIeEQVEGUkIgyS9j9VGSbywfvZ82I+zBfn7do/8E7yjGt+cKB4ezTGMw2JTSwmKX3S7zXI72nGIdsKb+5GJ4y+g2SPPCxr5q9TbNHgzwfLNtdKe9P+m2eVFWoczLQJpp82LWO7p6sfU8a6wbhBhjSkviaLc+StvrGSAYXYJtlKqjhu0opZTkme2iFTR0bP1JeBQfEBsAMwnXSTRnYU0RsxuJ5yii+aHBKGu0oYhAgHVksQ0iawVwHEz2ZC+AZLjsw+/oDRzBXouVqPIChkC6mCAKCVW0XgoKKLUv/SCf3QMAQsgaG2CJdaCMJ2tPmqn7kZuPooghsVmwYzUhBmEDi4A5/cQtU1a7F/fQaE6zuAqifL3FyNI5T6kNJP7I/MvaFA8ySUPcU37Uj3rlLqPjSEYhpXQYIAwE9ZcmjQx182hk+LVnAhnvNIdWn1lIPwEEAPrwOLC9Fis/ZmoP");
                byte[] b = Convert.FromBase64String("KLUv/SCszQMAkgcZG2Br21TYk8Z3HS1k8ZLjBAB5UEwvy2EgQ2C0coD3HkKQoc16WzkQi65hFYoqPwjpxn4QwjyvXbBDt2apu2x3eW1dfpIEU0JEAioMBPllVP+kfGP555tQ5mUCvbRZMevz2zz6LgYAM4nGafQmCZYiH5s2BnipKA==");
                string dir = Path.Combine(Path.GetTempPath(), "cum-dsh-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                try {
                    Pricing.EnsureLoaded();
                    string path = Path.Combine(dir, "session.v4.jsonl.zstd");
                    DateTime now = LogReader.FromMillis(1790833294366 + 3600000.0);
                    Func<LogIndex, LogIndex> scan = idx => LogReader.Scan(idx, new[] { path }, info => info.FullName.ToLowerInvariant(), now, HarnessLogs.Read);
                    File.WriteAllBytes(path, a.Concat(b.Take(b.Length - 4)).ToArray()); // the third frame is still being written
                    LogIndex index = scan(null);
                    List<HourUsage> first = index.Entries();
                    Require(first.Count == 1 && first[0].Model == "deepseek-v4-pro" && first[0].Input == 1000 && first[0].Cached == 500 && first[0].Output == 100 && first[0].Cost > 0);
                    Require(first[0].TimedRequests == 1 && Math.Abs(first[0].TimedSeconds - 2) < 1e-9 && index.Files[0].Offset == a.Length && index.Files[0].Model == "deepseek-flash");
                    File.WriteAllBytes(path, a.Concat(b).ToArray());
                    index = scan(index);
                    Require(index.Entries().Count == 2 && index.Entries().Any(h => h.Model == "deepseek-flash" && h.Input == 300 && h.Output == 20 && h.TimedRequests == 0));
                    index.Files[0].Offset = 0; index.Files[0].Length = 0; index = scan(index);  // read again from the start
                    Require(Math.Abs(index.Entries().Sum(h => h.Tokens) - 1920) < 1e-9);
                } finally { try { Directory.Delete(dir, true); } catch { } }
            });
            test("English interface: whole strings, built-up strings, placeholders, and back to Chinese", () => {
                try {
                    I18n.Use("zh");
                    Require(I18n.T("近 30 天") == "近 30 天");
                    I18n.Use("en");
                    Require(I18n.T("近 30 天") == "Last 30 days" && I18n.T("{0} 分钟", 5) == "5 min" && I18n.T("Codex") == "Codex");
                    Require(I18n.T("4 / 6" + " 已连接 · 每 " + 5 + " 分钟刷新") == "4 / 6 connected · every 5 min");
                    Require(I18n.T("Sonnet · 每周") == "Sonnet · Weekly");
                    // Every placeholder of a key is kept in its English text.
                    foreach (var pair in I18nTable.En) foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pair.Key, @"\{\d\}")) Require(pair.Value.Contains(m.Value));
                    var block = new System.Windows.Controls.TextBlock { Text = "近 7 天", ToolTip = "刷新全部" };
                    I18n.Localize(block); Require(block.Text == "Last 7 days" && (string)block.ToolTip == "Refresh all");
                    // A label followed by added runs (the bold percentage of a quota) keeps them.
                    var mixed = new System.Windows.Controls.TextBlock { Text = "每周 " };
                    mixed.Inlines.Add(new System.Windows.Documents.Run("22%")); mixed.Inlines.Add(new System.Windows.Documents.Run(" 剩余"));
                    I18n.Localize(mixed); Require(new System.Windows.Documents.TextRange(mixed.ContentStart, mixed.ContentEnd).Text == "Weekly 22% left");
                    I18n.Use("zh"); I18n.Localize(block); Require(block.Text == "近 7 天" && (string)block.ToolTip == "刷新全部");
                } finally { I18n.Use("zh"); }
            });
            test("Per-platform refresh: own interval, faster while in use, never under a minute", () => {
                var config = new AppConfig { RefreshMinutes = 5, FastInUse = true, InUseMinutes = 2 };
                config.ProviderMinutes["claude"] = 30;
                Require(Pacing.Minutes(config, "codex", false) == 5 && Pacing.Minutes(config, "codex", true) == 2);
                Require(Pacing.Minutes(config, "claude", false) == 30 && Pacing.Minutes(config, "claude", true) == 2);
                config.FastInUse = false; Require(Pacing.Minutes(config, "codex", true) == 5);
                config.InUseMinutes = 0; config.FastInUse = true; Require(Pacing.Minutes(config, "codex", true) == 1);
                DateTime now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
                Require(Pacing.Due(DateTime.MinValue, 5, now) && Pacing.Due(now.AddMinutes(-5), 5, now) && Pacing.Due(now.AddSeconds(-290), 5, now) && !Pacing.Due(now.AddMinutes(-4), 5, now));
                Require(Pacing.CountsAsWork("claude", @"C:\u\.claude\projects\p\s.jsonl") && !Pacing.CountsAsWork("claude", @"C:\u\.claude\settings.toml") && Pacing.CountsAsWork("deepseek", @"C:\u\.deepseek\sessions\a.db-wal"));
            });
            test("A truncated log is read again from the start and its surviving records count", () => {
                string dir = Path.Combine(Path.GetTempPath(), "headroom-trunc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                try {
                    string path = Path.Combine(dir, "rollout.jsonl"); DateTime now = DateTime.UtcNow, at = now.AddHours(-1);
                    Func<int, int, string> count = (sec, total) => "{\"timestamp\":\"" + at.AddSeconds(sec).ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + total + ",\"output_tokens\":0},\"last_token_usage\":{\"input_tokens\":200,\"output_tokens\":0}}}}";
                    Func<LogIndex, LogIndex> scan = idx => LogReader.Scan(idx, new[] { path }, info => info.Name, now, (i, st, p, s, off) => CodexLogs.Read(i, st, p, s, off));
                    File.WriteAllText(path, count(0, 200) + "\n" + count(10, 400) + "\n" + count(20, 600) + "\n");
                    LogIndex index = scan(null);
                    Require(Math.Abs(index.Entries().Sum(h => h.Tokens) - 600) < 1e-9);
                    // 600 tokens in three records → one 200-token record survives.
                    File.WriteAllText(path, count(0, 200) + "\n"); File.SetLastWriteTimeUtc(path, now.AddMinutes(-1));
                    index = scan(index);
                    Require(Math.Abs(index.Entries().Sum(h => h.Tokens) - 200) < 1e-9);
                } finally { try { Directory.Delete(dir, true); } catch { } }
            });
            test("Exact window clips hour buckets by overlap, tokens and cost alike", () => {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                var index = new LogIndex { CoveredFrom = "2026-09-01T00:00:00Z", Updated = "2026-09-29T10:00:00Z" };
                // The window opens at 02:30Z, halfway through the 02:00Z bucket.
                index.Files.Add(new LogFile { Name = "a.jsonl", Hours = { { "2026092702|openai|m", new Bucket { I = 1000, D = 1 } }, { "2026092703|openai|m", new Bucket { I = 2000, D = 2 } }, { "2026092802|openai|m", new Bucket { I = 4000, D = 8 } } } });
                var q = new Quota { WindowSeconds = 604800, ResetUtc = "2026-10-04T02:30:00Z" };
                var usage = UsageDetails.Window(new DayUsage[0], q, new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc), 0, index, zone);
                Require(usage.Exact && Math.Abs(usage.Tokens - 6500) < .001 && Math.Abs(usage.Cost - 10.5) < 1e-9 && usage.CostComplete);
                Require(!UsageDetails.Window(new DayUsage[0], q, new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc), 4, index, zone).Exact);
            });
            test("Custom provider definitions are validated before any request", () => {
                var p = CustomProviders.Parse("{\"name\":\"My Gateway\",\"url\":\"https://api.example.com/usage\",\"auth\":\"bearer\",\"windows\":[{\"label\":\"每日\",\"used\":\"d.used\",\"limit\":\"d.limit\"}]}");
                Require(p.Id == "custom-my-gateway" && p.Host == "api.example.com" && p.Windows.Count == 1);
                Func<string, bool> rejected = json => { try { CustomProviders.Parse(json); return false; } catch (ArgumentException) { return true; } };
                Require(rejected("{\"name\":\"x\",\"url\":\"http://api.example.com/u\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://user:pw@api.example.com/u\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\",\"remainingPercent\":\"q\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\",\"windows\":[{\"label\":\"a\",\"used\":\"u\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"a..b\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\",\"auth\":\"header\",\"header\":\"Cookie\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\",\"script\":\"x\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\"}]}"));
                Require(rejected("{\"name\":\"x\",\"url\":\"https://a.example.com\"}"));
                Require(!rejected("{\"name\":\"lan\",\"url\":\"http://192.168.1.20:3000/api/usage\",\"windows\":[{\"label\":\"a\",\"usedPercent\":\"p\"}]}") && !rejected("{\"name\":\"loop\",\"url\":\"http://127.0.0.1:8080/u\",\"balance\":{\"amount\":\"b\"}}"));
            });
            test("Custom provider mapping: paths, ratios, counts, resets and missing fields", () => {
                var p = CustomProviders.Parse("{\"name\":\"g\",\"url\":\"https://a.example.com\",\"plan\":\"data.plan\",\"windows\":[{\"label\":\"日\",\"used\":\"data.items[1].used\",\"limit\":\"data.items[1].limit\",\"resetInSeconds\":\"data.reset\",\"windowHours\":24},{\"label\":\"月\",\"remainingPercent\":\"data.left\",\"ratio\":true,\"resetsAt\":\"data.month_end\"},{\"label\":\"缺\",\"usedPercent\":\"data.nope\"}],\"balance\":{\"amount\":\"data.balance\",\"currencyPath\":\"data.cur\"}}");
                var now = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
                var s = CustomProviders.Map(p, J.Parse("{\"data\":{\"plan\":\"Team\",\"items\":[{\"used\":1},{\"used\":\"30\",\"limit\":120}],\"reset\":7200,\"left\":0.8,\"month_end\":1790675446,\"balance\":\"12.5\",\"cur\":\"cny\"}}"), now);
                Require(s.Plan == "Team" && s.Quotas.Count == 2 && s.Quotas[0].Used == 25 && s.Quotas[0].ResetUtc.StartsWith("2026-09-29T02:00") && s.Quotas[0].WindowSeconds == 86400);
                Require(Math.Abs(s.Quotas[1].Used - 20) < 1e-9 && s.Quotas[1].ResetUtc.StartsWith("2026-09-29") && s.Balances.Count == 1 && s.Balances[0].Currency == "CNY" && s.Balances[0].Amount == 12.5);
                bool threw = false; try { CustomProviders.Map(p, J.Parse("{\"other\":1}"), now); } catch (ProviderException) { threw = true; } Require(threw);
                Require(CustomProviders.Resolve(J.Parse("{\"a\":[{\"b\":5}]}"), "a[0].b").ToString() == "5" && CustomProviders.Resolve(J.Parse("{\"a\":[]}"), "a[3].b") == null);
            });
            test("Epoch milliseconds are recognised as timestamps", () => {
                Require(J.Iso(1790675446000.0).StartsWith("2026-09-29") && J.Iso(1790675446).StartsWith("2026-09-29"));
            });
            test("Quota cache entries round-trip through the serializer", () => {
                var state = new ProviderState { Id = "codex", Status = "ready" }; state.Quotas.Add(new Quota { Label = "每周", Used = 47, WindowSeconds = 604800, ResetUtc = "2026-10-04T02:33:45Z" });
                var back = J.Serializer().Deserialize<List<ProviderState>>(J.Serializer().Serialize(new List<ProviderState> { state }));
                Require(back.Count == 1 && back[0].Quotas[0].Remaining == 53 && back[0].Quotas[0].Label == "每周");
            });
            test("Unpriced local usage remains unknown instead of becoming a zero-dollar claim", () => {
                var idx = new LogIndex(); idx.Files.Add(new LogFile { Hours = { { "2026010102||x", new Bucket { I = 15, R = 1, U = 15 } } } });
                var h = HistoryService.FromIndexes(new Dictionary<string, LogIndex> { { "codex", idx } }, TimeZoneInfo.Utc, new DateTime(2026, 1, 1), 30);
                Require(h.Days.Count == 1 && !h.Days[0].CostKnown && h.Days[0].UnpricedTokens == 15);
            });
            test("Most-used model is ordered by recorded tokens including cache", () => {
                var day = new DayUsage { Day = "2026-01-01", Agent = "codex", Models = { new ModelUsage { Model = "test-a", Tokens = 91 }, new ModelUsage { Model = "test-b", Tokens = 10 } } };
                Require(UsageDetails.MainModel(new[] { day }) == "test-a");
            });
            test("Installed copies use the per-user directory; zip copies and portable.txt stay beside the exe", () => {
                string root = Path.Combine(Path.GetTempPath(), "headroom-data-" + Guid.NewGuid().ToString("N"));
                string user = Path.Combine(root, "userdir"), legacy = Path.Combine(root, "legacy");
                Directory.CreateDirectory(root);
                try {
                    Require(Store.PathsEqual(Store.UserDataDirectory(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Headroom")));
                    Require(!Store.PathsEqual(null, root) && !Store.PathsEqual("  ", root));
                    Require(Store.PathsEqual(root + Path.DirectorySeparatorChar, root));
                    Require(Store.NormalizeDirectory("C:\\") == "C:\\");
                    Require(!Store.TryMigratePortableData(Path.Combine(user, "inside"), user));
                    string beside = Path.Combine(root, "data");
                    Require(Store.PathsEqual(Store.ResolveData(root, "", null, false, user), beside));
                    Require(Store.PathsEqual(Store.ResolveData(root, Path.Combine(root, "other"), null, false, user), beside));
                    Require(Store.PathsEqual(Store.ResolveData(root, root + Path.DirectorySeparatorChar, null, false, user), user));
                    Require(Store.PathsEqual(Store.ResolveData(root, "", null, true, user), user));
                    Require(Store.PathsEqual(Store.ResolveData(root, "", Path.Combine(root, "override"), true, user), Path.Combine(root, "override")));
                    File.WriteAllText(Path.Combine(root, "portable.txt"), "portable");
                    Require(Store.PathsEqual(Store.ResolveData(root, root, Path.Combine(root, "override"), true, user), beside));
                    File.Delete(Path.Combine(root, "portable.txt"));
                    Directory.CreateDirectory(beside);
                    File.WriteAllText(Path.Combine(beside, "settings.json"), "{\"kept\":true}");
                    Directory.CreateDirectory(Path.Combine(beside, "nested"));
                    File.WriteAllText(Path.Combine(beside, "nested", "a.txt"), "a");
                    File.WriteAllText(Path.Combine(root, "installed.txt"), "installed");
                    // The pre-rename per-user folder wins over a data folder beside the exe.
                    Directory.CreateDirectory(legacy);
                    File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"legacy\":true}");
                    string chosen = Store.ChooseDataFor(root, "", null, user, legacy);
                    Require(Store.PathsEqual(chosen, user) && !Directory.Exists(legacy) && Directory.Exists(beside));
                    Require(File.ReadAllText(Path.Combine(user, "settings.json")) == "{\"legacy\":true}");
                    Directory.Delete(user, true);
                    chosen = Store.ChooseDataFor(root, "", null, user, legacy);
                    Require(Store.PathsEqual(chosen, user));
                    Require(File.ReadAllText(Path.Combine(user, "settings.json")) == "{\"kept\":true}");
                    Require(File.ReadAllText(Path.Combine(user, "nested", "a.txt")) == "a");
                    Require(!Directory.Exists(beside));
                    Directory.CreateDirectory(beside);
                    File.WriteAllText(Path.Combine(beside, "settings.json"), "{\"new\":true}");
                    Require(!Store.TryMigratePortableData(beside, user));
                    Require(File.ReadAllText(Path.Combine(user, "settings.json")) == "{\"kept\":true}");
                } finally { try { Directory.Delete(root, true); } catch { } }
            });
            test("A linked data directory is copied and the link is left in place", () => {
                string root = Path.Combine(Path.GetTempPath(), "headroom-link-" + Guid.NewGuid().ToString("N"));
                string real = Path.Combine(root, "real"); string link = Path.Combine(root, "data"); string user = Path.Combine(root, "user");
                Directory.CreateDirectory(real);
                File.WriteAllText(Path.Combine(real, "settings.json"), "{}");
                Process linkProcess = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + real + "\"") { UseShellExecute = false, CreateNoWindow = true });
                linkProcess.WaitForExit();
                Require(linkProcess.ExitCode == 0);
                try {
                    Require(Store.TryMigratePortableData(link, user));
                    Require(File.ReadAllText(Path.Combine(user, "settings.json")) == "{}");
                    Require(Directory.Exists(link) && File.Exists(Path.Combine(real, "settings.json")));
                } finally {
                    try {
                        Process remove = Process.Start(new ProcessStartInfo("cmd.exe", "/c rmdir \"" + link + "\"") { UseShellExecute = false, CreateNoWindow = true });
                        if (remove != null) remove.WaitForExit();
                        Directory.Delete(root, true);
                    } catch { }
                }
            });
            Directory.CreateDirectory(Path.Combine(Store.Root, "verification"));
            File.WriteAllText(Path.Combine(Store.Root, "verification", "tests.json"), J.Serializer().Serialize(new { passed = passed.Count, failed = failed.Count, checks = passed, errors = failed }));
            return failed.Count == 0 ? 0 : 1;
        }
        private static void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
    }
}
