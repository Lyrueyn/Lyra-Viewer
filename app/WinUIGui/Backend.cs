using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WinUIGui;

// 复用旧播放器的 Python 后端（B站下载/弹幕/评论/视频库），跑在 http://127.0.0.1:8765。
// 启动：backend/.venv/Scripts/python.exe backend/run_backend.py --port 8765。
public static class Backend
{
    // ★ 端口不再写死：若 8765 已被别的实例/僵尸后端占用，自动换下一个空闲端口，
    //   避免"两个播放器同时开 → 连到对方后端 → 看到别人的视频库"这种串数据问题。
    public static string BaseUrl { get; private set; } = "http://127.0.0.1:8765";
    private static HttpClient? _http, _longHttp;
    private static HttpClient Http => _http ??= new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    // 全量弹幕刷新要遍历历史每一天，可能跑几分钟 → 单独用长超时客户端，避免默认 30s 就断开
    private static HttpClient LongHttp => _longHttp ??= new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(15) };
    private static Process? _proc;
    private static int _port = 8765;

    /// <summary>端口是否已被占用（能连上就认为被占）</summary>
    private static bool IsPortBusy(int port)
    {
        try
        {
            using var t = new System.Net.Sockets.TcpClient();
            var ar = t.BeginConnect("127.0.0.1", port, null, null);
            bool ok = ar.AsyncWaitHandle.WaitOne(250);
            if (!ok) return false;
            t.EndConnect(ar);
            return true;
        }
        catch { return false; }
    }

    // 找项目根（含 backend/run_backend.py 的目录）
    private static string FindRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "backend", "run_backend.py"))) return dir;
            var up = Directory.GetParent(dir);
            if (up == null) break;
            dir = up.FullName;
        }
        return Environment.CurrentDirectory;
    }

    public static bool IsRunning => _proc != null && !_proc.HasExited;

    // 启动后端（若已在跑则不重复启）
    public static string Start()
    {
        string root = FindRoot();
        string backendDir = Path.Combine(root, "backend");
        // ★ 安装版（分发给别人）：优先用自带的 embeddable python；开发版回退 .venv；最后回退系统 python
        string py = Path.Combine(backendDir, "python", "python.exe");
        if (!File.Exists(py)) py = Path.Combine(backendDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(py)) py = "python";
        string script = Path.Combine(backendDir, "run_backend.py");
        if (!File.Exists(script)) return "后端脚本不存在: " + script;

        if (IsRunning) return "ok";

        // ★ 选端口：8765 被占就往后找一个空闲的
        int port = 8765;
        for (int k = 0; k < 20; k++)
        {
            if (!IsPortBusy(8765 + k)) { port = 8765 + k; break; }
        }
        _port = port;
        BaseUrl = "http://127.0.0.1:" + port;
        _http = null; _longHttp = null;   // 让 HttpClient 用新端口重建
        if (port != 8765) System.Diagnostics.Debug.WriteLine("[Backend] 8765 被占用，改用端口 " + port);

        var psi = new ProcessStartInfo(py, $"\"{script}\" --port {port}")
        {
            WorkingDirectory = backendDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception e)
        {
            return "启动后端失败: " + e.Message;
        }
        return "ok(" + _proc!.Id + ")";
    }

    public static void Stop()
    {
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        _proc = null;
    }

    // --- HTTP ---

    // ★ 倍速（保持音高）读写
    public static float LoadSpeed() { try { var o = ReadSettingsObj(); return (o != null && o.TryGetPropertyValue("speed", out var v) && v != null) ? (float)v.GetValue<double>() : 1.0f; } catch { return 1.0f; } }
    public static void SaveSpeed(float s) { try { var o = ReadSettingsObj() ?? new System.Text.Json.Nodes.JsonObject(); o["speed"] = Math.Round(s, 3); WriteSettingsObj(o); } catch { } }

    // ★ 字幕开关（player_settings.json 的 subtitle_on，默认 true）
    public static bool LoadSubtitleOn()
    {
        try { var o = ReadSettingsObj(); var v = o["subtitle_on"]; return !(v is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue(out bool b) && !b); }
        catch { return true; }
    }
    public static void SaveSubtitleOn(bool on) { try { var o = ReadSettingsObj(); o["subtitle_on"] = on; WriteSettingsObj(o); } catch { } }
    // ★ 独占模式（A2-12）：data/player_settings.json 的 "exclusive" 布尔键，默认 false=共享。
    //   注意：真正的"意图"存在引擎里（DshEngine_SetExclusive/GetExclusive），这里只负责"下次开播放器还记得"。
    public static bool LoadExclusive()
    {
        try { var v = ReadSettingsObj()["exclusive"]; return v is JsonValue jv && jv.TryGetValue(out bool b) && b; }
        catch { return false; }
    }

    public static void SaveExclusive(bool on)
    {
        try { var o = ReadSettingsObj(); o["exclusive"] = on; WriteSettingsObj(o); } catch { }
    }

    public static async Task<bool> HealthAsync()
    {
        try { var r = await Http.GetAsync("/health"); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    public static async Task<string> ParseAsync(string url)
    {
        var r = await Http.GetAsync("/api/bilibili/parse?url=" + Uri.EscapeDataString(url));
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task<string> SubmitDownloadAsync(string url, int qn = 120, int page = 0, bool hires = true, string formatSel = "bv*+ba/b")
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { url, qn, page, hi_res = hires, format_sel = (string.IsNullOrWhiteSpace(formatSel) ? "bv*+ba/b" : formatSel) }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/download/submit", payload);
        return await r.Content.ReadAsStringAsync();
    }

    // ★ 下载 YouTube 播放列表里"勾选的条目"（每一条可带自己的画质 format_sel）
    public static async Task<string> SubmitPlaylistItemsAsync(string itemsJson, string playlistName)
    {
        System.Text.Json.JsonElement items;
        try { items = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(string.IsNullOrWhiteSpace(itemsJson) ? "[]" : itemsJson); }
        catch { items = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("[]"); }
        using var payload = new StringContent(
            JsonSerializer.Serialize(new { items, playlist_name = playlistName ?? "" }),
            System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/download/playlist-items", payload);
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task<string> SubmitCollectionAsync(string url, int qn = 120, bool hires = true, string formatSel = "bv*+ba/b")
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { url, qn, hi_res = hires, format_sel = (string.IsNullOrWhiteSpace(formatSel) ? "bv*+ba/b" : formatSel) }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/download/collection", payload);
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task<string> CookieStatusAsync()
        => await Http.GetStringAsync("/api/config/cookie");

    // ================= ★ 增强管线（A2）：后端全都做好了，界面只读这几个接口 =================
    // 说明：这些接口出错时**响应体里就是后端的真实原因**，必须原样带给用户（不能吞）。
    //   所以统一走 SendAsync：非 2xx 不抛异常，直接返回 "后端错误(4xx): {body}"，界面照原样显示。
    private static async Task<string> SendAsync(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var r = await Http.SendAsync(req);
            string text = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) return $"后端错误({(int)r.StatusCode}): {text}";
            return text;
        }
        catch (Exception ex) { return "请求失败: " + ex.Message; }
    }

    public static Task<string> EnhancersAsync() => SendAsync(HttpMethod.Get, "/api/enhancers");
    // ★ AI 增强依赖（PyTorch 等不随安装包提供：按本机显卡在线安装）
    public static async Task<string> DepsStatusAsync()
    {
        try { var r = await Http.GetAsync("/api/enhance/deps"); return await r.Content.ReadAsStringAsync(); }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "") + "\"}"; }
    }
    public static async Task<string> DepsCancelAsync()
    {
        try
        {
            using var payload = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var r = await Http.PostAsync("/api/enhance/deps/cancel", payload);
            return await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { return "取消失败: " + ex.Message; }
    }
    public static async Task<string> DepsInstallAsync()
    {
        try
        {
            using var payload = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var r = await Http.PostAsync("/api/enhance/deps/install", payload);
            return await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { return "启动失败: " + ex.Message; }
    }
    // ---------- ★ AI 字幕（Whisper）：依赖 / 模型 / 转写 ----------
    private static async Task<string> SendLongAsync(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var r = await LongHttp.SendAsync(req);
            string text = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) return $"后端错误({(int)r.StatusCode}): {text}";
            return text;
        }
        catch (Exception ex) { return "请求失败: " + ex.Message; }
    }

    public static Task<string> AiSubStatusAsync() => SendAsync(HttpMethod.Get, "/api/ai_subtitle/status");
    public static Task<string> AiSubDepsInstallAsync() => SendAsync(HttpMethod.Post, "/api/ai_subtitle/deps/install", new { });
    public static Task<string> AiSubDepsCancelAsync() => SendAsync(HttpMethod.Post, "/api/ai_subtitle/deps/cancel", new { });
    public static Task<string> AiSubDownloadAsync(string model) => SendAsync(HttpMethod.Post, "/api/ai_subtitle/download", new { model });
    public static Task<string> AiSubDownloadCancelAsync() => SendAsync(HttpMethod.Post, "/api/ai_subtitle/download/cancel", new { });
    public static Task<string> AiSubSelectAsync(string model) => SendAsync(HttpMethod.Post, "/api/ai_subtitle/select", new { model });
    public static Task<string> AiSubDeleteAsync(string model) => SendAsync(HttpMethod.Post, "/api/ai_subtitle/delete", new { model });
    public static Task<string> AiSubGenerateAsync(string path) => SendAsync(HttpMethod.Post, "/api/ai_subtitle/generate", new { path });
    public static Task<string> AiSubGenerateCancelAsync() => SendAsync(HttpMethod.Post, "/api/ai_subtitle/generate/cancel", new { });

    // 重新下载字幕（简介面板按钮；B站/YouTube 走已有补字幕逻辑，耗时可能几分钟 → 用长超时）
    public static Task<string> ReDownloadSubtitleAsync(int videoId)
        => SendLongAsync(HttpMethod.Post, $"/api/videos/{videoId}/subtitle/redownload", new { });

    // ---- ★ 本地视频：打开/添加/编辑简介 ----
    public static async Task<string> ImportLocalVideoAsync(string path, string title = "")
    {
        try
        {
            using var payload = new StringContent(
                JsonSerializer.Serialize(new { path, title }),
                System.Text.Encoding.UTF8, "application/json");
            var r = await LongHttp.PostAsync("/api/videos/import", payload);
            return await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { return "添加失败: " + ex.Message; }
    }

    public static Task<string> UpdateVideoDescAsync(int videoId, string desc)
        => SendAsync(HttpMethod.Post, $"/api/videos/{videoId}/desc", new { desc });

    public static Task<string> PathsAsync() => SendAsync(HttpMethod.Get, "/api/paths");
    // ★ YouTube 登录 Cookie（登录窗口抓来的整串；解决 "Sign in to confirm you're not a bot" 风控）
    public static async Task<string> YtCookieStatusAsync()
    {
        var r = await Http.GetAsync("/api/config/youtube-cookie");
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task<string> SaveYtCookieAsync(string cookiePayload)
    {
        // cookiePayload：登录窗口给的 JSON（{cookies:[...]}）；空字符串 = 清除
        using var payload = new StringContent(JsonSerializer.Serialize(new { cookie = cookiePayload }),
                                              System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/config/youtube-cookie", payload);
        return await r.Content.ReadAsStringAsync();
    }
    // ★ 当前视频的详细信息（ffprobe）：文件大小/容器/时长/编码/分辨率/帧率/采样率/声道…
    public static Task<string> MediaInfoAsync(string path) => SendAsync(HttpMethod.Get, "/api/media/info?path=" + Uri.EscapeDataString(path ?? ""));
    public static Task<string> EnhanceTasksAsync() => SendAsync(HttpMethod.Get, "/api/enhance/tasks");
    public static Task<string> EnhanceAllAsync() => SendAsync(HttpMethod.Get, "/api/enhance/all");
    public static Task<string> EnhanceVersionsAsync(int mediaId, string mediaType)
        => SendAsync(HttpMethod.Get, $"/api/enhance/versions?media_id={mediaId}&media_type={Uri.EscapeDataString(mediaType)}");
    public static Task<string> SubmitEnhanceAsync(int mediaId, string mediaType, string enhancer, string device, string sourcePath)
        => SendAsync(HttpMethod.Post, "/api/enhance/submit",
            new { media_id = mediaId, media_type = mediaType, enhancer, device, source_path = sourcePath ?? "" });
    public static Task<string> CancelEnhanceAsync(int id) => SendAsync(HttpMethod.Post, $"/api/enhance/tasks/{id}/cancel");
    public static Task<string> DeleteEnhanceAsync(int id) => SendAsync(HttpMethod.Delete, $"/api/enhance/tasks/{id}");

    // ★ 增强链（P2 / T28）：一次提交多个节点（submit_chain）+ 查询某条链的祖先（chain/{id}）。
    //   ★ 后端可能还没实现（T27 并行中）→ SendAsync 会把 404/500 的 body 原样返回，界面显示"接口未就绪"而不是崩。
    // ★ 节点图：每个节点可带参数（params），提交给后端 /api/enhance/submit_chain 的 nodes 字段
    public static Task<string> SubmitEnhanceChainNodesAsync(int mediaId, string mediaType, string sourcePath, IEnumerable<object> nodes, string device)
        => SendAsync(HttpMethod.Post, "/api/enhance/submit_chain", new
        {
            media_id = mediaId,
            media_type = mediaType,
            source_path = sourcePath ?? "",
            nodes = nodes.ToArray(),
            device,
        });
    public static Task<string> SubmitEnhanceChainAsync(int mediaId, string mediaType, string sourcePath, IEnumerable<string> enhancers, string device)
        => SendAsync(HttpMethod.Post, "/api/enhance/submit_chain", new
        {
            media_id = mediaId,
            media_type = mediaType,
            source_path = sourcePath ?? "",
            enhancers = enhancers.ToArray(),
            device,
        });
    public static Task<string> EnhanceChainAsync(int taskId) => SendAsync(HttpMethod.Get, $"/api/enhance/chain/{taskId}");
    // ★ 播放增强版时：查这个文件是哪个增强产物 + 它的源视频信息（标题/作者/简介/发布日期）
    public static Task<string> EnhanceProductInfoAsync(string path)
        => SendAsync(HttpMethod.Get, "/api/enhance/product_info?path=" + Uri.EscapeDataString(path ?? ""));

    public static async Task<string> SaveCookieAsync(string cookie)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { cookie }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/config/cookie", payload);
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task<string> ImportCookieAsync()
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/api/config/cookie/import", payload);
        return await r.Content.ReadAsStringAsync();
    }

    public static async Task ClearTasksAsync()
    {
        var r = await Http.DeleteAsync("/api/download/tasks/clear");
        r.EnsureSuccessStatusCode();
    }

    public static async Task CancelTaskAsync(int id)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync($"/api/download/tasks/{id}/cancel", payload);
        r.EnsureSuccessStatusCode();
    }

    public static async Task<JsonElement> TasksAsync()
    {
        var r = await Http.GetAsync("/api/download/tasks");
        if (!r.IsSuccessStatusCode) return default;
        var s = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(s).RootElement;
    }

    public static async Task<JsonElement> VideosAsync()
    {
        var r = await Http.GetAsync("/api/videos");
        if (!r.IsSuccessStatusCode) return default;
        var s = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(s).RootElement;
    }

    // 通用 GET（返回 JSON 元素）
    public static async Task<T> GetAsync<T>(string path) => await Http.GetFromJsonAsync<T>(path);
    // ★ 长超时 GET：YouTube 解析首次可能要 30 秒以上（走代理/多轮网页请求）
    public static async Task<T> GetLongAsync<T>(string path) => await LongHttp.GetFromJsonAsync<T>(path);

    // 通用 POST（发送 JSON body）
    // ★ 必须用显式 StringContent：PostAsJsonAsync 产生的 JsonContent 不带 Content-Length（走 chunked），
    //   而后端的 _read_json 是按 Content-Length 读的 → body 读成空 → 字段全变默认值
    //   （实测：加入合集时 video_id 变成 0，报"video_id 无效"）。
    public static async Task PostAsync(string path, object body)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync(path, payload);
        r.EnsureSuccessStatusCode();
    }

    // 通用 POST 并读回 JSON（新建合集要拿返回的 playlist_id 之类）
    public static async Task<JsonElement> PostJsonAsync(string path, object body)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var r = await Http.PostAsync(path, payload);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    // 刷新某视频的弹幕+评论（后端 /api/videos/{id}/refresh）：按天抓历史弹幕，可能几分钟；
    // 返回后端 JSON（含 danmaku_added / danmaku_total / comments_fetching）。
    public static async Task<string> RefreshVideoAsync(int id)
    {
        using var r = await LongHttp.PostAsJsonAsync($"/api/videos/{id}/refresh", new { });
        string s = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"refresh video {id} 失败: {(int)r.StatusCode} {s}");
        return s;
    }

    // ---- 播放器本地设置（data/player_settings.json）----
    // 只存与后端无关的界面/播放器偏好：HDR 输出方式、弹幕设置（不透明度/显示区域/同屏上限）。
    // ★ 一律"读-改-写"整份 JSON 合并，不能像以前那样整文件覆盖（否则存 HDR 会把弹幕设置清掉）。
    private static string SettingsFile() => Path.Combine(FindRoot(), "data", "player_settings.json");

    private static JsonObject ReadSettingsObj()
    {
        try
        {
            string p = SettingsFile();
            if (!File.Exists(p)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(p))?.AsObject() ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    private static void WriteSettingsObj(JsonObject o)
    {
        try
        {
            string p = SettingsFile();
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool LoadHdrPassthrough()
    {
        var v = ReadSettingsObj()["hdr_passthrough"];
        return v is JsonValue jv && jv.TryGetValue(out bool b) && b;
    }

    public static void SaveHdrPassthrough(bool on)
    {
        var o = ReadSettingsObj();
        o["hdr_passthrough"] = on;
        WriteSettingsObj(o);
    }

    // ★ AI 字幕开关记忆：player_settings.json 的 "ai_subtitle"
    public static bool LoadAiSubtitle()
    {
        try
        {
            var v = ReadSettingsObj()["ai_subtitle"];
            return v is JsonValue jv && jv.TryGetValue(out bool b) && b;
        }
        catch { return false; }
    }

    public static void SaveAiSubtitle(bool on)
    {
        try { var o = ReadSettingsObj(); o["ai_subtitle"] = on; WriteSettingsObj(o); } catch { }
    }

    // ★ NVIDIA RTX VSR（视频超分开关）记忆：player_settings.json 的 "rtx_vsr"
    public static bool LoadRtxVsr()
    {
        try
        {
            var v = ReadSettingsObj()["rtx_vsr"];
            return v is JsonValue jv && jv.TryGetValue(out bool b) && b;
        }
        catch { return false; }
    }

    public static void SaveRtxVsr(bool on)
    {
        try { var o = ReadSettingsObj(); o["rtx_vsr"] = on; WriteSettingsObj(o); } catch { }
    }

    // ★ RTX VSR 挡位（1~4，默认 2）：player_settings.json 的 "rtx_vsr_quality"
    public static int LoadRtxVsrQuality()
    {
        try
        {
            var v = ReadSettingsObj()["rtx_vsr_quality"];
            if (v is JsonValue jv && jv.TryGetValue(out int q)) return Math.Clamp(q, 1, 4);
        }
        catch { }
        return 2;
    }

    public static void SaveRtxVsrQuality(int q)
    {
        try { var o = ReadSettingsObj(); o["rtx_vsr_quality"] = Math.Clamp(q, 1, 4); WriteSettingsObj(o); } catch { }
    }

    // ★ RTX Video SDK 目录（可选；不随安装包分发 SDK，用户自己选择 SDK 根目录）
    public static string LoadRtxSdkPath()
    {
        try
        {
            var v = ReadSettingsObj()["rtx_sdk_path"];
            if (v is JsonValue jv && jv.TryGetValue(out string? path)) return path ?? "";
        }
        catch { }
        return "";
    }

    public static void SaveRtxSdkPath(string path)
    {
        try { var o = ReadSettingsObj(); o["rtx_sdk_path"] = path ?? ""; WriteSettingsObj(o); } catch { }
    }

    // ★ 窗口置顶（Pin）记忆：player_settings.json 的 "always_on_top"
    public static bool LoadAlwaysOnTop()
    {
        try { var v = ReadSettingsObj()["always_on_top"]; return v is JsonValue jv && jv.TryGetValue(out bool b) && b; }
        catch { return false; }
    }

    public static void SaveAlwaysOnTop(bool on)
    {
        try { var o = ReadSettingsObj(); o["always_on_top"] = on; WriteSettingsObj(o); } catch { }
    }

    // ---- 弹幕设置（设置 tab）----
    // opacity 0~1；scrollAreaPct = 滚动弹幕只占视频最上 X%（100=全屏；固定弹幕不受影响）；maxActive = 同屏最多存在条数（<=0 不限）
    public static (double opacity, double scrollAreaPct, int maxActive, double fontPct, double speedPct, string blockWords) LoadDanmakuOptions()
    {
        var o = ReadSettingsObj();
        double op = 1.0, ar = 100.0; int mx = 0; double fp = 100.0, sp = 100.0; string bw = "";
        if (o["dm_opacity"] is JsonValue v1 && v1.TryGetValue(out double d1)) op = d1;
        if (o["dm_scroll_area_pct"] is JsonValue v2 && v2.TryGetValue(out double d2)) ar = d2;
        if (o["dm_max_active"] is JsonValue v3 && v3.TryGetValue(out int i3)) mx = i3;
        if (o["dm_font_pct"] is JsonValue v4 && v4.TryGetValue(out double d4)) fp = d4;
        if (o["dm_speed_pct"] is JsonValue v5 && v5.TryGetValue(out double d5)) sp = d5;
        if (o["dm_block_words"] is JsonValue v6 && v6.TryGetValue(out string? s6)) bw = s6 ?? "";
        if (op < 0) op = 0; if (op > 1) op = 1;
        if (ar < 5) ar = 5; if (ar > 100) ar = 100;
        return (op, ar, mx, fp, sp, bw);
    }

    public static void SaveDanmakuOptions(double opacity, double scrollAreaPct, int maxActive, double fontPct, double speedPct, string blockWords)
    {
        var o = ReadSettingsObj();
        o["dm_opacity"] = Math.Round(opacity, 3);
        o["dm_scroll_area_pct"] = Math.Round(scrollAreaPct, 1);
        o["dm_max_active"] = maxActive;
        o["dm_font_pct"] = Math.Round(fontPct, 1);
        o["dm_speed_pct"] = Math.Round(speedPct, 1);
        o["dm_block_words"] = blockWords ?? "";
        WriteSettingsObj(o);
    }

    // ★ 音量 / 静音（player_settings.json：volume 0~1、muted）
    public static (double volume, bool muted) LoadVolume()
    {
        var o = ReadSettingsObj();
        double v = 1.0; bool m = false;
        if (o["volume"] is JsonValue vv && vv.TryGetValue(out double dv)) v = dv;
        if (o["muted"] is JsonValue mv && mv.TryGetValue(out bool bv)) m = bv;
        if (v < 0) v = 0; if (v > 1) v = 1;
        return (v, m);
    }

    public static void SaveVolume(double volume, bool muted)
    {
        var o = ReadSettingsObj();
        o["volume"] = Math.Round(volume, 3);
        o["muted"] = muted;
        WriteSettingsObj(o);
    }

    // ★ 循环设置（player_settings.json：loop_one / loop_list）
    public static (bool loopOne, bool loopList) LoadLoopOptions()
    {
        var o = ReadSettingsObj();
        bool a = false, b2 = false;
        if (o["loop_one"] is JsonValue v1 && v1.TryGetValue(out bool b1)) a = b1;
        if (o["loop_list"] is JsonValue v2 && v2.TryGetValue(out bool b2v)) b2 = b2v;
        return (a, b2);
    }

    public static void SaveLoopOptions(bool loopOne, bool loopList)
    {
        var o = ReadSettingsObj();
        o["loop_one"] = loopOne;
        o["loop_list"] = loopList;
        WriteSettingsObj(o);
    }

    // 删除视频（本地文件 + 记录；delete_file 可选）
    public static async Task DeleteVideoAsync(int id, bool deleteFile = true)
    {
        var r = await Http.DeleteAsync($"/api/videos/{id}");
        r.EnsureSuccessStatusCode();
    }

    // 删除某个 BV 的某个分P（含它的所有画质版本 + 文件）—— 面向"一个卡片 = 一个视频"
    public static async Task DeleteVideoByBvidAsync(string bvid, int page = -1)
    {
        string url = $"/api/videos/by-bvid/{Uri.EscapeDataString(bvid)}";
        if (page >= 0) url += $"?page={page}";
        var r = await Http.DeleteAsync(url);
        r.EnsureSuccessStatusCode();
    }

    // 删除合集
    public static async Task DeletePlaylistAsync(int id)
    {
        var r = await Http.DeleteAsync($"/api/playlists/{id}");
        r.EnsureSuccessStatusCode();
    }

    // 从合集移除单个视频
    public static async Task RemovePlaylistItemAsync(int playlistId, int videoId)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { video_id = videoId }), System.Text.Encoding.UTF8, "application/json");
        var r = await Http.PostAsync($"/api/playlists/{playlistId}/items/remove", payload);
        r.EnsureSuccessStatusCode();
    }
}
