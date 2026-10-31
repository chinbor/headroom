using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Headroom {
    // Keeping a sign-in usable without taking it over: when an access token has expired, the
    // platform's own CLI is run in the background so that it refreshes and saves its tokens
    // itself; this program then reads them again. Refresh tokens are never used or written here.
    public static class SessionRecovery {
        private static readonly Dictionary<string, DateTime> lastTry = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // At most one recovery per platform in this window, so a real sign-out does not start a
        // CLI on every refresh.
        private static readonly TimeSpan Spacing = TimeSpan.FromMinutes(10);
        internal static bool MayTry(string platform, DateTime nowUtc) {
            lock (lastTry) {
                DateTime last; if (lastTry.TryGetValue(platform, out last) && nowUtc - last < Spacing) return false;
                lastTry[platform] = nowUtc; return true;
            }
        }
        internal static void ResetSpacing() { lock (lastTry) lastTry.Clear(); }

        // ── Claude Code ───────────────────────────────────────────────────
        // Its access token lasts about 8 hours and is renewed only when the claude CLI runs; the
        // desktop app keeps a sign-in of its own. Starting `claude` interactively in a hidden
        // console renews it within seconds; nothing is typed and no request is sent to a model.
        public static bool ClaudeExpired(object credentials, DateTime nowUtc) {
            double? expires = J.Num(credentials, "claudeAiOauth", "expiresAt");
            return expires.HasValue && LogReader.FromMillis(expires.Value) <= nowUtc.AddMinutes(1);
        }
        public static async Task<bool> RenewClaude(string credentialsPath, DateTime nowUtc) {
            if (!MayTry("claude", nowUtc)) return false;
            string claude = BackgroundCli.Find("claude"); if (claude == null) return false;
            DateTime before = File.Exists(credentialsPath) ? File.GetLastWriteTimeUtc(credentialsPath) : DateTime.MinValue;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return await BackgroundCli.RunUntil("claude", claude, "", home, () => {
                try { return File.Exists(credentialsPath) && File.GetLastWriteTimeUtc(credentialsPath) > before && !ClaudeExpired(J.File(credentialsPath), DateTime.UtcNow); } catch { return false; }
            }, 25).ConfigureAwait(false);
        }
    }
}
