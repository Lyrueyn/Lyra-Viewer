using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace WinUIGui
{
    /// <summary>
    /// 通用扩展宿主：扩展包是网页（index.html），主程序只负责用 WebView2 打开。
    /// 可选：把一本 EPUB 的字节通过 WebView2 消息交给扩展（不注入 DLL、关闭即释放）。
    /// </summary>
    public sealed partial class ExtensionHostWindow : Window
    {
        private readonly string _extensionDir;
        private readonly string _entry;
        private readonly string? _bookPath;
        private readonly string _host = "lyra-extension-" + Guid.NewGuid().ToString("N").Substring(0,8) + ".local";
        private readonly bool _isPdf;
        private bool _sentBook;
        private bool _overlayHidden;
        private bool _pendingOverlayClick;

        public ExtensionHostWindow(string extensionId, string name, string dir, string entry, string? bookPath = null)
        {
            InitializeComponent();
            _extensionDir = dir;
            _entry = string.IsNullOrWhiteSpace(entry) ? "index.html" : entry;
            _bookPath = bookPath;
            _isPdf = !string.IsNullOrEmpty(bookPath) && bookPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            Title = "璃鸢镜 · " + (string.IsNullOrWhiteSpace(name) ? extensionId : name);
            try
            {
                // ★ 和视频窗口默认尺寸一致：工作区 88% 宽 / 93% 高，居中（不默认全屏）
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var wa = area.WorkArea;
                int w = (int)Math.Min(wa.Width - 60, wa.Width * 0.88);
                int h = (int)Math.Min(wa.Height - 60, wa.Height * 0.93);
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));
            }
            catch { try { AppWindow.Resize(new SizeInt32(1400, 900)); } catch { } }
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await Wv.EnsureCoreWebView2Async();
                if (Wv.CoreWebView2 == null) return;
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, _extensionDir,
                    CoreWebView2HostResourceAccessKind.Allow);
                Wv.CoreWebView2.Settings.IsWebMessageEnabled = true;
                Wv.CoreWebView2.WebMessageReceived += (snd, args) =>
                {
                    try
                    {
                        string raw = args.WebMessageAsJson ?? "";
                        if (raw.Contains("lyra-ready")) _ = SendBookAsync();
                        if (raw.Contains("lyra-book-ready")) DispatcherQueue.TryEnqueue(HideLoadingOverlay);
                    }
                    catch { }
                };
                Wv.CoreWebView2.NavigationCompleted += async (snd2, ev2) =>
                {
                    await Task.Delay(1200);   // 普通扩展兜底；EPUB/PDF 会提前发 ready
                    DispatcherQueue.TryEnqueue(HideLoadingOverlay);
                    if (!_isPdf)
                    {
                        await SendBookScriptAsync();
                        await SendBookAsync();
                    }
                };
                string folder = _extensionDir;
                string query = "";
                if (!string.IsNullOrEmpty(_bookPath) && File.Exists(_bookPath))
                {
                    // 同源目录：扩展文件 + 文档，扩展用 ?book=book.epub / ?file=document.pdf 直接 fetch
                    string temp = Path.Combine(Path.GetTempPath(), "lyra_ext_host", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    CopyDir(_extensionDir, temp);
                    string inner = _isPdf ? "document.pdf" : "book.epub";
                    File.Copy(_bookPath!, Path.Combine(temp, inner), true);
                    folder = temp;
                    query = _isPdf
                        ? "?file=document.pdf&name=" + Uri.EscapeDataString(Path.GetFileName(_bookPath!)) + "&t=" + Environment.TickCount64
                        : "?book=book.epub&t=" + Environment.TickCount64;
                }
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, folder, CoreWebView2HostResourceAccessKind.Allow);
                string rel = _entry.Replace('\\', '/').TrimStart('/');
                Wv.CoreWebView2.Navigate("https://" + _host + "/" + string.Join("/",
                    rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + query);
            }
            catch (Exception ex)
            {
                try { Title = "璃鸢镜 · 扩展加载失败：" + ex.Message; } catch { }
            }
        }

        private async Task SendBookScriptAsync()
        {
            if (_sentByScript || string.IsNullOrEmpty(_bookPath) || !File.Exists(_bookPath)) return;
            _sentByScript = true;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(_bookPath!);
                string b64 = Convert.ToBase64String(bytes);
                await Wv.CoreWebView2.ExecuteScriptAsync(
                    "window.lyraParseBookData && window.lyraParseBookData(Uint8Array.from(atob('" + b64 + "'),c=>c.charCodeAt(0)).buffer,'book.epub'," + bytes.Length + ");");
            }
            catch { }
        }

        private bool _sentByScript;

        private async Task SendBookAsync()
        {
            if (_sentBook || string.IsNullOrEmpty(_bookPath) || !File.Exists(_bookPath)) return;
            _sentBook = true;
            try
            {
                await Task.Delay(350);
                byte[] bytes = await File.ReadAllBytesAsync(_bookPath!);
                string json = JsonSerializer.Serialize(new
                {
                    type = "openBook",
                    name = Path.GetFileName(_bookPath),
                    data = Convert.ToBase64String(bytes),
                });
                Wv.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                try { Title = "璃鸢镜 · EPUB 发送失败：" + ex.Message; } catch { }
            }
        }

        private void LoadingOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _pendingOverlayClick = true;   // 加载中点击：记住一次，稳定后补翻下一页
        }

        private async void HideLoadingOverlay()
        {
            if (_overlayHidden) return;
            _overlayHidden = true;
            try { LoadingOverlay.Visibility = Visibility.Collapsed; } catch { }
            if (_pendingOverlayClick)
            {
                _pendingOverlayClick = false;
                try
                {
                    await Task.Delay(120);
                    await Wv.CoreWebView2.ExecuteScriptAsync("document.getElementById('main')&&document.getElementById('main').click();");
                }
                catch { }
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}