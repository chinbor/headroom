using System;
using System.IO;
using System.Text;

namespace Headroom {
    // pi coding agent (pi.dev): each session is ~/.pi/agent/sessions/<workspace>/<id>.jsonl
    // (PI_CODING_AGENT_DIR moves the agent folder). An assistant "message" line carries its
    // provider, model, usage (input without cache hits, cacheRead, cacheWrite, output) and
    // durationMs. Only provider "openai" is read (see CodexClients). Message text is never stored.
    public static class PiLogs {
        public static string Root {
            get {
                string dir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
                return String.IsNullOrWhiteSpace(dir) ? Path.Combine(LogReader.Home, ".pi", "agent") : dir.Trim();
            }
        }
        public static string Sessions { get { return Path.Combine(Root, "sessions"); } }
        public static string Auth { get { return Path.Combine(Root, "auth.json"); } }
        private static readonly byte[] Usage = Encoding.ASCII.GetBytes("\"usage\":");

        public static long Read(LogIndex index, LogFile state, string path, Stream stream, long offset) {
            return LogReader.Read(stream, offset, 16 << 20, (bytes, length) => LogReader.Contains(bytes, length, Usage), line => Line(index, state, line));
        }
        internal static void Line(LogIndex index, LogFile state, string line) {
            object root; try { root = J.Parse(line); } catch { return; }
            if (J.Str(root, "type") != "message") return;
            object message = J.Get(root, "message");
            if (J.Str(message, "role") != "assistant" || J.Str(message, "provider") != CodexLogs.Official) return;
            object usage = J.Get(message, "usage"); if (usage == null) return;
            DateTime when; double? ms = J.Num(message, "timestamp");
            if (ms.HasValue) when = LogReader.FromMillis(ms.Value); else if (!LogReader.ParseTime(J.Str(root, "timestamp"), out when)) return;
            // A forked or cloned session copies its entries: the response id counts them once.
            string id = J.Str(message, "responseId"); if (id.Length == 0) id = J.Str(root, "id") + "|" + LogReader.Millis(when);
            CodexClients.Add(index, state, "pi|" + id, when, J.Str(message, "model"),
                LogReader.N(usage, "input"), LogReader.N(usage, "cacheRead"), LogReader.N(usage, "cacheWrite"), LogReader.N(usage, "output"),
                (J.Num(message, "durationMs") ?? 0) / 1000.0);
        }
    }
}
