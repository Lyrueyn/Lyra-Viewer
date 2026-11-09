using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace WinUIGui
{
    /// <summary>
    /// 通用扩展宿主：网页扩展 + 可选的同源文档（EPUB/PDF）。
    /// 新版 EPUB 阅读器通过 lyra-reader 协议在同一窗口内完成跨窗口持久化；
    /// 旧 EPUB/PDF 仍通过网址参数打开，并兼容其 lyra-book-ready 就绪通知。
    /// </summary>
    public sealed partial class ExtensionHostWindow : Window
    {
        // 所有扩展共享的静态资源域名：阅读器可同源 iframe 已安装的 layer 扩展
        // （例如 HTML_Editor）。映射整个 extensions 根目录，不复制文件。
        private const string SharedExtensionsHost = "lyra-extensions.local";
        private const string BookAssetsHost = "lyra-book-assets.local";

        private readonly string _extensionId;
        private readonly string _extensionDir;
        private readonly string _entry;
        private readonly string? _bookPath;
        private readonly string? _projectRoot;
        private readonly bool _isProject;
        private readonly string _projectEntry = "";
        private readonly bool _startEdit;
        private string? _bookAssetsRoot;
        // ★ 固定虚拟域名（按扩展 id）：网页的本地存储按“网址来源”隔离，
        // 以前每开一次窗口换一个随机域名，扩展存下的进度/设置下次就找不到了。
        private readonly string _host;
        private readonly ExtensionCatalog.HostSpec? _hostSpec;
        private readonly bool _hasBook;
        private ReaderStateStore? _store;
        private string _sessionId = Guid.NewGuid().ToString("N");
        private string _loadId = Guid.NewGuid().ToString("N");
        private string? _tempDir;
        private bool _overlayHidden;
        private bool _isActive;
        private bool _bookReady;
        private bool _bookFailed;
        private bool _navComplete;
        private bool _closeReady;
        private bool _finalizePending;
        private string _finalizeRequestId = "";
        private bool _closeStarted;
        private bool _supportsFinalize;   // 收到过当前协议的合法消息 = 新版扩展，关闭时值得等 finalize；PDF 0.3.0 等旧扩展不走等待
        private bool _suppressNavigationRotation;
        private long _activationEpoch;
        private long _navEpoch;
        private readonly List<DispatcherTimer> _timers = new();
        private DispatcherTimer? _closeTimer;

        public ExtensionHostWindow(string extensionId, string name, string dir, string entry, string? bookPath = null)
            : this(extensionId, name, dir, entry, bookPath, null, null, false)
        {
        }

        public ExtensionHostWindow(string extensionId, string name, string dir, string entry,
                                   string? bookPath, ExtensionCatalog.HostSpec? hostSpec, string? projectEntry = null,
                                     bool startEdit = false)
        {
            InitializeComponent();
            _extensionId = extensionId;
            _host = StableHostFor(extensionId);
            _extensionDir = dir;
            _entry = string.IsNullOrWhiteSpace(entry) ? "index.html" : entry;
            _bookPath = bookPath;
            _hostSpec = hostSpec;
            _isProject = !string.IsNullOrEmpty(bookPath) && Directory.Exists(bookPath);
            _projectRoot = _isProject ? Path.GetFullPath(bookPath!) : null;
            _projectEntry = (_isProject ? (projectEntry ?? "") : "").Replace('\\', '/').TrimStart('/');
            _startEdit = startEdit;
            _hasBook = !string.IsNullOrEmpty(bookPath) && File.Exists(bookPath);
            Title = "璃鸢镜 · " + (string.IsNullOrWhiteSpace(name) ? extensionId : name);
            if (_isProject) Title = "璃鸢镜 · " + Path.GetFileName(_projectRoot!.TrimEnd(Path.DirectorySeparatorChar));
            try
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var wa = area.WorkArea;
                int w = (int)Math.Min(wa.Width - 60, wa.Width * 0.88);
                int h = (int)Math.Min(wa.Height - 60, wa.Height * 0.93);
                AppWindow.MoveAndResize(new RectInt32(
                    wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));
            }
            catch { try { AppWindow.Resize(new SizeInt32(1400, 900)); } catch { } }
            try { _store = new ReaderStateStore(); }
            catch (Exception ex) { _store = null; LogHost("STORE_INIT_FAILED " + ex.Message); }
            try
            {
                Activated += (_, args) =>
                {
                    _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
                    // 每次激活/失活都推进 epoch；旧的 interact 请求到达后不会因“现在又激活了”而复活
                    _activationEpoch++;
                    LogHost("ACTIVATED active=" + _isActive + " epoch=" + _activationEpoch);
                    PostHostState();
                };
            }
            catch { }
            try { AppWindow.Closing += OnAppWindowClosing; } catch { }
            Closed += (_, _) => { try { Wv.CoreWebView2?.Stop(); } catch { } CleanupTemp(); };
        }

        private async void Root_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_closeStarted) return;   // 关闭已开始：不再启动初始化/导航
                await Wv.EnsureCoreWebView2Async();
                if (Wv.CoreWebView2 == null || _closeStarted) return;
                Wv.CoreWebView2.Settings.IsWebMessageEnabled = true;
                // PDF/EPUB 阅读时不要 WebView2 默认右键菜单（返回/刷新/另存为/打印/检查）。
                Wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Wv.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                try
                {
//                    Wv.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
//                    Wv.CoreWebView2.WebResourceRequested += OnBookAssetsRequested;
                }
                catch { }
                Wv.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                Wv.CoreWebView2.NavigationStarting += OnNavigationStarting;
                // 共享扩展根目录：layer 扩展（HTML_Editor 等）可通过固定域名被当前窗口嵌入。
                try
                {
                    var extRoot = ExtensionCatalog.ExtensionsDir;
                    if (Directory.Exists(extRoot))
                        Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            SharedExtensionsHost, extRoot, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { }
                NavigateToExtension();
            }
            catch (Exception ex)
            {
                ShowLoadingError("扩展加载失败：" + ex.Message, false);
            }
        }

        private void NavigateToExtension()
        {
            try
            {
                if (_closeStarted) return;   // 关闭后禁止延后的初始化导航续跑
                _sessionId = Guid.NewGuid().ToString("N");
                _loadId = Guid.NewGuid().ToString("N");
                _navEpoch++;
                _suppressNavigationRotation = true;
                _bookReady = false;
                _bookFailed = false;
                _navComplete = false;
                _overlayHidden = false;
                ShowLoading("正在打开…");
                string folder = _extensionDir;
                string query = "?sessionId=" + Uri.EscapeDataString(_sessionId) + "&loadId=" + Uri.EscapeDataString(_loadId);
                query += "&extensionsOrigin=" + Uri.EscapeDataString("https://" + SharedExtensionsHost);
                    query += "&bookAssetsOrigin=" + Uri.EscapeDataString("https://" + BookAssetsHost);
                    if (_isProject)
                    {
                        query += "&project=1&projectName=" + Uri.EscapeDataString(Path.GetFileName((_projectRoot ?? "").TrimEnd(Path.DirectorySeparatorChar)));
                        if (!string.IsNullOrWhiteSpace(_projectEntry)) query += "&projectEntry=" + Uri.EscapeDataString(_projectEntry);
                    }
                query += "&extensionId=" + Uri.EscapeDataString(_extensionId);
                if (string.Equals(_extensionId, "html-reader", StringComparison.OrdinalIgnoreCase))
                {
                    bool editorAvailable = false;
                    try
                    {
                        var defs = ExtensionCatalog.LoadDefinitions();
                        bool hasEditorHost = defs.Any(d => d.IsEditorHost);
                        bool hasHtmlPlugin = defs.Any(d => d.IsEditorPlugin &&
                            string.Equals(d.Info.Id, "html-editor", StringComparison.OrdinalIgnoreCase));
                        editorAvailable = hasEditorHost && hasHtmlPlugin;
                    }
                    catch { }
                    query += "&editorEnabled=" + (editorAvailable ? "1" : "0");
                      if (_startEdit) query += "&startEdit=1";
                }
                // 网页扩展需要调用本机 AI 后端（如 PDF 页面 Real-ESRGAN 超分）时，把当前实际端口传过去，
                // 不能假设固定 8765（多实例时端口会顺延）。
                try { query += "&backend=" + Uri.EscapeDataString(Backend.BaseUrl); } catch { }
                if (_hasBook)
                {
                    CleanupTemp();
                    _tempDir = Path.Combine(Path.GetTempPath(), "lyra_ext_host", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_tempDir);
                    CopyDir(_extensionDir, _tempDir);
                    // ★ 清单接口：innerName/fileParam/keepMtime 由 extension.json 提供；
                    //   旧版 EPUB/PDF/TXT 清单没写时按扩展 id 兼容。
                    string extId = (_extensionId ?? "").ToLowerInvariant();
                    string inner = !string.IsNullOrWhiteSpace(_hostSpec?.InnerName)
                        ? _hostSpec!.InnerName
                        : extId == "epub" ? "book.epub"
                        : extId == "pdf" ? "document.pdf"
                        : extId == "txt" ? "document.txt"
                        : "document" + Path.GetExtension(_bookPath!);
                    string fileParam = !string.IsNullOrWhiteSpace(_hostSpec?.FileParam)
                        ? _hostSpec!.FileParam
                        : extId == "epub" ? "book" : "file";
                    bool keepMtime = _hostSpec?.KeepMtime ?? (extId == "pdf" || extId == "txt");
                    File.Copy(_bookPath!, Path.Combine(_tempDir, inner), true);
                    folder = _tempDir;
                    string realName = Path.GetFileName(_bookPath!);
                    query += "&name=" + Uri.EscapeDataString(realName);
                    if (keepMtime)
                    {
                        try { query += "&mtime=" + new DateTimeOffset(File.GetLastWriteTimeUtc(_bookPath!)).ToUnixTimeMilliseconds(); } catch { }
                    }
                    query += "&" + Uri.EscapeDataString(fileParam) + "=" + Uri.EscapeDataString(inner);
                }
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, folder, CoreWebView2HostResourceAccessKind.Allow);
                try
                {
                    if (_isProject && !string.IsNullOrEmpty(_projectRoot) && Directory.Exists(_projectRoot))
                        _bookAssetsRoot = _projectRoot;
                    else if (!string.IsNullOrEmpty(_bookPath) && File.Exists(_bookPath))
                        _bookAssetsRoot = Path.GetDirectoryName(_bookPath);
                        if (!string.IsNullOrEmpty(_bookAssetsRoot) && Directory.Exists(_bookAssetsRoot))
                            Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(BookAssetsHost, _bookAssetsRoot, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { _bookAssetsRoot = null; }
                string rel = _entry.Replace('\\', '/').TrimStart('/');
                string url = "https://" + _host + "/" + string.Join("/",
                    rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + query;
                if (_debugLog) { try { System.IO.File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "reader_host_debug.log"), "NAV " + url + "\r\n"); } catch { } }
                Wv.CoreWebView2.Navigate(url);
                StartDeadline();
            }
            catch (Exception ex)
            {
                ShowLoadingError("导航扩展失败：" + ex.Message, false);
            }
        }

        private static string StableHostFor(string extensionId)
        {
            var sb = new StringBuilder();
            foreach (char c in (extensionId ?? "").ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            string id = sb.Length == 0 ? "ext" : sb.ToString();
            if (id.Length > 32) id = id.Substring(0, 32);
            return "lyra-ext-" + id + ".local";
        }

        private DispatcherTimer StartTimer(TimeSpan interval, Action action)
        {
            var t = new DispatcherTimer { Interval = interval };
            t.Tick += (_, _) =>
            {
                try { t.Stop(); } catch { }
                _timers.Remove(t);
                try { action(); } catch { }
            };
            _timers.Add(t);
            t.Start();
            return t;
        }

        private void StartDeadline()
        {
            if (_closeStarted) return;
            StopTimers();
            if (!_hasBook)
            {
                // 旧扩展没有 app.ready；页面导航后给一个短兜底，不能永久遮住选书入口
                StartTimer(TimeSpan.FromMilliseconds(1800), () =>
                {
                    if (!_overlayHidden && !_bookFailed) HideLoadingOverlay();
                });
            }
            else
            {
                StartTimer(TimeSpan.FromSeconds(15), () =>
                {
                    if (!_bookReady && !_bookFailed) ShowLoadingError("仍在加载这本书…", true);
                });
            }
        }

        private void StopTimers()
        {
            foreach (var t in _timers.ToArray())
            {
                if (ReferenceEquals(t, _closeTimer)) continue;   // 关闭 deadline 独立于加载/导航计时器
                try { t.Stop(); } catch { }
                _timers.Remove(t);
            }
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (_closeStarted) { args.Cancel = true; return; }
            // 自己发起的 NavigateToExtension 不轮换；其它 Reload/NewDocument 统一重建会话
            if (_suppressNavigationRotation)
            {
                _suppressNavigationRotation = false;
                return;
            }
            if (args.NavigationKind == CoreWebView2NavigationKind.Reload ||
                args.NavigationKind == CoreWebView2NavigationKind.NewDocument)
            {
                args.Cancel = true;
                try { DispatcherQueue.TryEnqueue(() => NavigateToExtension()); } catch { }
            }
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (_closeStarted) return;
            _navComplete = true;
            if (!args.IsSuccess)
            {
                _bookFailed = true;
                StopTimers();
                ShowLoadingError("扩展页面加载失败（" + args.WebErrorStatus + "）", true);
                return;
            }
            if (!_hasBook && !_bookReady && !_bookFailed)
            {
                // 新扩展会尽快发 app.ready；旧扩展依赖这个兜底
                StartTimer(TimeSpan.FromMilliseconds(1400), () =>
                {
                    if (!_overlayHidden && !_bookFailed) HideLoadingOverlay();
                });
            }
        }

        private bool IsCurrentSource(string? source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            if (!source.StartsWith("https://" + _host + "/", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var uri = new Uri(source);
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    string key = eq < 0 ? part : part.Substring(0, eq);
                    string val = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1));
                    if (key == "sessionId") return string.Equals(val, _sessionId, StringComparison.Ordinal);
                }
                return false;
            }
            catch { return false; }
        }

        private void PostHostState()
        {
            try
            {
                var state = new JsonObject
                {
                    ["type"] = "host.state",
                    ["sessionId"] = _sessionId,
                    ["activationEpoch"] = _activationEpoch,
                    ["navEpoch"] = _navEpoch,
                };
                Wv.CoreWebView2?.PostWebMessageAsJson(state.ToJsonString());
            }
            catch { }
        }

        private ReaderStateStore RequireStore()
        {
            if (_store == null) throw new ReaderStateStore.StoreException("STORE_UNAVAILABLE", "阅读状态存储初始化失败，已降级为只读/本地模式");
            return _store;
        }

        private bool TryHandleToolHostMessage(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                string type = ReaderProtocol.GetString(root, "type", 80);
                if (type != "host.capabilities" && type != "host.importBook" &&
                    type != "host.workspace.load" && type != "host.workspace.save" && type != "host.file.save" && type != "host.file.import" && type != "host.file.delete" && type != "host.file.rename" && type != "host.file.mkdir" && type != "editor.open-file" && type != "host.project.list" && type != "host.project.info" &&
                    type != "host.ready") return false;
                string requestId = ReaderProtocol.GetString(root, "requestId", 128);
                if (type == "host.ready")
                {
                    // 笔记本等工具窗口不走 lyra-reader 生命周期，页面加载后主动解除加载层。
                    _bookReady = true;
                    _bookFailed = false;
                    HideLoadingOverlay();
                    return true;
                }
                if (type == "host.capabilities")
                {
                    var arr = new JsonArray();
                    foreach (var def in ExtensionCatalog.LoadDefinitions().Where(d => d.IsReader))
                    {
                        var ft = new JsonArray();
                        foreach (var t in def.Info.FileTypes) ft.Add(t);
                        arr.Add(new JsonObject
                        {
                            ["id"] = def.Info.Id,
                            ["name"] = def.Info.Name,
                            ["fileTypes"] = ft,
                            ["navLabel"] = def.Library.NavLabel,
                        });
                    }
                    var reply = new JsonObject
                    {
                        ["type"] = "host.capabilities.result",
                        ["requestId"] = requestId,
                        ["extensions"] = arr,
                    };
                    Wv.CoreWebView2?.PostWebMessageAsJson(reply.ToJsonString());
                    return true;
                }
                if (type == "host.project.list" || type == "host.project.info")
                {
                    if (!_isProject || string.IsNullOrEmpty(_projectRoot) || !Directory.Exists(_projectRoot))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = type + ".result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "当前打开的不是 HTML 项目",
                        }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        var files = new JsonArray();
                        var dirs = new JsonArray();
                        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".vs", "bin", "obj", ".idea", "dist", "build" };
                        foreach (string fullDir in Directory.EnumerateDirectories(_projectRoot, "*", SearchOption.AllDirectories))
                        {
                            string relDir = Path.GetRelativePath(_projectRoot, fullDir).Replace('\\', '/');
                            if (relDir.Split('/').Any(seg => skipDirs.Contains(seg))) continue;
                            if (Path.GetFileName(relDir).StartsWith(".")) continue;
                            dirs.Add(relDir);
                            if (dirs.Count >= 1000) break;
                        }
                        foreach (string full in Directory.EnumerateFiles(_projectRoot, "*.*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(_projectRoot, full).Replace('\\', '/');
                            if (rel.Split('/').Any(seg => skipDirs.Contains(seg))) continue;
                            string fileName = Path.GetFileName(rel);
                            if (fileName.StartsWith(".")) continue;
                            string ext = Path.GetExtension(rel).ToLowerInvariant();
                            bool isHtml = ext == ".html" || ext == ".htm";
                            string dir = rel.Contains('/') ? rel.Substring(0, rel.LastIndexOf('/')) : "";
                            files.Add(new JsonObject
                            {
                                ["rel"] = rel,
                                ["name"] = fileName,
                                ["title"] = Path.GetFileNameWithoutExtension(rel),
                                ["dir"] = dir,
                                ["ext"] = ext,
                                ["html"] = isHtml,
                                ["isIndex"] = rel.Equals("index.html", StringComparison.OrdinalIgnoreCase) || rel.Equals("index.htm", StringComparison.OrdinalIgnoreCase),
                            });
                            if (files.Count >= 3000) break;
                        }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = type + ".result",
                            ["requestId"] = requestId,
                            ["ok"] = true,
                            ["root"] = _projectRoot,
                            ["name"] = Path.GetFileName(_projectRoot.TrimEnd(Path.DirectorySeparatorChar)),
                            ["files"] = files,
                            ["dirs"] = dirs,
                        }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = type + ".result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = ex.Message,
                        }.ToJsonString());
                    }
                    return true;
                }
                if (type == "editor.open-file")
                {
                    string rel = ReaderProtocol.GetString(root, "rel", 4096) ?? "";
                    if (string.IsNullOrWhiteSpace(rel) || !_isProject || _projectRoot == null)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "只有项目模式支持跨编辑器插件打开文件" }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        string ext = Path.GetExtension(rel).ToLowerInvariant();
                        var defs = ExtensionCatalog.LoadDefinitions();
                        if (ext == ".html" || ext == ".htm")
                        {
                            var readerDef = defs.FirstOrDefault(d => string.Equals(d.Info.Id, "html-reader", StringComparison.OrdinalIgnoreCase));
                            bool hasPlugin = defs.Any(d => d.IsEditorPlugin && string.Equals(d.Info.Id, "html-editor", StringComparison.OrdinalIgnoreCase));
                            if (readerDef == null || !hasPlugin)
                            {
                                Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "需要 HTML 阅读器 + HTML 编辑器插件" }.ToJsonString());
                                return true;
                            }
                            var win = ExtensionManager.OpenReader(readerDef, _projectRoot, rel, true);
                            if (win == null)
                            {
                                Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "无法打开 HTML 编辑器窗口" }.ToJsonString());
                                return true;
                            }
                            win.Activate();
                            Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = true, ["plugin"] = "html-editor" }.ToJsonString());
                            return true;
                        }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "还没有适配 " + (string.IsNullOrEmpty(ext) ? "该文件类型" : ext) + " 的编辑器插件" }.ToJsonString());
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "editor.open-file.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
                        return true;
                    }
                }
                if (type == "host.workspace.load")
                {
                    string? relRequest = ReaderProtocol.GetString(root, "path", 4096);
                    string path = "";
                    string relOut = "";
                    if (_isProject && !string.IsNullOrEmpty(_projectRoot))
                    {
                        string rel = (relRequest ?? "").Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(rel))
                        {
                            string? index = Directory.EnumerateFiles(_projectRoot, "index.htm*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (index == null) index = Directory.EnumerateFiles(_projectRoot, "*.htm*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (index == null) index = Directory.EnumerateFiles(_projectRoot, "*.htm*", SearchOption.AllDirectories).FirstOrDefault();
                            if (index == null) throw new InvalidOperationException("项目里没有 HTML 文件");
                            rel = Path.GetRelativePath(_projectRoot, index).Replace('\\', '/');
                        }
                        string full = Path.GetFullPath(Path.Combine(_projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                        string prefix = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("项目路径越界");
                        if (!File.Exists(full)) throw new InvalidOperationException("项目文件不存在：" + rel);
                        path = full; relOut = rel;
                    }
                    else path = _bookPath ?? "";
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.workspace.load.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "笔记本文档路径不可用",
                        }.ToJsonString());
                        return true;
                    }
                    byte[] bytes = File.ReadAllBytes(path);
                    Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                    {
                        ["type"] = "host.workspace.load.result",
                        ["requestId"] = requestId,
                        ["ok"] = true,
                        ["data"] = Convert.ToBase64String(bytes),
                        ["name"] = Path.GetFileName(path),
                        ["path"] = _isProject ? relOut : path,
                        ["rel"] = relOut,
                        ["project"] = _isProject,
                    }.ToJsonString());
                    return true;
                }
                if (type == "host.workspace.save")
                {
                    string? relRequest = ReaderProtocol.GetString(root, "path", 4096);
                    string path = _bookPath ?? "";
                    string relOut = "";
                    if (_isProject && !string.IsNullOrEmpty(_projectRoot))
                    {
                        string rel = (relRequest ?? "").Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrWhiteSpace(rel)) rel = "index.html";
                        string full = Path.GetFullPath(Path.Combine(_projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                        string prefix = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("项目路径越界");
                        path = full; relOut = rel;
                    }
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.workspace.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "笔记本路径不可用",
                        }.ToJsonString());
                        return true;
                    }
                    string? b64 = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String
                        ? dataEl.GetString() : null;
                    if (string.IsNullOrEmpty(b64))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.workspace.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "保存数据为空",
                        }.ToJsonString());
                        return true;
                    }
                    byte[] bytes = Convert.FromBase64String(b64);
                    if (bytes.Length > 100 * 1024 * 1024)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.workspace.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "笔记本超过 100MB",
                        }.ToJsonString());
                        return true;
                    }
                    string dir = Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(dir);
                    string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    string bak = path + ".bak";
                    try
                    {
                        File.WriteAllBytes(tmp, bytes);
                        if (File.Exists(path)) File.Copy(path, bak, true);
                        File.Move(tmp, path, true);
                    }
                    finally
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                    Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                    {
                        ["type"] = "host.workspace.save.result",
                        ["requestId"] = requestId,
                        ["ok"] = true,
                    }.ToJsonString());
                    return true;
                }
                if (type == "host.file.save")
                {
                    string rel = ReaderProtocol.GetString(root, "path", 2048) ?? "";
                    string? fileB64 = root.TryGetProperty("data", out var fileDataEl) && fileDataEl.ValueKind == JsonValueKind.String
                        ? fileDataEl.GetString() : null;
                    bool projectMode = _isProject && _projectRoot != null;
                    if (string.IsNullOrWhiteSpace(rel) || (fileB64 == null && !projectMode) || string.IsNullOrEmpty(_bookPath))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "文件路径或数据为空",
                        }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        byte[] fileBytes = Convert.FromBase64String(fileB64 ?? "");
                        string baseDir = projectMode ? _projectRoot! : (Path.GetDirectoryName(Path.GetFullPath(_bookPath)) ?? "");
                        string prefix = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径越界");
                        if (!projectMode)
                        {
                            string ext = Path.GetExtension(full).ToLowerInvariant();
                            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp" && ext != ".gif" && ext != ".bmp" && ext != ".svg")
                                throw new InvalidOperationException("只允许图片文件");
                            if (fileBytes.Length > 50 * 1024 * 1024) throw new InvalidOperationException("图片超过 50MB");
                        }
                        else
                        {
                            if (Directory.Exists(full)) throw new InvalidOperationException("目标路径是文件夹");
                            if (fileBytes.Length > 100 * 1024 * 1024) throw new InvalidOperationException("文件超过 100MB");
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
                        try { File.WriteAllBytes(tmp, fileBytes); File.Move(tmp, full, true); }
                        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = true,
                            ["path"] = rel,
                        }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.save.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = ex.Message,
                        }.ToJsonString());
                    }
                    return true;
                }
                if (type == "host.file.delete")
                {
                    string rel = ReaderProtocol.GetString(root, "path", 4096) ?? "";
                    if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrEmpty(_bookPath))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.delete.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "路径不可用" }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        bool projectMode = _isProject && _projectRoot != null;
                        string baseDir = projectMode ? _projectRoot! : (Path.GetDirectoryName(Path.GetFullPath(_bookPath)) ?? "");
                        string allowedRoot = projectMode
                            ? Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
                            : Path.GetFullPath(Path.Combine(baseDir, "assets")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!full.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(projectMode ? "路径越界" : "只允许删除 assets 目录内的文件");
                        if (Directory.Exists(full))
                        {
                            if (!projectMode) throw new InvalidOperationException("只允许删除 assets 目录内的文件");
                            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("不允许删除链接目录");
                            Directory.Delete(full, true);
                        }
                        else if (File.Exists(full))
                        {
                            File.Delete(full);
                        }
                        else
                        {
                            throw new FileNotFoundException("文件或文件夹不存在");
                        }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.delete.result", ["requestId"] = requestId, ["ok"] = true }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.delete.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
                    }
                    return true;
                }
                if (type == "host.file.rename")
                {
                    string fromRel = ReaderProtocol.GetString(root, "from", 4096) ?? "";
                    string toRel = ReaderProtocol.GetString(root, "to", 4096) ?? "";
                    if (string.IsNullOrWhiteSpace(fromRel) || string.IsNullOrWhiteSpace(toRel) || string.IsNullOrEmpty(_bookPath))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.rename.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "路径不可用" }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        bool projectMode = _isProject && _projectRoot != null;
                        string baseDir = projectMode ? _projectRoot! : (Path.GetDirectoryName(Path.GetFullPath(_bookPath)) ?? "");
                        string allowedRoot = projectMode
                            ? Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
                            : Path.GetFullPath(Path.Combine(baseDir, "assets")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string fromFull = Path.GetFullPath(Path.Combine(baseDir, fromRel.Replace('/', Path.DirectorySeparatorChar)));
                        string toFull = Path.GetFullPath(Path.Combine(baseDir, toRel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!fromFull.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || !toFull.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(projectMode ? "路径越界" : "只允许重命名 assets 目录内的文件");
                        if (string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("源和目标相同");
                        if (File.Exists(toFull) || Directory.Exists(toFull)) throw new InvalidOperationException("目标已存在");
                        if (Directory.Exists(fromFull))
                        {
                            if (!projectMode) throw new InvalidOperationException("只允许重命名 assets 目录内的文件");
                            Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
                            Directory.Move(fromFull, toFull);
                        }
                        else if (File.Exists(fromFull))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
                            File.Move(fromFull, toFull);
                        }
                        else
                        {
                            throw new FileNotFoundException("源文件或文件夹不存在");
                        }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.rename.result", ["requestId"] = requestId, ["ok"] = true, ["path"] = toRel }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.rename.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
                    }
                    return true;
                }
                if (type == "host.file.mkdir")
                {
                    string rel = ReaderProtocol.GetString(root, "path", 4096) ?? "";
                    if (string.IsNullOrWhiteSpace(rel) || !_isProject || _projectRoot == null)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.mkdir.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = "仅项目模式支持新建文件夹" }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        string baseDir = Path.GetFullPath(_projectRoot);
                        string prefix = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径越界");
                        if (File.Exists(full)) throw new InvalidOperationException("同名文件已存在");
                        Directory.CreateDirectory(full);
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.mkdir.result", ["requestId"] = requestId, ["ok"] = true, ["path"] = rel }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "host.file.mkdir.result", ["requestId"] = requestId, ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
                    }
                    return true;
                }
                if (type == "host.file.import")
                {
                    string source = ReaderProtocol.GetString(root, "path", 4096) ?? "";
                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrEmpty(_bookPath))
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.import.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = "资源路径或当前文件路径不可用",
                        }.ToJsonString());
                        return true;
                    }
                    try
                    {
                        string importRaw = Uri.UnescapeDataString(source.Trim().Trim('"'));
                        if (importRaw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        {
                            try { importRaw = new Uri(importRaw).LocalPath; } catch { }
                        }
                        int cut = importRaw.IndexOfAny(new[] { '?', '#' });
                        if (cut >= 0) importRaw = importRaw.Substring(0, cut);
                        string baseDir = _isProject && _projectRoot != null ? _projectRoot : (Path.GetDirectoryName(Path.GetFullPath(_bookPath)) ?? "");
                        string full = Path.IsPathRooted(importRaw)
                            ? Path.GetFullPath(importRaw)
                            : Path.GetFullPath(Path.Combine(baseDir, importRaw.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(full)) throw new InvalidOperationException("文件不存在：" + full);
                        string ext = Path.GetExtension(full).ToLowerInvariant();
                        string[] allowed = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".avif", ".mp4", ".webm", ".ogv", ".mov", ".m4v", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".css", ".js", ".mjs", ".json", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".ico" };
                        if (!allowed.Contains(ext)) throw new InvalidOperationException("只允许网页资源：" + ext);
                        string hash;
                        using (var fs = File.OpenRead(full)) hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant().Substring(0, 12);
                        string safeName = Path.GetFileName(full);
                        foreach (char bad in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(bad, '_');
                        string relAsset = "assets/" + hash + "-" + safeName;
                        string dest = Path.Combine(baseDir, "assets", hash + "-" + safeName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (!File.Exists(dest))
                        {
                            string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N");
                            try { File.Copy(full, tmp, true); File.Move(tmp, dest, true); }
                            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                        }
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.import.result",
                            ["requestId"] = requestId,
                            ["ok"] = true,
                            ["path"] = relAsset,
                            ["source"] = full,
                        }.ToJsonString());
                    }
                    catch (Exception ex)
                    {
                        Wv.CoreWebView2?.PostWebMessageAsJson(new JsonObject
                        {
                            ["type"] = "host.file.import.result",
                            ["requestId"] = requestId,
                            ["ok"] = false,
                            ["error"] = ex.Message,
                        }.ToJsonString());
                    }
                    return true;
                }
                string path2 = ReaderProtocol.GetString(root, "path", 2048);
                var result = App.Main?.ImportDownloadedBook(path2) ?? (false, "", "", "主窗口不可用");
                var resp = new JsonObject
                {
                    ["type"] = "host.importBook.result",
                    ["requestId"] = requestId,
                    ["ok"] = result.Item1,
                    ["extensionId"] = result.Item2,
                    ["title"] = result.Item3,
                    ["error"] = result.Item4,
                };
                Wv.CoreWebView2?.PostWebMessageAsJson(resp.ToJsonString());
                return true;
            }
            catch { return false; }
        }

        private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string raw = args.WebMessageAsJson ?? "";
            if (_debugLog) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "reader_host_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] raw={raw.Substring(0, Math.Min(raw.Length, 600))} source={args.Source}\r\n"); } catch { } }
            try
            {
                string source = args.Source ?? "";
                if (!IsCurrentSource(source)) return;
                // 关闭冲刷回执（普通对象，不是正式协议）
                try
                {
                    using var closeDoc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
                    if (closeDoc.RootElement.ValueKind == JsonValueKind.Object &&
                        ReaderProtocol.GetString(closeDoc.RootElement, "type", 64) == "host.finalized")
                    {
                        string req = ReaderProtocol.GetString(closeDoc.RootElement, "requestId", 128);
                        string finSession = ReaderProtocol.GetString(closeDoc.RootElement, "sessionId", 128);
                        bool finOk = closeDoc.RootElement.TryGetProperty("ok", out var fok) && fok.ValueKind == JsonValueKind.True;
                        long finRev = closeDoc.RootElement.TryGetProperty("revision", out var frv) && frv.ValueKind == JsonValueKind.Number ? frv.GetInt64() : 0;
                        if (_finalizePending && !string.IsNullOrEmpty(req) && req == _finalizeRequestId &&
                            !string.IsNullOrEmpty(finSession) && finSession == _sessionId)
                            FinishFinalizeAndClose(finOk ? "ack" : "error", req, finRev);
                        else LogHost("CLOSE_IGNORED request=" + req + " session=" + finSession);
                        return;
                    }
                }
                catch { }
                // 旧扩展的就绪通知：EPUB 0.5.x 字符串，PDF 0.3.x 是 {type:"lyra-book-ready"} 对象
                if (raw == "\"lyra-book-ready\"" || raw == "lyra-book-ready")
                {
                    _bookReady = true;
                    _bookFailed = false;
                    HideLoadingOverlay();
                    return;
                }
                try
                {
                    using var legacyDoc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
                    if (legacyDoc.RootElement.ValueKind == JsonValueKind.Object &&
                        ReaderProtocol.GetString(legacyDoc.RootElement, "type", 64) == "lyra-book-ready")
                    {
                        _bookReady = true;
                        _bookFailed = false;
                        HideLoadingOverlay();
                        return;
                    }
                }
                catch { }
                // 工具扩展通用桥接：不要求 lyra-reader 协议版本，
                // 只做「查询已安装读者扩展」和「把下载文件导入对应书库」两件事。
                if (TryHandleToolHostMessage(raw)) return;
                if (!ReaderProtocol.TryParse(raw, out JsonDocument doc)) return;
                using (doc)
                {
                    var root = doc.RootElement;
                    if (ReaderProtocol.GetString(root, "protocol", 32) != ReaderProtocol.Protocol) return;
                    string type = ReaderProtocol.GetString(root, "type", 64);
                    string sessionId = ReaderProtocol.GetString(root, "sessionId", 128);
                    // 能发出当前协议对象消息的扩展，关闭时才等 host.finalize 回执；PDF 0.3.0 只发旧 book-ready
                    if (!string.IsNullOrEmpty(sessionId) && sessionId == _sessionId) _supportsFinalize = true;
                    string requestId = ReaderProtocol.GetString(root, "requestId", 128);
                    int reqVersion = root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number ? ver.GetInt32() : 0;
                    if (reqVersion != ReaderProtocol.Version)
                    {
                        // 旧 R1/R2/R3 客户端使用 version1：只接受导航/就绪生命周期，不提供 R4 状态服务；
                        // hello 明确回旧版本错误，让旧客户端降级为当前窗口存储，而不是错误宣称 host 模式。
                        // v2（R4）只承接受信 lifecycle：不提供状态/持久化服务，但当前 session/source/loadId 的
                        // app.ready/book.begin/book.ready/book.error 必须解除加载层，否则 local 正文永久被挡。
                        if (reqVersion == 2 && !string.IsNullOrEmpty(sessionId) && sessionId == _sessionId && ReaderProtocol.IsAllowedType(type))
                        {
                            if (type == "app.ready") { if (!_hasBook) HideLoadingOverlay(); }
                            else if (type == "book.begin")
                            {
                                string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                if (!string.IsNullOrEmpty(loadId)) { _loadId = loadId; _bookReady = false; _bookFailed = false; ShowLoading("正在打开…"); }
                            }
                            else if (type == "book.ready")
                            {
                                string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                if (!string.IsNullOrEmpty(loadId) && loadId == _loadId)
                                {
                                    _bookReady = true; _bookFailed = false; _overlayHidden = false; HideLoadingOverlay();
                                }
                            }
                            else if (type == "book.error")
                            {
                                string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                if (!string.IsNullOrEmpty(loadId) && loadId == _loadId)
                                {
                                    _bookFailed = true; _overlayHidden = false;
                                    ShowLoadingError("旧版扩展报告加载失败", true);
                                }
                            }
                            else if (type == "hello")
                            {
                                LogHost("LEGACY_V2_HELLO_LOCAL");
                                var legacy = ReaderProtocol.ResponseVersion(2, requestId, _sessionId, false, null, "INCOMPATIBLE_PROTOCOL", "宿主 R6 协议不提供 v2 持久化，旧扩展使用当前窗口存储");
                                try { Wv.CoreWebView2?.PostWebMessageAsJson(ReaderProtocol.Serialize(legacy)); } catch { }
                            }
                            return;
                        }
                        if (reqVersion == 1 && !string.IsNullOrEmpty(sessionId) && sessionId == _sessionId && ReaderProtocol.IsAllowedType(type))
                        {
                            if (type == "app.ready" || type == "book.ready" || type == "book.error")
                            {
                                if (type == "book.ready") { _bookReady = true; _bookFailed = false; HideLoadingOverlay(); }
                                else if (type == "book.error") { _bookFailed = true; ShowLoadingError("旧版扩展报告加载失败", true); }
                                else if (!_hasBook) HideLoadingOverlay();
                            }
                            else if (type == "book.begin") { _bookReady = false; _bookFailed = false; ShowLoading("正在打开…"); }
                            else if (type == "hello")
                            {
                                LogHost("LEGACY_HELLO_INCOMPATIBLE");
                                var legacy = ReaderProtocol.ResponseVersion(1, requestId, _sessionId, false, null, "INCOMPATIBLE_PROTOCOL", "宿主已升级到 R4 协议，旧扩展只能使用当前窗口存储");
                                try { Wv.CoreWebView2?.PostWebMessageAsJson(ReaderProtocol.Serialize(legacy)); } catch { }
                            }
                        }
                        return;
                    }
                    if (!ReaderProtocol.IsAllowedType(type)) return;
                    if (string.IsNullOrEmpty(sessionId)) return;
                    // 宿主持有当前导航会话；hello 只能确认匹配，不能反向选择/复活旧会话
                    if (sessionId != _sessionId) return;

                    JsonObject? result = null;
                    bool ok = true;
                    string? errCode = null, errMessage = null;
                    try
                    {
                        switch (type)
                        {
                            case "hello":
                                result = new JsonObject
                                {
                                    ["protocolVersion"] = ReaderProtocol.Version,
                                    ["appVersion"] = typeof(App).Assembly.GetName().Version?.ToString() ?? "",
                                    ["activationEpoch"] = _activationEpoch,
                                    ["navEpoch"] = _navEpoch,
                                    ["capabilities"] = _store != null ? ReaderProtocol.Capabilities() : ReaderProtocol.UnavailableCapabilities(),
                                };
                                _bookFailed = false;
                                PostHostState();
                                break;
                            case "app.ready":
                                if (!_hasBook) HideLoadingOverlay();
                                return;
                            case "book.ready":
                                {
                                    string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                    if (string.IsNullOrEmpty(loadId) || loadId != _loadId) return;   // 缺失/过期 loadId 不接受
                                    _bookReady = true;
                                    _bookFailed = false;
                                    _overlayHidden = false;
                                    HideLoadingOverlay();
                                    return;
                                }
                            case "book.begin":
                                {
                                    string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                    if (string.IsNullOrEmpty(loadId)) return;
                                    _loadId = loadId;
                                    _bookReady = false;
                                    _bookFailed = false;
                                    _overlayHidden = false;
                                    ShowLoading("正在打开…");
                                    return;
                                }
                            case "book.error":
                                {
                                    string loadId = ReaderProtocol.GetString(root, "loadId", 128);
                                    if (string.IsNullOrEmpty(loadId) || loadId != _loadId) return;
                                    _bookFailed = true;
                                    _overlayHidden = false;
                                    string msg = ReaderProtocol.GetString(root, "message", 512);
                                    ShowLoadingError("打开失败：" + msg, true);
                                    return;
                                }
                            case "state.load":
                                {
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    result = RequireStore().Load(bookId, _sessionId);
                                    break;
                                }
                            case "state.interact":
                                {
                                    // 请求必须属于当前激活/导航代次；重新获焦不会让旧请求复活
                                    long reqAct = root.TryGetProperty("activationEpoch", out var ae) && ae.ValueKind == JsonValueKind.Number ? ae.GetInt64() : -1;
                                    long reqNav = root.TryGetProperty("navEpoch", out var ne) && ne.ValueKind == JsonValueKind.Number ? ne.GetInt64() : -1;
                                    if (reqAct != _activationEpoch || reqNav != _navEpoch)
                                        throw new ReaderStateStore.StoreException("STALE_OPERATION_EPOCH", "交互请求属于已失效的激活/导航代次");
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    result = RequireStore().GrantInteraction(bookId, _sessionId, _isActive);
                                    break;
                                }
                            case "state.savePosition":
                                {
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    long token = root.TryGetProperty("interactionToken", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                                    long seq = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : 0;
                                    var pos = ReaderProtocol.GetObject(root, "position");
                                    if (pos.ValueKind != JsonValueKind.Object) throw new ReaderStateStore.StoreException("BAD_POSITION", "position 必须是对象");
                                    result = RequireStore().SavePosition(bookId, _sessionId, pos, token, seq);
                                    break;
                                }
                            case "state.grantPreferences":
                                {
                                    long reqAct = root.TryGetProperty("activationEpoch", out var gae) && gae.ValueKind == JsonValueKind.Number ? gae.GetInt64() : -1;
                                    long reqNav = root.TryGetProperty("navEpoch", out var gne) && gne.ValueKind == JsonValueKind.Number ? gne.GetInt64() : -1;
                                    if (reqAct != _activationEpoch || reqNav != _navEpoch)
                                        throw new ReaderStateStore.StoreException("STALE_OPERATION_EPOCH", "偏好授权请求属于已失效的激活/导航代次");
                                    string operationId = ReaderProtocol.GetString(root, "operationId", 128);
                                    if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                                        throw new ReaderStateStore.StoreException("BAD_FIELDS", "fields 必须是数组");
                                    result = RequireStore().GrantPreferences(_sessionId, _isActive, operationId, fields);
                                    break;
                                }
                            case "state.savePreferences":
                                {
                                    string scope = ReaderProtocol.GetString(root, "scope", 64);
                                    if (scope != "global")
                                        throw new ReaderStateStore.StoreException("UNSUPPORTED_SCOPE", "只支持 global 偏好作用域");
                                    var prefs = ReaderProtocol.GetObject(root, "preferences");
                                    if (prefs.ValueKind != JsonValueKind.Object) throw new ReaderStateStore.StoreException("BAD_PREFERENCES", "preferences 必须是对象");
                                    var grants = ReaderProtocol.GetObject(root, "grants");
                                    if (grants.ValueKind != JsonValueKind.Object) throw new ReaderStateStore.StoreException("MISSING_GRANT", "偏好提交缺少字段授权");
                                    result = RequireStore().SavePreferences(_sessionId, prefs, grants);
                                    break;
                                }
                            case "bookmark.list":
                                {
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    string cursor = ReaderProtocol.GetString(root, "cursor", 128);
                                    int limit = root.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number ? lim.GetInt32() : 50;
                                    result = RequireStore().ListBookmarks(bookId, cursor, limit);
                                    break;
                                }
                            case "bookmark.add":
                                {
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    var bm = ReaderProtocol.GetObject(root, "bookmark");
                                    if (bm.ValueKind != JsonValueKind.Object) throw new ReaderStateStore.StoreException("BAD_BOOKMARK", "bookmark 必须是对象");
                                    result = RequireStore().AddBookmark(bookId, _sessionId, bm);
                                    break;
                                }
                            case "bookmark.delete":
                                {
                                    string bookId = ReaderProtocol.GetString(root, "bookId", 128);
                                    string bookmarkId = ReaderProtocol.GetString(root, "bookmarkId", 128);
                                    result = RequireStore().DeleteBookmark(bookId, _sessionId, bookmarkId);
                                    break;
                                }
                            default:
                                return;
                        }
                    }
                    catch (ReaderStateStore.StoreException ex)
                    {
                        ok = false; errCode = ex.Code; errMessage = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        ok = false; errCode = "HOST_ERROR"; errMessage = ex.Message;
                    }
                    if (ok || !string.IsNullOrEmpty(errCode))
                    {
                        LogHost("REPLY " + type + " ok=" + ok + " err=" + errCode + " " + errMessage);
                        var res = ReaderProtocol.Response(requestId, _sessionId, ok, result, errCode, errMessage);
                        string json = ReaderProtocol.Serialize(res);
                        if (Encoding.UTF8.GetByteCount(json) > ReaderProtocol.MaxMessageBytes)
                        {
                            var tooLarge = ReaderProtocol.Response(requestId, _sessionId, false, null, "RESPONSE_TOO_LARGE", "响应超过宿主消息上限");
                            json = ReaderProtocol.Serialize(tooLarge);
                        }
                        try { Wv.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
                    }
                }
            }
            catch { }
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeReady) return;
            if (_closeStarted) { args.Cancel = true; return; }
            _closeStarted = true;
            _finalizePending = true;
            _finalizeRequestId = Guid.NewGuid().ToString("N");
            args.Cancel = true;
            // ★ 先让窗口消失：finalize/状态保存仍在后台完成，用户不再盯着 1.5 秒的关闭等待。
            try { AppWindow.Hide(); } catch { }
            LogHost("CLOSING request=" + _finalizeRequestId + " supportsFinalize=" + _supportsFinalize);
            try
            {
                var req = new JsonObject { ["type"] = "host.finalize", ["requestId"] = _finalizeRequestId };
                Wv.CoreWebView2?.PostWebMessageAsJson(req.ToJsonString());
            }
            catch { }
            // PDF 0.3.0 没有 finalize：直接关，不要固定等满 1.5s。新扩展最多等 250ms 回执兜底。
            if (!_supportsFinalize)
            {
                FinishFinalizeAndClose("legacy", _finalizeRequestId, 0);
                return;
            }
            // 即使 WebView2 尚未创建，关闭期限也必须独立存在且不能被初始化/导航取消。
            _closeTimer = StartTimer(TimeSpan.FromMilliseconds(250), () => FinishFinalizeAndClose("timeout", _finalizeRequestId, 0));
        }

        private void FinishFinalizeAndClose(string reason, string requestId, long revision)
        {
            if (_closeReady) return;
            LogHost("CLOSE_" + reason.ToUpperInvariant() + " request=" + requestId + " revision=" + revision);
            _finalizePending = false;
            _closeStarted = true;
            try { if (_closeTimer != null) { _closeTimer.Stop(); _timers.Remove(_closeTimer); _closeTimer = null; } } catch { }
            _closeReady = true;
            try { Close(); } catch { }
        }

        private static readonly bool _debugLog = Environment.GetEnvironmentVariable("LYRA_READER_DEBUG") == "1";

        private static void LogHost(string message)
        {
            if (!_debugLog) return;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "reader_host_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n"); } catch { }
        }

        private void ShowLoading(string text)
        {
            try
            {
                _overlayHidden = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = text;
                LoadingDetail.Text = "";
                LoadingRetry.Visibility = Visibility.Collapsed;
                LoadingCancel.Visibility = Visibility.Collapsed;
                LoadingRing.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void ShowLoadingError(string message, bool canRetry)
        {
            try
            {
                _overlayHidden = false;
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingText.Text = message;
                LoadingDetail.Text = canRetry ? "可以重试，或取消关闭这个窗口。" : "";
                LoadingRetry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
                LoadingCancel.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void HideLoadingOverlay()
        {
            if (_overlayHidden) return;
            _overlayHidden = true;
            try { LoadingOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        private void LoadingRetry_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                HideLoadingOverlay();
                _overlayHidden = false;
                NavigateToExtension();
            }
            catch { }
        }

        private void LoadingCancel_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }

        private void OnBookAssetsRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string root = _bookAssetsRoot ?? "";
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                var uri = new Uri(args.Request.Uri);
                if (!string.Equals(uri.Host, BookAssetsHost, StringComparison.OrdinalIgnoreCase)) return;
                string rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = Wv.CoreWebView2.Environment.CreateWebResourceResponse(Ras(new MemoryStream()), 403, "Forbidden", "Content-Type: text/plain");
                    return;
                }
                if (!File.Exists(full))
                {
                    args.Response = Wv.CoreWebView2.Environment.CreateWebResourceResponse(Ras(new MemoryStream()), 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                string contentType = MimeForPath(full);
                long total = new FileInfo(full).Length;
                long start = 0, end = total - 1;
                bool partial = false;
                string? range = null;
                try { range = args.Request.Headers.GetHeader("Range"); } catch { }
                if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range.Substring(6).Split(',')[0].Trim();
                    string[] pieces = spec.Split('-');
                    if (pieces.Length == 2)
                    {
                        if (string.IsNullOrWhiteSpace(pieces[0]))
                        {
                            if (long.TryParse(pieces[1], out long suffix) && suffix > 0)
                            {
                                start = Math.Max(0, total - suffix);
                                end = total - 1;
                                partial = true;
                            }
                        }
                        else if (long.TryParse(pieces[0], out long from))
                        {
                            start = Math.Max(0, from);
                            end = string.IsNullOrWhiteSpace(pieces[1]) ? total - 1 : Math.Min(total - 1, long.Parse(pieces[1]));
                            if (start <= end) partial = true;
                        }
                    }
                }
                if (start > end || total <= 0)
                {
                    args.Response = Wv.CoreWebView2.Environment.CreateWebResourceResponse(Ras(new MemoryStream()), 416, "Range Not Satisfiable", "Content-Range: bytes */" + total);
                    return;
                }
                long length = end - start + 1;
                var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan);
                fs.Seek(start, SeekOrigin.Begin);
                Stream responseStream = partial ? new LimitedReadStream(fs, length) : fs;
                var headers = new StringBuilder();
                headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
                headers.Append("Accept-Ranges: bytes\r\n");
                headers.Append("Access-Control-Allow-Origin: *\r\n");
                headers.Append("Cross-Origin-Resource-Policy: cross-origin\r\n");
                headers.Append("Cache-Control: no-cache\r\n");
                headers.Append("Content-Length: ").Append(length).Append("\r\n");
                if (partial) headers.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
                args.Response = Wv.CoreWebView2.Environment.CreateWebResourceResponse(
                    Ras(responseStream), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers.ToString());
            }
            catch { }
        }

        private static Windows.Storage.Streams.IRandomAccessStream Ras(Stream stream)
            => WindowsRuntimeStreamExtensions.AsRandomAccessStream(stream);

        private static string MimeForPath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" or ".mjs" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                ".mp4" or ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".ogv" => "video/ogg",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                ".ogg" or ".oga" => "audio/ogg",
                ".flac" => "audio/flac",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".eot" => "application/vnd.ms-fontobject",
                ".txt" or ".md" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly FileStream _inner;
            private readonly long _start;
            private readonly long _length;
            private long _pos;
            public LimitedReadStream(FileStream inner, long length)
            {
                _inner = inner;
                _start = inner.Position;
                _length = length;
                _pos = 0;
            }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _pos; set => Seek(value, SeekOrigin.Begin); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                long remaining = _length - _pos;
                if (remaining <= 0) return 0;
                _inner.Position = _start + _pos;
                int n = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
                _pos += n;
                return n;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin)
            {
                long next = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _pos + offset,
                    SeekOrigin.End => _length + offset,
                    _ => 0,
                };
                _pos = Math.Max(0, Math.Min(_length, next));
                return _pos;
            }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
        private void CleanupTemp()
        {
            // 临时书目录清理不等于窗口关闭；绝不能在此停止关闭期限。
            // ★ 不阻塞关闭路径：WebView2 可能还短暂持有目录映射，后台重试删除。
            string dir = _tempDir;
            _tempDir = null;
            if (string.IsNullOrEmpty(dir)) return;
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    try { Directory.Delete(dir, true); return; }
                    catch { await Task.Delay(50); }
                }
            });
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}