using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        private readonly string _extensionId;
        private readonly string _extensionDir;
        private readonly string _entry;
        private readonly string? _bookPath;
        // ★ 固定虚拟域名（按扩展 id）：网页的本地存储按“网址来源”隔离，
        // 以前每开一次窗口换一个随机域名，扩展存下的进度/设置下次就找不到了。
        private readonly string _host;
        private readonly bool _isPdf;
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
        {
            InitializeComponent();
            _extensionId = extensionId;
            _host = StableHostFor(extensionId);
            _extensionDir = dir;
            _entry = string.IsNullOrWhiteSpace(entry) ? "index.html" : entry;
            _bookPath = bookPath;
            _isPdf = !string.IsNullOrEmpty(bookPath) && bookPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            _hasBook = !string.IsNullOrEmpty(bookPath) && File.Exists(bookPath);
            Title = "璃鸢镜 · " + (string.IsNullOrWhiteSpace(name) ? extensionId : name);
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
                Wv.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                Wv.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                Wv.CoreWebView2.NavigationStarting += OnNavigationStarting;
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
                if (_hasBook)
                {
                    CleanupTemp();
                    _tempDir = Path.Combine(Path.GetTempPath(), "lyra_ext_host", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_tempDir);
                    CopyDir(_extensionDir, _tempDir);
                    string inner = _isPdf ? "document.pdf" : "book.epub";
                    File.Copy(_bookPath!, Path.Combine(_tempDir, inner), true);
                    folder = _tempDir;
                    string realName = Path.GetFileName(_bookPath!);
                    query += "&name=" + Uri.EscapeDataString(realName);
                    query += _isPdf ? "&file=document.pdf" : "&book=book.epub";
                }
                Wv.CoreWebView2.SetVirtualHostNameToFolderMapping(_host, folder, CoreWebView2HostResourceAccessKind.Allow);
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
