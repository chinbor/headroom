using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Headroom {
    // Where a quota window stands against time: how much of the window has passed, how much
    // would be left if it were used evenly (the pace mark on the bar), and when it runs out
    // at the rate used so far. Only for windows with a known length and reset time.
    public sealed class WindowPace {
        public double Elapsed;          // share of the window already passed, 0..1
        public double Even;             // remaining % if used evenly up to now
        public TimeSpan Left;           // until reset
        public TimeSpan? RunOut;        // until 0% at the rate so far; null when nothing is used yet
        public bool Short { get { return RunOut.HasValue && RunOut.Value < Left; } }
    }
    public static class Pace {
        public static WindowPace Of(Quota q, DateTime nowUtc) {
            if (q == null || q.WindowSeconds <= 0 || Double.IsNaN(q.Used)) return null;
            DateTime reset; if (!DateTime.TryParse(q.ResetUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out reset)) return null;
            double left = (reset.ToUniversalTime() - nowUtc).TotalSeconds; if (left <= 0 || left > q.WindowSeconds * 1.05) return null;
            double elapsed = Math.Max(0, q.WindowSeconds - left);
            var p = new WindowPace { Elapsed = Math.Min(1, elapsed / q.WindowSeconds), Left = TimeSpan.FromSeconds(left) };
            p.Even = (1 - p.Elapsed) * 100;
            // Too early in the window, a few minutes of use would project nonsense.
            if (q.Used > 0 && elapsed >= Math.Min(600, q.WindowSeconds * .02)) p.RunOut = TimeSpan.FromSeconds(Math.Max(0, q.Remaining) / (q.Used / elapsed));
            return p;
        }
        public static string Span(TimeSpan t) {
            return t.TotalDays >= 1 ? (int)t.TotalDays + " 天 " + t.Hours + " 小时" : t.TotalHours >= 1 ? (int)t.TotalHours + " 小时 " + t.Minutes + " 分" : Math.Max(1, (int)Math.Round(t.TotalMinutes)) + " 分钟";
        }
        // Compact form for narrow cells: "2天23时", "1时29分", "29分".
        public static string Compact(TimeSpan t) { return t.TotalDays >= 1 ? (int)t.TotalDays + "天" + t.Hours + "时" : t.TotalHours >= 1 ? (int)t.TotalHours + "时" + t.Minutes + "分" : Math.Max(1, (int)Math.Round(t.TotalMinutes)) + "分"; }
        // One line under a window: "预计 40 分钟后用完" when the rate outruns the reset.
        public static string Note(WindowPace p, double remaining) {
            if (p == null) return null;
            if (remaining <= 0) return "已用完，" + Span(p.Left) + "后重置";
            if (p.Short) return "预计 " + Span(p.RunOut.Value) + "后用完";
            if (remaining < 40) return "剩得不多，照现在的速度撑得到重置";
            return remaining >= p.Even ? "用得比均速慢，够用" : "略快于均速，仍撑得到重置";
        }
        // The single most pressing item, for the taskbar's two-line focus: the window that runs
        // out soonest at the current rate, else the lowest remaining (or balance score).
        public sealed class Focus {
            public ProviderState State; public string Title = "", Value = "", Sub = ""; public double? Score; public bool Alert, Loading;
        }
        public static Focus Primary(IEnumerable<ProviderState> states, DateTime nowUtc) {
            var list = (states ?? Enumerable.Empty<ProviderState>()).Where(s => s != null).ToList();
            if (list.Count == 0) return null;
            var live = list.Where(s => !SubscriptionView.Old(s) && s.Status == "ready").ToList();
            var windows = live.SelectMany(s => s.Quotas.Where(q => !Double.IsNaN(q.Remaining)).Select(q => new { State = s, Quota = q, Pace = Of(q, nowUtc) })).ToList();
            var running = windows.Where(w => w.Pace != null && w.Pace.Short && w.Quota.Remaining > 0).OrderBy(w => w.Pace.RunOut.Value).FirstOrDefault();
            var lowest = windows.OrderBy(w => w.Quota.Remaining).FirstOrDefault();
            var balance = SubscriptionView.BalanceWarn > 0 ? live.SelectMany(s => s.Balances.Select(b => new { State = s, Balance = b, Score = b.Amount / SubscriptionView.BalanceWarn * 20 })).OrderBy(b => b.Score).FirstOrDefault() : null;
            if (running == null && balance != null && (lowest == null || balance.Score < lowest.Quota.Remaining))
                return new Focus { State = balance.State, Title = SubscriptionView.Name(balance.State.Id) + " 余额", Value = SubscriptionView.Money(balance.Balance), Score = balance.Score, Sub = "余额" + (balance.Score < 20 ? "告急" : balance.Score < 40 ? "偏紧" : "充足"), Alert = balance.Score < 40 };
            var pick = running ?? lowest;
            if (pick == null) {
                // Nothing read yet since the program started: say so, without alarm.
                if (list.All(s => live.Contains(s) || SubscriptionView.Loading(s))) {
                    ProviderState first = list.FirstOrDefault(SubscriptionView.Loading) ?? list[0];
                    return new Focus { State = first, Title = SubscriptionView.Name(first.Id), Sub = "正在读取额度…", Loading = true };
                }
                ProviderState odd = list.FirstOrDefault(s => !live.Contains(s) && !SubscriptionView.Loading(s)) ?? list[0];
                return new Focus { State = odd, Title = SubscriptionView.Name(odd.Id), Sub = SubscriptionView.Old(odd) ? "旧读数" : "读取异常", Alert = true };
            }
            var f = new Focus { State = pick.State, Title = SubscriptionView.Name(pick.State.Id) + " " + SubscriptionView.AlertWindow(pick.Quota.Label), Value = pick.Quota.Remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%", Score = pick.Quota.Remaining };
            if (pick == running) { f.Sub = "预计 " + Compact(pick.Pace.RunOut.Value) + "后用完"; f.Alert = true; }
            else if (pick.Quota.Remaining <= 0) { f.Sub = "已用完"; f.Alert = true; }
            else { DateTime reset; f.Sub = DateTime.TryParse(pick.Quota.ResetUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out reset) && reset.ToUniversalTime() > nowUtc ? Compact(reset.ToUniversalTime() - nowUtc) + "后重置" : "重置时间未知"; }
            return f;
        }
        // A platform's tightest remaining share (balances as score); null when nothing is known.
        public static double? Score(ProviderState s) {
            if (s == null || SubscriptionView.Old(s) || s.Status != "ready") return null;
            var items = SubscriptionView.Items(s).ToList(); return items.Count == 0 ? (double?)null : items.Min(i => i.Score);
        }
        // The overview's one-sentence conclusion.
        public static string Headline(IEnumerable<ProviderState> states, DateTime nowUtc) {
            var list = (states ?? Enumerable.Empty<ProviderState>()).Where(s => s != null).ToList();
            var overview = SubscriptionView.Classify(list);
            var shortest = list.Where(s => !SubscriptionView.Old(s)).SelectMany(s => s.Quotas.Select(q => new { State = s, Quota = q, Pace = Of(q, nowUtc) }))
                .Where(x => x.Pace != null && x.Pace.Short && x.Quota.Remaining > 0).OrderBy(x => x.Pace.RunOut.Value).FirstOrDefault();
            if (overview.OnlyLoading) return "正在读取额度…";
            int attention = overview.Alerts.Count + overview.Attention.Count;
            string head = attention == 0 ? "全部充足" : attention + " 项需要注意";
            if (shortest != null) return head + " · " + SubscriptionView.Name(shortest.State.Id) + " " + SubscriptionView.AlertWindow(shortest.Quota.Label) + " 预计 " + Span(shortest.Pace.RunOut.Value) + "后用完";
            if (overview.Alerts.Count > 0) { var a = overview.Alerts[0]; return head + " · " + SubscriptionView.Name(a.State.Id) + " " + a.Window + " 剩 " + a.Value; }
            if (overview.Attention.Count > 0) return head + " · " + SubscriptionView.Name(overview.Attention[0].Id) + " 读取异常";
            return list.Count == 0 ? "还没有订阅" : head + "，照现在的速度都撑得到重置";
        }
    }
}
