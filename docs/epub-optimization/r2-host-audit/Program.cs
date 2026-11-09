// R2 宿主业务代码诊断：链接真实宿主/协议/存储源文件，用事件替身走原消息入口。
// 不是桌面/WebView2 原生集成测试；原生双窗证据见候选运行目录实测。
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using WinUIGui;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static readonly string Base = AppContext.BaseDirectory;
    private static string Book(char c) => "sha256:" + new string(c, 64);
    private static JsonElement Json(string raw) { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
    private static void Record(string id, bool pass, object details) => Results.Add(new { id, pass, details });

    private static ExtensionHostWindow Host(string? book = null)
    {
        var h = new ExtensionHostWindow("epub", "audit", Base, "index.html", book);
        h.AuditInitialize();
        return h;
    }

    private static JsonObject Request(ExtensionHostWindow h, string type, JsonObject? fields = null, string? session = null, string? source = null)
    {
        var r = h.AuditRequest(type, fields, session, source);
        if (r == null) throw new Exception("No reply for " + type);
        return r;
    }

    private static void Send(ExtensionHostWindow h, string type, JsonObject? fields = null)
    {
        var msg = fields ?? new JsonObject();
        msg["protocol"] = ReaderProtocol.Protocol;
        msg["version"] = 1;
        msg["type"] = type;
        msg["requestId"] = Guid.NewGuid().ToString("N");
        msg["sessionId"] = h.AuditSession;
        h.AuditRaw(msg.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static bool Ok(JsonObject r) => r["ok"]?.GetValue<bool>() == true;
    private static long Token(ExtensionHostWindow h, string book)
    {
        var r = Request(h, "state.interact", new() { ["bookId"] = book });
        return Ok(r) ? r["result"]!["interactionToken"]!.GetValue<long>() : 0;
    }
    private static JsonObject Save(ExtensionHostWindow h, string book, string quote, long token, long seq) =>
        Request(h, "state.savePosition", new()
        {
            ["bookId"] = book,
            ["interactionToken"] = token,
            ["sequence"] = seq,
            ["position"] = new JsonObject { ["spineHref"] = "synthetic.xhtml", ["locator"] = new JsonObject { ["quote"] = quote } }
        });
    private static string? Quote(ReaderStateStore store, string book) => store.Load(book, "observer")["position"]?["locator"]?["quote"]?.GetValue<string>();

    private static void Main()
    {
        if (!Base.Contains("r2-host-audit", StringComparison.OrdinalIgnoreCase)) throw new Exception("Refuse non-isolated data root");
        if (Directory.Exists(Path.Combine(Base, "data"))) throw new Exception("Choose a fresh output directory");
        Environment.SetEnvironmentVariable("LYRA_READER_DEBUG", null);
        var store = new ReaderStateStore();
        string bookA = Book('a');
        var a = Host();
        var b = Host();
        a.AuditActivate(WindowActivationState.CodeActivated);
        long ta = Token(a, bookA);
        Save(a, bookA, "A first", ta, 1);
        a.AuditActivate(WindowActivationState.Deactivated);
        b.AuditActivate(WindowActivationState.CodeActivated);
        long tb = Token(b, bookA);
        Save(b, bookA, "B latest", tb, 2);
        // 失活 A 仍尝试申请资格：必须被 WINDOW_NOT_ACTIVE 拒绝
        var inactiveGrant = Request(a, "state.interact", new() { ["bookId"] = bookA });
        var staleSave = Save(a, bookA, "A delayed old token", ta, 3);
        Record("H-ACTIVE", !Ok(inactiveGrant) && !Ok(staleSave) && Quote(store, bookA) == "B latest",
            new { inactiveGrant, staleSave, finalQuote = Quote(store, bookA), activeA = a.AuditActive });
        // 真正重新激活并产生新的用户交互后可以接管
        a.AuditActivate(WindowActivationState.PointerActivated);
        long ta2 = Token(a, bookA);
        var takeover = Save(a, bookA, "A new explicit interaction", ta2, 4);
        Record("H-ACTIVE-TAKEOVER", Ok(takeover) && Quote(store, bookA) == "A new explicit interaction",
            new { ta2, takeover, finalQuote = Quote(store, bookA) });

        string bookB = Book('b');
        long owner = Token(b, bookB);
        var wrongOwner = Save(a, bookB, "wrong owner", owner, 20);
        Save(b, bookB, "B seq20", owner, 20);
        var rollback = Save(b, bookB, "sequence 2 late", owner, 2);
        var fakeToken = Save(a, bookB, "fake future token", owner + 100, 21);
        var idem = Save(b, bookB, "B seq20", owner, 20);
        Record("H-TOKEN", !Ok(wrongOwner) && !Ok(rollback) && !Ok(fakeToken) && Ok(idem) && Quote(store, bookB) == "B seq20",
            new { wrongOwner, rollback, fakeToken, idem, finalQuote = Quote(store, bookB) });

        // 偏好：不同窗口不同字段合并；同字段迟到不覆盖；新操作可覆盖
        string bookC = Book('c');
        var p1 = Request(a, "state.savePreferences", new() { ["sequence"] = 3002L, ["preferences"] = new JsonObject { ["fontSize"] = 18 } });
        var p2 = Request(b, "state.savePreferences", new() { ["sequence"] = 5002L, ["preferences"] = new JsonObject { ["fontSize"] = 32, ["theme"] = "dark" } });
        var p3 = Request(a, "state.savePreferences", new() { ["sequence"] = 4002L, ["preferences"] = new JsonObject { ["fontSize"] = 20, ["theme"] = "sepia" } });
        var prefs = store.Load(bookC, "observer")["preferences"]!;
        Record("H-PREF-ORDER", (int)prefs["fontSize"]! == 32 && prefs["theme"]!.GetValue<string>() == "dark",
            new { p1, p2, p3, prefs });

        // 遮罩状态机：app.ready → error → 新 begin/ready 后必须隐藏
        string tempBook = Path.Combine(Base, "synthetic.epub");
        File.WriteAllBytes(tempBook, new byte[] { 1, 2, 3 });
        var life = Host(tempBook);
        Send(life, "app.ready");
        Send(life, "book.begin", new() { ["loadId"] = "manual-open" });
        Send(life, "book.error", new() { ["loadId"] = "manual-open", ["message"] = "synthetic failure" });
        Send(life, "book.begin", new() { ["loadId"] = "manual-retry" });
        Send(life, "book.ready", new() { ["loadId"] = "manual-retry" });
        Record("H-ERROR-OVERLAY", life.LoadingOverlay.Visibility == Visibility.Collapsed && life.AuditReady,
            new { overlay = life.LoadingOverlay.Visibility.ToString(), text = life.LoadingText.Text, ready = life.AuditReady });

        // 缺失 loadId / 错误来源旧 ready 不接受
        var missing = Host();
        Send(missing, "book.begin", new() { ["loadId"] = "new-open" });
        Send(missing, "book.ready");
        var foreign = Host();
        foreign.AuditRaw("\"lyra-book-ready\"", "https://other.invalid/old.html");
        Record("H-MISSING-LOAD", !missing.AuditReady, new { currentLoad = missing.AuditLoad, ready = missing.AuditReady });
        Record("H-LEGACY-SOURCE", !foreign.AuditReady, new { ready = foreign.AuditReady, overlay = foreign.LoadingOverlay.Visibility.ToString() });

        // 刷新导航：建立新会话，取消旧导航
        var refresh = Host();
        string oldSession = refresh.AuditSession;
        string oldLoad = refresh.AuditLoad;
        bool canceled = refresh.Wv.CoreWebView2.AuditNavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2NavigationKind.Reload);
        Record("H-REFRESH-SESSION", canceled && oldSession != refresh.AuditSession && oldLoad != refresh.AuditLoad,
            new { oldSession, newSession = refresh.AuditSession, oldLoad, newLoad = refresh.AuditLoad, canceled });

        // 无书导航失败：失败遮罩不能被空窗兜底计时器隐藏
        var failNav = Host();
        failNav.Wv.CoreWebView2.AuditNavigation(false);
        string errorBefore = failNav.LoadingOverlay.Visibility.ToString();
        foreach (var t in Microsoft.UI.Xaml.DispatcherTimer.All.ToArray()) t.AuditTick();
        Record("H-EMPTY-NAV-FAIL", failNav.LoadingOverlay.Visibility == Visibility.Visible,
            new { beforeTimer = errorBefore, afterTimer = failNav.LoadingOverlay.Visibility.ToString(), text = failNav.LoadingText.Text });

        // 关闭结构：停止计时器
        var closing = Host();
        int timersBefore = Microsoft.UI.Xaml.DispatcherTimer.All.Count(t => t.Running);
        closing.Close();
        int timersAfter = Microsoft.UI.Xaml.DispatcherTimer.All.Count(t => t.Running);
        Record("H-CLOSE-STRUCTURE", timersAfter == 0, new { timersBefore, timersAfter });

        File.WriteAllText(Path.Combine(Base, "r2-host-results.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            baseDirectory = Base,
            evidenceKind = "original C# host source with headless event doubles; not desktop integration",
            results = Results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        int failed = Results.Count(r => r.GetType().GetProperty("pass")!.GetValue(r) is false);
        Console.WriteLine("SUMMARY " + (failed == 0 ? "ALL PASS" : failed + " FAILED"));
    }
}
