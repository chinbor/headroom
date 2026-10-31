using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Headroom {
    // Platform recipes (接入方案): a JSON description of how to read one platform — the quota
    // request and its field paths, an optional daily-usage request, how the user gets the token
    // (drives the in-app guide) and which response codes mean success or an expired token.
    // Built-in recipes ship in platforms/*.json (DeepSeek, Agnes); users add their own in
    // Settings. Fixed auth header forms, DPAPI secrets, no redirects, bounded responses, and the
    // secret only ever goes to the recipe's own host.
    public sealed class CustomWindow {
        public string Label = "", UsedPercent = "", RemainingPercent = "", Used = "", Limit = "", Remaining = "", ResetsAt = "", ResetInSeconds = "";
        public bool Ratio;
        public double WindowHours;
    }
    public sealed class CustomUsage {
        public string Url = "", Groups = "", Items = "", Model = "", Day = "", Input = "", Cached = "", Output = "", CacheWrite = "", Requests = "";
        public List<string> Tokens = new List<string>();
        public bool Priced;
    }
    public sealed class CustomProvider {
        public string Id = "", Name = "", Color = "", Icon = "", Url = "", Auth = "none", Header = "";
        public bool Builtin;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public List<CustomWindow> Windows = new List<CustomWindow>();
        public string Balance = "", Currency = "", CurrencyPath = "", Plan = "", PlanLabel = "", Account = "";
        public List<string> BalanceLists = new List<string>();
        // How the user gets the secret: "console" (the web console's sign-in token, copied with
        // the guide), "apikey", or "" (plain secret field).
        public string TokenKind = "", TokenConsole = "", TokenCopy = "", TokenHint = "", TokenRead = "";
        // The expression the sign-in window evaluates in the console page to read the token:
        // token.read, or the argument of copy(...) in token.copy.
        public string TokenScript {
            get {
                if (TokenRead.Length > 0) return TokenRead;
                string c = TokenCopy; if (!c.StartsWith("copy(", StringComparison.Ordinal)) return "";
                int end = c.LastIndexOf("),", StringComparison.Ordinal); if (end < 0 && c.EndsWith(")", StringComparison.Ordinal)) end = c.Length - 1;
                return end > 5 ? c.Substring(5, end - 5) : "";
            }
        }
        public string CodePath = "";
        public List<double> OkCodes = new List<double>(), ExpiredCodes = new List<double>();
        public CustomUsage Usage;
        public string Source = "";
        public string Host { get { Uri uri; return Uri.TryCreate(Url, UriKind.Absolute, out uri) ? uri.Authority : ""; } }
        public bool ConsoleToken { get { return TokenKind == "console"; } }
    }
    public sealed class CustomProviderFile { public List<string> Definitions = new List<string>(); }

    public static class CustomProviders {
        public const int MaxResponseBytes = 1 << 20, MaxWindows = 6, UsageDays = 30;
        private static readonly Regex PathPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_\-]*(\.[A-Za-z_][A-Za-z0-9_\-]*|\[\d{1,4}\])*$");
        private static readonly string[] ForbiddenHeaders = { "host", "cookie", "content-length", "content-type", "transfer-encoding", "connection", "proxy-authorization", "authorization" };

        public const string Template = "{\n  \"name\": \"我的平台\",\n  \"color\": \"#8FD1E8\",\n  \"url\": \"https://api.example.com/api/subscription\",\n  \"auth\": \"bearer\",\n  \"token\": {\n    \"kind\": \"console\",\n    \"console\": \"https://console.example.com/usage\",\n    \"copy\": \"copy(localStorage.token)\"\n  },\n  \"success\": { \"code\": \"code\", \"ok\": [0], \"expired\": [401] },\n  \"windows\": [\n    { \"label\": \"每日\", \"used\": \"data.daily.used\", \"limit\": \"data.daily.limit\", \"resetsAt\": \"data.daily.reset_at\", \"windowHours\": 24 }\n  ],\n  \"balance\": { \"amount\": \"data.balance\", \"currency\": \"CNY\" },\n  \"plan\": \"data.plan_name\",\n  \"usage\": {\n    \"url\": \"https://api.example.com/api/usage?start_date={from}&end_date={to}\",\n    \"items\": \"data.days\",\n    \"day\": \"date\",\n    \"tokens\": [\"tokens\"],\n    \"requests\": \"requests\"\n  }\n}";

        // User recipes (data/custom-providers.json).
        public static List<CustomProvider> Load() {
            var result = new List<CustomProvider>();
            foreach (string text in Store.Read<CustomProviderFile>("custom-providers.json").Definitions) { try { result.Add(Parse(text)); } catch { } }
            return result;
        }
        public static void Save(IEnumerable<CustomProvider> providers) { Store.Write("custom-providers.json", new CustomProviderFile { Definitions = providers.Where(p => !p.Builtin).Select(p => p.Source).ToList() }); }
        // Built-in recipes next to the program (platforms/*.json); an invalid file is skipped.
        public static List<CustomProvider> Builtin() {
            var result = new List<CustomProvider>();
            try {
                string folder = Path.Combine(Store.Root, "platforms");
                if (Directory.Exists(folder))
                    foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) { try { result.Add(Parse(File.ReadAllText(file, Encoding.UTF8), true)); } catch { } }
            } catch { }
            return result;
        }

        // Parses and validates a recipe; ArgumentException messages are shown to the user.
        public static CustomProvider Parse(string json) { return Parse(json, false); }
        public static CustomProvider Parse(string json, bool builtin) {
            object root;
            try { root = J.Parse(json ?? ""); } catch (Exception e) { throw new ArgumentException("JSON 格式错误：" + e.Message); }
            if (!(root is Dictionary<string, object>)) throw new ArgumentException("定义必须是一个 JSON 对象");
            var known = new HashSet<string> { "id", "name", "color", "icon", "url", "auth", "header", "headers", "windows", "balance", "plan", "planLabel", "account", "token", "success", "usage", "type" };
            foreach (string key in J.Dict(root).Keys) if (!known.Contains(key)) throw new ArgumentException("未知字段：" + key);
            string type = J.Str(root, "type").Trim();
            if (type == "agnes-plan") throw new ArgumentException("Agnes 已经内置，直接在订阅里添加即可");
            if (type.Length > 0 && type != "http-json") throw new ArgumentException("type 只能是 http-json");
            var p = new CustomProvider { Source = json.Trim(), Builtin = builtin };
            p.Name = J.Str(root, "name").Trim();
            if (p.Name.Length == 0 || p.Name.Length > 40) throw new ArgumentException("name 为 1–40 个字符");
            string id = J.Str(root, "id").Trim().ToLowerInvariant();
            if (id.Length == 0) id = Slug(p.Name);
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,31}$")) throw new ArgumentException("id 只能包含小写字母、数字和连字符（最多 32 个）");
            // User recipes live under their own prefix, so they never take over a built-in id.
            p.Id = builtin ? id : "custom-" + id;
            p.Color = J.Str(root, "color").Trim();
            if (p.Color.Length > 0 && !Regex.IsMatch(p.Color, "^#[0-9A-Fa-f]{6}$")) throw new ArgumentException("color 应为 #RRGGBB");
            p.Icon = J.Str(root, "icon").Trim().ToLowerInvariant();
            if (p.Icon.Length > 0 && !Regex.IsMatch(p.Icon, "^[a-z0-9][a-z0-9-]{0,31}$")) throw new ArgumentException("icon 是 icons 文件夹里的图标名，例如 deepseek");
            p.Url = J.Str(root, "url").Trim();
            string urlError = UrlError(p.Url); if (urlError != null) throw new ArgumentException(urlError);
            p.Auth = J.Str(root, "auth").Trim().ToLowerInvariant(); if (p.Auth.Length == 0) p.Auth = "none";
            if (!new[] { "none", "bearer", "x-api-key", "token", "header" }.Contains(p.Auth)) throw new ArgumentException("auth 只能是 none、bearer、x-api-key、token 或 header");
            p.Header = J.Str(root, "header").Trim();
            if (p.Auth == "header" && !HeaderName(p.Header)) throw new ArgumentException("auth 为 header 时需要合法的 header 名称，例如 X-Api-Key");
            foreach (var pair in J.Dict(J.Get(root, "headers"))) {
                string value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "";
                if (!HeaderName(pair.Key) || value.Length > 200 || value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("headers 里的“" + pair.Key + "”不合法（不能设置认证、Cookie 等请求头）");
                p.Headers[pair.Key] = value;
            }
            if (p.Headers.Count > 8) throw new ArgumentException("headers 最多 8 个");

            object token = J.Get(root, "token");
            if (token != null) {
                p.TokenKind = J.Str(token, "kind").Trim().ToLowerInvariant();
                if (p.TokenKind != "console" && p.TokenKind != "apikey") throw new ArgumentException("token.kind 只能是 console 或 apikey");
                p.TokenConsole = J.Str(token, "console").Trim(); p.TokenCopy = J.Str(token, "copy").Trim(); p.TokenHint = J.Str(token, "hint").Trim(); p.TokenRead = J.Str(token, "read").Trim();
                if (p.TokenConsole.Length > 0) { Uri console; if (!Uri.TryCreate(p.TokenConsole, UriKind.Absolute, out console) || console.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("token.console 必须是 https 网址"); }
                if (p.ConsoleToken && (p.TokenConsole.Length == 0 || p.TokenCopy.Length == 0)) throw new ArgumentException("token.kind 为 console 时需要 console（控制台网址）和 copy（复制 token 的命令）");
                if (p.TokenCopy.Length > 300 || p.TokenHint.Length > 300 || p.TokenRead.Length > 300) throw new ArgumentException("token.copy / token.read / token.hint 最多 300 个字符");
                if (p.Auth == "none") throw new ArgumentException("设置了 token 时 auth 不能是 none");
            }
            object success = J.Get(root, "success");
            if (success != null) {
                p.CodePath = PathOf(success, "code");
                if (p.CodePath.Length == 0) throw new ArgumentException("success 需要 code 路径");
                p.OkCodes = Numbers(J.Get(success, "ok")); p.ExpiredCodes = Numbers(J.Get(success, "expired"));
                if (p.OkCodes.Count == 0) throw new ArgumentException("success.ok 至少列一个成功码");
            }

            foreach (object item in J.Arr(J.Get(root, "windows"))) {
                var w = new CustomWindow {
                    Label = J.Str(item, "label").Trim(), UsedPercent = PathOf(item, "usedPercent"), RemainingPercent = PathOf(item, "remainingPercent"),
                    Used = PathOf(item, "used"), Limit = PathOf(item, "limit"), Remaining = PathOf(item, "remaining"),
                    ResetsAt = PathOf(item, "resetsAt"), ResetInSeconds = PathOf(item, "resetInSeconds"),
                    Ratio = J.Get(item, "ratio") is bool && (bool)J.Get(item, "ratio"), WindowHours = J.Num(item, "windowHours") ?? 0
                };
                if (w.Label.Length == 0 || w.Label.Length > 30) throw new ArgumentException("每个窗口都需要 1–30 个字符的 label");
                bool counts = w.Used.Length > 0 || w.Remaining.Length > 0;
                if (w.UsedPercent.Length > 0 && w.RemainingPercent.Length > 0) throw new ArgumentException("窗口“" + w.Label + "”的 usedPercent 和 remainingPercent 只能用一个");
                if (w.UsedPercent.Length == 0 && w.RemainingPercent.Length == 0 && !counts) throw new ArgumentException("窗口“" + w.Label + "”需要 usedPercent、remainingPercent，或 used/remaining + limit");
                if (counts && w.Limit.Length == 0) throw new ArgumentException("窗口“" + w.Label + "”使用 used/remaining 时必须提供 limit");
                if (w.WindowHours < 0 || w.WindowHours > 24 * 366) throw new ArgumentException("windowHours 超出范围");
                p.Windows.Add(w);
            }
            if (p.Windows.Count > MaxWindows) throw new ArgumentException("最多 " + MaxWindows + " 个窗口");
            object balance = J.Get(root, "balance");
            if (balance != null) {
                p.Balance = PathOf(balance, "amount"); p.CurrencyPath = PathOf(balance, "currencyPath");
                p.Currency = J.Str(balance, "currency").Trim().ToUpperInvariant();
                foreach (object list in J.Arr(J.Get(balance, "lists"))) p.BalanceLists.Add(CheckPath(Convert.ToString(list, CultureInfo.InvariantCulture)));
                if (p.Balance.Length == 0) throw new ArgumentException("balance 需要 amount 路径");
                if (p.Currency.Length > 0 && !Regex.IsMatch(p.Currency, "^[A-Z]{3}$")) throw new ArgumentException("currency 应为三位货币代码，例如 USD、CNY");
            }
            p.Plan = PathOf(root, "plan"); p.Account = PathOf(root, "account");
            p.PlanLabel = J.Str(root, "planLabel").Trim(); if (p.PlanLabel.Length > 30) throw new ArgumentException("planLabel 最多 30 个字符");

            object usage = J.Get(root, "usage");
            if (usage != null) {
                var u = new CustomUsage {
                    Url = J.Str(usage, "url").Trim(), Groups = PathOf(usage, "groups"), Items = PathOf(usage, "items"), Model = PathOf(usage, "model"), Day = PathOf(usage, "day"),
                    Input = PathOf(usage, "input"), Cached = PathOf(usage, "cached"), Output = PathOf(usage, "output"), CacheWrite = PathOf(usage, "cacheWrite"), Requests = PathOf(usage, "requests"),
                    Priced = J.Get(usage, "priced") is bool && (bool)J.Get(usage, "priced")
                };
                foreach (object t in J.Arr(J.Get(usage, "tokens"))) u.Tokens.Add(CheckPath(Convert.ToString(t, CultureInfo.InvariantCulture)));
                string expanded = UsageUrl(u.Url, new DateTime(2026, 1, 31), TimeSpan.FromHours(8));
                string usageError = UrlError(expanded); if (usageError != null) throw new ArgumentException("usage." + usageError);
                Uri a = new Uri(expanded), b = new Uri(p.Url);
                if (!String.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase) || a.Scheme != b.Scheme) throw new ArgumentException("usage.url 必须和 url 在同一个地址（" + b.Authority + "），token 不会发给别的主机");
                if (u.Items.Length == 0 || u.Day.Length == 0) throw new ArgumentException("usage 需要 items（每天一条的列表）和 day（日期字段）");
                if (u.Tokens.Count == 0 && u.Input.Length == 0 && u.Cached.Length == 0 && u.Output.Length == 0) throw new ArgumentException("usage 需要 tokens，或 input / cached / output 字段");
                p.Usage = u;
            }
            if (p.Windows.Count == 0 && p.Balance.Length == 0) throw new ArgumentException("至少需要一个 windows 窗口或 balance");
            return p;
        }
        private static bool HeaderName(string name) { return Regex.IsMatch(name ?? "", "^[A-Za-z0-9-]{1,64}$") && !ForbiddenHeaders.Contains(name.ToLowerInvariant()); }
        private static List<double> Numbers(object list) { return J.Arr(list).Select(v => J.Num(v)).Where(v => v.HasValue).Select(v => v.Value).ToList(); }
        private static string PathOf(object item, string key) { return CheckPath(J.Str(item, key)); }
        private static string CheckPath(string path) {
            path = (path ?? "").Trim();
            if (path.Length == 0) return "";
            if (path.Length > 256 || path.Count(c => c == '.' || c == '[') > 31 || !PathPattern.IsMatch(path)) throw new ArgumentException("路径“" + path + "”不合法：只支持 a.b.c 和 [0] 形式");
            return path;
        }
        private static string Slug(string name) {
            string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 24) slug = slug.Substring(0, 24).Trim('-');
            if (slug.Length == 0) using (var sha = System.Security.Cryptography.SHA256.Create()) slug = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name)), 0, 3).Replace("-", "").ToLowerInvariant();
            return slug;
        }
        // Public origins must use HTTPS. Plain HTTP is allowed only for this machine or a
        // private network (self-hosted gateways). No user info or fragments.
        public static string UrlError(string url) {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "url 必须是完整地址，例如 https://api.example.com/usage";
            if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return "url 不能包含用户名、密码或 # 片段";
            if (uri.Scheme == Uri.UriSchemeHttps) return null;
            if (uri.Scheme == Uri.UriSchemeHttp && IsPrivateHost(uri)) return null;
            return "公网地址必须使用 https；http 只允许本机或局域网地址";
        }
        public static bool IsPrivateHost(Uri uri) {
            if (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress ip; if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out ip)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 127;
        }
        // The usage request covers the last 30 days ending today. Placeholders: {from} {to}
        // (yyyy-MM-dd), {fromUnix} {toUnix} (local midnight of the first day / the day after
        // today, as Unix seconds) and {tz} (UTC offset in seconds).
        public static string UsageUrl(string template, DateTime today, TimeSpan offset) {
            DateTime from = today.Date.AddDays(1 - UsageDays), end = today.Date.AddDays(1);
            long tz = (long)offset.TotalSeconds;
            return (template ?? "").Replace("{from}", HistoryService.DayKey(from)).Replace("{to}", HistoryService.DayKey(today.Date))
                .Replace("{fromUnix}", ((long)(from - Epoch).TotalSeconds - tz).ToString(CultureInfo.InvariantCulture))
                .Replace("{toUnix}", ((long)(end - Epoch).TotalSeconds - tz).ToString(CultureInfo.InvariantCulture))
                .Replace("{tz}", tz.ToString(CultureInfo.InvariantCulture));
        }
        // Dot-path lookup: "data.items[0].used". Missing segments return null.
        public static object Resolve(object root, string path) {
            if (String.IsNullOrEmpty(path)) return null;
            object current = root;
            foreach (Match part in Regex.Matches(path, @"[A-Za-z_][A-Za-z0-9_\-]*|\[\d+\]")) {
                if (current == null) return null;
                if (part.Value.StartsWith("[")) {
                    int index = Int32.Parse(part.Value.Trim('[', ']'), CultureInfo.InvariantCulture);
                    var list = J.Arr(current).ToList(); current = index < list.Count ? list[index] : null;
                } else current = J.Get(current, part.Value);
            }
            return current;
        }
        // The "success" rule: a code outside the ok list is an error, or an expired token when listed.
        public static void Check(CustomProvider p, object root) {
            if (p.CodePath.Length == 0) return;
            double? code = J.Num(Resolve(root, p.CodePath));
            if (!code.HasValue || p.OkCodes.Contains(code.Value)) return;
            if (p.ExpiredCodes.Contains(code.Value)) throw Expired(p);
            // Never echo server messages: they may contain account details.
            throw new ProviderException(p.Name + " 接口未成功（code " + code.Value.ToString(CultureInfo.InvariantCulture) + "）", "error");
        }
        private static ProviderException Expired(CustomProvider p) {
            if (p.ConsoleToken) { Uri console; string host = Uri.TryCreate(p.TokenConsole, UriKind.Absolute, out console) ? console.Host : "控制台"; return new ProviderException("登录已过期：点“重新登录”登录 " + host + " 即可。", "expired"); }
            return new ProviderException("密钥无效或无权限", "expired");
        }
        public static ProviderState Map(CustomProvider p, object root, DateTime nowUtc) {
            var s = new ProviderState { Id = p.Id, Plan = Text(Resolve(root, p.Plan)), Account = Text(Resolve(root, p.Account)) };
            if (s.Plan.Length == 0) s.Plan = p.PlanLabel.Length > 0 ? p.PlanLabel : "自定义";
            foreach (CustomWindow w in p.Windows) {
                double scale = w.Ratio ? 100 : 1; double? used = null;
                double? limit = w.Limit.Length > 0 ? J.Num(Resolve(root, w.Limit)) : null;
                double? usedCount = w.Used.Length > 0 ? J.Num(Resolve(root, w.Used)) : null, left = w.Remaining.Length > 0 ? J.Num(Resolve(root, w.Remaining)) : null;
                if (!usedCount.HasValue && left.HasValue && limit.HasValue) usedCount = limit - left;
                if (w.UsedPercent.Length > 0) { double? v = J.Num(Resolve(root, w.UsedPercent)); if (v.HasValue) used = v * scale; }
                else if (w.RemainingPercent.Length > 0) { double? v = J.Num(Resolve(root, w.RemainingPercent)); if (v.HasValue) used = 100 - v * scale; }
                if (!used.HasValue && limit.HasValue) {
                    if (limit > 0 && usedCount.HasValue) used = usedCount.Value / limit.Value * 100;
                    else if (limit == 0) used = 100;   // a zero allocation is exhausted
                }
                if (!used.HasValue) continue; // a missing value stays unknown, never 0%
                string reset = w.ResetsAt.Length > 0 ? J.Iso(Resolve(root, w.ResetsAt)) : "";
                if (reset.Length == 0 && w.ResetInSeconds.Length > 0) { double? seconds = J.Num(Resolve(root, w.ResetInSeconds)); if (seconds >= 0 && seconds <= 366 * 86400) reset = nowUtc.AddSeconds(seconds.Value).ToString("o"); }
                Parsers.Add(s, w.Label, used.Value, reset, w.WindowHours * 3600);
                if (limit.HasValue && w.Limit.Length > 0) { s.Quotas[s.Quotas.Count - 1].UsedCount = usedCount; s.Quotas[s.Quotas.Count - 1].LimitCount = limit; }
            }
            if (p.Balance.Length > 0) {
                // With lists, every element of every list is one wallet; amounts add up per currency.
                var wallets = p.BalanceLists.Count == 0 ? new List<object> { root } : p.BalanceLists.SelectMany(list => J.Arr(Resolve(root, list))).ToList();
                var sums = new Dictionary<string, double>();
                foreach (object wallet in wallets) {
                    double? amount = J.Num(Resolve(wallet, p.Balance)); if (!amount.HasValue) continue;
                    string currency = p.CurrencyPath.Length > 0 ? Text(Resolve(wallet, p.CurrencyPath)).ToUpperInvariant() : p.Currency;
                    if (!Regex.IsMatch(currency, "^[A-Z]{3}$")) currency = p.Currency.Length > 0 ? p.Currency : "USD";
                    double old; sums.TryGetValue(currency, out old); sums[currency] = old + amount.Value;
                }
                foreach (var pair in sums) s.Balances.Add(new Balance { Currency = pair.Key, Amount = pair.Value });
            }
            if (s.Quotas.Count == 0 && s.Balances.Count == 0) throw new ProviderException("接口返回中找不到定义的字段，请检查路径（没有把缺失数据当作 0）", "error");
            return s;
        }
        // Daily usage rows: one per item (optionally inside groups, e.g. one group per model).
        // A numeric day is a Unix time (local midnight); a text day starts with yyyy-MM-dd.
        public static List<DayUsage> MapUsage(CustomProvider p, object root, TimeZoneInfo zone) {
            CustomUsage u = p.Usage; var days = new Dictionary<string, DayUsage>();
            if (u == null) return new List<DayUsage>();
            var rows = new List<KeyValuePair<object, object>>();
            if (u.Groups.Length > 0) { foreach (object g in J.Arr(Resolve(root, u.Groups))) foreach (object item in J.Arr(Resolve(g, u.Items))) rows.Add(new KeyValuePair<object, object>(g, item)); }
            else foreach (object item in J.Arr(Resolve(root, u.Items))) rows.Add(new KeyValuePair<object, object>(null, item));
            foreach (var row in rows) {
                object item = row.Value;
                string key = DayOf(Resolve(item, u.Day), zone); if (key == null) continue;
                Func<string, double> num = path => path.Length == 0 ? 0 : (J.Num(Resolve(item, path)) ?? 0);
                double input = num(u.Input), cached = num(u.Cached), output = num(u.Output), write = num(u.CacheWrite), requests = num(u.Requests);
                double tokens = u.Tokens.Count > 0 ? u.Tokens.Sum(num) : input + cached + output + write;
                if (tokens <= 0 && requests <= 0) continue;
                string model = u.Model.Length == 0 ? "" : Text(Resolve(item, u.Model)); if (model.Length == 0 && row.Key != null && u.Model.Length > 0) model = Text(Resolve(row.Key, u.Model));
                DayUsage day; if (!days.TryGetValue(key, out day)) days[key] = day = new DayUsage { Day = key, Agent = p.Id };
                day.Tokens += tokens; day.InputTokens += input; day.CachedTokens += cached; day.OutputTokens += output; day.CacheCreationTokens += write; day.Requests += requests;
                ModelPrice price = u.Priced && model.Length > 0 ? Pricing.Find(model) : null;
                double cost = price != null ? Pricing.Cost(price, new TokenUse { Input = input, Output = output, CacheRead = cached, CacheWrite5m = write }) : 0;
                if (price == null) { day.UnpricedTokens += tokens; day.CostKnown = false; }
                day.Cost += cost;
                if (model.Length > 0) {
                    ModelUsage m = day.Models.FirstOrDefault(x => x.Model == model);
                    if (m == null) day.Models.Add(m = new ModelUsage { Model = model });
                    m.Tokens += tokens; m.Cost += cost;
                }
            }
            return days.Values.OrderBy(d => d.Day, StringComparer.Ordinal).ToList();
        }
        private static string DayOf(object value, TimeZoneInfo zone) {
            if (value == null) return null;
            double? seconds = value is string ? null : J.Num(value);
            if (seconds.HasValue) {
                double s = seconds.Value > 100000000000 ? seconds.Value / 1000 : seconds.Value;
                try { return HistoryService.DayKey(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Epoch.AddSeconds(s), DateTimeKind.Utc), zone)); } catch { return null; }
            }
            string text = Text(value); DateTime parsed;
            return DateTime.TryParseExact(text.Length >= 10 ? text.Substring(0, 10) : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ? HistoryService.DayKey(parsed) : null;
        }
        private static string Text(object value) {
            if (value == null || value is IDictionary<string, object> || (value is System.Collections.IEnumerable && !(value is string))) return "";
            string text = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            return text.Length > 60 ? text.Substring(0, 60) : text;
        }
        public static async Task<ProviderState> Fetch(HttpClient client, CustomProvider p, string secret, DateTime nowUtc) {
            string error = UrlError(p.Url); if (error != null) throw new ProviderException(error, "setup");
            if (p.Auth != "none" && String.IsNullOrWhiteSpace(secret))
                throw new ProviderException(p.ConsoleToken ? "还没有登录：在设置里点“登录”，在弹出的页面登录后自动完成。" : "还没有填写密钥：在设置里点“配置”填写。", "setup");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20))) {
                ProviderState s = Map(p, await Get(client, p, p.Url, secret, deadline.Token).ConfigureAwait(false), nowUtc);
                if (p.Usage != null) {
                    // Usage is extra: an expired token fails the platform, anything else only drops the days.
                    try {
                        string url = UsageUrl(p.Usage.Url, DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
                        s.PlatformDays = MapUsage(p, await Get(client, p, url, secret, deadline.Token).ConfigureAwait(false), TimeZoneInfo.Local);
                        s.PlatformAuth = "ok";
                    } catch (ProviderException e) { if (e.State == "expired") throw; s.PlatformAuth = "error"; }
                    catch (OperationCanceledException) { s.PlatformAuth = "error"; }
                }
                return s;
            }
        }
        private static async Task<object> Get(HttpClient client, CustomProvider p, string url, string secret, CancellationToken ct) {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                foreach (var pair in p.Headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                // The secret goes only into the declared header form, never the URL or logs.
                string key = (secret ?? "").Trim();
                if (p.Auth == "bearer") request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                else if (p.Auth == "token") request.Headers.TryAddWithoutValidation("Authorization", "token " + key);
                else if (p.Auth == "x-api-key") request.Headers.TryAddWithoutValidation("X-API-Key", key);
                else if (p.Auth == "header") request.Headers.TryAddWithoutValidation(p.Header, key);
                using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400) throw new ProviderException("接口返回了重定向（HTTP " + status + "）；为防止密钥被转发，不跟随重定向", "error");
                    if (status == 401 || status == 403) throw Expired(p);
                    if (!response.IsSuccessStatusCode) throw new ProviderException(p.Name + " 接口返回 HTTP " + status, "error");
                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) {
                        var buffer = new MemoryStream(); byte[] chunk = new byte[65536]; int read;
                        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0) {
                            buffer.Write(chunk, 0, read);
                            if (buffer.Length > MaxResponseBytes) throw new ProviderException("接口返回超过 1 MB，已中止", "error");
                        }
                        object root;
                        try { root = J.Parse(Encoding.UTF8.GetString(buffer.ToArray())); } catch { throw new ProviderException("接口返回的不是 JSON", "error"); }
                        Check(p, root);
                        return root;
                    }
                }
            }
        }
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    }
}
