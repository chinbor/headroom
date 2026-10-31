using System;
using System.Linq;

namespace CodeUsageMonit {
    // Shared-quota regressions ported from xing-skyline/codeusagemonit PR #1.
    // Cursor Grok Bot coverage lives with the plan meters and is not repeated here.
    internal static class GrokTests {
        private static void Check(bool condition) { if (!condition) throw new Exception("Feature assertion failed"); }
        internal static void Run(Action<string, Action> test) {
            test("Grok product consumption stays separate from shared remaining quota", () => {
                var s = Parsers.Grok(J.Parse("{\"config\":{\"creditUsagePercent\":63,\"productUsage\":[{\"product\":\"build\",\"usagePercent\":55},{\"product\":\"chat\",\"usagePercent\":8}]}}"));
                Check(s.Quotas.Count == 1 && s.Quotas[0].Remaining == 37);
                var shares = J.Arr(J.Get(J.Parse(J.Serializer().Serialize(s)), "ProductUsage")).ToList();
                Check(shares.Count == 2 && J.Num(shares[0], "UsedPercent") == 55 && J.Num(shares[1], "UsedPercent") == 8);
                Check(!J.Serializer().Serialize(s.Quotas).Contains("Build 占比"));
            });
            test("An inconsistent Grok product breakdown does not invent separate quotas", () => {
                var s = Parsers.Grok(J.Parse("{\"config\":{\"creditUsagePercent\":63,\"productUsage\":[{\"product\":\"build\",\"usagePercent\":80}]}}"));
                Check(s.Quotas.Count == 1 && s.Quotas[0].Remaining == 37);
                Check(!J.Arr(J.Get(J.Parse(J.Serializer().Serialize(s)), "ProductUsage")).Any());
            });
            test("Legacy Grok cache removes the fake Build quota and keeps shared usage", () => {
                var state = J.Serializer().Deserialize<ProviderState>("{\"Id\":\"grok\",\"ProductUsage\":null,\"Quotas\":[{\"Label\":\"当前账期\",\"Used\":63},{\"Label\":\"Build 占比\",\"Used\":55}]}");
                Parsers.Normalize(state);
                Check(state.Quotas.Count == 1 && state.Quotas[0].Remaining == 37 && state.ProductUsage.Count == 0);
                Parsers.Normalize(state);
                Check(state.Quotas.Count == 1);
            });
            test("Malformed and duplicate Grok shares never replace the shared allowance", () => {
                foreach (string rows in new[] { "null", "[{\"product\":\"build\",\"usagePercent\":null}]", "[{\"product\":\"build\",\"usagePercent\":-2}]", "[{\"product\":\"build\",\"usagePercent\":30},{\"product\":\"BUILD\",\"usagePercent\":33}]" }) {
                    var state = Parsers.Grok(J.Parse("{\"config\":{\"creditUsagePercent\":63,\"productUsage\":" + rows + "}}"));
                    Check(state.Quotas.Count == 1 && state.Quotas[0].Remaining == 37 && state.ProductUsage.Count == 0);
                }
                var rounded = Parsers.Grok(J.Parse("{\"config\":{\"creditUsagePercent\":63,\"productUsage\":[{\"product\":\"build\",\"usagePercent\":55},{\"product\":\"chat\",\"usagePercent\":8.5}]}}"));
                Check(rounded.ProductUsage.Count == 2);
                var restored = J.Serializer().Deserialize<ProviderState>(J.Serializer().Serialize(rounded));
                Check(restored.ProductUsage.Count == 2 && restored.ProductUsage[0].UsedPercent == 55 && restored.Quotas.Count == 1);
            });
            test("A failed Grok refresh keeps the shared quota and the consumption breakdown", () => {
                var old = Parsers.Grok(J.Parse("{\"config\":{\"creditUsagePercent\":63,\"productUsage\":[{\"product\":\"build\",\"usagePercent\":55},{\"product\":\"chat\",\"usagePercent\":8}]}}"));
                old.Status = "ready"; old.LastSuccess = "2026-09-30T00:00:00Z";
                var incoming = new ProviderState { Id = "grok", Status = "error", Message = "synthetic timeout" };
                Parsers.RetainLastGood(incoming, old);
                Check(incoming.Stale && incoming.Quotas.Count == 1 && incoming.Quotas[0].Remaining == 37 && incoming.ProductUsage.Count == 2 && incoming.ProductUsage[0].UsedPercent == 55 && incoming.LastSuccess == old.LastSuccess);
                var fresh = new ProviderState { Id = "grok", Status = "ready" };
                Parsers.RetainLastGood(fresh, old);
                Check(fresh.Quotas.Count == 0 && fresh.ProductUsage.Count == 0);
            });
        }
    }
}
