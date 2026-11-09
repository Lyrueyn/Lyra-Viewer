// R5 host/state audit: original host source compiled with headless WinUI/WebView2 doubles.
// Covers field-level superseded, completion cache vs capacity, scope rejection,
// positionRevision/metadataRevision separation, S03 failed-save state, finalize association.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using WinUIGui;

internal static class Program
{
    private static readonly List<object> Results = new();
    private const int MaxGrantProbe = 4096;
    private static readonly string Base = AppContext.BaseDirectory;
    private static string BookA => "sha256:" + new string('a', 64);
    private static string BookB => "sha256:" + new string('b', 64);
    private static string BookC => "sha256:" + new string('c', 64);
    private static void Rec(string id, bool pass, object detail) => Results.Add(new { id, pass, details = detail });
    private static ExtensionHostWindow Host(bool initialize = true)
    {
        var h = new ExtensionHostWindow("epub", "audit-r5", Base, "index.html");
        if (initialize) { h.AuditInitialize(); h.AuditActivate(WindowActivationState.CodeActivated); h.Wv.CoreWebView2.AuditNavigationStarting(Microsoft.Web.WebView2.Core.CoreWebView2NavigationKind.NewDocument); }
        return h;
    }
    private static JsonObject Request(ExtensionHostWindow h, string type, JsonObject? fields = null)
        => (h.AuditRequest(type, fields) ?? throw new Exception("no reply " + type)).AsObject();
    private static bool Ok(JsonObject r) => r["ok"]?.GetValue<bool>() == true;
    private static JsonObject Grant(ExtensionHostWindow h, string op, params string[] fields)
    {
        var a = new JsonArray(); foreach (var f in fields) a.Add(f);
        return Request(h, "state.grantPreferences", new JsonObject
        {
            ["operationId"] = op, ["fields"] = a,
            ["activationEpoch"] = h.AuditActivationEpoch, ["navEpoch"] = h.AuditNavEpoch,
        });
    }
    private static JsonObject SavePrefs(ExtensionHostWindow h, JsonObject prefs, JsonObject grants, string scope = "global")
        => Request(h, "state.savePreferences", new JsonObject { ["scope"] = scope, ["preferences"] = prefs, ["grants"] = grants });
    private static string GrantToken(JsonObject g) => g["result"]?["grant"]?.GetValue<string>() ?? "";
    private static JsonObject PrefsFile()
    {
        var p = Path.Combine(Base, "data", "reader", "epub", "preferences.json");
        return JsonNode.Parse(File.ReadAllText(p))!.AsObject();
    }
    private static string StateFile(string bookId) => Path.Combine(Base, "data", "reader", "epub", "books", bookId.Substring(7) + ".json");
    private static JsonObject LoadState(ExtensionHostWindow h, string bookId) => Request(h, "state.load", new JsonObject { ["bookId"] = bookId });

    private static void Main()
    {
        if (!Base.Contains("r6-host-audit", StringComparison.OrdinalIgnoreCase)) throw new Exception("refuse non-isolated root");
        if (Directory.Exists(Path.Combine(Base, "data"))) throw new Exception("fresh output dir required");
        Directory.CreateDirectory(Path.Combine(Base, "data", "reader", "epub", "books"));
        Environment.SetEnvironmentVariable("LYRA_READER_DEBUG", null);
        var h = Host();

        // 1. field-level superseded: old font18 + new line2.2 in one transaction must apply line and mark font superseded.
        var g18 = Grant(h, "f18", "fontSize"); var t18 = GrantToken(g18);
        var s18 = SavePrefs(h, new JsonObject { ["fontSize"] = 18 }, new JsonObject { ["fontSize"] = t18 });
        var g32 = Grant(h, "f32", "fontSize"); var t32 = GrantToken(g32);
        var s32 = SavePrefs(h, new JsonObject { ["fontSize"] = 32 }, new JsonObject { ["fontSize"] = t32 });
        var gl = Grant(h, "line", "lineHeight"); var tl = GrantToken(gl);
        var mixed = SavePrefs(h, new JsonObject { ["fontSize"] = 18, ["lineHeight"] = 2.2 }, new JsonObject { ["fontSize"] = t18, ["lineHeight"] = tl });
        var disk = PrefsFile();
        bool fieldLevel = Ok(s18) && Ok(s32) && Ok(mixed) &&
            string.Join(",", (mixed["result"]?["applied"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>())) == "lineHeight" &&
            string.Join(",", (mixed["result"]?["superseded"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>())) == "fontSize" &&
            disk["preferences"]?["fontSize"]?.GetValue<int>() == 32 &&
            Math.Abs((disk["preferences"]?["lineHeight"]?.GetValue<double>() ?? 0) - 2.2) < 0.0001;
        Rec("R5-PREF-FIELD-SUPERSEDED", fieldLevel, new { g18, s18, g32, s32, gl, mixed, disk });

        // 2. completion credential survives grant capacity recycle; duplicate old save is idempotent, not MISSING_GRANT.
        var h2 = Host();
        for (int i = 0; i < 4095; i++) Grant(h2, "fill-" + i, "fontSize");
        var gf = Grant(h2, "font28", "fontSize"); var tf = GrantToken(gf);
        var sf = SavePrefs(h2, new JsonObject { ["fontSize"] = 28 }, new JsonObject { ["fontSize"] = tf });
        var gline = Grant(h2, "line-after-cap", "lineHeight"); var tline = GrantToken(gline);
        var sline = SavePrefs(h2, new JsonObject { ["lineHeight"] = 2.1 }, new JsonObject { ["lineHeight"] = tline });
        var retry = SavePrefs(h2, new JsonObject { ["fontSize"] = 28 }, new JsonObject { ["fontSize"] = tf });
        var capDisk = PrefsFile();
        bool capOk = Ok(sf) && Ok(sline) && Ok(retry) &&
            string.Join(",", (retry["result"]?["idempotent"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>())) == "fontSize" &&
            capDisk["preferences"]?["fontSize"]?.GetValue<int>() == 28 &&
            Math.Abs((capDisk["preferences"]?["lineHeight"]?.GetValue<double>() ?? 0) - 2.1) < 0.0001;
        Rec("R5-PREF-COMPLETION-CAPACITY", capOk, new { gf, sf, gline, sline, retry, capDisk });

        // 3. scope contract: non-global must reject and not write.
        var h3 = Host();
        var gs = Grant(h3, "scope", "fontSize"); var ts = GrantToken(gs);
        var before = PrefsFile().ToJsonString();
        var badScope = SavePrefs(h3, new JsonObject { ["fontSize"] = 20 }, new JsonObject { ["fontSize"] = ts }, "book:foreign");
        var after = PrefsFile().ToJsonString();
        Rec("R5-PREF-SCOPE", !Ok(badScope) && before == after, new { badScope, beforeHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before)), afterHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(after)) });

        // 4. positionRevision vs metadataRevision: grant interaction does not bump positionRevision; actual position does.
        var h4 = Host();
        var pre = LoadState(h4, BookC);
        var inter = Request(h4, "state.interact", new JsonObject { ["bookId"] = BookC, ["activationEpoch"] = h4.AuditActivationEpoch, ["navEpoch"] = h4.AuditNavEpoch });
        var afterInteract = LoadState(h4, BookC);
        long tok = inter["result"]!["interactionToken"]!.GetValue<long>();
        var posSave = Request(h4, "state.savePosition", new JsonObject { ["bookId"] = BookC, ["interactionToken"] = tok, ["sequence"] = 1, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 100 } } });
        var afterPos = LoadState(h4, BookC);
        var bm = Request(h4, "bookmark.add", new JsonObject { ["bookId"] = BookC, ["bookmark"] = new JsonObject { ["id"] = "bm-1", ["summary"] = "s", ["locator"] = new JsonObject { ["textOffset"] = 100 } } });
        var afterBm = LoadState(h4, BookC);
        bool revSep = Ok(posSave) && Ok(bm) &&
            (pre["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 0 &&
            (afterInteract["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 0 &&
            (afterPos["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 1 &&
            (afterBm["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 1 &&
            (afterBm["result"]?["metadataRevision"]?.GetValue<long>() ?? -1) == 1;
        Rec("R5-POS-REVISION-SEPARATION", revSep, new { pre, inter, afterInteract, posSave, afterPos, bm, afterBm });

        // 5. S03 host chain: interaction writes token but not positionRevision; failed position write leaves position null/rev0.
        var h5 = Host();
        var loadS03 = LoadState(h5, BookA);
        var tokS03 = Request(h5, "state.interact", new JsonObject { ["bookId"] = BookA, ["activationEpoch"] = h5.AuditActivationEpoch, ["navEpoch"] = h5.AuditNavEpoch })["result"]!["interactionToken"]!.GetValue<long>();
        File.SetAttributes(StateFile(BookA), FileAttributes.ReadOnly);
        var failedPos = Request(h5, "state.savePosition", new JsonObject { ["bookId"] = BookA, ["interactionToken"] = tokS03, ["sequence"] = 1, ["position"] = new JsonObject { ["locator"] = new JsonObject { ["textOffset"] = 100 } } });
        File.SetAttributes(StateFile(BookA), FileAttributes.Normal);
        var reloadS03 = LoadState(h5, BookA);
        var stateS03 = JsonNode.Parse(File.ReadAllText(StateFile(BookA)))!.AsObject();
        bool s03 = !Ok(failedPos) && stateS03["position"] == null &&
            (reloadS03["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 0 &&
            (loadS03["result"]?["positionRevision"]?.GetValue<long>() ?? -1) == 0;
        Rec("R5-POS-S03-FAILED-WRITE", s03, new { loadS03, tokS03, failedPos, reloadS03, stateS03 });

        // R6 completion: full grant field set + per-field superseded credentials.
        {
            var hk = Host();
            var ga = Grant(hk, "r6-k01-a", "theme", "bg", "fg");
            var gb = Grant(hk, "r6-k01-b", "theme", "bg");
            string ta = GrantToken(ga), tb = GrantToken(gb);
            var sa = SavePrefs(hk, new JsonObject { ["theme"] = "sepia", ["bg"] = "#aa0000", ["fg"] = "#ffffff" },
                new JsonObject { ["theme"] = ta, ["bg"] = ta, ["fg"] = ta });
            var sb = SavePrefs(hk, new JsonObject { ["theme"] = "custom", ["bg"] = "#00aa00" }, new JsonObject { ["theme"] = tb, ["bg"] = tb });
            var kretry2 = SavePrefs(hk, new JsonObject { ["theme"] = "sepia", ["bg"] = "#aa0000", ["fg"] = "#ffffff" },
                new JsonObject { ["theme"] = ta, ["bg"] = ta, ["fg"] = ta });
            var retryApplied = (kretry2["result"]?["applied"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            var retrySup = (kretry2["result"]?["superseded"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            bool ok = retryApplied.Contains("fg") && retrySup.Contains("theme") && retrySup.Contains("bg") &&
                (kretry2["result"]?["revision"]?.GetValue<long>() ?? 0) >= (sa["result"]?["revision"]?.GetValue<long>() ?? 0);
            Rec("R6-K01-COMPLETION-SUPERSEDED", ok && Ok(sa) && Ok(sb), new { ta, tb, sa, sb, kretry2 });
        }
        // R6 C02 pressure: 4096 same-field grants; new supersedes old, final intent must settle.
        {
            var hc = Host();
            string last = "";
            bool every = true;
            for (int i = 0; i < MaxGrantProbe; i++)
            {
                var g = Grant(hc, "r6-c02-" + i, "lineHeight");
                every &= Ok(g);
                last = GrantToken(g);
            }
            var sp = SavePrefs(hc, new JsonObject { ["lineHeight"] = 2.2 }, new JsonObject { ["lineHeight"] = last });
            var pf = PrefsFile();
            bool kdisk = (pf["preferences"] as JsonObject)?["lineHeight"]?.GetValue<double>() == 2.2;
            Rec("R6-C02-PRESSURE-LAST-INTENT", every && Ok(sp) && kdisk, new { every, sp, kdisk, last });
        }
        // 6. finalize strict association + early close remain.
        int beforeClose = Window.ClosedCount;
        var closing = Host();
        closing.AuditClosing();
        string reqId = "";
        foreach (var rep in closing.Wv.CoreWebView2.Replies)
        {
            try { var n = JsonNode.Parse(rep)!.AsObject(); if (n["type"]?.GetValue<string>() == "host.finalize") reqId = n["requestId"]!.GetValue<string>(); } catch { }
        }
        closing.AuditRaw(new JsonObject { ["type"] = "host.finalized", ["requestId"] = reqId, ["ok"] = true }.ToJsonString());
        bool missing = Window.ClosedCount == beforeClose;
        closing.AuditRaw(new JsonObject { ["type"] = "host.finalized", ["requestId"] = reqId, ["sessionId"] = closing.AuditSession, ["ok"] = false }.ToJsonString());
        bool exact = Window.ClosedCount > beforeClose;
        int beforeEarly = Window.ClosedCount;
        var early = Host(false); early.AuditClosing();
        foreach (var t in Microsoft.UI.Xaml.DispatcherTimer.All.ToArray()) t.AuditTick();
        bool earlyOk = Window.ClosedCount > beforeEarly;
        Rec("R5-CLOSE-ASSOC-EARLY", missing && exact && earlyOk, new { reqId, missing, exact, earlyOk });

        File.WriteAllText(Path.Combine(Base, "r6-host-results.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.UtcNow,
            baseDirectory = Base,
            evidenceKind = "original C# host source with headless WinUI/WebView2 event doubles; not native WinUI/WebView2",
            results = Results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SUMMARY " + (Results.Any(r => r.GetType().GetProperty("pass")!.GetValue(r) is false) ? "FAILED" : "ALL PASS"));
    }
}
