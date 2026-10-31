using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeUsageMonit {
    // Shared display semantics for native taskbar cells and the WPF dashboard.
    // Never equate account balance, local token statistics and subscription quota.
    public static class SubscriptionView {
        // One platform's most pressing item. Score is a remaining-percent equivalent: quotas use
        // their remaining %, balances amount / warn × 20, so < 20 is urgent and < 40 tight.
        public sealed class Alert {
            public ProviderState State; public string Window, Value, Key; public double Score;
            public Quota Quota;           // null for balances
            public string Sub = "";       // the notification's second line
            public bool Soon;             // not urgent yet, but runs out within the hour at the current rate
            public bool Urgent { get { return Score < 20; } }
        }
        public sealed class Overview {
            public readonly List<Alert> Alerts = new List<Alert>();
            // Loading: not read yet since the program started (not an error, nothing to show yet).
            public readonly List<ProviderState> Normal = new List<ProviderState>(), Attention = new List<ProviderState>(), Loading = new List<ProviderState>();
            public bool OnlyLoading { get { return Loading.Count > 0 && Alerts.Count + Normal.Count + Attention.Count == 0; } }
            public string Headline {
                get { return Alerts.Count > 0 ? "▲ " + Alerts.Count + " 项告警" : Attention.Count > 0 ? "○ " + Attention.Count + " 项异常" : OnlyLoading ? "正在读取额度…" : "● 全部充足"; }
            }
            // Compact form for the taskbar pill.
            public string Short {
                get { return Alerts.Count > 0 ? "▲ " + Alerts.Count + " 告警" : Attention.Count > 0 ? "○ " + Attention.Count + " 异常" : OnlyLoading ? "读取中" : "● 充足"; }
            }
            public string Tone { get { return Alerts.Any(a => a.Urgent) ? "#FF6A6A" : Alerts.Count > 0 || Attention.Count > 0 ? "#FFB323" : OnlyLoading ? "#8A8F98" : "#25CB77"; } }
        }
        public static double BalanceWarn = 5;
        public static Overview Classify(IEnumerable<ProviderState> input) {
            var result = new Overview();
            foreach (var state in input ?? new ProviderState[0]) {
                if (state == null) continue;
                if (Loading(state)) { result.Loading.Add(state); continue; }
                if (Old(state) || state.Status != "ready" || (state.Quotas.Count == 0 && state.Balances.Count == 0)) { result.Attention.Add(state); continue; }
                Alert worst = Items(state).OrderBy(a => a.Score).FirstOrDefault();
                if (worst != null && worst.Score < 40) result.Alerts.Add(worst); else result.Normal.Add(state);
            }
            result.Alerts.Sort((a, b) => a.Score.CompareTo(b.Score));
            return result;
        }
        // Every window and balance of a platform as alert candidates (healthy ones included).
        public static IEnumerable<Alert> Items(ProviderState state) {
            foreach (Quota q in state.Quotas) {
                if (Double.IsNaN(q.Remaining)) continue;
                yield return new Alert { State = state, Quota = q, Window = AlertWindow(q.Label), Value = q.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%", Score = q.Remaining, Key = state.Id + "|" + q.Label };
            }
            if (BalanceWarn > 0)
                foreach (Balance b in state.Balances)
                    yield return new Alert { State = state, Window = "余额", Value = Money(b), Score = b.Amount / BalanceWarn * 20, Key = state.Id + "|balance|" + b.Currency };
        }
        public static string AlertWindow(string label) {
            label = label ?? "";
            if (label.Contains("图像") || label.Contains("图片")) return "图片";
            if (label.Contains("视频")) return "视频";
            return ShortWindow(label);
        }
        public static string Money(Balance b) {
            return (b.Currency == "CNY" ? "¥" : b.Currency == "USD" ? "$" : b.Currency + " ") + b.Amount.ToString("0.##", CultureInfo.InvariantCulture);
        }
        public static Quota Tightest(ProviderState state) { return state.Quotas.OrderBy(q => q.Remaining).FirstOrDefault(); }
        public static bool Loading(ProviderState state) { return state.Status == "loading" && state.Quotas.Count == 0 && state.Balances.Count == 0; }
        public static bool Old(ProviderState state) { return state.Stale || (state.Status != "ready" && (state.Quotas.Count > 0 || state.Balances.Count > 0)); }
        public static string ShortWindow(string label) {
            label = label ?? "";
            // Whole words, never one-character abbreviations ("图"/"视" read as nothing).
            if (label.Contains("5 小时") || label == "5h") return "5 小时";
            if (label.Contains("每周") || label == "周") return "每周";
            if (label.Contains("图像") || label.Contains("图片")) return "图片";
            if (label.Contains("视频")) return "视频";
            if (label.Contains("每日")) return "每日";
            if (label.Contains("每月")) return "每月";
            return label.Length > 4 ? label.Substring(0, 4) : label;
        }
        public static string Name(string id) { return ProviderCatalog.Name(id); }
        public static string Missing(ProviderState state) {
            return state.Status == "loading" ? "读取中" : state.Status == "expired" ? "授权失效" : state.Status == "error" ? "读取失败" : "未连接";
        }
        public static string Summary(ProviderState state) {
            Quota q = Tightest(state); string value;
            if (q != null) value = ShortWindow(q.Label) + " " + q.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            else if (state.Balances.Count > 0) {
                value = Money(state.Balances[0]);
            } else value = Missing(state);
            return value + (Old(state) ? " 旧" : "");
        }
        public static string Tone(ProviderState state) {
            if (Old(state)) return "#FFBE63";
            var q = Tightest(state);
            if (q != null) return q.Remaining < 20 ? "#FF6767" : q.Remaining < 40 ? "#FFB323" : "#25CB77";
            return state.Status == "ready" && state.Balances.Count > 0 ? "#AAB9D0" : "#929CAB";
        }
        public static string Reset(string utc, DateTime now) {
            DateTime time;
            if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) return "重置时间未知";
            TimeSpan left = time.ToUniversalTime() - now.ToUniversalTime();
            if (left.TotalSeconds <= 0) return "待刷新";
            return (left.TotalDays >= 1 ? (int)left.TotalDays + "天" + left.Hours + "时" : left.TotalHours >= 1 ? (int)left.TotalHours + "时" + left.Minutes + "分" : Math.Max(1, (int)left.TotalMinutes) + "分") + "后重置";
        }
        public static string Tooltip(ProviderState state, DateTime now) {
            var text = new StringBuilder(Name(state.Id));
            if (!String.IsNullOrEmpty(state.Plan)) text.Append(" · ").Append(state.Plan);
            if (Old(state)) text.Append("\n旧读数 · 当前读取未成功，不代表实时额度");
            foreach (var q in state.Quotas) {
                text.Append("\n").Append(q.Label).Append("  剩余 ").Append(q.Remaining.ToString("0.#", CultureInfo.InvariantCulture)).Append("% · ").Append(Reset(q.ResetUtc, now));
                if (q.UsedCount.HasValue && q.LimitCount.HasValue) text.Append(" · 已用 ").Append(q.UsedCount.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append('/').Append(q.LimitCount.Value.ToString("0.#", CultureInfo.InvariantCulture));
            }
            foreach (var b in state.Balances) text.Append("\n余额 ").Append(b.Currency).Append(' ').Append(b.Amount.ToString("0.##", CultureInfo.InvariantCulture));
            if (state.Quotas.Count == 0 && state.Balances.Count == 0) text.Append("\n").Append(Missing(state)).Append(" · 请打开订阅管理检查连接");
            DateTime at;
            if (DateTime.TryParse(state.LastSuccess, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) text.Append("\n更新于 ").Append(at.ToLocalTime().ToString("M/d HH:mm"));
            text.Append("\n点击展开详情");
            return text.ToString();
        }
    }
    // Remembers which windows / balances are urgent; Update returns the ones that just became
    // urgent. Stale and unavailable readings never count, so a failed read cannot alert.
    public sealed class AlertWatch {
        private readonly HashSet<string> urgent = new HashSet<string>();
        public List<SubscriptionView.Alert> Update(IEnumerable<ProviderState> states) { return Update(states, DateTime.UtcNow); }
        // The second line for an item: when it runs out or resets, or the balance warning line.
        public static string Describe(SubscriptionView.Alert a, DateTime nowUtc) {
            if (a.Quota == null) return "低于告警线 " + SubscriptionView.BalanceWarn.ToString("0.##", CultureInfo.InvariantCulture);
            WindowPace pace = Pace.Of(a.Quota, nowUtc);
            if (pace == null) return "";
            if (pace.Short || a.Score <= 0) return Pace.Note(pace, a.Score);
            return Pace.Span(pace.Left) + "后重置，照现在的速度撑得到";
        }
        // Urgent items, plus windows that run out within the hour at the current rate (a separate
        // key, so the same window alerts again once it actually turns urgent).
        public List<SubscriptionView.Alert> Update(IEnumerable<ProviderState> states, DateTime nowUtc) {
            var now = new List<SubscriptionView.Alert>();
            foreach (var state in states ?? new ProviderState[0]) {
                if (state == null || SubscriptionView.Old(state) || state.Status != "ready") continue;
                foreach (var a in SubscriptionView.Items(state)) {
                    WindowPace pace = Pace.Of(a.Quota, nowUtc);
                    bool soon = !a.Urgent && pace != null && pace.Short && a.Score > 0 && pace.RunOut.Value.TotalMinutes <= 60;
                    if (!a.Urgent && !soon) continue;
                    if (soon) { a.Soon = true; a.Key += "|soon"; a.Sub = "预计 " + Pace.Span(pace.RunOut.Value) + "后用完，" + Pace.Span(pace.Left) + "后才重置"; }
                    else a.Sub = Describe(a, nowUtc);
                    now.Add(a);
                }
            }
            var fresh = now.Where(a => !urgent.Contains(a.Key)).OrderBy(a => a.Score).ToList();
            urgent.Clear(); foreach (var a in now) urgent.Add(a.Key);
            return fresh;
        }
    }
    // Local-log token totals and their API-equivalent cost (list prices, not a bill) for the
    // flyout header. Days come from UsageHistory, keyed yyyy-MM-dd in local time.
    public sealed class UsageTotals {
        public double TodayTokens, TodayCost, WeekTokens, WeekCost, MonthTokens, MonthCost;
        // Some tokens had no known price, so the cost is a lower bound.
        public bool Partial;
        public static UsageTotals From(UsageHistory history, DateTime today) {
            var totals = new UsageTotals();
            if (history == null || history.Days == null) return totals;
            string day = HistoryService.DayKey(today), week = HistoryService.DayKey(today.AddDays(-6)), month = HistoryService.DayKey(today.AddDays(-29));
            foreach (DayUsage d in history.Days) {
                if (d == null || String.IsNullOrEmpty(d.Day) || String.CompareOrdinal(d.Day, month) < 0 || String.CompareOrdinal(d.Day, day) > 0) continue;
                totals.MonthTokens += d.Tokens; totals.MonthCost += d.Cost;
                if (String.CompareOrdinal(d.Day, week) >= 0) { totals.WeekTokens += d.Tokens; totals.WeekCost += d.Cost; }
                if (d.Day == day) { totals.TodayTokens += d.Tokens; totals.TodayCost += d.Cost; }
                if (!d.CostKnown || d.UnpricedTokens > 0) totals.Partial = true;
            }
            return totals;
        }
        public static string Tokens(double n) {
            var c = CultureInfo.InvariantCulture;
            return n >= 1e9 ? (n / 1e9).ToString("0.##", c) + "B" : n >= 1e6 ? (n / 1e6).ToString("0.##", c) + "M" : n >= 1e3 ? (n / 1e3).ToString("0.#", c) + "K" : n.ToString("0", c);
        }
        public static string Usd(double value) { return "$" + value.ToString(value < 100 ? "0.00" : "N0", CultureInfo.InvariantCulture); }
    }
}
