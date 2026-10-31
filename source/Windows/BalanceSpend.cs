using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Headroom {
    // Actual spend derived from successive balance readings (DeepSeek: GET /user/balance with
    // the API key, which does not expire). A drop between two readings is money spent on the
    // day of the later reading; a rise is a top-up and only moves the baseline. Readings start
    // when the program first sees the balance, and a top-up and spending between the same two
    // readings net out, so the figures are a lower bound.
    public sealed class BalanceReading { public string Id = "", Currency = ""; public double Amount; }
    public sealed class SpendDay { public string Day = "", Id = "", Currency = ""; public double Spent, TopUp; }
    public sealed class BalanceLedger {
        public string Since = "";
        public List<BalanceReading> Last = new List<BalanceReading>();
        public List<SpendDay> Days = new List<SpendDay>();
    }
    public static class BalanceSpend {
        public const int KeepDays = 40;
        // Records one successful reading; true when the ledger changed and should be saved.
        public static bool Record(BalanceLedger ledger, string id, IEnumerable<Balance> balances, DateTime localNow) {
            if (ledger == null || balances == null) return false;
            bool changed = false; string today = HistoryService.DayKey(localNow);
            foreach (Balance b in balances) {
                if (b == null || String.IsNullOrEmpty(b.Currency) || Double.IsNaN(b.Amount)) continue;
                BalanceReading last = ledger.Last.FirstOrDefault(r => r.Id == id && r.Currency == b.Currency);
                if (last == null) {
                    ledger.Last.Add(new BalanceReading { Id = id, Currency = b.Currency, Amount = b.Amount });
                    if (ledger.Since.Length == 0) ledger.Since = today;
                    changed = true; continue;
                }
                double delta = b.Amount - last.Amount; if (Math.Abs(delta) < 1e-6) continue;
                SpendDay day = ledger.Days.FirstOrDefault(d => d.Day == today && d.Id == id && d.Currency == b.Currency);
                if (day == null) ledger.Days.Add(day = new SpendDay { Day = today, Id = id, Currency = b.Currency });
                if (delta < 0) day.Spent += -delta; else day.TopUp += delta;
                last.Amount = b.Amount; changed = true;
            }
            string oldest = HistoryService.DayKey(localNow.Date.AddDays(-KeepDays));
            if (ledger.Days.RemoveAll(d => String.CompareOrdinal(d.Day, oldest) < 0) > 0) changed = true;
            return changed;
        }
        // Spend over the last `days` days ending today, per currency.
        public static Dictionary<string, double> Spent(BalanceLedger ledger, string id, DateTime today, int days) {
            var result = new Dictionary<string, double>(); if (ledger == null) return result;
            string from = HistoryService.DayKey(today.Date.AddDays(-(days - 1))), to = HistoryService.DayKey(today.Date);
            foreach (SpendDay d in ledger.Days.Where(d => d.Id == id && String.CompareOrdinal(d.Day, from) >= 0 && String.CompareOrdinal(d.Day, to) <= 0)) {
                double old; result.TryGetValue(d.Currency, out old); result[d.Currency] = old + d.Spent;
            }
            return result;
        }
        // "今日 ¥0.62 · 7 天 ¥3.10 · 30 天 ¥12.00" for the provider's first currency; null before
        // any reading of it exists.
        public static string Summary(BalanceLedger ledger, string id, DateTime today) {
            BalanceReading first = ledger == null ? null : ledger.Last.FirstOrDefault(r => r.Id == id);
            if (first == null) return null;
            Func<int, string> span = n => { double v; Spent(ledger, id, today, n).TryGetValue(first.Currency, out v); return SubscriptionView.Money(new Balance { Currency = first.Currency, Amount = v }); };
            return "今日 " + span(1) + " · 7 天 " + span(7) + " · 30 天 " + span(30);
        }
        public static string Note(BalanceLedger ledger, DateTime today) {
            DateTime since = DateTime.MinValue; bool parsed = ledger != null && DateTime.TryParseExact(ledger.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out since);
            string from = parsed && (today.Date - since).TotalDays < 30 ? "自 " + since.ToString("M/d", CultureInfo.InvariantCulture) + " 起记录，" : "";
            return from + "按余额变化计算，已排除充值";
        }
    }
}
