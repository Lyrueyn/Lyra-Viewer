// R3 宿主/保存专项诊断：链接真实宿主源码，用无界面事件替身驱动原入口。
// 不是原生窗口测试；真实 WebView2 结果在 r3-evidence 另行记录。
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
    private static void Record(string id, bool pass, object details) => Results.Add(new { id, pass, details });
    private static ExtensionHostWindow Host()
    {
        var h = new ExtensionHostWindow("epub", "audit", Base, "index.html");
        h.AuditInitialize();
        // 替身没有真实初始导航事件：消费一次 suppress 标志，后续 Reload/NewDocument 才能走轮换
        h.Wv.CoreWebView2.AuditNavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2NavigationKind.NewDocument);
        return h;
    }
    private static JsonObject Request(ExtensionHostWindow h, string type, JsonObject? fields = null, string? session = null, string? source = null)
        => (h.AuditRequest(type, fields, session, source) ?? throw new Exception("no reply " + type)).AsObject();
    private static bool Ok(JsonObject r) => r["ok"]?.GetValue<bool>() == true;
    private static JsonObject Interact(ExtensionHostWindow h, string book, long? act = null, long? nav = null)
        => Request(h, "state.interact", new() { ["bookId"] = book, ["activationEpoch"] = act ?? h.AuditActivationEpoch, ["navEpoch"] = nav ?? h.AuditNavEpoch });
    private static JsonObject Prefs(ExtensionHostWindow h, string book, JsonObject prefs, JsonObject versions)
        => Request(h, "state.savePreferences", new() { ["bookId"] = book, ["preferences"] = prefs, ["fieldVersions"] = versions });
    private static void Raw(ExtensionHostWindow h, JsonObject obj, string? source = null) => h.AuditRaw(obj.ToJsonString(), source ?? h.AuditOrigin);

    private static void Main()
    {
        if (!Base.Contains("r3-host-audit", StringComparison.OrdinalIgnoreCase)) throw new Exception("refuse non-isolated root");
        if (Directory.Exists(Path.Combine(Base, "data"))) throw new Exception("choose a fresh output directory");
        Environment.SetEnvironmentVariable("LYRA_READER_DEBUG", null);
        var store = new ReaderStateStore();
        string bookA = Book('a');

        // R3-01：字段操作版本在用户修改时授予；旧字段版本不能覆盖新字段，伪造大数被拒
        var h = Host();
        h.AuditActivate(WindowActivationState.CodeActivated);
        var i1 = Interact(h, bookA);
        long fv1 = i1["result"]!["fieldVersion"]!.GetValue<long>();
        var prefs18 = Prefs(h, bookA, new JsonObject { ["fontSize"] = 18 }, new JsonObject { ["fontSize"] = fv1 });
        var i2 = Interact(h, bookA);
        long fv2 = i2["result"]!["fieldVersion"]!.GetValue<long>();
        var prefs32 = Prefs(h, bookA, new JsonObject { ["fontSize"] = 32 }, new JsonObject { ["fontSize"] = fv2 });
        var late18 = Prefs(h, bookA, new JsonObject { ["fontSize"] = 18 }, new JsonObject { ["fontSize"] = fv1 });
        var forged = Prefs(h, bookA, new JsonObject { ["fontSize"] = 99 }, new JsonObject { ["fontSize"] = long.MaxValue });
        var finalPrefs = store.Load(bookA, "observer")["preferences"]!;
        Record("H-R3-PREF-VERSION",
            Ok(prefs18) && Ok(prefs32) && !Ok(late18) && !Ok(forged) && (int)finalPrefs["fontSize"]! == 32,
            new { fv1, fv2, prefs18, prefs32, late18, forged, finalPrefs });

        // R3-02：重新获焦但旧 epoch 的 interact 不能复活
        string oldAct = h.AuditActivationEpoch.ToString();
        string oldNav = h.AuditNavEpoch.ToString();
        long oldActivation = h.AuditActivationEpoch;
        h.AuditActivate(WindowActivationState.Deactivated);
        h.AuditActivate(WindowActivationState.PointerActivated);
        var oldInteract = Interact(h, bookA, oldActivation, h.AuditNavEpoch);
        var newInteract = Interact(h, bookA);
        Record("H-R3-OLD-INTERACT", !Ok(oldInteract) && Ok(newInteract) && oldInteract["error"]?["code"]?.GetValue<string>() == "STALE_OPERATION_EPOCH",
            new { oldAct, oldNav, oldActivation, newEpoch = h.AuditActivationEpoch, oldInteract, newInteract });

        // R3-02：Reload 后旧 hello 不能重新选择/复活旧 session
        var refresh = Host();
        string oldSession = refresh.AuditSession;
        bool canceled = refresh.Wv.CoreWebView2.AuditNavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2NavigationKind.Reload);
        string newSession = refresh.AuditSession;
        int before = refresh.Wv.CoreWebView2.Replies.Count;
        Raw(refresh, new JsonObject { ["protocol"] = "lyra-reader", ["version"] = 1, ["type"] = "hello", ["requestId"] = "old-hello", ["sessionId"] = oldSession }, refresh.AuditOrigin.Replace("sessionId=" + newSession, "sessionId=" + oldSession));
        bool oldHelloRejected = refresh.Wv.CoreWebView2.Replies.Count == before && oldSession != newSession && canceled;
        int before2 = refresh.Wv.CoreWebView2.Replies.Count;
        Raw(refresh, new JsonObject { ["protocol"] = "lyra-reader", ["version"] = 1, ["type"] = "hello", ["requestId"] = "new-hello", ["sessionId"] = newSession });
        bool newHelloAccepted = refresh.Wv.CoreWebView2.Replies.Count > before2;
        Record("H-R3-REFRESH-HELLO", oldHelloRejected && newHelloAccepted, new { oldSession, newSession, canceled, oldHelloRejected, newHelloAccepted });

        // R3-03：关闭必须区分 ack/timeout；错误/空 requestId 不能关闭
        int closedBefore = Window.ClosedCount;
        var closing = Host();
        closing.AuditClosing();
        string? reqId = null;
        foreach (var rep in closing.Wv.CoreWebView2.Replies)
        {
            try { var n = JsonNode.Parse(rep)!.AsObject(); if (n["type"]?.GetValue<string>() == "host.finalize") reqId = n["requestId"]!.GetValue<string>(); } catch { }
        }
        Raw(closing, new JsonObject { ["type"] = "host.finalized", ["requestId"] = "" });
        bool emptyIgnored = Window.ClosedCount == closedBefore;
        Raw(closing, new JsonObject { ["type"] = "host.finalized", ["requestId"] = reqId ?? "", ["sessionId"] = closing.AuditSession, ["ok"] = true, ["revision"] = 7 });
        bool ackClosed = Window.ClosedCount > closedBefore;
        var timeoutHost = Host(); int beforeTimeout = Window.ClosedCount; timeoutHost.AuditClosing();
        foreach (var t in Microsoft.UI.Xaml.DispatcherTimer.All.ToArray()) t.AuditTick();
        bool timeoutClosed = Window.ClosedCount > beforeTimeout;
        var navFailHost = Host(); int beforeNavClose = Window.ClosedCount; navFailHost.AuditClosing();
        navFailHost.Wv.CoreWebView2.AuditNavigation(false);
        foreach (var t in Microsoft.UI.Xaml.DispatcherTimer.All.ToArray()) t.AuditTick();
        bool navFailClosed = Window.ClosedCount > beforeNavClose;
        Record("H-R3-FINALIZE", !string.IsNullOrEmpty(reqId) && emptyIgnored && ackClosed && timeoutClosed && navFailClosed,
            new { reqId, emptyIgnored, ackClosed, timeoutClosed, navFailClosed, closedCount = Window.ClosedCount });

        // R3-02 保存序号：新 token 重置序号空间；中文幂等按语义比较
        string bookB = Book('b');
        var t1 = Interact(h, bookB)["result"]!["interactionToken"]!.GetValue<long>();
        store.SavePosition(bookB, h.AuditSession, Json("""{"locator":{"quote":"相同位置"}}"""), t1, 10);
        bool unicodeIdem = false;
        try { var r = store.SavePosition(bookB, h.AuditSession, Json("""{"locator":{"quote":"相同位置"}}"""), t1, 10); unicodeIdem = r["idempotent"]?.GetValue<bool>() == true; } catch { }
        bool unicodeDiffRejected = false;
        try { store.SavePosition(bookB, h.AuditSession, Json("""{"locator":{"quote":"不同位置"}}"""), t1, 10); }
        catch (ReaderStateStore.StoreException ex) { unicodeDiffRejected = ex.Code == "DUPLICATE_SEQUENCE"; }
        var t2 = Interact(h, bookB)["result"]!["interactionToken"]!.GetValue<long>();
        bool newTokenSeqReset;
        try { store.SavePosition(bookB, h.AuditSession, Json("""{"locator":{"quote":"新资格"}}"""), t2, 1); newTokenSeqReset = true; } catch { newTokenSeqReset = false; }
        Record("H-R3-IDEMPOTENT-SEQ", unicodeIdem && unicodeDiffRejected && newTokenSeqReset,
            new { unicodeIdem, unicodeDiffRejected, newTokenSeqReset, final = store.Load(bookB, "observer")["position"]?["locator"]?["quote"]?.GetValue<string>() });

        File.WriteAllText(Path.Combine(Base, "r3-host-results.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            baseDirectory = Base,
            evidenceKind = "original C# host source with headless event doubles; not native WinUI/WebView2",
            results = Results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        int failed = Results.Count(r => r.GetType().GetProperty("pass")!.GetValue(r) is false);
        Console.WriteLine("SUMMARY " + (failed == 0 ? "ALL PASS" : failed + " FAILED"));
    }

    private static JsonElement Json(string raw) { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
}
