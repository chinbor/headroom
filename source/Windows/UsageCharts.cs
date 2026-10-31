using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeUsageMonit {
    // Data for the consumption charts in the detail window: per-day values split by agent
    // (stacked bars) and per-agent totals (ranking), as tokens or API-equivalent cost.
    public static class UsageCharts {
        public sealed class Day {
            public DateTime Date;
            public readonly Dictionary<string, double> Parts = new Dictionary<string, double>();
            public double Total { get { return Parts.Values.Sum(); } }
        }
        // The last `days` days ending today, oldest first; days without usage stay as zero bars.
        public static List<Day> Daily(UsageHistory history, DateTime today, int days, bool cost) {
            var result = new List<Day>(); var byKey = new Dictionary<string, Day>();
            for (int i = days - 1; i >= 0; i--) { var d = new Day { Date = today.Date.AddDays(-i) }; result.Add(d); byKey[HistoryService.DayKey(d.Date)] = d; }
            if (history == null || history.Days == null) return result;
            foreach (DayUsage u in history.Days) {
                Day day; if (u == null || String.IsNullOrEmpty(u.Agent) || u.Day == null || !byKey.TryGetValue(u.Day, out day)) continue;
                double value = cost ? u.Cost : u.Tokens;
                if (Double.IsNaN(value) || value <= 0) continue;
                double old; day.Parts.TryGetValue(u.Agent, out old); day.Parts[u.Agent] = old + value;
            }
            return result;
        }
        // Agents by total over the given days, largest first.
        public static List<KeyValuePair<string, double>> Ranking(IEnumerable<Day> days) {
            var totals = new Dictionary<string, double>();
            foreach (Day d in days) foreach (var p in d.Parts) { double old; totals.TryGetValue(p.Key, out old); totals[p.Key] = old + p.Value; }
            return totals.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ToList();
        }
        // Totals over a span, for the summary tiles and the per-platform table.
        public sealed class Totals {
            public string Agent = "";
            public double Tokens, Cost, Requests, Cached, Input, CacheWrite, TimedOutput, TimedSeconds;
            public bool Partial;
            public readonly Dictionary<string, double> Models = new Dictionary<string, double>();
            // Share of input served from cache: cache reads / (fresh input + cache reads + cache writes).
            public double? CacheRate { get { double all = Input + Cached + CacheWrite; return all > 0 ? Cached / all : (double?)null; } }
            public double? Speed { get { return OutputTiming.Speed(TimedOutput, TimedSeconds); } }
            public string TopModel { get { return Models.Count == 0 ? "" : Models.OrderByDescending(m => m.Value).First().Key; } }
            internal void Add(DayUsage u) {
                Tokens += u.Tokens; Cost += Double.IsNaN(u.Cost) ? 0 : u.Cost; Requests += u.Requests; Cached += u.CachedTokens; Input += u.InputTokens; CacheWrite += u.CacheCreationTokens;
                TimedOutput += u.TimedOutput; TimedSeconds += u.TimedSeconds;
                if (!u.CostKnown || u.UnpricedTokens > 0) Partial = true;
                if (u.Models != null) foreach (ModelUsage m in u.Models) { if (m == null || String.IsNullOrEmpty(m.Model)) continue; double old; Models.TryGetValue(m.Model, out old); Models[m.Model] = old + m.Tokens; }
            }
        }
        private static IEnumerable<DayUsage> Span(UsageHistory history, DateTime today, int days) {
            if (history == null || history.Days == null) yield break;
            string from = HistoryService.DayKey(today.Date.AddDays(1 - days)), to = HistoryService.DayKey(today.Date);
            foreach (DayUsage u in history.Days)
                if (u != null && !String.IsNullOrEmpty(u.Agent) && u.Day != null && String.CompareOrdinal(u.Day, from) >= 0 && String.CompareOrdinal(u.Day, to) <= 0) yield return u;
        }
        public static Totals Sum(UsageHistory history, DateTime today, int days) {
            var t = new Totals(); foreach (DayUsage u in Span(history, today, days)) t.Add(u); return t;
        }
        // One row per agent, most tokens first.
        public static List<Totals> ByAgent(UsageHistory history, DateTime today, int days) {
            var rows = new Dictionary<string, Totals>();
            foreach (DayUsage u in Span(history, today, days)) { Totals t; if (!rows.TryGetValue(u.Agent, out t)) rows[u.Agent] = t = new Totals { Agent = u.Agent }; t.Add(u); }
            return rows.Values.Where(t => t.Tokens > 0).OrderByDescending(t => t.Tokens).ToList();
        }
        // Tokens by local hour of day (0–23) over the last `days` days.
        public static double[] Hours(IEnumerable<HourUsage> entries, DateTime today, int days, TimeZoneInfo zone) {
            var hours = new double[24]; DateTime from = today.Date.AddDays(1 - days), to = today.Date.AddDays(1);
            foreach (HourUsage e in entries ?? Enumerable.Empty<HourUsage>()) {
                if (e == null || e.Tokens <= 0) continue;
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Hour, DateTimeKind.Utc), zone);
                if (local >= from && local < to) hours[local.Hour] += e.Tokens;
            }
            return hours;
        }
        public static string Format(double value, bool cost) { return cost ? UsageTotals.Usd(value) : UsageTotals.Tokens(value); }
        public static string Short(DateTime day) { return day.ToString("M/d", CultureInfo.InvariantCulture); }
    }
}
