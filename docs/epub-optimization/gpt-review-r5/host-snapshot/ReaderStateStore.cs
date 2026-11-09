using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const int MaxPreferenceGrants = 4096;
        private const int MaxPreferenceFields = 16;
        private const int MaxPreferenceCompletions = 2048;

        private static readonly object PrefsLock = new();
        private static readonly object PrefGrantLock = new();
        private static readonly ConcurrentDictionary<string, object> BookLocks = new();

        private sealed class PreferenceGrant
        {
            public string SessionId = "";
            public string OperationId = "";
            public string[] Fields = Array.Empty<string>();
            public long Order;
            public bool Completed;
            public DateTime CreatedAtUtc;
        }

        // R4：偏好授权是宿主签发的不透明字符串；顺序在宿主内部用安全小整数维护，绝不把 long/ticks 交给 JS Number。
        private static readonly ConcurrentDictionary<string, PreferenceGrant> PreferenceGrants = new();
        private static readonly ConcurrentQueue<string> PreferenceGrantQueue = new();
        private static readonly ConcurrentDictionary<string, string> PreferenceOperations = new();
        private static readonly ConcurrentDictionary<string, long> LatestPreferenceOrders = new();
        private static readonly ConcurrentDictionary<string, PreferenceCompletion> PreferenceCompletions = new();
        private static readonly ConcurrentQueue<string> PreferenceCompletionQueue = new();
        private static readonly ConcurrentDictionary<string, string> PreferenceCompletionOperations = new();
        private static long _globalPreferenceOrder;

        private sealed class PreferenceCompletion
        {
            public string SessionId = "";
            public string OperationId = "";
            public string[] Fields = Array.Empty<string>();
            public JsonObject Values = new();
            public long Order;
            public DateTime CompletedAtUtc;
        }
        private sealed class FieldCompletion
        {
            public string Token = "";
            public string Field = "";
            public JsonNode? Value;
            public long Order;
        }

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

        private static bool BackupCorrupt(string path)
        {
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true); return true; }
            catch { return false; }   // 备份失败时不能继续把坏文件当空状态覆盖
        }

        private static (JsonObject? obj, ReadStatus status) ReadObjectSafe(string path)
        {
            if (!File.Exists(path)) return (null, ReadStatus.Missing);
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) { return (null, ReadStatus.Unreadable); }
            catch (UnauthorizedAccessException) { return (null, ReadStatus.Unreadable); }
            catch { return (null, ReadStatus.Unreadable); }
            if (string.IsNullOrWhiteSpace(text)) return BackupCorrupt(path) ? (null, ReadStatus.Corrupt) : (null, ReadStatus.Unreadable);
            try
            {
                var node = JsonNode.Parse(text);
                if (node is not JsonObject obj) return BackupCorrupt(path) ? (null, ReadStatus.Corrupt) : (null, ReadStatus.Unreadable);
                if (obj["version"] is not JsonValue v || !v.TryGetValue<int>(out int version) || version != StoreVersion)
                {
                    // 未知版本不能静默清空，也不允许继续写入覆盖
                    return (null, ReadStatus.InvalidVersion);
                }
                return (obj, ReadStatus.Ok);
            }
            catch (JsonException)
            {
                return BackupCorrupt(path) ? (null, ReadStatus.Corrupt) : (null, ReadStatus.Unreadable);
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
            ["positionRevision"] = 0L,
            ["metadataRevision"] = 0L,
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
                case ReadStatus.Missing: return new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject(), ["preferenceOrders"] = new JsonObject() };
                case ReadStatus.Corrupt: recoveredCorrupt = true; return new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject(), ["preferenceOrders"] = new JsonObject() };
                case ReadStatus.Unreadable:
                    throw new StoreException("PREFS_UNREADABLE", "阅读设置暂时不可读，已停止写入以保护原文件");
                case ReadStatus.InvalidVersion:
                    throw new StoreException("PREFS_VERSION_UNSUPPORTED", "阅读设置版本未知，拒绝覆盖");
                default:
                    return obj ?? new JsonObject { ["version"] = StoreVersion, ["revision"] = 0L, ["preferences"] = new JsonObject(), ["fieldSequences"] = new JsonObject(), ["preferenceOrders"] = new JsonObject() };
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
                    ["positionRevision"] = Long(state, "positionRevision"),
                    ["metadataRevision"] = Long(state, "metadataRevision"),
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

        private static long ReadMaxPreferenceOrderLocked()
        {
            try
            {
                string prefsPath = Path.Combine(AppContext.BaseDirectory, "data", "reader", "epub", "preferences.json");
                if (!File.Exists(prefsPath)) return 0;
                var (obj, status) = ReadObjectSafe(prefsPath);
                if (status != ReadStatus.Ok || obj == null) return 0;
                long max = 0;
                if (obj["preferenceOrders"] is JsonObject orders)
                    foreach (var kv in orders) max = Math.Max(max, Long(orders, kv.Key));
                return max;
            }
            catch { return 0; }
        }

        private static void RemovePreferenceGrantLocked(string token)
        {
            if (!PreferenceGrants.TryRemove(token, out var grant)) return;
            PreferenceOperations.TryRemove(grant.SessionId + "|" + grant.OperationId, out _);
        }

        private static void PrunePreferenceGrantsLocked()
        {
            // 有序回收：只淘汰已完成或已过期的授权；仍在途的授权不能被随机淘汰。
            int guard = 0;
            while (PreferenceGrants.Count >= MaxPreferenceGrants && guard++ < MaxPreferenceGrants * 2)
            {
                if (!PreferenceGrantQueue.TryDequeue(out string? token) || token == null) break;
                if (!PreferenceGrants.TryGetValue(token, out var grant)) continue;
                bool expired = DateTime.UtcNow - grant.CreatedAtUtc > TimeSpan.FromMinutes(10);
                if (grant.Completed || expired) { RemovePreferenceGrantLocked(token); continue; }
                PreferenceGrantQueue.Enqueue(token);
            }
            if (PreferenceGrants.Count >= MaxPreferenceGrants)
                throw new StoreException("GRANT_LIMIT", "偏好授权数量达到上限，请稍后重试");
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
                // 位置 token 只由位置操作取得；偏好授权走 GrantPreferences，不修改这里。
                state["positionSequence"] = 0L;
                // 授权不是位置提交，不改变 positionRevision/metadataRevision/revision。
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["interactionToken"] = next };
            }
        }

        public JsonObject GrantPreferences(string sessionId, bool windowActive, string operationId, JsonElement fieldsElement)
        {
            if (!windowActive) throw new StoreException("WINDOW_NOT_ACTIVE", "窗口未激活，不能取得偏好写入授权");
            if (string.IsNullOrEmpty(sessionId)) throw new StoreException("BAD_SESSION", "缺少会话");
            if (string.IsNullOrEmpty(operationId) || operationId.Length > 128) throw new StoreException("BAD_OPERATION", "偏好操作身份非法");
            if (fieldsElement.ValueKind != JsonValueKind.Array) throw new StoreException("BAD_FIELDS", "偏好字段必须是数组");
            var fields = new List<string>();
            foreach (var f in fieldsElement.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.String) throw new StoreException("BAD_FIELDS", "偏好字段必须是字符串");
                string name = f.GetString() ?? "";
                if (!PrefKeys.Contains(name)) throw new StoreException("BAD_FIELDS", "未知偏好字段：" + name);
                if (!fields.Contains(name)) fields.Add(name);
            }
            if (fields.Count == 0 || fields.Count > MaxPreferenceFields) throw new StoreException("BAD_FIELDS", "偏好字段数量非法");
            lock (PrefGrantLock)
            {
                string opKey = sessionId + "|" + operationId;
                if (PreferenceOperations.TryGetValue(opKey, out string? oldToken) && PreferenceGrants.TryGetValue(oldToken, out var oldGrant))
                {
                    if (oldGrant.Fields.SequenceEqual(fields))
                        return new JsonObject { ["grant"] = oldToken, ["fields"] = new JsonArray(fields.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["idempotent"] = true };
                    throw new StoreException("DUPLICATE_OPERATION", "同一偏好操作身份对应不同字段");
                }
                // 活跃授权可能因容量回收；已完成的幂等凭据独立保留，同一 operation 仍返回同一 token。
                if (PreferenceCompletionOperations.TryGetValue(opKey, out string? completedToken) && PreferenceCompletions.TryGetValue(completedToken, out var completed))
                {
                    if (completed.Fields.SequenceEqual(fields))
                        return new JsonObject { ["grant"] = completedToken, ["fields"] = new JsonArray(fields.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["idempotent"] = true, ["completed"] = true };
                    throw new StoreException("DUPLICATE_OPERATION", "同一偏好操作身份对应不同字段");
                }
                PrunePreferenceGrantsLocked();
                PrunePreferenceCompletionsLocked();
                long maxStored = ReadMaxPreferenceOrderLocked();
                if (_globalPreferenceOrder < maxStored) _globalPreferenceOrder = maxStored;
                long order = ++_globalPreferenceOrder;
                string token = "pg-" + Guid.NewGuid().ToString("N");
                var grant = new PreferenceGrant
                {
                    SessionId = sessionId,
                    OperationId = operationId,
                    Fields = fields.ToArray(),
                    Order = order,
                    Completed = false,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                PreferenceGrants[token] = grant;
                PreferenceGrantQueue.Enqueue(token);
                PreferenceOperations[opKey] = token;
                foreach (string field in fields) LatestPreferenceOrders[field] = order;
                return new JsonObject
                {
                    ["grant"] = token,
                    ["fields"] = new JsonArray(fields.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                };
            }
        }

        private static void PrunePreferenceCompletionsLocked()
        {
            int guard = 0;
            while (PreferenceCompletions.Count >= MaxPreferenceCompletions && guard++ < MaxPreferenceCompletions * 2)
            {
                if (!PreferenceCompletionQueue.TryDequeue(out string? token) || token == null) break;
                if (PreferenceCompletions.TryRemove(token, out var comp))
                {
                    PreferenceCompletionOperations.TryRemove(comp.SessionId + "|" + comp.OperationId, out _);
                }
            }
        }

        private static void RecordPreferenceCompletionLocked(string token, string sessionId, string operationId, string[] fields, JsonObject values, long order)
        {
            var comp = new PreferenceCompletion
            {
                SessionId = sessionId,
                OperationId = operationId,
                Fields = fields.ToArray(),
                Values = (JsonObject)values.DeepClone(),
                Order = order,
                CompletedAtUtc = DateTime.UtcNow,
            };
            PreferenceCompletions[token] = comp;
            PreferenceCompletionQueue.Enqueue(token);
            PreferenceCompletionOperations[sessionId + "|" + operationId] = token;
            PrunePreferenceCompletionsLocked();
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
                    // 同一序号只允许语义幂等重复；不能比较不同编码层的原始字符串
                    JsonNode? existingNode = null, incomingNode = null;
                    try { existingNode = state["position"]?.DeepClone(); } catch { }
                    try { incomingNode = JsonNode.Parse(positionJson); } catch { }
                    bool same = existingNode != null && incomingNode != null && JsonNode.DeepEquals(existingNode, incomingNode);
                    if (same) return new JsonObject { ["revision"] = Long(state, "revision"), ["positionRevision"] = Long(state, "positionRevision"), ["positionToken"] = interactionToken, ["idempotent"] = true };
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
                long posRev = Long(state, "positionRevision") + 1;
                state["positionRevision"] = posRev;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = Long(state, "revision"), ["positionRevision"] = posRev, ["positionToken"] = interactionToken };
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

        private static bool JsonValueEqual(JsonNode? a, JsonNode? b)
        {
            try { return a != null && b != null && JsonNode.DeepEquals(a, b); }
            catch { return false; }
        }

        public JsonObject SavePreferences(string sessionId, JsonElement preferences, JsonElement grantsElement)
        {
            var patch = SanitizePreferences(preferences);
            if (patch.Count == 0) throw new StoreException("BAD_PREFERENCES", "偏好提交没有可应用字段");
            if (grantsElement.ValueKind != JsonValueKind.Object) throw new StoreException("MISSING_GRANT", "偏好提交缺少字段授权");
            lock (PrefsLock)
            {
                var existing = ReadPreferencesOrThrow(out _);
                var prefs = existing["preferences"] as JsonObject ?? new JsonObject();
                var orders = existing["preferenceOrders"] as JsonObject ?? new JsonObject();
                var applied = new JsonArray();
                var idempotentFields = new JsonArray();
                var superseded = new JsonArray();
                var completions = new List<FieldCompletion>();
                var activeTokens = new List<(string token, PreferenceGrant grant)>();
                long rev = Long(existing, "revision");
                bool changed = false;
                lock (PrefGrantLock)
                {
                    // 字段级结算：被更新操作取代的字段不阻止其它仍有效字段落盘。
                    // 只有缺失授予/属主/字段/过期等契约错误才整笔失败；写失败在写盘处抛错。
                    foreach (var kv in patch)
                    {
                        string token = "";
                        if (grantsElement.TryGetProperty(kv.Key, out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String)
                            token = tokenEl.GetString() ?? "";
                        if (string.IsNullOrEmpty(token))
                            throw new StoreException("MISSING_GRANT", "偏好字段没有有效授权：" + kv.Key);

                        PreferenceGrant? grant = null;
                        PreferenceCompletion? completion = null;
                        if (!PreferenceGrants.TryGetValue(token, out grant))
                        {
                            PreferenceCompletions.TryGetValue(token, out completion);
                            if (completion == null)
                                throw new StoreException("MISSING_GRANT", "偏好字段没有有效授权：" + kv.Key);
                        }

                        long order;
                        if (grant != null)
                        {
                            if (grant.SessionId != sessionId) throw new StoreException("GRANT_OWNER_MISMATCH", "偏好授权不属于当前窗口");
                            if (!grant.Fields.Contains(kv.Key)) throw new StoreException("FIELD_NOT_GRANTED", "偏好授权未包含字段：" + kv.Key);
                            if (DateTime.UtcNow - grant.CreatedAtUtc > TimeSpan.FromMinutes(10))
                                throw new StoreException("GRANT_EXPIRED", "偏好授权已过期，请重新修改：" + kv.Key);
                            order = grant.Order;
                        }
                        else
                        {
                            if (completion!.SessionId != sessionId) throw new StoreException("GRANT_OWNER_MISMATCH", "偏好完成凭据不属于当前窗口");
                            if (!completion.Fields.Contains(kv.Key)) throw new StoreException("FIELD_NOT_GRANTED", "偏好完成凭据未包含字段：" + kv.Key);
                            if (DateTime.UtcNow - completion.CompletedAtUtc > TimeSpan.FromMinutes(10))
                                throw new StoreException("GRANT_EXPIRED", "偏好完成凭据已过期，请重新修改：" + kv.Key);
                            order = completion.Order;
                        }

                        long latest = 0;
                        LatestPreferenceOrders.TryGetValue(kv.Key, out latest);
                        long storedOrder = Long(orders, kv.Key);
                        if ((latest > 0 && order < latest) || storedOrder > order)
                        {
                            // 该字段已有更新操作，明确回传 superseded；不再重发必失败组合。
                            superseded.Add(kv.Key);
                            continue;
                        }

                        JsonNode? incoming = null;
                        try { incoming = JsonNode.Parse(kv.Value?.ToJsonString() ?? "null"); } catch { }
                        if (storedOrder == order)
                        {
                            if (JsonValueEqual(prefs[kv.Key], incoming))
                            {
                                applied.Add(kv.Key);
                                idempotentFields.Add(kv.Key);
                                completions.Add(new FieldCompletion { Token = token, Field = kv.Key, Value = incoming, Order = order });
                                if (grant != null) activeTokens.Add((token, grant));
                                continue;
                            }
                            // 同字段同序号但值不同：说明该字段已被其它值占用，不能覆盖；旧操作视为被取代。
                            superseded.Add(kv.Key);
                            continue;
                        }

                        // 新操作：只更新内存副本；全部字段结算后统一写。
                        prefs[kv.Key] = kv.Value?.DeepClone();
                        orders[kv.Key] = order;
                        applied.Add(kv.Key);
                        completions.Add(new FieldCompletion { Token = token, Field = kv.Key, Value = incoming, Order = order });
                        if (grant != null) activeTokens.Add((token, grant));
                        changed = true;
                    }

                    if (changed)
                    {
                        rev += 1;
                        existing["version"] = StoreVersion;
                        existing["preferences"] = prefs;
                        // 保留旧 fieldSequences 作为 R2/R3 数据迁移痕迹；R5 使用安全的 preferenceOrders
                        existing["preferenceOrders"] = orders;
                        existing["revision"] = rev;
                        existing["updatedAt"] = DateTime.UtcNow.ToString("O");
                        AtomicWrite(PrefsFile, existing);
                    }

                    // 真实写盘成功后才完成活跃授权并写幂等完成凭据；写失败不会清客户端 pending。
                    foreach (var (token, grant) in activeTokens)
                    {
                        grant.Completed = true;
                        var fields = grant.Fields.Where(f => completions.Any(c => c.Token == token && c.Field == f)).ToArray();
                        var values = new JsonObject();
                        foreach (var c in completions.Where(c => c.Token == token)) values[c.Field] = c.Value?.DeepClone();
                        if (fields.Length > 0)
                            RecordPreferenceCompletionLocked(token, grant.SessionId, grant.OperationId, fields, values, grant.Order);
                    }
                    // 完成凭据（活跃授权已被回收时）也要保证再次保存可幂等返回。
                    foreach (var c in completions)
                    {
                        if (PreferenceCompletions.ContainsKey(c.Token)) continue;
                        var single = new JsonObject { [c.Field] = c.Value?.DeepClone() };
                        RecordPreferenceCompletionLocked(c.Token, sessionId, "", new[] { c.Field }, single, c.Order);
                    }
                }
                return new JsonObject
                {
                    ["revision"] = rev,
                    ["applied"] = applied,
                    ["idempotent"] = idempotentFields,
                    ["superseded"] = superseded,
                };
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
                state["metadataRevision"] = Long(state, "metadataRevision") + 1;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = rev, ["metadataRevision"] = Long(state, "metadataRevision"), ["bookmark"] = node.DeepClone() };
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
                state["metadataRevision"] = Long(state, "metadataRevision") + 1;
                state["revision"] = Long(state, "revision") + 1;
                AtomicWrite(StatePath(bookId), state);
                return new JsonObject { ["revision"] = rev, ["metadataRevision"] = Long(state, "metadataRevision"), ["deleted"] = true };
            }
        }
    }
}
