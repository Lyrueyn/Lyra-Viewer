// ★ YouTube 登录窗口：用系统自带 WebView2 打开 Google 登录页 → 登录成功后抓 Cookie → 存后端
// 目的：解决 YouTube 的 "Sign in to confirm you're not a bot" 风控
//
// ★ 关键教训（用户实测踩坑）：只抓到 .google.com 的登录 Cookie 是**没用的** ——
//   YouTube 只认 .youtube.com 域下的登录 Cookie（SID / __Secure-3PSID / LOGIN_INFO …）。
//   所以本窗口：
//     ① 登录判定必须看到 **youtube 域** 的登录 Cookie
//     ② 检测到 Google 已登录但 YouTube 还没登录时，自动跳去 youtube.com 触发登录态同步
//     ③ 保存后由后端做真·校验（请求 youtube.com 首页看 "LOGGED_IN":true），
//        校验不过就不关窗、提示用户继续操作
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUIGui;

public sealed partial class YouTubeLoginWindow : Window
{
    private Microsoft.UI.Xaml.DispatcherTimer? _poll;
    private bool _grabbing;
    private bool _autoSaveDone;          // 自动保存只做一次（避免反复保存）
    private bool _syncedToYoutube;       // 是否已自动跳过一次 youtube.com
    private DateTime _lastSyncNav = DateTime.MinValue;

    /// <summary>是否已成功抓取并保存 Cookie（主窗用这个判断结果）</summary>
    public bool Saved { get; private set; }
    /// <summary>后端校验的真实登录态（true = YouTube 真的登录了）</summary>
    public bool LoggedIn { get; private set; }

    public YouTubeLoginWindow()
    {
        this.InitializeComponent();
        this.Title = "登录 YouTube · 自动获取 Cookie";

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            aw.Resize(new Windows.Graphics.SizeInt32(1180, 860));
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            aw.Move(new Windows.Graphics.PointInt32(wa.WorkArea.X + (wa.WorkArea.Width - 1180) / 2,
                                                    wa.WorkArea.Y + Math.Max(0, (wa.WorkArea.Height - 860) / 2)));
        }
        catch { }

        Wv.Loaded += async (_, _) => await InitWebViewAsync();

        _poll = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await TryGrabAsync(quiet: true);
        _poll.Start();

        this.Closed += (_, _) =>
        {
            try { _poll?.Stop(); } catch { }
        };
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            Hint.Text = "正在初始化内置浏览器…";
            await Wv.EnsureCoreWebView2Async();
            if (Wv.CoreWebView2 == null) { Hint.Text = "✗ 内置浏览器初始化失败（WebView2 运行时缺失？）"; return; }
            Wv.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                try
                {
                    if (!e.IsSuccess) Hint.Text = "页面加载失败：" + (e.WebErrorStatus.ToString());
                    else Hint.Text = "请登录 Google 账号（登录后会跳到 YouTube，等右上角出现头像即成功）…";
                }
                catch { }
                await TryGrabAsync(quiet: true);
            };
            Hint.Text = "正在打开 YouTube 登录页…";
            // ★ service=youtube：登录成功后直接回到 YouTube，让 YouTube 建立自己的登录 Cookie
            Wv.CoreWebView2.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F");
        }
        catch (Exception ex)
        {
            Hint.Text = "✗ 初始化内置浏览器失败：" + ex.Message;
        }
    }

    private async void ManualGrab_Click(object sender, RoutedEventArgs e)
    {
        _autoSaveDone = false;
        await TryGrabAsync(quiet: false);
    }

    private void Close_Click(object sender, RoutedEventArgs e) { try { this.Close(); } catch { } }

    /// <summary>youtube 域的登录 Cookie 名字</summary>
    private static readonly HashSet<string> YtLoginNames = new(StringComparer.OrdinalIgnoreCase)
    { "SID", "__Secure-1PSID", "__Secure-3PSID", "LOGIN_INFO", "SAPISID", "__Secure-1PAPISID", "__Secure-3PAPISID" };

    private async Task TryGrabAsync(bool quiet)
    {
        if (_grabbing) return;
        if (Wv.CoreWebView2 == null) return;
        _grabbing = true;
        try
        {
            var all = new List<Microsoft.Web.WebView2.Core.CoreWebView2Cookie>();
            foreach (var url in new[] { "https://www.youtube.com", "https://accounts.google.com",
                                        "https://www.google.com", "https://accounts.youtube.com" })
            {
                try
                {
                    var got = await Wv.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    if (got != null) all.AddRange(got);
                }
                catch { }
            }

            var items = new List<object>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool ytLoggedIn = false, googleLoggedIn = false;
            foreach (var c in all)
            {
                try
                {
                    if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                    string dom = c.Domain ?? "";
                    if (!(dom.Contains("youtube") || dom.Contains("google"))) continue;
                    if (string.IsNullOrEmpty(c.Value)) continue;
                    string key = dom + "|" + c.Name;
                    var item = new
                    {
                        name = c.Name,
                        value = c.Value,
                        domain = dom,
                        path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                        secure = c.IsSecure,
                        httpOnly = c.IsHttpOnly,
                        expires = 4102444800L,
                    };
                    if (seen.TryGetValue(key, out int idx)) items[idx] = item;
                    else { seen[key] = items.Count; items.Add(item); }

                    bool isYtDomain = dom.Contains("youtube");
                    if (isYtDomain && YtLoginNames.Contains(c.Name)) ytLoggedIn = true;
                    if (!isYtDomain && YtLoginNames.Contains(c.Name)) googleLoggedIn = true;
                }
                catch { }
            }

            // ① 还没到 YouTube 登录态：给提示；如果 Google 已登录则自动跳 youtube.com 触发同步
            if (!ytLoggedIn)
            {
                if (googleLoggedIn && (!_syncedToYoutube || (DateTime.UtcNow - _lastSyncNav).TotalSeconds > 20))
                {
                    _syncedToYoutube = true;
                    _lastSyncNav = DateTime.UtcNow;
                    try
                    {
                        Hint.Text = "已登录 Google ✓ 正在打开 youtube.com 让 YouTube 建立登录态（等右上角出现你的头像）…";
                        Wv.CoreWebView2.Navigate("https://www.youtube.com/");
                    }
                    catch { }
                }
                else if (!quiet)
                {
                    Hint.Text = googleLoggedIn
                        ? $"Google 已登录，但 YouTube 登录态还没同步（已读到 {items.Count} 条 Cookie）。请在窗口里打开 youtube.com 并确认右上角显示头像，再点「登录好了，立即抓取」。"
                        : $"还没检测到登录（已读到 {items.Count} 条 Cookie）。请先登录 Google 账号。";
                }
                return;
            }

            // ② 已有 YouTube 登录态 → 保存（自动只做一次）
            if (_autoSaveDone && quiet) return;
            _autoSaveDone = true;
            _poll?.Stop();
            Hint.Text = $"✓ 已抓到 YouTube 登录 Cookie（{items.Count} 项），正在保存并校验…";

            string payload = JsonSerializer.Serialize(new { cookies = items });
            try
            {
                string resp = await Backend.SaveYtCookieAsync(payload);
                bool ok = false, loggedIn = false; int savedCount = 0;
                try
                {
                    using var d = JsonDocument.Parse(resp);
                    ok = d.RootElement.TryGetProperty("ok", out var oe) && oe.ValueKind == JsonValueKind.True;
                    loggedIn = d.RootElement.TryGetProperty("logged_in", out var li) && li.ValueKind == JsonValueKind.True;
                    if (d.RootElement.TryGetProperty("count", out var ce) && ce.ValueKind == JsonValueKind.Number) savedCount = ce.GetInt32();
                }
                catch { }
                Saved = ok;
                LoggedIn = loggedIn;

                if (ok && loggedIn)
                {
                    Hint.Text = $"✓ 保存成功（{savedCount} 条），且校验 YouTube **已登录**！本窗口即将关闭";
                    await Task.Delay(900);
                    try { this.Close(); } catch { }
                    return;
                }

                // 保存成功但校验未登录 → 不关窗，继续引导
                Hint.Text = ok
                    ? $"⚠ 已保存 {savedCount} 条 Cookie，但后端校验 YouTube **仍未登录**。请确认窗口里已打开 youtube.com 且右上角显示你的头像，然后点「登录好了，立即抓取」。"
                    : "⚠ 已抓取 Cookie 但后端保存返回异常：" + resp;
                _poll?.Start();
            }
            catch (Exception ex)
            {
                Hint.Text = "⚠ 抓到了 Cookie 但保存失败：" + ex.Message;
                _poll?.Start();
            }
        }
        catch (Exception ex)
        {
            if (!quiet) Hint.Text = "抓取失败：" + ex.Message;
        }
        finally { _grabbing = false; }
    }
}
