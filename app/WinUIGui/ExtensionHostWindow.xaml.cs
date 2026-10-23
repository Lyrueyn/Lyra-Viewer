using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
        private bool _sentBook;

        public ExtensionHostWindow(string extensionId, string name, string dir, string entry, string? bookPath = null)
        {
            InitializeComponent();
            _extensionDir = dir;
            _entry = string.IsNullOrWhiteSpace(entry) ? "index.html" : entry;
            _bookPath = bookPath;
            Title = "璃鸢镜 · " + (string.IsNullOrWhiteSpace(name) ? extensionId : name);
            try
            {
                // 阅读器/扩展窗口默认最大化，避免小窗口阅读
                if (AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
            }
            catch
            {
                try
                {
                    var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                    var wa = area.WorkArea;
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(wa.X, wa.Y, wa.Width, wa.Height));
                }
                catch { }
            }
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await Wv.EnsureCoreWebView2Async();
                if (Wv.CoreWebView2 == null) return;
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, _extensionDir,
                    CoreWebView2HostResourceAccessKind.Allow);
                Wv.CoreWebView2.NavigationCompleted += async (_, _) =>
                {
                    await Task.Delay(500);
                    await SendBookScriptAsync();
                    await SendBookAsync();
                };
                string folder = _extensionDir;
                string query = "";
                if (!string.IsNullOrEmpty(_bookPath) && File.Exists(_bookPath))
                {
                    // 同源目录：扩展文件 + book.epub，扩展用 ?book=book.epub 直接 fetch
                    string temp = Path.Combine(Path.GetTempPath(), "lyra_epub_host", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    CopyDir(_extensionDir, temp);
                    File.Copy(_bookPath!, Path.Combine(temp, "book.epub"), true);
                    folder = temp;
                    query = "?book=book.epub&t=" + Environment.TickCount64;
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

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}