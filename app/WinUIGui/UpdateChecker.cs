using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUIGui
{
    /// <summary>
    /// 主程序自动更新：查 GitHub 最新 Release，比较版本号；新版本则下载安装包并交给安装器。
    /// 仓库固定为 Lyrueyn/Lyra-Viewer，Release 资产名以 LyraViewer-Setup-*.exe 为准。
    /// </summary>
    public static class UpdateChecker
    {
        public const string Owner = "Lyrueyn";
        public const string Repo = "Lyra-Viewer";

        public sealed class UpdateInfo
        {
            public Version Version = new(1, 0, 0);
            public string Tag = "";
            public string Notes = "";
            public string AssetName = "";
            public string AssetUrl = "";
        }

        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v ?? new Version(1, 0, 0);
                }
                catch { return new Version(1, 0, 0); }
            }
        }

        public static string CurrentVersionText
        {
            get
            {
                var v = CurrentVersion;
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        public static async Task<UpdateInfo?> CheckAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LyraViewer-UpdateChecker/1.0");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
                string json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
                if (latest <= CurrentVersion) return null;

                string assetName = "", assetUrl = "";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        string dl = a.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        { assetName = name; assetUrl = dl; break; }
                        if (string.IsNullOrEmpty(assetName) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        { assetName = name; assetUrl = dl; }
                    }
                }
                if (string.IsNullOrEmpty(assetUrl)) return null;
                return new UpdateInfo { Version = latest, Tag = tag, Notes = notes, AssetName = assetName, AssetUrl = assetUrl };
            }
            catch { return null; }
        }

        public static async Task<string> DownloadAsync(string url, string assetName, Action<double>? progress = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "LyraViewerUpdate");
            Directory.CreateDirectory(dir);
            string safe = string.IsNullOrWhiteSpace(assetName) ? "LyraViewer-Setup.exe" : Path.GetFileName(assetName);
            string path = Path.Combine(dir, safe);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LyraViewer-UpdateChecker/1.0");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            byte[] buf = new byte[81920];
            long done = 0;
            while (true)
            {
                int n = await src.ReadAsync(buf);
                if (n <= 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (progress != null && total > 0) progress(done * 100.0 / total);
            }
            return path;
        }
    }
}
