using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Headroom {
    // Third-party agents signed in with the ChatGPT account draw on the same subscription as
    // Codex, so their OpenAI requests count under Codex (provider "openai", like official Codex).
    // A client counts only while its auth.json says OpenAI is an "oauth" login: an API key is
    // billed separately. Only that type field is read, never the tokens.
    // To add a client: write its reader (JSONL files: Folder + Read; a database: Refresh) and list it here.
    public sealed class CodexClient {
        public string Name, Auth;
        public Func<string> Folder;                                           // JSONL sessions, read incrementally
        public Func<LogIndex, LogFile, string, Stream, long, long> Read;
        public Action<LogIndex, LogFile, DateTime> Refresh;                   // anything else, reread when changed
        public Func<string> Watch;                                            // folder that changes while it works
        public bool SignedIn() {
            try { return File.Exists(Auth) && J.Str(J.File(Auth), "openai", "type") == "oauth"; } catch { return false; }
        }
        public bool Owns(string path) {
            if (Folder == null) return false;
            return path.StartsWith(Store.NormalizeDirectory(Folder()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        // Rollout files are keyed by bare file name; a client's files and state carry its name.
        public string Key(string path) { return Name + "|" + path.ToLowerInvariant(); }
    }

    public static class CodexClients {
        public static IEnumerable<CodexClient> All {
            get {
                yield return new CodexClient { Name = "pi", Auth = PiLogs.Auth, Folder = () => PiLogs.Sessions, Read = PiLogs.Read, Watch = () => PiLogs.Sessions };
                yield return new CodexClient { Name = "opencode", Auth = OpenCodeLogs.Auth, Refresh = OpenCodeLogs.Refresh, Watch = () => OpenCodeLogs.Root };
            }
        }
        public static List<CodexClient> SignedIn() { return All.Where(c => c.SignedIn()).ToList(); }

        // Codex's scan with the clients' sessions beside the rollout files.
        public static LogIndex Scan(LogIndex index, DateTime nowUtc, IEnumerable<string> rollouts, Func<LogIndex, LogFile, string, Stream, long, long> codex) {
            List<CodexClient> clients = SignedIn();
            var paths = rollouts.ToList();
            foreach (CodexClient c in clients.Where(c => c.Folder != null)) paths.AddRange(LogReader.Files(c.Folder()));
            Func<string, CodexClient> owner = path => clients.FirstOrDefault(c => c.Owns(path));
            var databases = clients.Where(c => c.Refresh != null).ToList();
            // Database states stay between scans even without usage, so an unchanged database is not queried again.
            index = LogReader.Scan(index, paths, info => { CodexClient c = owner(info.FullName); return c == null ? info.Name : c.Key(info.FullName); }, nowUtc,
                (idx, state, path, stream, offset) => { CodexClient c = owner(path); return c == null ? codex(idx, state, path, stream, offset) : c.Read(idx, state, path, stream, offset); },
                databases.Select(c => c.Name + "|database"));
            foreach (CodexClient c in databases) {
                string name = c.Name + "|database";
                LogFile state = index.Files.FirstOrDefault(f => f.Name == name);
                if (state == null) { state = new LogFile { Name = name }; index.Files.Add(state); }
                try { c.Refresh(index, state, nowUtc); } catch { }
            }
            index.InvalidateEntries();
            return index;
        }

        // One model request: input without cache hits, cache read / write, output (reasoning included).
        internal static void Add(LogIndex index, LogFile state, string key, DateTime when, string model, double input, double read, double write, double output, double seconds) {
            if (input + read + write + output <= 0 || index.Seen.ContainsKey(key)) return;
            LogIndex.See(index, key, when, state);
            model = (model ?? "").Trim(); if (model.Length == 0) model = "gpt-5";
            ModelPrice price = Pricing.Find(model);
            var bucket = new Bucket { I = input, C = read, W = write, O = output, R = 1, D = Pricing.CodexCost(price, input + read + write, read, write, output) };
            if (price == null) bucket.U = bucket.Tokens();
            if (OutputTiming.Accept(output, seconds)) { bucket.TN = 1; bucket.TO = output; bucket.TS = seconds; }
            LogIndex.Add(state, when, CodexLogs.Official, model, bucket);
        }
    }
}
