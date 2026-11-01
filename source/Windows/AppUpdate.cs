using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace Headroom {
    // Only stable releases from the project's GitHub repository are offered. Installer downloads
    // require confirmation and checksum verification; portable copies open the release page.
    internal static class AppUpdate {
        public const string Api = "https://api.github.com/repos/chinbor/headroom/releases/latest";
        public sealed class Release { public string Version, Notes; }
        public static Release Parse(object json) {
            if (J.Get(json, "draft") is bool && (bool)J.Get(json, "draft")) throw new FormatException();
            if (J.Get(json, "prerelease") is bool && (bool)J.Get(json, "prerelease")) throw new FormatException();
            string tag = J.Str(json, "tag_name");
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")) throw new FormatException();
            Version version; if (!Version.TryParse(tag.Substring(1), out version)) throw new FormatException();
            string body = J.Str(json, "body");
            var match = Regex.Match(body, @"(?ms)^## 本版更新\s*\r?\n(.*?)(?=^## |\z)");
            string notes = match.Success ? match.Groups[1].Value.Trim() : "";
            if (notes.Length > 1200) notes = notes.Substring(0, 1200) + "…";
            return new Release { Version = version.ToString(3), Notes = notes };
        }
        public static bool IsNewer(string latest, string current) {
            Version a, b; return Version.TryParse(latest, out a) && Version.TryParse(current, out b) && a > b;
        }
        public static string ReleaseUrl(Release release) { return "https://github.com/chinbor/headroom/releases/tag/v" + release.Version; }
        public static bool ValidChecksum(byte[] installer, string sums, string filename) {
            var match = Regex.Match(sums, @"(?m)^([a-fA-F0-9]{64})\s+\*?" + Regex.Escape(filename) + @"\s*$");
            if (!match.Success) return false;
            using (var sha = SHA256.Create()) return String.Equals(BitConverter.ToString(sha.ComputeHash(installer)).Replace("-", ""), match.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }
        public static async Task<string> DownloadInstaller(Release release, AppConfig config) {
            string name = "headroom-setup-" + release.Version + ".exe";
            string root = "https://github.com/chinbor/headroom/releases/download/v" + release.Version + "/";
            string proxy = ProviderService.ResolveProxy(config.Proxy);
            var handler = new HttpClientHandler { UseProxy = proxy.Length > 0, UseCookies = false };
            if (handler.UseProxy) handler.Proxy = new WebProxy(proxy);
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 100 * 1024 * 1024 }) {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
                byte[] installer = await client.GetByteArrayAsync(root + name);
                string sums = await client.GetStringAsync(root + "SHA256SUMS.txt");
                if (!ValidChecksum(installer, sums, name)) throw new InvalidDataException("安装包校验失败");
                string dir = Path.Combine(Path.GetTempPath(), "headroom-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name); File.WriteAllBytes(path, installer); return path;
            }
        }
    }
    public sealed partial class MonitorPanel {
        private DateTime nextUpdateCheck = DateTime.MinValue;
        private bool checkingUpdate;
        private async Task CheckAppUpdate(bool manual) {
            if (demo || checkingUpdate) return;
            if (!manual && (!config.UpdateCheck || DateTime.UtcNow < nextUpdateCheck)) return;
            checkingUpdate = true; nextUpdateCheck = DateTime.UtcNow.AddDays(1);
            bool confirmed = false;
            try {
                AppUpdate.Release release;
                using (var service = new ProviderService(config)) release = AppUpdate.Parse(await service.GetJson(AppUpdate.Api));
                if (!AppUpdate.IsNewer(release.Version, AppInfo.Version)) {
                    if (manual) MessageBox.Show("当前已是最新版本（" + AppInfo.Version + "）。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string text = "当前版本：" + AppInfo.Version + "\n最新版本：" + release.Version;
                if (release.Notes.Length > 0) text += "\n\n本版更新：\n" + release.Notes;
                bool installed = !File.Exists(Path.Combine(Store.Root, "portable.txt")) && (File.Exists(Path.Combine(Store.Root, "installed.txt")) || Store.PathsEqual(Store.Root, Store.ReadInstallPath()));
                text += installed ? "\n\n下载并校验安装包，然后退出 Headroom 并启动安装程序？" : "\n\n打开官方发布页面下载免安装版？更新时请保留 data 文件夹。";
                if (MessageBox.Show(text, "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) {
                    confirmed = true;
                    if (installed) {
                        string installer = await AppUpdate.DownloadInstaller(release, config);
                        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                        Quit();
                    } else Process.Start(new ProcessStartInfo(AppUpdate.ReleaseUrl(release)) { UseShellExecute = true });
                }
            } catch {
                if (manual || confirmed) MessageBox.Show("检查、下载或启动更新失败，请检查网络连接，或到 GitHub 发布页面手动下载。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            } finally { checkingUpdate = false; }
        }
    }
}
