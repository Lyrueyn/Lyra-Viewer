using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinUIGui
{
    /// <summary>
    /// EPUB 阅读器宿主协议常量与消息验证。通信消息是带 requestId/sessionId 的 JSON 对象；
    /// 旧扩展的 lyra-book-ready 字符串由扩展宿主单独兼容。
    /// </summary>
    public static class ReaderProtocol
    {
        public const string Protocol = "lyra-reader";
        public const int Version = 3;
        public const int MaxMessageBytes = 256 * 1024;

        public static readonly string[] AllowedTypes =
        {
            "hello", "app.ready", "book.begin", "book.ready", "book.error",
            "state.load", "state.savePosition", "state.savePreferences", "state.interact",
            "state.grantPreferences",
            "bookmark.list", "bookmark.add", "bookmark.delete",
        };

        public static bool IsAllowedType(string type) => Array.IndexOf(AllowedTypes, type) >= 0;

        public static JsonObject Capabilities() => new()
        {
            ["persistentState"] = true,
            ["preferences"] = true,
            ["bookmarks"] = true,
            ["bookIdentity"] = "sha256",
            ["atomicWrites"] = true,
            ["interactionTokens"] = true,
            ["preferenceGrants"] = true,
            ["positionOperations"] = true,
            ["finalizeAck"] = true,
            ["fieldResults"] = true,
            ["positionSubmitRevision"] = true,
            ["frozenFinalize"] = true,
            ["protocolVersion"] = 3,
            ["bookmarkPageSize"] = 50,
        };

        public static JsonObject UnavailableCapabilities() => new()
        {
            ["persistentState"] = false,
            ["preferences"] = false,
            ["bookmarks"] = false,
            ["bookIdentity"] = "sha256",
            ["atomicWrites"] = false,
            ["interactionTokens"] = false,
            ["preferenceGrants"] = false,
            ["positionOperations"] = false,
            ["finalizeAck"] = false,
            ["fieldResults"] = false,
            ["positionSubmitRevision"] = false,
            ["frozenFinalize"] = false,
            ["protocolVersion"] = 3,
            ["bookmarkPageSize"] = 50,
        };

        public static JsonObject ResponseVersion(int version, string requestId, string sessionId, bool ok, JsonNode? result, string? errorCode, string? errorMessage)
        {
            var obj = new JsonObject
            {
                ["protocol"] = Protocol,
                ["version"] = version,
                ["type"] = "response",
                ["requestId"] = requestId ?? "",
                ["sessionId"] = sessionId ?? "",
                ["ok"] = ok,
            };
            if (ok) obj["result"] = result ?? new JsonObject();
            else obj["error"] = new JsonObject { ["code"] = errorCode ?? "HOST_ERROR", ["message"] = errorMessage ?? "宿主错误" };
            return obj;
        }

        public static JsonObject Response(string requestId, string sessionId, bool ok, JsonNode? result, string? errorCode, string? errorMessage)
            => ResponseVersion(Version, requestId, sessionId, ok, result, errorCode, errorMessage);

        public static string Serialize(JsonObject obj) => obj.ToJsonString(new JsonSerializerOptions
        {
            // 保持紧凑，且忽略 null 可选字段
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        /// <summary>严格取字符串字段；长度受限防止恶意超大字段。</summary>
        public static string GetString(JsonElement obj, string name, int maxLen = 4096)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
            string s = v.GetString() ?? "";
            return s.Length > maxLen ? s.Substring(0, maxLen) : s;
        }

        public static JsonElement GetObject(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        }

        public static bool TryParse(string raw, out JsonDocument doc)
        {
            doc = null;
            if (string.IsNullOrEmpty(raw)) return false;
            if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxMessageBytes) return false;
            try
            {
                doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
                return doc.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch { return false; }
        }
    }
}
