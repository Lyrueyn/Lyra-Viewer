using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinUIGui
{
    /// <summary>
    /// EPUB 阅读器的受限持久化存储。状态只允许按 sha256 书身份读写；
    /// 读取失败不能被当成空状态覆盖，成功回执以真实落盘为准。
    /// </summary>
    public sealed class ReaderStateStore
    {
        private const int StoreVersion = 1;
        private const int MaxBookmarks = 500;
        private const int MaxBookmarkBytes = 4096;
        private const int MaxPositionBytes = 32768;
        private const int MaxSummaryUtf16 = 256;
        private const int MaxBookmarkPage = 50;
        private const int MaxBookmarkPageBytes = 180 * 1024;

        private static readonly object PrefsLock = new();
        private static readonly ConcurrentDictionary<string, object> BookLocks = new();

        private static readonly Regex BookIdRegex = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);
        private static readonly string[] PrefKeys =
        {
            "fontSize", "lineHeight", "contentWidth", "theme", "bg", "fg",
            "selectable", "reduceMotion", "sidebar"
        };

        private enum ReadStatus { Missing, Ok, Corrupt, Unreadable, InvalidVersion }

        public string Root => Path.Combine(AppContext.BaseDirectory, "data", "reader", "epub");
        private string BooksDir => Path.Combine(Root, "books");
        private string PrefsFile => Path.Combine(Root, "preferences.json");

        public ReaderStateStore()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(BooksDir);
        }

        public sealed class StoreException : Exception
        {
            public string Code { get; }
            public StoreException(string code, string message) : base(message) { Code = code; }
        }

        private static object LockFor(string bookId)
        {
            ValidateBookId(bookId);
            return BookLocks.GetOrAdd(bookId, _ => new object());
        }

        private static void ValidateBookId(string bookId)
        {
            if (string.IsNullOrEmpty(bookId) || !BookIdRegex.IsMatch(bookId))
                throw new StoreException("BAD_BOOK_ID", "bookId 必须是 sha256:<64位小写十六进制>");
        }

        private string StatePath(string bookId) => Path.Combine(BooksDir, bookId.Substring("sha256:".Length) + ".json");

        private static void BackupCorrupt(string path)
        {
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true); }
            catch { }
        }

        private static (JsonObject? obj, ReadStatus status) ReadObjectSafe(string path)
        {
            if (!File.Exists(path)) return (null, ReadStatus.Missing);
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) { return (null, ReadStatus.Unreadable); }
            catch (UnauthorizedAccessException) { return (null, ReadStatus.Unreadable); }
            catch { return (null, ReadStatus.Unreadable); }
            if (string.IsNullOrWhiteSpace(text)) { BackupCorrupt(path); return (null, ReadStatus.Corrupt); }
            try
            {
                var node = JsonNode.Parse(text);
                if (node is not JsonObject obj) { BackupCorrupt(path); return (null, ReadStatus.Corrupt); }
                if (obj["version"] is not JsonValue v || !v.TryGetValue<int>(out int version) || version != StoreVersion)
                {
                    // 未知版本不能静默清空，也不允许继续写入覆盖
                    return (null, ReadStatus.InvalidVersion);
                }
                return (obj, ReadStatus.Ok);
            }
            catch (JsonException)
            {
                BackupCorrupt(path);
                return (null, ReadStatus.Corrupt);
            }
            catch (IOException) { return (null, ReadStatus.Unreadable); }
            catch { return (null, ReadStatus.Unreadable); }
        }

        private static void AtomicWrite(string path, JsonObject obj)
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string text = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    fs.Flush(true);
                if (File.Exists(path))
                    File.Replace(tmp, path, null, true);   // 失败时直接抛错，绝不退回非原子覆盖
                else
                    File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (ex is StoreException) throw;
                throw new StoreException("WRITE_FAILED", "状态写入失败：" + ex.Message);
            }
        }

        private static long Long(JsonObject obj, string key)
        {
            if (obj == null || obj[key] is not JsonValue value) return 0;
            if (value.TryGetValue<long>(out long l)) return l;
            if (value.TryGetValue<int>(out int i)) return i;
            if (value.TryGetValue<double>(out double d)) return (long)d;
            return 0;
        }

        private static JsonObject NewState() => new()
        {
            ["version"] = StoreVersion,
            ["revision"] = 0L,
            ["interaction"] = 0L,
            ["interactionOwner"] = "",
            ["position"] = null,
            ["positionToken"] = 0L,
            ["positionSequence"] = 0L,
            ["bookmarks"] = new JsonArray(),
            ["bookmarksRevision"] = 0L,
        };

        private JsonObject ReadStateOrThrow(string bookId, out bool recoveredCorrupt)
        {
            var (obj, status) = ReadObjectSafe(StatePath(bookId));
            recoveredCorrupt = false;
            switch (status)
            {
                case ReadStatus.Missing: return NewState();
                case ReadStatus.Corrupt: recoveredCorrupt = true; return NewState();
                case ReadStatus.Unreadable:
                    throw new StoreException("STATE_UNREADABLE", "阅读状态暂时不可读，已停止写入以保护原文件");
                case ReadStatus.InvalidVersion:
                    throw new StoreException("STATE_VERSION_UNSUPPORTED", "阅读状态版本未知，拒绝覆盖");
                case ReadStatus.Ok:
                default:
                    return obj ?? NewState();
            }
        }

        private JsonObject ReadPreferencesOrThrow(out bool recoveredCorrupt)
        {
            var (obj, status) = ReadObjectSafe(PrefsFile);
            recoveredCorrupt = false;
            switch (status)
            {
                case ReadStatus.Missing: return new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject() };
                case ReadStatus.Corrupt: recoveredCorrupt = true; return new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject() };
                case ReadStatus.Unreadable:
                    throw new StoreException("PREFS_UNREADABLE", "阅读设置暂时不可读，已停止写入以保护原文件");
                case ReadStatus.InvalidVersion:
                    throw new StoreException("PREFS_VERSION_UNSUPPORTED", "阅读设置版本未知，拒绝覆盖");
                default:
                    return obj ?? new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject() };
            }
        }

        public JsonObject Load(string bookId, string sessionId)
        {
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                var prefs = LoadPreferencesInternal();
                var bookmarks = state["bookmarks"] as JsonArray ?? new JsonArray();
                return new JsonObject
                {
                    ["revision"] = Long(state, "revision"),
                    ["position"] = state["position"]?.DeepClone(),
                    ["positionToken"] = Long(state, "positionToken"),
                    ["bookmarksRevision"] = Long(state, "bookmarksRevision"),
                    ["bookmarkCount"] = bookmarks.Count,
                    ["interactionToken"] = Long(state, "interaction"),
                    ["preferences"] = prefs.DeepClone(),
                };
            }
        }

        private JsonObject LoadPreferencesInternal()
        {
            var prefsFile = ReadPreferencesOrThrow(out _);
            var prefs = prefsFile["preferences"] as JsonObject;
            return prefs != null ? (JsonObject)prefs.DeepClone() : new JsonObject();
        }

        public JsonObject GrantInteraction(string bookId, string sessionId, bool windowActive)
        {
            if (!windowActive) throw new StoreException("WINDOW_NOT_ACTIVE", "窗口未激活，不能取得阅读位置写入资格");
            if (string.IsNullOrEmpty(sessionId)) throw new StoreException("BAD_SESSION", "缺少会话");
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                long next = Long(state, "interaction") + 1;
                state["interaction"] = next;
                state["interactionOwner"] = sessionId;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["interactionToken"] = next };
            }
        }

        public JsonObject SavePosition(string bookId, string sessionId, JsonElement position, long interactionToken, long sequence)
        {
            ValidateBookId(bookId);
            if (position.ValueKind != JsonValueKind.Object) throw new StoreException("BAD_POSITION", "position 必须是对象");
            string positionJson = position.GetRawText();
            if (Encoding.UTF8.GetByteCount(positionJson) > MaxPositionBytes) throw new StoreException("POSITION_TOO_LARGE", "位置数据过大");
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                long currentToken = Long(state, "interaction");
                string owner = state["interactionOwner"]?.GetValue<string>() ?? "";
                if (interactionToken <= 0 || interactionToken != currentToken)
                    throw new StoreException("STALE_INTERACTION", "位置使用的交互资格不是当前有效资格");
                if (owner != sessionId)
                    throw new StoreException("TOKEN_OWNER_MISMATCH", "位置使用的交互资格不属于当前窗口");
                long lastSeq = Long(state, "positionSequence");
                if (sequence <= 0) throw new StoreException("BAD_SEQUENCE", "位置序号非法");
                if (sequence < lastSeq) throw new StoreException("STALE_SEQUENCE", "位置序号已过期，不能覆盖更新位置");
                if (sequence == lastSeq)
                {
                    // 同一序号只允许幂等重复；内容不同说明是旧任务，不能覆盖
                    string existing = state["position"]?.ToJsonString() ?? "";
                    if (existing == positionJson) return new JsonObject { ["revision"] = Long(state, "revision"), ["positionToken"] = interactionToken, ["idempotent"] = true };
                    throw new StoreException("DUPLICATE_SEQUENCE", "同一位置序号对应不同位置，拒绝覆盖");
                }
                JsonNode posNode;
                try { posNode = JsonNode.Parse(positionJson); }
                catch { throw new StoreException("BAD_POSITION", "位置 JSON 非法"); }
                state["position"] = posNode;
                state["positionToken"] = interactionToken;
                state["positionSequence"] = sequence;
                state["positionSession"] = sessionId ?? "";
                state["positionSavedAt"] = DateTime.UtcNow.ToString("O");
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = Long(state, "revision"), ["positionToken"] = interactionToken };
            }
        }

        private static JsonObject SanitizePreferences(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) throw new StoreException("BAD_PREFERENCES", "preferences 必须是对象");
            var outObj = new JsonObject();
            foreach (var prop in obj.EnumerateObject())
            {
                if (!PrefKeys.Contains(prop.Name)) continue;
                switch (prop.Name)
                {
                    case "fontSize":
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int fs))
                            outObj["fontSize"] = Math.Clamp(fs, 16, 36);
                        break;
                    case "lineHeight":
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double lh))
                            outObj["lineHeight"] = Math.Clamp(lh, 1.4, 2.4);
                        break;
                    case "contentWidth":
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int cw))
                            outObj["contentWidth"] = Math.Clamp(cw, 480, 1100);
                        break;
                    case "theme":
                    case "bg":
                    case "fg":
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            string s = prop.Value.GetString() ?? "";
                            if (s.Length <= 40) outObj[prop.Name] = s;
                        }
                        break;
                    case "selectable":
                    case "reduceMotion":
                    case "sidebar":
                        if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                            outObj[prop.Name] = prop.Value.GetBoolean();
                        break;
                }
            }
            return outObj;
        }

        public JsonObject SavePreferences(string sessionId, long sequence, JsonElement preferences)
        {
            var patch = SanitizePreferences(preferences);
            if (sequence <= 0) throw new StoreException("BAD_SEQUENCE", "偏好序号非法");
            lock (PrefsLock)
            {
                var existing = ReadPreferencesOrThrow(out _);
                var prefs = existing["preferences"] as JsonObject ?? new JsonObject();
                var seqs = existing["fieldSequences"] as JsonObject ?? new JsonObject();
                var applied = new JsonArray();
                foreach (var kv in patch)
                {
                    long previous = Long(seqs, kv.Key);
                    if (sequence <= previous) continue;   // 迟到的旧窗口不能覆盖新字段
                    prefs[kv.Key] = kv.Value?.DeepClone();
                    seqs[kv.Key] = sequence;
                    applied.Add(kv.Key);
                }
                long rev = Long(existing, "revision");
                if (applied.Count > 0)
                {
                    existing["version"] = StoreVersion;
                    existing["preferences"] = prefs;
                    existing["fieldSequences"] = seqs;
                    rev += 1;
                    existing["revision"] = rev;
                    existing["updatedAt"] = DateTime.UtcNow.ToString("O");
                    AtomicWrite(PrefsFile, existing);
                }
                return new JsonObject { ["revision"] = rev, ["applied"] = applied };
            }
        }

        public JsonObject ListBookmarks(string bookId, string cursor, int limit)
        {
            limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxBookmarkPage);
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                var list = state["bookmarks"] as JsonArray ?? new JsonArray();
                long revision = Long(state, "bookmarksRevision");
                int offset = 0;
                if (!string.IsNullOrEmpty(cursor))
                {
                    var m = Regex.Match(cursor, @"^(\d+):(\d+)$");
                    if (!m.Success) throw new StoreException("BAD_CURSOR", "书签游标非法");
                    long cursorRevision = long.Parse(m.Groups[1].Value);
                    if (cursorRevision != revision) throw new StoreException("CONFLICT", "书签列表已变化，请重新读取");
                    offset = Math.Clamp(int.Parse(m.Groups[2].Value), 0, list.Count);
                }
                var page = new JsonArray();
                int i = offset;
                while (i < list.Count && page.Count < limit)
                {
                    page.Add(list[i]?.DeepClone());
                    i++;
                    // 按完整列表响应的实际 UTF-8 字节做预算，避免 50×4KB 书签超过消息上限
                    var probe = new JsonObject
                    {
                        ["bookmarks"] = page.DeepClone(),
                        ["revision"] = revision,
                        ["total"] = list.Count,
                        ["nextCursor"] = i < list.Count ? revision + ":" + i : null
                    };
                    if (Encoding.UTF8.GetByteCount(probe.ToJsonString()) > MaxBookmarkPageBytes && page.Count > 1)
                    {
                        page.RemoveAt(page.Count - 1);
                        i--;
                        break;
                    }
                }
                string next = i < list.Count ? revision + ":" + i : null;
                return new JsonObject
                {
                    ["bookmarks"] = page,
                    ["nextCursor"] = next,
                    ["revision"] = revision,
                    ["total"] = list.Count,
                };
            }
        }

        public JsonObject AddBookmark(string bookId, string sessionId, JsonElement bookmark)
        {
            if (bookmark.ValueKind != JsonValueKind.Object) throw new StoreException("BAD_BOOKMARK", "bookmark 必须是对象");
            string raw = bookmark.GetRawText();
            if (Encoding.UTF8.GetByteCount(raw) > MaxBookmarkBytes) throw new StoreException("BOOKMARK_TOO_LARGE", "单条书签超过 4 KiB");
            var node = JsonNode.Parse(raw) as JsonObject ?? throw new StoreException("BAD_BOOKMARK", "bookmark JSON 非法");
            string id = node["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(id) || id.Length > 120) throw new StoreException("BAD_BOOKMARK", "书签 id 非法");
            string summary = node["summary"]?.GetValue<string>() ?? "";
            if (summary.Length > MaxSummaryUtf16) throw new StoreException("BOOKMARK_TOO_LARGE", "书签摘要超过 256 个字符");
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                var list = state["bookmarks"] as JsonArray ?? new JsonArray();
                if (list.Count >= MaxBookmarks) throw new StoreException("BOOKMARK_LIMIT", "每本书最多 500 条书签，旧书签已保留");
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i]?["id"]?.GetValue<string>() == id) throw new StoreException("DUPLICATE_BOOKMARK", "书签已存在");
                }
                list.Add(node);
                state["bookmarks"] = list;
                long rev = Long(state, "bookmarksRevision") + 1;
                state["bookmarksRevision"] = rev;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = rev, ["bookmark"] = node.DeepClone() };
            }
        }

        public JsonObject DeleteBookmark(string bookId, string sessionId, string bookmarkId)
        {
            if (string.IsNullOrWhiteSpace(bookmarkId) || bookmarkId.Length > 120) throw new StoreException("BAD_BOOKMARK", "书签 id 非法");
            lock (LockFor(bookId))
            {
                var state = ReadStateOrThrow(bookId, out _);
                var list = state["bookmarks"] as JsonArray ?? new JsonArray();
                int index = -1;
                for (int i = 0; i < list.Count; i++)
                    if (list[i]?["id"]?.GetValue<string>() == bookmarkId) { index = i; break; }
                if (index < 0) return new JsonObject { ["revision"] = Long(state, "bookmarksRevision"), ["deleted"] = false };
                list.RemoveAt(index);
                state["bookmarks"] = list;
                long rev = Long(state, "bookmarksRevision") + 1;
                state["bookmarksRevision"] = rev;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = rev, ["deleted"] = true };
            }
        }
    }
}
