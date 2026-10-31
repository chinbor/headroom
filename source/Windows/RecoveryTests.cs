using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Headroom {
    // Session recovery (SessionRecovery, BackgroundCli) and the platforms offered.
    internal static class RecoveryTests {
        private static void Check(bool condition) { if (!condition) throw new Exception("Feature assertion failed"); }
        private static readonly DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        private static double Unix(DateTime utc) { return (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        internal static void Run(Action<string, Action> test) {
            // ── Claude Code ──
            test("Claude token expiry is read from expiresAt (ms) with a minute of margin", () => {
                Check(SessionRecovery.ClaudeExpired(J.Parse("{\"claudeAiOauth\":{\"expiresAt\":" + (Unix(Now.AddSeconds(30)) * 1000) + "}}"), Now));
                Check(!SessionRecovery.ClaudeExpired(J.Parse("{\"claudeAiOauth\":{\"expiresAt\":" + (Unix(Now.AddHours(3)) * 1000) + "}}"), Now));
                // No expiry recorded: not treated as expired (the usage request decides).
                Check(!SessionRecovery.ClaudeExpired(J.Parse("{\"claudeAiOauth\":{\"accessToken\":\"x\"}}"), Now));
            });
            test("Renewal is not repeated within its spacing window", () => {
                // (The gate itself: a real renewal would start the user's own claude CLI.)
                SessionRecovery.ResetSpacing();
                Check(SessionRecovery.MayTry("selftest", Now) && !SessionRecovery.MayTry("selftest", Now.AddMinutes(5)) && SessionRecovery.MayTry("selftest", Now.AddMinutes(11)));
                Check(SessionRecovery.MayTry("selftest-other", Now.AddMinutes(5)));
                SessionRecovery.ResetSpacing();
            });
            test("Renewal is on by default, also for settings saved before it existed", () => {
                Check(new AppConfig().RenewSessions);
                Check(J.Serializer().Deserialize<AppConfig>("{\"RefreshMinutes\":5}").RenewSessions);
                Check(!J.Serializer().Deserialize<AppConfig>("{\"RenewSessions\":false}").RenewSessions);
            });

            // ── Background CLI ──
            test("Executables are found on PATH with their extension", () => {
                string found = BackgroundCli.Find("cmd"); Check(found != null && found.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
                Check(BackgroundCli.Find("headroom-no-such-cli") == null);
            });

            // ── Platforms ──
            test("Only Claude, Codex, DeepSeek and Agnes are offered for now", () => {
                var offered = ProviderCatalog.All.Where(id => ProviderCatalog.Ids.Contains(id) || ProviderCatalog.Custom[id].Builtin).OrderBy(id => id).ToArray();
                Check(String.Join(",", offered) == "agnes,claude,codex,deepseek" && !ProviderCatalog.IsKnown("cursor") && ProviderCatalog.DefaultEnabled.SequenceEqual(new[] { "claude", "codex" }));
            });
        }
    }
}
