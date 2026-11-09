# 01 · 切换画质：概率性「未响应」+ 闪现开头帧

- 工作区 `F:\DSH\LocalMediaPlayer-Workspace`，分支 `opt/player-top3`，HEAD `30f6d3c`。
- 被查对象：1.1.13 + Top3。已复算哈希：`dsh_engine.dll` SHA256=`405A7778FE55814E…`（与工作区 `app\dsh_engine\dsh_engine_vsr.dll` 逐字节相同）、`WinUIGui.dll` SHA256=`1C9B8B4587D8188B…`（与 `D:\Temp\lyra_host_top3_build\WinUIGui.dll` 相同）→ 跑的就是本仓库源码。
- 证据：`dm_debug.log`（前端 85687 行）、`dsh_engine.log`（引擎 60561 行）、`crash.log`（594 行，全是后端 SocketException(995)，与本问题无关）。
- 只读调查，未改源码/安装目录/配置/数据。

**时间对齐方法**（两份日志都没有日期）：把前端 `切换画质完成`（`VideoWindow.xaml.cs:2993`，紧跟 `DshEngine_SeekJump`）与引擎 `seek jump N ms` 配对，得 `engine_tick − 258793176 = 当日毫秒`；第 3 次切换独立验到 1 ms 内，可用。下文「前端 Lxxxxx」= `dm_debug.log` 行号，「引擎 Lxxxxx」= `dsh_engine.log` 行号。

## 0. 一句话结论

切画质 = 换分辨率 = **RTX VSR 的 NGX 特性在 UI 线程的 `Present()` 内部被销毁并重建**（`dsh_engine.cpp:543-552`），这一步实测恒定吃掉 390/422/375 ms（`Rv_Shutdown` 段无日志），既让 UI 线程必卡半秒，又可能把 GPU 拖进 TDR：本次以 `DXGI_ERROR_DEVICE_HUNG` 收尾，**单次 `Present` 阻塞 5.257 s + 设备重建 141 ms = 5.41 s 停摆 → Windows「未响应」**。而新文件按设计从 0 开始出帧（`dsh_engine.cpp:2085/2114`），前端的回位 seek 又要等 `GetDurationMs()>0` 轮询（`VideoWindow.xaml.cs:2986-2991`），于是「开头画面」曝光 445/476 ms；卡死那一次正好把这段拖到 5.41 s。两个问题同一根因，且**都不是 Top3 引入的回归**。

## 1. 现象与日志还原（时间线）

三次切换（前端 L85457-L85546）全部命中，逐条对齐：

| # | 点击 | LoadMedia ok | 回位 seek | 开头帧曝光 | 备注 |
|---|---|---|---|---|---|
| 1 | 30.272 → 480P | 30.355（引擎 L60251） | 35.791 | **5.41 s** | 中途 `DEVICE_HUNG` + 重建 |
| 2 | 40.790 → 1080P+ | 40.902（L60345） | 41.355 | **0.45 s** | 无停摆 |
| 3 | 42.738 → 480P | 42.824（L60377） | 43.214 | **0.48 s** | 无停摆 |

第 1 次（关键词：TICK 空档 5.4 s）完整时序：

```
30.272 L85457 切换画质 → 480P（位置 3833ms）
30.277-30.333 L85458-85469 OpenMedia 同步段（字幕/音量/画质版本/META）≈ 18ms
30.355 引擎L60251 LoadMedia ok 852x480；L60249 视频纹理 1920x1080→852x480
30.379 L85470 TICK pos=29        ← 新文件已从 0 出帧，UI 线程此时还活着
30.379→35.79 无任何 TICK（约 43 条缺失）；引擎侧仅 30.745 VSR 重建、35.636 设备丢失
35.636 引擎L60257 ★Present 设备丢失 hr=0x887A0005 reason=0x887A0006
35.777 引擎L60273 ★设备已重建（141 ms）
35.791 L85471 切换画质完成（此刻才 SeekJump(3833)）→ 引擎L60275 seek jump 3833 ms
35.806 L85476 ★C# 渲染回调停摆：已 5.4 秒没触发 OnRenderFrame
36.541 起 TICK pos=3900（已回到目标位）
```

TICK 是 `OnRenderFrame` → `UpdateDanmaku` 里每 30 个渲染帧打一条（`VideoWindow.xaml.cs:5028`），它断档就等于 UI 线程断档——与 L85476 的 C# 看门狗（`VideoWindow.xaml.cs:3166-3175`）互相印证，**「未响应」在日志里是可证的**（不是"感觉卡"，是 43 条 TICK + 看门狗 5.4 s 双证）。第 2/3 次同样有 0.45/0.48 s 的「0~476 ms 帧正在屏上」窗口（见 2.B）。

另有一处旁证「不是用户瞎点」：36.402 s 引擎 `pause`（L60305）、37.886 s `resume`（L60313），对应前端唯一会让 `_paused` 变真的入口 `TogglePause`（`VideoWindow.xaml.cs:3061-3064`）——是窗口恢复后用户手动点/按空格，正好解释 TICK 里 pos=3900 冻住 1.35 s（L85526 区段）。

## 2. 根因

### A. 未响应：一条被放大到 5 s 的 UI 线程同步等待

**A1【确凿】VSR 特性在 `Present()` 里重建。** `Present()` 每帧调 `vsr_process_cpu()`（`dsh_engine.cpp:2641-2644`），其内 `vsr_prepare()` 发现 `g_vsr.outW/H` 变了就 `vsr_release_feature()`（`Rv_Shutdown`，`:515-518`，**无日志**）+ `Rv_Init`（`:545-552`，有日志）。三次切换从 `LoadMedia ok` 到 `RTX VSR: 已初始化` 的差：30.355→30.745=**390 ms**（L60253）、40.902→41.324=**422 ms**（L60347）、42.824→43.199=**375 ms**（L60380）。日志之前那一段就是 `Rv_Shutdown`；这 ≈0.4 s 全在 UI 线程里。

**A2【确凿】Present 内部有多处无界/超长的 GPU 等待。** Vulkan 上传/渲染都走 `oneShot()`：`QueueSubmit` 后 `WaitForFences(..., 5000000000ull)` = **5 s 超时**（`vulkan_video.h:499`），且失败不复位 fence；源图尺寸变化分支先 `waitIdle()`（`:318` → `:405` `vkQueueWaitIdle`，**无超时**）。逐帧还有 VSR 的 D3D11 回读（实测 5.76 ms/帧 L60325、9.47 ms/帧 L60231）。这些全在 UI 线程的 `Present()` 里。

**A3【确凿】5.257 s 阻塞 + 设备判挂 = 未响应的直接证据。** 30.379→35.636 无 TICK，引擎在 35.636 打出 `Present 设备丢失 reason=0x887A0006`（DXGI_ERROR_DEVICE_HUNG，即 TDR），随后 141 ms 重建、35.791 前端才恢复。5.257 s 与 A2 的 5 s fence 超时同量级。

**A4【确凿，第二条未响应路径，本次未触发】** `Present()` 用阻塞 `lock_guard(g_mtx)`（`dsh_engine.cpp:2599/2619`），而 `LoadMedia()` 全程持锁（`:2033`），锁内包含 `avformat_open_input/find_stream_info/avcodec_open2` 和 `g_audio.openFile`（`:2071`）——后者有 `for(i<400) sleep(10ms)` 等设备就绪（`audio_out.h:155`，**最多 4 s**）。本次该段仅 63 ms（L60248），但独占模式/设备被占用时这就是一条现成的 4 s 卡死路径。

**A5【已排除】Top3 帧队列不是死锁源。** `QMAX=16`（`dsh_engine.cpp:253`）把 `qCap()` 封顶（`:325`），1080p/480p 改造前（192MB）就已经是 16、改造后仍是 16，只有 4K 从 5 变 9；`DecodeAhead` 顶部有 `qCount>=qCap()` 就 break（`:2212`），`qPush` 失败后 `break`（`:2281-2283`）是死代码（顶部已挡），无死循环。`OpenMedia` 同步段实测 ≤18 ms（L85457-L85469），也不是主因。

**A6【推断】为什么是「概率性」。** 三次切换的 A1 代价都稳定（390/422/375 ms），差别只在收尾：第 1 次在 `Rv_Shutdown`+重建的窗口里还压着上一画质 4K（3840×2160）VSR 输出的 GPU 任务与 33 MB/帧回读，撞上 `waitIdle()` 就把驱动顶穿 → TDR；第 2/3 次（更小的输出、或者 GPU 已空闲）就只是慢半秒。所以「未响应」的触发条件是**GPU 队列里是否还有前一代 VSR 的工作在飞**，与文件大小/磁盘无关，这解释了为什么是概率性的、而且更容易在「从高画质切到低画质」时撞上。

**证据索引**（每条都能一眼复核）：

| 结论 | 证据 |
|---|---|
| VSR 在 Present 内重建、390~422 ms | `dsh_engine.cpp:543-552`；引擎 L60253/60347/60380 |
| 单次 Present 阻塞 5.257 s + 设备判挂 | 前端 L85470→L85471 空档；引擎 L60257-60273 |
| Present 内 GPU 等待无界 | `vulkan_video.h:499`（5 s）、`:318`/`:405`（无超时） |
| 新文件从 0 出帧 | `dsh_engine.cpp:2085/2114/2115`；前端 L85470 `TICK pos=29` |
| 回位 seek 等轮询 | `VideoWindow.xaml.cs:2986-2991`；窗口 445/476 ms |
| seek 后落点帧先上屏 | `dsh_engine.cpp:2159/2239-2247`；引擎 L60352/L60384 |
| 非 Top3 回归 | `git show c8d3f06 --stat`（未触碰上述函数） |

### B. 开头帧：设计上「先显示 0 帧」，再被 A 放大

**B1【确凿】加载后从 0 出帧。** `LoadMedia` 里 `g.lastPtsUs=0`（`:2085`）、`A/V 主时钟显式锚定 0`（`:2114`）、`g_mediaLoaded=true`（`:2115`），解码线程随即 `DecodeAhead` 入队，`Present` 按「pts≤时钟」弹帧上屏（`:2619-2627`）。前端 L85470 `TICK pos=29` 就是这一帧的铁证（pos 取自主时钟）。

**B2【确凿】前端要等轮询才回位。** `SwitchQualityAsync` 先 `OpenMedia(path)`，再 `for(60){ await Task.Delay(100); if(GetDurationMs()>0) break; }` 才 `SeekJump(pos)`（`VideoWindow.xaml.cs:2983-2991`）；`GetDurationMs` 在 `!g_mediaLoaded` 时返回 0（`dsh_engine.cpp:1990`）。实测窗口 445 ms（40.902→41.355）/476 ms（42.824→43.214）——按 23.98 fps 算，等于把片头 **11 帧**按顺序放给用户看；比 100 ms 轮询粒度大 4 倍，因为这段里 UI 线程正卡在 A1 的那次 `Present`（VSR 重建）里，`Task.Delay` 的续体排不上。

**B3【确凿】seek 后还会闪一帧「更早」的内容。** `DoSeek2` 置 `queueFirstAfterSeek=true`（`:2159`），`DecodeAhead` 对落点关键帧跳过判据、先入队上屏（`:2239-2247`）。日志：目标 7267 ms、落点 5033 ms（L60352，差 2234 ms）；目标 8600 ms、落点 5033 ms（L60384，差 3567 ms），随后才「追赶 67/107 帧、45~57 ms」。即回位后先看 2.2~3.6 s 前的内容约 50~100 ms。

**B4【确凿】不是 Top3 回归。** `git show c8d3f06` 只动了 `qCap/qPush/DecodeAhead` 顶部判据、`dsh_clock_ms_double` 的 `!fileEnded()`、三个新导出；`LoadMedia/DoSeek2/Present/queueFirstAfterSeek/SwitchQualityAsync/OpenMedia` 一个字没改（`git show c8d3f06 --stat`：4 文件 +107/−25）。

## 3. 修复方案

**B（主修，一并解决 A1 的窗口）——`DshEngine_OpenMediaAt(path, startMs)`，加载内 seek + 保持旧画面**
1. 引擎：`dsh_engine.cpp:1505` 旁新增导出 `DshEngine_OpenMediaAt(const wchar_t* path, int64_t startMs)`，请求结构加 `g_reqStartMs`；`DshEngine_GetApiVersion` 返回 **3**，前端 `DshNative.ExpectedEngineApiVersion`（`VideoWindow.xaml.cs:109`）同步改 3。
2. `LoadMedia()`（`:2032`）末尾、`g_mediaLoaded=true` 之前，若 `startMs>0`：`avformat_seek_file` + `avcodec_flush_buffers` + `g_qCount=0` + `g_originPts=startMs` + `lastPtsUs=startMs` + `dsh_clock_anchor_to(startMs)` + `g.seekTargetUs=startMs` + `g.skipsToClock=true` + **`g.queueFirstAfterSeek=false`** + `g_audio.flush(startMs)`。这样落点帧不再上屏，0 帧永不入队。
3. `Present()`（`:2619` 弹出后）：`if(startMs 未达成) { 丢弃该帧、不上传、不 render }` —— Vulkan 路径目标纹理保留上一画质的最后一帧，肉眼是「旧画面停 ~50 ms」而非「闪开头」；D3D11 路径沿用 `lastFrame+needReupload`。`startMs` 需 clamp 到 `[0, dur-1000]`（各画质时长可能差几百 ms）。
4. 前端：`SwitchQualityAsync` 改调 `OpenMediaAt(path, pos)`，**删掉 6 s 轮询**；`SetPaused(wasPaused)` 保留。`OpenMedia` 加 `startMs=0` 重载即可。
- 验收：切 20 次，① 引擎日志里 `LoadMedia ok` 紧随一条「起始位置 seek <pos> ms」，全程不出现 `落点 0 ms`，也不出现 `丢弃陈旧 seek 请求`；② 前端每次切完第一条 TICK 的 pos 与原位置差 ≤±200 ms，绝不出现 `pos<1000`；③ 眼视无开头帧。工作量 **0.5~1 人日**。
- 止血（当天可上，20 行）：点击画质时立刻在视频层盖一块黑 `Grid`（或不透明 `Image`），`SeekJump` 后 300 ms 再撤。

**A1——把 VSR 生命周期移出 `Present()`**
正解：`vsr_prepare()` 只允许在 `LoadMedia` 之后由解码线程预建（新导出 `DshEngine_PrepareVsr()`，或在 `LoadMedia` 末尾同步调一次）；`Present` 内若「目标尺寸已变但未就绪」就直接跳过该帧 VSR（走不超分路径），下一帧前由解码线程补建。**1.5 人日**。
止血（0.5 人日，目标消掉本次 A1 的 ~0.4 s 与 TDR 触发路径，效果待 A/B 验证）：`SwitchQualityAsync` 里 `OpenMedia` 之前 `DshEngine_SetRtxVsr(0)`（引擎 `:2510-2515` 只改内存标志 + `vsr_release_feature()`，**不写配置**），seek 完成后再恢复；注意 `OpenMedia` 里的 `ApplyRtxVsrOnOpen()`（`VideoWindow.xaml.cs:664-694`）会自动重新开启，所以要加 `_vsrSuspended` 字段让它在切换期间跳过，切完再 `SetRtxVsr(1)+SetRtxVsrQuality(旧值)`。

**A2——Present 内的一切等待都必须有界**
`vulkan_video.h:499` 的 5 s 改为 100 ms，超时后 `ResetFences` + 停用 Vulkan 30 s（`g_vkFrameWarned`），本帧回退 D3D11；`:318 waitIdle()` 在尺寸变化分支改为「不等待，直接标记重建、本帧跳过」。**0.5 人日**。

**A3——`SwitchQualityAsync` 加代次校验**
`int gen = _mediaGen;`（`OpenMedia` 内 `++_mediaGen`，`VideoWindow.xaml.cs:5479`）后每次 `await` 回来先 `if(gen != _mediaGen) return;`，避免连点两次画质时把 A 的 seek 打到 B 上（本次三次切换间隔 1.9 s，尚未踩到）。**0.5 人日**。

**A4——`Present` 改 `try_lock`、缩小 `LoadMedia` 持锁范围**
`Present` 的 `lock_guard`→`unique_lock(..., try_to_lock)`，拿不到就本拍跳过；`LoadMedia` 把 open/find_stream_info/codec_open 放锁外，只在换指针时持锁，`g_audio.openFile`（最多 4 s）移出 `g_mtx`。**2~3 人日**（FFmpeg 打开过程的线程安全要单独验证）。

## 4. 风险与回滚

- ABI 升到 3：旧 `dsh_engine.dll` 会被 `_engineApiMismatch` 判出（`VideoWindow.xaml.cs:347-358`）→ 必须「DLL+UI 成对发布」；前端保留 `api<3` 时回退旧轮询路径，可灰度。
- `LoadMedia` 内 seek 拖慢首帧 50~150 ms（日志实测追赶 45~57 ms）；4K 片源落点更远时可能 300~600 ms，可用 `DSH_SEEK_FAST=1`（放宽精确追赶）兜底。
- 不显示落点帧会让「极慢盘 + 大 GOP」的首帧更晚；用「保持上一帧」而非黑屏即可掩盖。
- 回滚：全部改动集中在 `SwitchQualityAsync`/`OpenMedia`/`LoadMedia`/`vsr_prepare`/`oneShot`，Top3 的队列改动**不要回滚**（与本案无关，`QMAX=16` 的封顶行为前后一致）。

## 5. 未证实项（需实测）

1. **GPU 到底卡在哪个调用**：`Rv_Shutdown`/`Rv_Init`、VSR 的 D3D11 回读、`vkQueueWaitIdle` 都不打日志，5.257 s 只能定位到「单次 `Present()` 内」。建议加 `[性能] Present 分段耗时`（VSR 准备/回读/上传/渲染/Present 各一段，>100 ms 必打），下次现场可直接定位。
2. **TDR 的触发条件**：本会话 60561 行日志只出现 1 次设备丢失，样本不足。需 A/B：①界面里关掉「超分辨率」②`DSH_GRAPHICS=d3d11`，各切 20 次，确认「关掉 VSR 后设备丢失 0 次、单次 Present < 100 ms」。
3. **Top3 320 MB 对 4K 的影响**：4K 队列 5→9 帧，多留 ~130 MB 常驻 + VSR 输出 4K（33 MB/帧）叠加，是否加剧 GPU 侧压力未测（`DSH_FRAME_QUEUE_MB=192` 可回归对照）。
4. 前端 445/476 ms 的精确归因（`Task.Delay` 续体被渲染回调饿死 vs 被 A1 卡住）只从时间上推断，需给 `SwitchQualityAsync` 加分段计时（点击→OpenMedia 返回→首次轮询→SeekJump）。
