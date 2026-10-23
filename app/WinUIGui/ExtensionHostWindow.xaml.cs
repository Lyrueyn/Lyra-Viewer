using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace WinUIGui
{
    /// <summary>
    /// 通用扩展宿主：扩展包是网页（index.html），主程序只负责用 WebView2 打开。
    /// 这样扩展不需要注入 DLL，也不会常驻播放器内存；关闭窗口即释放。
    /// </summary>
    public sealed partial class ExtensionHostWindow : Window
    {
        private readonly string _extensionDir;
        private readonly string _entry;
        private readonly string _host = "lyra-extension.local";

        public ExtensionHostWindow(string extensionId, string name, string dir, string entry)
        {
            InitializeComponent();
            _extensionDir = dir;
            _entry = string.IsNullOrWhiteSpace(entry) ? "index.html" : entry;
            Title = "璃鸢镜 · " + (string.IsNullOrWhiteSpace(name) ? extensionId : name);
            try { AppWindow.Resize(new SizeInt32(1180, 820)); } catch { }
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await Wv.EnsureCoreWebView2Async();
                if (Wv.CoreWebView2 == null) return;
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, _extensionDir,
                    CoreWebView2HostResourceAccessKind.Allow);
                string rel = _entry.Replace('\\', '/').TrimStart('/');
                Wv.CoreWebView2.Navigate("https://" + _host + "/" + string.Join("/",
                    rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)));
            }
            catch (Exception ex)
            {
                try { Title = "璃鸢镜 · 扩展加载失败：" + ex.Message; } catch { }
            }
        }
    }
}
