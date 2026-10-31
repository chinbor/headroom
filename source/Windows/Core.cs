// Windows provider adapters, written for this project after studying how CodexBar (MIT)
// and CodeZeno/Claude-Code-Usage-Monitor (MIT) talk to each provider; no code is shared.
// See THIRD-PARTY-NOTICES.md. Credentials remain in their owning applications.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Headroom {
    public sealed class Quota {
        public string Label, ResetUtc;
        public double WindowSeconds;
        public double Used;
        public double? UsedCount, LimitCount;
        public double Remaining { get { return Math.Max(0, 100 - Used); } }
    }
    public sealed class Balance { public string Currency; public double Amount; }
    public sealed class ProductConsumption {
        public string Product;
        // Percentage points taken from the shared allowance, not a quota of its own.
        public double UsedPercent;
        [ScriptIgnore]
        public string DisplayName {
            get {
                if (String.IsNullOrEmpty(Product)) return "";
                if (Product.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0) return "Build";
                if (Product.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0) return "Chat";
                return Product;
            }
        }
    }
    public sealed class ProviderState {
        public string Id, Status = "loading", Message = "等待首次读取", Plan = "", Account = "", LastSuccess = "", LastAttempt = "";
        public List<Quota> Quotas = new List<Quota>();
        public List<Balance> Balances = new List<Balance>();
        public List<ProductConsumption> ProductUsage = new List<ProductConsumption>();
        // Daily usage from the platform's own console (PlatformUsage); replaces the local estimate.
        public List<DayUsage> PlatformDays = new List<DayUsage>();
        // The console token behind PlatformDays: "" not used, "ok", "expired" or "error".
        public string PlatformAuth = "";
        public bool Stale;
        public int? ResetCreditsAvailable;
        public List<string> ResetCreditExpiries = new List<string>();
        public string ResetCreditsUpdated = "", ResetCreditsError = "";
    }
    public sealed class ModelUsage { public string Model; public double Tokens, Cost, TimedOutput, TimedSeconds; }
    public sealed class DayUsage {
        public string Day, Agent;
        // Tokens = fresh input + output + cache read + cache write (+ other counted tokens).
        // CachedTokens is the cache-read subset; InputTokens excludes cached input.
        public double Cost, Tokens, CachedTokens, InputTokens, OutputTokens, CacheCreationTokens, Requests, UnpricedTokens;
        // Requests with a measured duration: output tokens / seconds = output speed (OutputTiming).
        public double TimedRequests, TimedOutput, TimedSeconds;
        public bool CostKnown = true;
        public List<ModelUsage> Models = new List<ModelUsage>();
    }
    public sealed class UsageHistory {
        public string Updated = "", Error = "", Zone = "", Engine = "";
        public List<DayUsage> Days = new List<DayUsage>();
    }
    public sealed class AppConfig {
        public string Proxy = "auto";
        public int RefreshMinutes = 5;
        public bool PinWindow = false;
        // Opt-in overlay in verified free space on the primary horizontal taskbar.
        public bool TaskbarPanel = true;
        // Balances below this are urgent, below twice this tight (0 turns balance alerts off).
        public double BalanceWarn = 5;
        // How new alerts are shown: "card" (the app's own card), "system" (tray balloon) or "off".
        public string AlertStyle = "card";
        public string[] SubscriptionOrder = new string[0];
        public bool SimplifiedMode = true;
        public bool HideAccounts = true;
        public string[] Enabled = ProviderCatalog.DefaultEnabled;
        public int UiVersion;
        public bool AlwaysOnTop = false;
        public bool HideOnDeactivate = false;
        public double? WindowLeft, WindowTop;
        public double WindowWidth = 420, WindowHeight = 790, UiScale = 1;
        public string Material = "acrylic";
        public bool ShowThirdParty = true;
        // Fetch the repository's pricing.json once a day (Pricing.SyncAsync).
        public bool PriceSync = true;
        
        // Alpha of the smoke layer over Acrylic/Mica (0–1). Settings show it as
        // transparency = 1 − alpha, so 0% transparency is fully opaque.
        public double SurfaceOpacity = .66;
        // Colour of that layer (#RRGGBB); text stays light, so dark colours read best.
        public string SurfaceColor = "#15171B";
        // Optional background picture: a file in data/ (background.*), how it fills the
        // window (fill | fit | tile) and how much it is dimmed for legibility (0–0.85).
        public string BackgroundImage = "", BackgroundFit = "fill";
        public double BackgroundDim = .35;
        // One window, four sizes: "small" | "medium" | "large" | "full" (the panel).
        public string DisplaySize = "full";
        // Page shown by the compact sizes: "overview" or a provider id.
        public string CompactProvider = "overview";
        // Position of the compact sizes before each size kept its own geometry (read once).
        public double? CompactLeft, CompactTop;
        // Size and position of each compact size; the full panel uses WindowLeft/Top/Width/Height.
        public Dictionary<string, WindowGeometry> Layouts = new Dictionary<string, WindowGeometry>();
        // Interface language: "auto" (follows Windows) | "zh" | "en".
        public string Language = "auto";
        // Automatic backups (Backups in DataSync.cs): every BackupHours (0 = off), keeping the
        // newest BackupKeep automatic ones, in BackupFolder (empty = data\backups).
        public int BackupHours = 24, BackupKeep = 7;
        public string BackupFolder = "";
        // WebDAV sync of usage between devices: folder URL, user name (the password is a
        // DPAPI key, "webdav"), and how often to sync automatically (0 = only on demand).
        public string WebDavUrl = "", WebDavUser = "";
        public int SyncMinutes = 0;
        // Look for a newer release on GitHub when the app starts and once a day, and offer it
        // (UpdatePrompt.cs) unless it is SkippedVersion.
        public bool UpdateCheck = true;
        public string SkippedVersion = "";
        // Per-platform refresh (Pacing.cs): a platform whose client is writing its logs is read at
        // least every InUseMinutes; ProviderMinutes holds platforms with their own interval.
        public bool FastInUse = true;
        public int InUseMinutes = 2;
        public Dictionary<string, int> ProviderMinutes = new Dictionary<string, int>();
        // An expired Claude Code sign-in is renewed by briefly running its own CLI out of sight
        // (SessionRecovery); off = only report that it has expired.
        public bool RenewSessions = true;
    }
    public sealed class WindowGeometry { public double Width, Height; public double? Left, Top; }
    public static class AppInfo {
        public const string ShortVersion = "1.0";
        public const string UserAgent = "headroom/" + ShortVersion;
        // Full version from the assembly ("1.0.0").
        public static string Version { get { System.Version v = typeof(AppInfo).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); } }
    }
    public static class ProviderCatalog {
        // Platforms read by code of their own. The others are platform recipes (CustomProviders.cs):
        // built-in ones from platforms/*.json (DeepSeek, Agnes) and the user's own.
        public static readonly string[] Ids = { "codex", "claude" };
        public static readonly string[] DefaultEnabled = { "claude", "codex" };
        // Not a quota provider: a page of usage that went through third-party endpoints.
        public const string ThirdParty = "thirdparty";
        // Platform recipes by id: built-in ("deepseek", "agnes") and the user's ("custom-…").
        public static readonly Dictionary<string, CustomProvider> Custom = Recipes(new CustomProvider[0]);
        private static Dictionary<string, CustomProvider> Recipes(IEnumerable<CustomProvider> user) {
            var all = new Dictionary<string, CustomProvider>();
            foreach (CustomProvider p in CustomProviders.Builtin().Concat(user)) if (!all.ContainsKey(p.Id) && !Ids.Contains(p.Id)) all[p.Id] = p;
            return all;
        }
        // Replaces the user recipes; the built-in ones always stay.
        public static void Use(IEnumerable<CustomProvider> user) { var all = Recipes(user.Where(p => !p.Builtin)); Custom.Clear(); foreach (var pair in all) Custom[pair.Key] = pair.Value; }
        public static IEnumerable<string> All { get { return Ids.Concat(Custom.Keys.OrderBy(k => k, StringComparer.Ordinal)); } }
        public static bool IsKnown(string id) { return Ids.Contains(id) || Custom.ContainsKey(id); }
        public static string Name(string id) {
            CustomProvider custom; if (id != null && Custom.TryGetValue(id, out custom)) return custom.Name;
            switch (id) { case "codex": return "Codex"; case "claude": return "Claude"; case "deepseek": return "DeepSeek"; case "agnes": return "Agnes"; case ThirdParty: return "第三方"; default: return "概览"; }
        }
        public static string Color(string id) {
            CustomProvider custom; if (id != null && Custom.TryGetValue(id, out custom)) return custom.Color.Length > 0 ? custom.Color : "#A9B4C6";
            switch (id) { case "codex": return "#63D5E5"; case "claude": return "#E8AB8C"; case "deepseek": return "#7FA9FF"; case ThirdParty: return "#9DB4CF"; default: return "#8CB9FF"; }
        }
        public static string Help(string id) {
            CustomProvider recipe; if (id != null && Custom.TryGetValue(id, out recipe)) {
                if (recipe.ConsoleToken) return "在设置里点“登录”，在弹出的页面登录后自动读取。" + recipe.TokenHint;
                return recipe.Builtin ? "在设置里点“配置”填写密钥后刷新。" : "检查接入方案里的地址、字段路径和密钥（设置 → 订阅 → 配置），然后刷新。";
            }
            switch (id) {
                case "codex": return "使用 Codex 应用或 CLI 登录后，点击刷新。";
                case "claude": return "使用 Claude Code 登录后，点击刷新。";
                case "deepseek": return "填写 DeepSeek API Key 即可查询 API 账户余额（卡片或设置中都可以填写），也可设置环境变量 DEEPSEEK_API_KEY。";
                default: return "";
            }
        }
    }
    public static class J {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 100 }; }
        public static object Parse(string text) { return Serializer().DeserializeObject(text); }
        public static object Get(object obj, params string[] keys) {
            foreach (string key in keys) { var d = obj as IDictionary<string, object>; if (d == null || !d.TryGetValue(key, out obj)) return null; }
            return obj;
        }
        public static string Str(object obj, params string[] keys) { object v = keys.Length == 0 ? obj : Get(obj, keys); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static double? Num(object obj, params string[] keys) {
            object v = keys.Length == 0 ? obj : Get(obj, keys); if (v == null || v is bool) return null;
            double n; return Double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !Double.IsNaN(n) && !Double.IsInfinity(n) ? (double?)n : null;
        }
        public static IEnumerable<object> Arr(object obj) { var a = obj as IEnumerable; if (a == null || obj is string || obj is IDictionary) yield break; foreach (object v in a) yield return v; }
        public static Dictionary<string, object> Dict(object obj) { return obj as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object File(string path) { if (!System.IO.File.Exists(path)) return null; return Parse(System.IO.File.ReadAllText(path)); }
        public static string Iso(object value) {
            double? seconds = Num(value);
            if (seconds.HasValue && seconds > 100000000) {
                // Some endpoints return epoch milliseconds; treat 12+ digit values as ms.
                double s = seconds.Value > 100000000000 ? seconds.Value / 1000 : seconds.Value;
                try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s).ToString("o"); } catch { return ""; }
            }
            DateTimeOffset date; return DateTimeOffset.TryParse(Str(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date.UtcDateTime.ToString("o") : "";
        }
        public static object Jwt(string token) { try { string[] p = token.Split('.'); if (p.Length < 2) return null; string s = p[1].Replace('-', '+').Replace('_', '/'); s += new string('=', (4 - s.Length % 4) % 4); return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s))); } catch { return null; } }
    }
    public static class Store {
        // Keep in sync with source/Windows/installer/headroom.iss.
        public const string InstallRegistryKey = @"Software\Headroom";
        public const string InstallRegistryValue = "InstallPath";
        public static readonly string Root = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
        // Chosen on first read. EnableDemoMode assigns this before anything reads it, so
        // --demo never migrates or opens the real data directory.
        private static string dataPath;
        public static string Data {
            get {
                if (dataPath == null) dataPath = ChooseDataFor(Root, ReadInstallPath(), Environment.GetEnvironmentVariable("HEADROOM_DATA"), UserDataDirectory(), LegacyUserDataDirectory());
                return dataPath;
            }
            set { dataPath = value; }
        }
        public static void EnableDemoMode() { Data = Path.Combine(Root, "verification", "demo-data"); }
        // Self-tests point the data directory at a temporary folder and back, without choosing
        // (or migrating) the real one.
        internal static string SwapData(string path) { string old = dataPath; dataPath = path; return old; }
        // Zip, Scoop and install.ps1 keep using <exe>\data. setup.exe writes installed.txt
        // (and HKCU\Software\Headroom\InstallPath); those copies use
        // %LOCALAPPDATA%\Headroom so Program Files can stay read-only.
        // portable.txt next to the exe forces <exe>\data even for an installed copy.
        // HEADROOM_DATA overrides the directory unless portable.txt is present.
        public static string UserDataDirectory() {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Headroom");
        }
        // Where installed copies up to 2.0.1 (codeusagemonit.exe) kept their data.
        public static string LegacyUserDataDirectory() {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codeusagemonit");
        }
        public static string ResolveData(string root, string installPath, string envData, bool installedMarker, string userData) {
            string beside = Path.Combine(root ?? "", "data");
            try { if (File.Exists(Path.Combine(root ?? "", "portable.txt"))) return beside; } catch { }
            if (!String.IsNullOrWhiteSpace(envData)) { try { return Path.GetFullPath(envData.Trim()); } catch { } }
            if (installedMarker || PathsEqual(installPath, root)) return String.IsNullOrEmpty(userData) ? beside : userData;
            return beside;
        }
        public static string ChooseDataFor(string root, string installPath, string envData, string userData, string legacyUserData) {
            bool marker = false;
            try { marker = File.Exists(Path.Combine(root, "installed.txt")); } catch { }
            string chosen = ResolveData(root, installPath, envData, marker, userData);
            if (!String.IsNullOrEmpty(userData) && PathsEqual(chosen, userData)) {
                // Each step only runs while the per-user folder is still empty.
                TryMigratePortableData(legacyUserData, chosen);
                TryMigratePortableData(Path.Combine(root, "data"), chosen);
            }
            return chosen;
        }
        public static string ReadInstallPath() {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InstallRegistryKey)) {
                    if (key == null) return "";
                    object value = key.GetValue(InstallRegistryValue);
                    return value == null ? "" : Convert.ToString(value);
                }
            } catch { return ""; }
        }
        public static bool PathsEqual(string a, string b) {
            if (String.IsNullOrWhiteSpace(a) || String.IsNullOrWhiteSpace(b)) return false;
            try { return String.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        // First launch of an installed copy: if the per-user folder is still empty and a
        // real data directory sits beside the exe (zip or the PowerShell installer) or in the
        // pre-rename per-user folder, copy it across and remove the old folder. A junction (Scoop's persist link) is copied and left in place.
        public static bool TryMigratePortableData(string from, string to) {
            string staging = null;
            try {
                if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to) || PathsEqual(from, to)) return false;
                if (IsNested(from, to) || IsNested(to, from)) return false;
                if (!Directory.Exists(from)) return false;
                DirectoryInfo source = new DirectoryInfo(from);
                bool link = (source.Attributes & FileAttributes.ReparsePoint) != 0;
                if (Directory.GetFileSystemEntries(from).Length == 0) return false;
                if (Directory.Exists(to) && Directory.GetFileSystemEntries(to).Length > 0) return false;
                string parent = Path.GetDirectoryName(NormalizeDirectory(to));
                if (String.IsNullOrEmpty(parent)) return false;
                Directory.CreateDirectory(parent);
                staging = Path.Combine(parent, Path.GetFileName(NormalizeDirectory(to).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".migrating");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                CopyAll(from, staging);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(staging, to);
                staging = null;
                if (!link) { try { Directory.Delete(from, true); } catch { } }
                return true;
            } catch {
                if (staging != null && Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
                return false;
            }
        }
        private static bool IsNested(string parent, string child) {
            try {
                string p = NormalizeDirectory(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string c = NormalizeDirectory(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase) && !PathsEqual(parent, child);
            } catch { return false; }
        }
        private static void CopyAll(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
            foreach (string dir in Directory.GetDirectories(from)) {
                DirectoryInfo info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                CopyAll(dir, Path.Combine(to, info.Name));
            }
        }
        public static string NormalizeDirectory(string path) {
            if (String.IsNullOrEmpty(path)) return "";
            try { path = Path.GetFullPath(path); } catch { }
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;
            return trimmed.Length == 0 ? path : trimmed;
        }
        public static void Write(string name, object value) {
            Directory.CreateDirectory(Data); string path = Path.Combine(Data, name); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, J.Serializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static T Read<T>(string name) where T : new() { try { string path = Path.Combine(Data, name); return File.Exists(path) ? J.Serializer().Deserialize<T>(File.ReadAllText(path)) : new T(); } catch { return new T(); } }
        // Secrets live in data/<id>.key, encrypted with DPAPI for the current Windows user.
        public static string SavedKey(string id) {
            try { string file = KeyPath(id); if (!File.Exists(file)) return ""; return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
        }
        public static bool HasSavedKey(string id) { return File.Exists(KeyPath(id)); }
        public static void SetProviderKey(string id, string key) {
            Directory.CreateDirectory(Data); string path = KeyPath(id);
            if (String.IsNullOrWhiteSpace(key)) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        }
        private static string KeyPath(string id) {
            if (String.IsNullOrEmpty(id) || id.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '-')) throw new ArgumentException("invalid key id");
            return Path.Combine(Data, id + ".key");
        }
        public static string DeepSeekKey() { return SavedKey("deepseek"); }
        public static void SetDeepSeekKey(string key) { SetProviderKey("deepseek", key); }
    }
    public sealed class ProviderException : Exception {
        public string State;
        public ProviderException(string message, string state) : base(message) { State = state; }
    }
    public static class Parsers {
        public static void Add(ProviderState s, string label, object used, object reset, double windowSeconds = 0) {
            double? n = J.Num(used); if (!n.HasValue) return;
            s.Quotas.Add(new Quota { Label = label, Used = Math.Max(0, Math.Min(100, n.Value)), ResetUtc = J.Iso(reset), WindowSeconds = windowSeconds });
        }
        public static string WindowLabel(double seconds) { if (seconds >= 2500000) return "每月"; if (seconds >= 432000) return "每周"; if (seconds >= 86400) return "每日"; if (seconds > 0) return (seconds / 3600).ToString("0.#") + " 小时"; return "当前窗口"; }
        public static ProviderState Codex(object root) {
            var s = new ProviderState { Id = "codex", Plan = UsageDetails.PlanName(J.Str(root, "plan_type")) };
            foreach (string key in new[] { "primary_window", "secondary_window" }) { object w = J.Get(root, "rate_limit", key); double seconds = J.Num(w, "limit_window_seconds") ?? 0; if (w != null) Add(s, WindowLabel(seconds), J.Get(w, "used_percent"), J.Get(w, "reset_at"), seconds); }
            foreach (object extra in J.Arr(J.Get(root, "additional_rate_limits"))) {
                string model = J.Str(extra, "limit_name"); if (model.Length == 0) model = J.Str(extra, "metered_feature");
                foreach (string key in new[] { "primary_window", "secondary_window" }) { object w = J.Get(extra, "rate_limit", key); double seconds = J.Num(w, "limit_window_seconds") ?? 0; if (w != null) Add(s, model + " · " + WindowLabel(seconds), J.Get(w, "used_percent"), J.Get(w, "reset_at"), seconds); }
            }
            return s;
        }
        public static ProviderState Claude(object root) {
            var s = new ProviderState { Id = "claude", Plan = "Claude Code" };
            foreach (var pair in new[] { new[] { "five_hour", "5 小时" }, new[] { "seven_day", "每周" }, new[] { "seven_day_sonnet", "Sonnet · 每周" }, new[] { "seven_day_opus", "Opus · 每周" } }) {
                object w = J.Get(root, pair[0]); if (w != null) Add(s, pair[1], J.Get(w, "utilization"), J.Get(w, "resets_at"), pair[0] == "five_hour" ? 18000 : 604800);
            }
            foreach (object w in J.Arr(J.Get(root, "limits"))) {
                string kind = J.Str(w, "kind"), model = J.Str(w, "model"); string label = J.Str(w, "display_name");
                if (label.Length == 0) label = kind.Contains("weekly") ? (model.Length == 0 ? "每周" : model + " · 每周") : kind.Contains("five") || kind.Contains("session") ? "5 小时" : kind;
                object used = J.Get(w, "utilization") ?? J.Get(w, "used_percent");
                if (!s.Quotas.Any(q => q.Label == label)) Add(s, label, used, J.Get(w, "resets_at") ?? J.Get(w, "reset_at"));
            }
            return s;
        }
        public static ProviderState DeepSeek(object root) {
            var s = new ProviderState { Id = "deepseek", Plan = "API 余额" };
            foreach (object b in J.Arr(J.Get(root, "balance_infos"))) { double? n = J.Num(b, "total_balance"); string c = J.Str(b, "currency"); if (n.HasValue && c.Length > 0) s.Balances.Add(new Balance { Currency = c, Amount = n.Value }); }
            return s;
        }
        // Readings cached by older versions may lack newer fields.
        public static ProviderState Normalize(ProviderState s) {
            if (s == null) return null;
            if (s.ProductUsage == null) s.ProductUsage = new List<ProductConsumption>();
            return s;
        }
        // A failed refresh keeps the last good reading, including the consumption breakdown.
        public static void RetainLastGood(ProviderState incoming, ProviderState old) {
            if (incoming == null || (incoming.Status != "error" && incoming.Status != "expired")) return;
            if (old == null) old = new ProviderState();
            incoming.Quotas = old.Quotas; incoming.Balances = old.Balances; incoming.ProductUsage = old.ProductUsage ?? new List<ProductConsumption>();
            incoming.LastSuccess = old.LastSuccess; incoming.Plan = old.Plan; incoming.Account = old.Account;
            incoming.ResetCreditsAvailable = old.ResetCreditsAvailable; incoming.ResetCreditExpiries = old.ResetCreditExpiries; incoming.ResetCreditsUpdated = old.ResetCreditsUpdated;
            incoming.Stale = (old.Quotas != null && old.Quotas.Count > 0) || (old.Balances != null && old.Balances.Count > 0);
        }
    }
    public sealed class ProviderService : IDisposable {
        private readonly HttpClient client;
        private readonly AppConfig config;
        public ProviderService(AppConfig config) {
            this.config = config ?? new AppConfig();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; ServicePointManager.DefaultConnectionLimit = 12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false };
            string proxy = ResolveProxy(config.Proxy); handler.UseProxy = proxy.Length > 0; if (handler.UseProxy) handler.Proxy = new WebProxy(proxy);
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        }
        public static string ResolveProxy(string setting) {
            if (setting == "direct") return "";
            if (setting != "auto" && !String.IsNullOrWhiteSpace(setting)) { Uri u; if (!Uri.TryCreate(setting, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new ArgumentException("代理地址应为 http://主机:端口"); return setting; }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")) { if (k != null && Convert.ToInt32(k.GetValue("ProxyEnable", 0)) != 0) { string server = Convert.ToString(k.GetValue("ProxyServer", "")); if (server.Contains("=")) { string choice = server.Split(';').FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase)) ?? server.Split(';').FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase)); server = choice == null ? "" : choice.Substring(choice.IndexOf('=') + 1); } if (server.Length > 0) return server.Contains("://") ? server : "http://" + server; } } } catch { }
            foreach (string key in new[] { "HTTPS_PROXY", "HTTP_PROXY" }) { string value = Environment.GetEnvironmentVariable(key); Uri u; if (Uri.TryCreate(value, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) return value; }
            return "";
        }
        private async Task<object> Request(string url, string token, Dictionary<string, string> extra, int timeoutSeconds = 18) {
            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
                request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                if (!String.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                if (extra != null) foreach (var h in extra) { request.Headers.Remove(h.Key); request.Headers.TryAddWithoutValidation(h.Key, h.Value); }
                using (HttpResponseMessage response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false)) {
                    int status = (int)response.StatusCode;
                    if (status == 401) throw new ProviderException("登录已过期，请在原应用中重新登录", "expired");
                    if (status == 403) throw new ProviderException("接口拒绝访问（403），请检查代理或登录状态", "error");
                    if (status == 429) throw new ProviderException("查询过于频繁，稍后自动重试", "error");
                    if (!response.IsSuccessStatusCode) throw new ProviderException("服务暂时不可用（HTTP " + status + "）", "error");
                    return J.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
        private static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        private Task<object> ClaudeUsage(string token) { return Request("https://api.anthropic.com/api/oauth/usage", token, new Dictionary<string, string> { { "anthropic-beta", "oauth-2025-04-20" } }); }
        private static ProviderException ClaudeExpired(bool renewTried) {
            return new ProviderException(renewTried ? "Claude Code 的登录令牌已过期，后台续期暂未成功。运行一次 Claude Code（必要时重新登录）后刷新。" : "Claude Code 的登录令牌已过期。运行一次 Claude Code 即可自动续期，然后刷新。", "expired");
        }
        private static string ConfigPath(string variable, string fallback, string file) { string path = Environment.GetEnvironmentVariable(variable); if (String.IsNullOrWhiteSpace(path)) path = Path.Combine(Home, fallback); path = Environment.ExpandEnvironmentVariables(path); if (path.StartsWith("~")) path = Home + path.Substring(1); return Path.Combine(path, file); }
        public static string CodexHome() { return Path.GetDirectoryName(ConfigPath("CODEX_HOME", ".codex", "auth.json")); }
        public async Task<ProviderState> Fetch(string id) {
            try {
                ProviderState s;
                switch (id) {
                    case "codex": {
                        object a = J.File(ConfigPath("CODEX_HOME", ".codex", "auth.json")); string token = J.Str(a, "tokens", "access_token"); Need(token, id);
                        var headers = new Dictionary<string, string> { { "User-Agent", "codex-cli" } }; string account = J.Str(a, "tokens", "account_id"); if (account.Length > 0) headers.Add("ChatGPT-Account-Id", account);
                        object usage;
                        try { usage = await Request("https://chatgpt.com/backend-api/wham/usage", token, headers).ConfigureAwait(false); }
                        catch (ProviderException e) { if (e.State == "expired") throw new ProviderException("Codex 登录令牌已过期。打开 Codex 应用或运行一次 codex 即可自动续期，然后刷新。", "expired"); throw; }
                        s = Parsers.Codex(usage); s.Account = J.Str(J.Jwt(J.Str(a, "tokens", "id_token")), "email");
                        try {
                            var resetHeaders = new Dictionary<string, string>(headers) { { "OpenAI-Beta", "codex-1" }, { "originator", "Codex Desktop" }, { "Accept", "application/json" } };
                            object credits = await Request("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", token, resetHeaders, 4).ConfigureAwait(false);
                            UsageDetails.ParseResetCredits(s, credits, DateTime.UtcNow);
                        } catch { s.ResetCreditsError = "重置额度暂未返回"; }
                        break;
                    }
                    case "claude": {
                        string credentials = ConfigPath("CLAUDE_CONFIG_DIR", ".claude", ".credentials.json");
                        object a = J.File(credentials); string token = J.Str(a, "claudeAiOauth", "accessToken"); Need(token, id);
                        // The token lasts about 8 hours and only the claude CLI renews it: let it do so
                        // (SessionRecovery) before asking with a token that is known to have expired.
                        bool renew = config.RenewSessions, renewed = false;
                        if (renew && SessionRecovery.ClaudeExpired(a, DateTime.UtcNow) && await SessionRecovery.RenewClaude(credentials, DateTime.UtcNow).ConfigureAwait(false)) { a = J.File(credentials); token = J.Str(a, "claudeAiOauth", "accessToken"); Need(token, id); renewed = true; }
                        object usage = null; bool rejected = false;
                        try { usage = await ClaudeUsage(token).ConfigureAwait(false); }
                        catch (ProviderException e) { if (e.State != "expired") throw; rejected = true; }
                        if (rejected) {
                            // A 401: renew once (unless that just happened) and ask again.
                            if (!renew || renewed || !await SessionRecovery.RenewClaude(credentials, DateTime.UtcNow).ConfigureAwait(false)) throw ClaudeExpired(renew);
                            a = J.File(credentials); token = J.Str(a, "claudeAiOauth", "accessToken"); Need(token, id);
                            try { usage = await ClaudeUsage(token).ConfigureAwait(false); }
                            catch (ProviderException e) { if (e.State == "expired") throw ClaudeExpired(renew); throw; }
                        }
                        s = Parsers.Claude(usage);
                        string plan = J.Str(a, "claudeAiOauth", "subscriptionType"); if (plan.Length > 0) s.Plan = "Claude " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan);
                        try { s.Account = J.Str(J.File(Path.Combine(Home, ".claude.json")), "oauthAccount", "emailAddress"); } catch { } break;
                    }
                    default: {
                        CustomProvider custom;
                        if (!ProviderCatalog.Custom.TryGetValue(id, out custom)) throw new ProviderException("不支持的平台", "setup");
                        // DeepSeek with only an API key (no console sign-in): the official balance endpoint, no daily usage.
                        string apiKey = id == "deepseek" && !Store.HasSavedKey(id) ? Store.SavedKey("deepseek-apikey") : null;
                        if (!String.IsNullOrEmpty(apiKey)) { s = Parsers.DeepSeek(await KeyedRequest(id, "https://api.deepseek.com/user/balance", apiKey, null).ConfigureAwait(false)); s.Plan = "API Key · 只读余额"; break; }
                        s = await CustomProviders.Fetch(client, custom, Store.SavedKey(id), DateTime.UtcNow).ConfigureAwait(false); break;
                    }
                }
                if (s.Quotas.Count == 0 && s.Balances.Count == 0) throw new ProviderException("接口未返回可识别的额度；没有将缺失数据当作零", "error");
                s.Status = "ready"; s.Message = "已连接"; s.LastSuccess = DateTime.UtcNow.ToString("o"); s.LastAttempt = s.LastSuccess; return s;
            } catch (ProviderException e) { return new ProviderState { Id = id, Status = e.State, Message = e.Message, LastAttempt = DateTime.UtcNow.ToString("o") }; }
            catch (TaskCanceledException) { return Failure(id, "查询超时，请检查代理连接"); }
            catch (HttpRequestException) { return Failure(id, "网络连接失败，请检查代理设置"); }
            catch (UnauthorizedAccessException) { return Failure(id, "无法读取本机登录文件，请检查文件权限"); }
            catch (Exception) { return Failure(id, "无法读取此平台的数据格式，请在原应用登录后重试"); }
        }
        // A public JSON document (the GitHub releases API for the update check).
        public Task<object> GetJson(string url) { return Request(url, null, new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } }); }
        public Task<ProviderState> FetchCustom(CustomProvider provider, string secret) { return CustomProviders.Fetch(client, provider, secret, DateTime.UtcNow); }
        // API-key providers: a rejected key is a settings problem, not an expired app login.
        private async Task<object> KeyedRequest(string id, string url, string key, Dictionary<string, string> headers) {
            try { return await Request(url, key, headers).ConfigureAwait(false); }
            catch (ProviderException e) {
                if (e.State != "expired") throw;
                throw new ProviderException(ProviderCatalog.Name(id) + " 的 API Key 无效或已过期，请重新填写。", "expired");
            }
        }
        private static ProviderState Failure(string id, string message) { return new ProviderState { Id = id, Status = "error", Message = message, LastAttempt = DateTime.UtcNow.ToString("o") }; }
        private static void Need(string value, string id) { if (String.IsNullOrWhiteSpace(value)) throw new ProviderException(ProviderCatalog.Help(id), "setup"); }
        public void Dispose() { client.Dispose(); }
    }
    internal static class NativeCredentials {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
        // Read-only query returning every column as text; empty on any failure.
        public static List<string[]> SqliteRows(string path, string query) {
            var rows = new List<string[]>(); if (!File.Exists(path)) return rows; IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try {
                if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 1, IntPtr.Zero) != 0) return rows; sqlite3_busy_timeout(db, 1500);
                byte[] sql = Encoding.UTF8.GetBytes(query + "\0"); if (sqlite3_prepare_v2(db, sql, sql.Length, out stmt, IntPtr.Zero) != 0) return rows;
                int columns = sqlite3_column_count(stmt);
                while (rows.Count < 5000 && sqlite3_step(stmt) == 100) {
                    var row = new string[columns];
                    for (int c = 0; c < columns; c++) {
                        IntPtr text = sqlite3_column_text(stmt, c); int count = sqlite3_column_bytes(stmt, c);
                        if (text == IntPtr.Zero || count <= 0 || count > 4 * 1024 * 1024) { row[c] = ""; continue; }
                        byte[] b = new byte[count]; Marshal.Copy(text, b, 0, count); row[c] = Encoding.UTF8.GetString(b);
                    }
                    rows.Add(row);
                }
            } catch { }
            finally { if (stmt != IntPtr.Zero) sqlite3_finalize(stmt); if (db != IntPtr.Zero) sqlite3_close(db); }
            return rows;
        }
    }
    public static partial class HistoryService {
        public static string DayKey(DateTime day) { return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        // Clients may delete or prune old session files, and a rebuilt index cannot count
        // them any more. Past days therefore keep the largest total ever observed. Today is
        // always replaced (it is still growing); a time-zone or engine change starts over.
        public static UsageHistory Merge(UsageHistory previous, UsageHistory next, string oldestDay, string today) {
            if (previous == null || next == null || previous.Days.Count == 0 || previous.Zone != next.Zone || previous.Engine != next.Engine) return next;
            foreach (DayUsage old in previous.Days) {
                if (old.Day == null || String.CompareOrdinal(old.Day, today) >= 0 || String.CompareOrdinal(old.Day, oldestDay) < 0) continue;
                int index = next.Days.FindIndex(d => d.Day == old.Day && d.Agent == old.Agent);
                if (index < 0) next.Days.Add(old); else if (next.Days[index].Tokens < old.Tokens) next.Days[index] = old;
            }
            return next;
        }
    }
}
