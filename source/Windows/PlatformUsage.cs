using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeUsageMonit {
    // Daily usage reported by a platform's own console (recipes with a "usage" request, e.g.
    // DeepSeek and Agnes). Where a platform reports its days they replace the local log
    // estimate for that agent: the console also counts requests made from other computers
    // and other tools, and the DeepSeek Harness usage is already part of it.
    public static class PlatformUsage {
        // Local log days of the supported platforms, with each agent that has platform days
        // taken from the platform instead.
        public static UsageHistory Combine(UsageHistory local, IEnumerable<ProviderState> states) {
            if (local == null) return null;
            var platform = (states ?? Enumerable.Empty<ProviderState>()).Where(s => s != null && s.PlatformDays != null && s.PlatformDays.Count > 0).ToList();
            var replaced = new HashSet<string>(platform.Select(s => s.Id));
            var merged = new UsageHistory { Updated = local.Updated, Error = local.Error, Zone = local.Zone, Engine = local.Engine };
            merged.Days.AddRange((local.Days ?? new List<DayUsage>()).Where(d => d != null && ProviderCatalog.IsKnown(d.Agent) && !replaced.Contains(d.Agent)));
            foreach (ProviderState s in platform) merged.Days.AddRange(s.PlatformDays.Where(d => d != null).Select(d => { d.Agent = s.Id; return d; }));
            return merged;
        }
    }
}