using System;
using System.IO;
using System.Linq;

namespace Headroom {
    // opencode (opencode.ai): messages live in ~/.local/share/opencode/opencode.db (XDG_DATA_HOME
    // moves it), table "message", one JSON object per row. An assistant row carries providerID,
    // modelID, tokens (input without cache hits, output, reasoning, cache.read / cache.write) and
    // time.created / time.completed (epoch ms). Only providerID "openai" is read (see CodexClients).
    // Rows are rewritten while a reply streams, so the database is read whole whenever it changed.
    public static class OpenCodeLogs {
        public static string Root {
            get {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                return Path.Combine(String.IsNullOrWhiteSpace(data) ? Path.Combine(LogReader.Home, ".local", "share") : data.Trim(), "opencode");
            }
        }
        public static string Database { get { return Path.Combine(Root, "opencode.db"); } }
        public static string Auth { get { return Path.Combine(Root, "auth.json"); } }

        public static void Refresh(LogIndex index, LogFile state, DateTime nowUtc) {
            string db = Database;
            long stamp = 0;
            foreach (string path in new[] { db, db + "-wal" }) { try { if (File.Exists(path)) stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(path).Ticks); } catch { } }
            if (stamp == 0 || stamp == state.Stamp) return;
            long since = LogReader.Millis(nowUtc.AddDays(-LogReader.HorizonDays));
            var rows = NativeCredentials.SqliteRows(db, "SELECT id, data FROM message WHERE time_created >= " + since + " AND data LIKE '%\"providerID\":\"openai\"%' ORDER BY time_created DESC");
            LogReader.Reset(index, state);
            foreach (string[] row in rows) Row(index, state, row[0], row[1]);
            state.Stamp = stamp;
        }
        internal static void Row(LogIndex index, LogFile state, string id, string data) {
            object root; try { root = J.Parse(data); } catch { return; }
            if (J.Str(root, "role") != "assistant" || J.Str(root, "providerID") != CodexLogs.Official) return;
            object tokens = J.Get(root, "tokens"); if (tokens == null) return;
            double? created = J.Num(root, "time", "created"), completed = J.Num(root, "time", "completed");
            if (!completed.HasValue) return; // still streaming: counted once it finishes
            DateTime when = LogReader.FromMillis(completed.Value);
            // Reasoning is reported beside output; Codex counts it inside output.
            CodexClients.Add(index, state, "opencode|" + id, when, J.Str(root, "modelID"),
                LogReader.N(tokens, "input"), LogReader.N(J.Get(tokens, "cache"), "read"), LogReader.N(J.Get(tokens, "cache"), "write"),
                LogReader.N(tokens, "output") + LogReader.N(tokens, "reasoning"),
                created.HasValue ? (completed.Value - created.Value) / 1000.0 : 0);
        }
    }
}
