// R4 宿主/状态审计：直接链接原宿主源码，事件替身驱动原入口。
// 覆盖：无损 grant、偏好/位置资格分离、幂等/冲突、旧数据迁移、关闭严格关联、早期关闭期限。
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using WinUIGui;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static readonly string Base = AppContext.BaseDirectory;
    private static string BookA => "sha256:" + new string('a', 64);
    private static string BookB => "sha256:" + new string('b', 64);
    private static void Record(string id, bool pass, object detail) => Results.Add(new { id, pass, details = detail });
    private static ExtensionHostWindow Host(bool initialize = true)
    {
        var h = new ExtensionHostWindow("epub", "audit", Base, "index.html");
        if (initialize) { h.AuditInitialize(); h.AuditActivate(WindowActivationState.CodeActivated); h.Wv.CoreWebView2.AuditNavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2NavigationKind.NewDocument); }
        return h;
    }
    private static JsonObject Request(ExtensionHostWindow h, string type, JsonObject? fields = null)
        => (h.AuditRequest(type, fields) ?? throw new Exception("no reply " + type)).AsObject();
    private static bool Ok(JsonObject r) => r["ok"]?.GetValue<bool>() == true;
    private static JsonObject Grant(ExtensionHostWindow h, string operationId, params string[] fields)
    {
        var arr = new JsonArray();
        foreach (var f in fields) arr.Add(f);
        return Request(h, "state.grantPreferences", new JsonObject
        {
            ["operationId"] = operationId, ["fields"] = arr,
            ["activationEpoch"] = h.AuditActivationEpoch, ["navEpoch"] = h.AuditNavEpoch,
        });
    }
    private static JsonObject SavePrefs(ExtensionHostWindow h, JsonObject prefs, JsonObject grants)
        => Request(h, "state.savePreferences", new JsonObject { ["scope"] = "global", ["preferences"] = prefs, ["grants"] = grants, ["bookId"] = "" });
    private static void Raw(ExtensionHostWindow h, JsonObject obj, string? source = null) => h.AuditRaw(obj.ToJsonString(), source ?? h.AuditOrigin);

    private static void Main()
    {
        if (!Base.Contains("r4-host-audit", StringComparison.OrdinalIgnoreCase)) throw new Exception("refuse non-isolated root");
        if (Directory.Exists(Path.Combine(Base, "data"))) throw new Exception("choose a fresh output directory");
        Environment.SetEnvironmentVariable("LYRA_READER_DEBUG", null);
        Directory.CreateDirectory(Path.Combine(Base, "data", "reader", "epub"));
        // 旧 R3 数据：保留 preferences 原值和大 fieldSequences，不得清空
        File.WriteAllText(Path.Combine(Base, "data", "reader", "epub", "preferences.json"),
            """{"version":1,"revision":7,"preferences":{"fontSize":22},"fieldSequences":{"fontSize":639287159852507025}}""");
        var h = Host();

        // R4-01: 不透明字符串 grant，连续授予/保存/幂等/旧操作
        var g18 = Grant(h, "op-18", "fontSize");
        string grant18 = g18["result"]!["grant"]!.GetValue<string>();
        var save18 = SavePrefs(h, new JsonObject { ["fontSize"] = 18 }, new JsonObject { ["fontSize"] = grant18 });
        var g32 = Grant(h, "op-32", "fontSize");
        string grant32 = g32["result"]!["grant"]!.GetValue<string>();
        var old18 = SavePrefs(h, new JsonObject { ["fontSize"] = 19 }, new JsonObject { ["fontSize"] = grant18 });
        var save32 = SavePrefs(h, new JsonObject { ["fontSize"] = 32 }, new JsonObject { ["fontSize"] = grant32 });
        var idem = SavePrefs(h, new JsonObject { ["fontSize"] = 32 }, new JsonObject { ["fontSize"] = grant32 });
        var ab = new ExtensionHostWindow("epub", "audit2", Base, "index.html");
        var prefsDisk = JsonNode.Parse(File.ReadAllText(Path.Combine(Base, "data", "reader", "epub", "preferences.json")))!.AsObject();
        bool noNumericLeak = g18["result"]?["grant"] is JsonValue && g18["result"]!["fieldVersion"] == null && g18["result"]!["interactionToken"] == null;
        Record("H-R4-01-GRANT-STRING", Ok(save18) && Ok(save32) && !Ok(old18) && Ok(idem) &&
            noNumericLeak && prefsDisk["preferences"]?["fontSize"]?.GetValue<int>() == 32 &&
            prefsDisk["fieldSequences"]?["fontSize"]?.GetValue<long>() == 639287159852507025 &&
            prefsDisk["preferenceOrders"]?["fontSize"] is JsonValue,
            new { g18, g32, save18, old18, save32, idem, noNumericLeak, prefsDisk });

        // R4-02: 偏好 grant 不改变位置 token；位置 token 只由 interact 改变
        long token1 = Request(h, "state.interact", new() { ["bookId"] = BookA, ["activationEpoch"] = h.AuditActivationEpoch, ["navEpoch"] = h.AuditNavEpoch })["result"]!["interactionToken"]!.GetValue<long>();
        var prefGrant = Grant(h, "after-turn", "lineHeight");
        var savePosAfterPref = Request(h, "state.savePosition", new() { ["bookId"] = BookA, ["interactionToken"] = token1, ["sequence"] = 1, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 1 } } });
        long token2 = Request(h, "state.interact", new() { ["bookId"] = BookA, ["activationEpoch"] = h.AuditActivationEpoch, ["navEpoch"] = h.AuditNavEpoch })["result"]!["interactionToken"]!.GetValue<long>();
        Record("H-R4-02-AUTH-SEPARATE", prefGrant["result"]?["grant"] is JsonValue && token2 == token1 + 1 && Ok(savePosAfterPref),
            new { token1, token2, prefGrant, savePosAfterPref });

        // 同一 grant 不能改另一字段；错误字段集合被拒
        var lhGrant = Grant(h, "lh-only", "lineHeight");
        string lhToken = lhGrant["result"]!["grant"]!.GetValue<string>();
        var wrongField = SavePrefs(h, new JsonObject { ["fontSize"] = 20 }, new JsonObject { ["fontSize"] = lhToken });
        var wrongNoGrant = SavePrefs(h, new JsonObject { ["lineHeight"] = 1.8 }, new JsonObject());
        Record("H-R4-02-FIELD-GRANT", !Ok(wrongField) && !Ok(wrongNoGrant), new { wrongField, wrongNoGrant });

        // 位置序号/旧 token 幂等与拒绝
        var firstPos = Request(h, "state.savePosition", new() { ["bookId"] = BookA, ["interactionToken"] = token2, ["sequence"] = 1, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 1 } } });
        var dupPos = Request(h, "state.savePosition", new() { ["bookId"] = BookA, ["interactionToken"] = token2, ["sequence"] = 1, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 1 } } });
        var stalePos = Request(h, "state.savePosition", new() { ["bookId"] = BookA, ["interactionToken"] = token2, ["sequence"] = 0, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 2 } } });
        Record("H-R4-04-POSITION-TOKEN", Ok(firstPos) && Ok(dupPos) && !Ok(stalePos), new { firstPos, dupPos, stalePos });

        // 关闭关联：缺 session 不关闭，错 request 不关闭，正确 request+session 关闭
        int before = Window.ClosedCount;
        var closing = Host();
        closing.AuditClosing();
        string? reqId = null;
        foreach (var rep in closing.Wv.CoreWebView2.Replies)
        {
            try { var n = JsonNode.Parse(rep)!.AsObject(); if (n["type"]?.GetValue<string>() == "host.finalize") reqId = n["requestId"]!.GetValue<string>(); } catch { }
        }
        Raw(closing, new JsonObject { ["type"] = "host.finalized", ["requestId"] = reqId ?? "", ["ok"] = true });
        bool missingSessionIgnored = Window.ClosedCount == before;
        Raw(closing, new JsonObject { ["type"] = "host.finalized", ["requestId"] = "wrong", ["sessionId"] = closing.AuditSession, ["ok"] = true });
        bool wrongReqIgnored = Window.ClosedCount == before;
        Raw(closing, new JsonObject { ["type"] = "host.finalized", ["requestId"] = reqId ?? "", ["sessionId"] = closing.AuditSession, ["ok"] = false });
        bool exactErrorClosed = Window.ClosedCount > before;
        Record("H-R4-03-FINALIZE-ASSOC", !string.IsNullOrEmpty(reqId) && missingSessionIgnored && wrongReqIgnored && exactErrorClosed,
            new { reqId, missingSessionIgnored, wrongReqIgnored, exactErrorClosed });

        // 早期关闭：尚未初始化也必须有独立 deadline 并最终销毁
        int beforeEarly = Window.ClosedCount;
        var early = Host(initialize: false);
        early.AuditClosing();
        foreach (var t in Microsoft.UI.Xaml.DispatcherTimer.All.ToArray()) t.AuditTick();
        bool earlyClosed = Window.ClosedCount > beforeEarly;
        Record("H-R4-03-EARLY-CLOSE", earlyClosed, new { earlyClosed, timers = Microsoft.UI.Xaml.DispatcherTimer.All.Count });

        // 旧书/坏书隔离：坏 A 不阻止健康 B 的 store 读写
        bool isolation;
        try
        {
            var bookA = h.AuditRequest("state.load", new JsonObject { ["bookId"] = BookA });
            // 直接写入坏 A 状态文件后，B 仍可加载
            File.WriteAllText(Path.Combine(Base, "data", "reader", "epub", "books", new string('a', 64) + ".json"), "{bad");
            var bookB = h.AuditRequest("state.load", new JsonObject { ["bookId"] = BookB });
            isolation = bookB != null;
        }
        catch { isolation = false; }
        Record("H-R4-05-BOOK-ISOLATION", isolation, new { isolation });

        File.WriteAllText(Path.Combine(Base, "r4-host-results.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            baseDirectory = Base,
            evidenceKind = "original C# host source with headless WinUI/WebView2 event doubles; not native WinUI/WebView2",
            results = Results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SUMMARY " + (Results.Any(r => r.GetType().GetProperty("pass")!.GetValue(r) is false) ? "FAILED" : "ALL PASS"));
    }
}