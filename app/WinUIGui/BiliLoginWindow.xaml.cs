// ★ B 站登录窗口：用系统自带 WebView2 打开登录页 → 自动抓 Cookie → 保存到后端
// 为什么用 WebView2：Windows 10/11 自带运行时，不需要 Node.js / Playwright，安装版也能用
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WinUIGui;

public sealed partial class BiliLoginWindow : Window
{
    private Microsoft.UI.Xaml.DispatcherTimer? _poll;
    private bool _grabbing;

    /// <summary>是否已成功抓取并保存 Cookie（MainWindow 用这个判断结果）</summary>
    public bool Saved { get; private set; }

    public BiliLoginWindow()
    {
        this.InitializeComponent();
        this.Title = "登录 B 站 · 自动获取 Cookie";

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

        // 每 2 秒尝试抓一次（登录完成后 WebView 里就有 SESSDATA 了）
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
            Wv.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try { Hint.Text = "页面已加载：请登录 B 站。登录成功后会自动抓取 Cookie…"; } catch { }
                await TryGrabAsync(quiet: true);
            };
            Hint.Text = "正在打开 B 站登录页…";
            Wv.CoreWebView2.Navigate("https://passport.bilibili.com/login");
        }
        catch (Exception ex)
        {
            Hint.Text = "✗ 初始化内置浏览器失败：" + ex.Message + "\n（可改用手动粘贴 Cookie：浏览器 F12 → Application → Cookies → 复制 SESSDATA）";
        }
    }

    private async void ManualGrab_Click(object sender, RoutedEventArgs e) => await TryGrabAsync(quiet: false);

    private void Close_Click(object sender, RoutedEventArgs e) { try { this.Close(); } catch { } }

    /// <summary>从 WebView2 里读 bilibili 域下的 Cookie，找到 SESSDATA 就保存。</summary>
    private async Task TryGrabAsync(bool quiet)
    {
        if (_grabbing) return;
        if (Wv.CoreWebView2 == null) return;
        _grabbing = true;
        try
        {
            var cookies = await Wv.CoreWebView2.CookieManager.GetCookiesAsync("https://www.bilibili.com");
            var parts = new List<string>();
            bool hasSess = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cookies)
            {
                try
                {
                    if (c == null) continue;
                    if (string.IsNullOrEmpty(c.Name)) continue;
                    string dom = c.Domain ?? "";
                    if (!dom.Contains("bilibili")) continue;
                    if (!seen.Add(c.Name)) continue;   // 同名只取一条
                    parts.Add(c.Name + "=" + (c.Value ?? ""));
                    if (string.Equals(c.Name, "SESSDATA", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Value))
                        hasSess = true;
                }
                catch { }
            }

            if (!hasSess)
            {
                if (!quiet)
                    Hint.Text = $"还没检测到登录（已读到 {cookies.Count} 条 Cookie）。请先在窗口里完成登录，再点「登录好了，立即抓取」。";
                return;
            }

            _poll?.Stop();
            string cookieStr = string.Join("; ", parts);
            Hint.Text = "✓ 检测到登录 Cookie（" + parts.Count + " 项），正在保存…";
            try
            {
                string resp = await Backend.SaveCookieAsync(cookieStr);
                bool ok = false;
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(resp);
                    ok = d.RootElement.TryGetProperty("has_cookie", out var hc) && hc.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch { }
                Saved = true;
                Hint.Text = ok
                    ? "✓ Cookie 已保存（" + parts.Count + " 项），本窗口即将关闭"
                    : "⚠ 已抓取 Cookie 但后端保存返回异常：" + resp;
            }
            catch (Exception ex)
            {
                Hint.Text = "⚠ 抓到了 Cookie 但保存失败：" + ex.Message;
            }

            await Task.Delay(600);
            try { this.Close(); } catch { }
        }
        catch (Exception ex)
        {
            if (!quiet) Hint.Text = "抓取失败：" + ex.Message;
        }
        finally { _grabbing = false; }
    }
}
