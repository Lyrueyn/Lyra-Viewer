using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WinUIGui;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static int _failed;

    private static void Check(string id, string name, bool ok, object? detail = null)
    {
        Results.Add(new { id, name, status = ok ? "PASS" : "FAIL", detail });
        if (!ok) _failed++;
        Console.WriteLine(JsonSerializer.Serialize(new { id, name, status = ok ? "PASS" : "FAIL", detail }));
    }

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static void Main()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "data", "reader", "epub");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        var store = new ReaderStateStore();
        string bookA = "sha256:" + new string('a', 64);
        string bookB = "sha256:" + new string('b', 64);
        try
        {
            var empty = store.Load(bookA, "session-a");
            Check("H01", "empty state loads", empty["position"] == null && (long)empty["revision"]! == 0, empty.ToJsonString());

            long tokenA = (long)store.GrantInteraction(bookA, "session-a", true)["interactionToken"]!;
            store.SavePosition(bookA, "session-a", Json("""{"spineHref":"a.xhtml","spineIndex":1,"locator":{"quote":"hello"}}"""), tokenA, 1);
            var loadedA = store.Load(bookA, "session-a");
            Check("H02", "save and load position", loadedA["position"]?["spineHref"]?.GetValue<string>() == "a.xhtml", loadedA.ToJsonString());

            long tokenB = (long)store.GrantInteraction(bookA, "session-b", true)["interactionToken"]!;
            bool staleRejected = false;
            try
            {
                store.SavePosition(bookA, "session-a", Json("""{"spineHref":"a.xhtml","spineIndex":2,"locator":{"quote":"late"}}"""), tokenA, 2);
            }
            catch (ReaderStateStore.StoreException ex) { staleRejected = ex.Code == "STALE_INTERACTION"; }
            store.SavePosition(bookA, "session-b", Json("""{"spineHref":"b.xhtml","spineIndex":1,"locator":{"quote":"B wins"}}"""), tokenB, 2);
            Check("H03", "stale interaction cannot overwrite newer window", staleRejected && store.Load(bookA, "session-b")["position"]?["locator"]?["quote"]?.GetValue<string>() == "B wins");

            bool inactiveRejected = false;
            try { store.GrantInteraction(bookA, "session-a", false); }
            catch (ReaderStateStore.StoreException ex) { inactiveRejected = ex.Code == "WINDOW_NOT_ACTIVE"; }
            long tokenA2 = (long)store.GrantInteraction(bookA, "session-a", true)["interactionToken"]!;
            store.SavePosition(bookA, "session-a", Json("""{"spineHref":"a2.xhtml","spineIndex":1,"locator":{"quote":"A again"}}"""), tokenA2, 3);
            Check("H04", "inactive window rejected; newly active window can take over", inactiveRejected && store.Load(bookA, "session-a")["position"]?["locator"]?["quote"]?.GetValue<string>() == "A again");

            store.SavePreferences("session-a", 100, Json("""{"fontSize":28,"theme":"sepia"}"""));
            store.SavePreferences("session-b", 200, Json("""{"lineHeight":2.1,"theme":"dark"}"""));
            var prefs = store.Load(bookA, "session-a")["preferences"]!;
            Check("H05", "preferences merge per-field", (int)prefs["fontSize"]! == 28 && (double)prefs["lineHeight"]! == 2.1 && prefs["theme"]!.GetValue<string>() == "dark", prefs.ToJsonString());

            var bm1 = Json("""{"id":"bm-1","title":"第一章","summary":"摘要一","spineHref":"a.xhtml","locator":{"quote":"one"},"createdAt":"2026-01-01T00:00:00Z"}""");
            var bm2 = Json("""{"id":"bm-2","title":"第二章","summary":"摘要二","spineHref":"b.xhtml","locator":{"quote":"two"},"createdAt":"2026-01-01T00:00:01Z"}""");
            store.AddBookmark(bookA, "session-a", bm1);
            store.AddBookmark(bookA, "session-b", bm2);
            var page1 = store.ListBookmarks(bookA, null, 1);
            string cursor = page1["nextCursor"]?.GetValue<string>() ?? "";
            var page2 = store.ListBookmarks(bookA, cursor, 1);
            Check("H06", "bookmarks paginated read", (int)page1["total"]! == 2 && page1["bookmarks"]!.AsArray().Count == 1 && page2["bookmarks"]!.AsArray().Count == 1 && !string.IsNullOrEmpty(cursor));

            bool dupRejected = false;
            try { store.AddBookmark(bookA, "session-a", bm1); }
            catch (ReaderStateStore.StoreException ex) { dupRejected = ex.Code == "DUPLICATE_BOOKMARK"; }
            var del = store.DeleteBookmark(bookA, "session-a", "bm-1");
            Check("H07", "bookmark duplicate/delete", dupRejected && del["deleted"]!.GetValue<bool>());

            bool cursorConflict = false;
            try { store.ListBookmarks(bookA, cursor, 1); }
            catch (ReaderStateStore.StoreException ex) { cursorConflict = ex.Code == "CONFLICT"; }
            Check("H08", "bookmark cursor revision conflict", cursorConflict);

            var statePath = Path.Combine(root, "books", bookB.Substring(7) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, "{not valid json");
            var recovered = store.Load(bookB, "session-x");
            bool backupExists = Directory.GetFiles(Path.GetDirectoryName(statePath)!, "*.corrupt-*").Length > 0;
            Check("H09", "corrupt state recovers and preserves original", recovered["position"] == null && backupExists);

            var store2 = new ReaderStateStore();
            Check("H10", "independent store reopens same data root", (int)store2.Load(bookA, "s")["bookmarkCount"]! == 1);


            // ---- R2：F06/F09/F10 宿主存储回归 ----
            long cur = (long)store.Load(bookA, "session-a")["interactionToken"]!;
            long tA = (long)store.GrantInteraction(bookB, "window-a", true)["interactionToken"]!;
            long tB = (long)store.GrantInteraction(bookB, "window-b", true)["interactionToken"]!;
            bool oldTokenRejected = false;
            try { store.SavePosition(bookB, "window-a", Json("""{"p":1}"""), tA, 20); }
            catch (ReaderStateStore.StoreException ex) { oldTokenRejected = ex.Code == "STALE_INTERACTION"; }
            bool wrongOwnerRejected = false;
            try { store.SavePosition(bookB, "window-b", Json("""{"p":2}"""), tA, 21); }
            catch (ReaderStateStore.StoreException ex) { wrongOwnerRejected = ex.Code == "TOKEN_OWNER_MISMATCH" || ex.Code == "STALE_INTERACTION"; }
            bool fakeTokenRejected = false;
            try { store.SavePosition(bookB, "window-b", Json("""{"p":3}"""), 101, 22); }
            catch (ReaderStateStore.StoreException ex) { fakeTokenRejected = ex.Code == "STALE_INTERACTION"; }
            store.SavePosition(bookB, "window-b", Json("""{"p":4}"""), tB, 30);
            bool rollbackRejected = false;
            try { store.SavePosition(bookB, "window-b", Json("""{"p":5}"""), tB, 2); }
            catch (ReaderStateStore.StoreException ex) { rollbackRejected = ex.Code == "STALE_SEQUENCE"; }
            var idem = store.SavePosition(bookB, "window-b", Json("""{"p":4}"""), tB, 30);
            Check("H11", "F06 exact token/owner/sequence monotonic/idempotent",
                oldTokenRejected && wrongOwnerRejected && fakeTokenRejected && rollbackRejected && idem["idempotent"]?.GetValue<bool>() == true);

            store.SavePreferences("window-a", 300, Json("""{"fontSize":18}"""));
            store.SavePreferences("window-b", 500, Json("""{"fontSize":32,"theme":"dark"}"""));
            store.SavePreferences("window-a", 400, Json("""{"fontSize":18,"theme":"sepia"}"""));
            var prefs2 = store.Load(bookB, "window-b")["preferences"]!;
            Check("H12", "F08 late preference field cannot overwrite newer field",
                (int)prefs2["fontSize"]! == 32 && prefs2["theme"]!.GetValue<string>() == "dark", prefs2.ToJsonString());

            // 书签 51 条：游标必须推进，读回不能有重复/遗漏
            string bookC = "sha256:" + new string('c', 64);
            for (int i = 0; i < 51; i++)
            {
                store.AddBookmark(bookC, "window-a", Json($$"""{"id":"c-{{i}}","title":"t{{i}}","summary":"s{{i}}","spineHref":"c.xhtml","locator":{"quote":"q{{i}}"},"createdAt":"2026-01-01T00:00:00Z"}"""));
            }
            var seen = new HashSet<string>(); string? cursor2 = null; int loops = 0;
            while (loops++ < 20)
            {
                var pg = store.ListBookmarks(bookC, cursor2, 50);
                foreach (var b in pg["bookmarks"]!.AsArray()) seen.Add(b!["id"]!.GetValue<string>());
                cursor2 = pg["nextCursor"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cursor2)) break;
            }
            Check("H13", "F09 51 bookmarks paginate without duplicate/loss", (int)store.Load(bookC, "x")["bookmarkCount"]! == 51 && seen.Count == 51, new { seen = seen.Count, loops });

            // 50 条接近 4KB 的书签，分页响应不能超过 256KiB；按字节减少单页
            string bookD = "sha256:" + new string('d', 64);
            string big = new string('中', 200);
            string longTitle = new string('题', 1100);
            for (int i = 0; i < 50; i++)
            {
                store.AddBookmark(bookD, "window-a", Json($$"""{"id":"d-{{i}}","title":"{{longTitle}}","summary":"{{big}}","spineHref":"d.xhtml","locator":{"quote":"q"},"createdAt":"2026-01-01T00:00:00Z"}"""));
            }
            var bytePage = store.ListBookmarks(bookD, null, 50);
            int pageCount = bytePage["bookmarks"]!.AsArray().Count;
            int bytes = System.Text.Encoding.UTF8.GetByteCount(bytePage.ToJsonString());
            Check("H14", "F09 bookmark page respects UTF-8 byte budget", pageCount > 0 && pageCount < 50 && bytes <= 180 * 1024, new { pageCount, bytes });

            bool limitRejected = false;
            try
            {
                for (int i = 51; i <= 500; i++) store.AddBookmark(bookC, "window-a", Json($$"""{"id":"c-{{i}}","title":"t","summary":"s","spineHref":"c.xhtml","locator":{"quote":"q"},"createdAt":"2026-01-01T00:00:00Z"}"""));
                store.AddBookmark(bookC, "window-a", Json("""{"id":"c-501","title":"t","summary":"s","spineHref":"c.xhtml","locator":{"quote":"q"},"createdAt":"2026-01-01T00:00:00Z"}"""));
            }
            catch (ReaderStateStore.StoreException ex) { limitRejected = ex.Code == "BOOKMARK_LIMIT"; }
            Check("H15", "F09 501st bookmark rejected, first 500 retained", limitRejected && (int)store.Load(bookC, "x")["bookmarkCount"]! == 500, new { count = (int)store.Load(bookC, "x")["bookmarkCount"]! });

            // 暂时拒读：不能当空状态覆盖，文件字节必须保持不变
            string statePath2 = Path.Combine(root, "books", bookB.Substring(7) + ".json");
            byte[] beforeBytes = File.ReadAllBytes(statePath2);
            bool unreadableRejected = false;
            using (var exclusive = new FileStream(statePath2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { store.Load(bookB, "x"); }
                catch (ReaderStateStore.StoreException ex) { unreadableRejected = ex.Code == "STATE_UNREADABLE"; }
            }
            byte[] afterBytes = File.ReadAllBytes(statePath2);
            Check("H16", "F10 unreadable state is not treated as empty/overwritten",
                unreadableRejected && beforeBytes.SequenceEqual(afterBytes), new { unreadableRejected, same = beforeBytes.SequenceEqual(afterBytes) });

            // 写失败：保留原文件，不留下临时文件，不把失败报成成功
            string bookE = "sha256:" + new string('e', 64);
            long tE = (long)store.GrantInteraction(bookE, "window-a", true)["interactionToken"]!;
            store.SavePosition(bookE, "window-a", Json("""{"p":"before"}"""), tE, 100);
            string pathE = Path.Combine(root, "books", bookE.Substring(7) + ".json");
            byte[] bytesBeforeWriteFail = File.ReadAllBytes(pathE);
            File.SetAttributes(pathE, FileAttributes.ReadOnly);
            bool writeFailReported = false;
            try { store.SavePosition(bookE, "window-a", Json("""{"p":"after"}"""), tE, 101); }
            catch (ReaderStateStore.StoreException ex) { writeFailReported = ex.Code == "WRITE_FAILED"; }
            finally { File.SetAttributes(pathE, FileAttributes.Normal); }
            bool bytesPreserved = bytesBeforeWriteFail.SequenceEqual(File.ReadAllBytes(pathE));
            int tmpLeft = Directory.GetFiles(Path.GetDirectoryName(pathE)!, Path.GetFileName(pathE) + ".tmp-*").Length;
            Check("H17", "F10 write failure preserves old bytes and reports error", writeFailReported && bytesPreserved && tmpLeft == 0,
                new { writeFailReported, bytesPreserved, tmpLeft });
            var reportPath = Path.Combine(AppContext.BaseDirectory, "host-state-results.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { generatedAt = DateTime.UtcNow.ToString("O"), results = Results }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("SUMMARY " + (_failed == 0 ? "ALL PASS" : _failed + " FAILED"));
        }
        catch (Exception ex)
        {
            Check("HXX", "host state test crashed", false, ex.ToString());
        }
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}

