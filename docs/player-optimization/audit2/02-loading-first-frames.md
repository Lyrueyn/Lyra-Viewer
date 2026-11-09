# 璃鸢镜 · 「打开慢」与「启动后必卡一次」根因与修复报告

- 对象：1.1.13 + Top3 构建（工作区 `opt/player-top3`，HEAD `30f6d3c`，Top3=`c8d3f06`）
- 引擎 `dsh_engine.dll` SHA256 405A7778…（= `app\dsh_engine\dsh_engine_vsr.dll`）；界面 `WinUIGui.dll` 1C9B8B45…
- 证据：`D:\Users\Administrator\AppData\Local\LocalMediaPlayer\dsh_engine.log`（60561 行，跨 2 次开机：L1–L15665 为 ep0，tick 785010484–954297281；L15666–L60561 为 ep1，tick 13231718–258991984，**tick 在 L15666 处回绕是重启导致，不是乱序**）、`dm_debug.log`（85687 行）
- 全程只读，未改动任何源码或日志

## 0. 一句话结论

「加载慢」的主因**不是**解封装/DLL 复制，而是**每打开一次视频就把引擎整个拆了重装**（`Shutdown`→`Init`），其中「重装时无条件重新探测 NVNGX」独占 **453–718 ms（占打开耗时 60–80%）**；「启动后必卡一次」是 **RTX VSR 首次初始化（NGX 实例 + 3840×2160 输出纹理）跑在 UI 线程上、在播放开始后约 1.5 s 才执行**，一次冻结 **438–672 ms**（前端 125 ms 心跳实测空洞 585 / 668 ms）。

## 1. 双击到首帧的时序拆解

取日志里最完整的一次打开（`dm_debug.log:85635`→`85677`，18:36:13.978→16.165；引擎侧 `dsh_engine.log:60499`→`60552`）：

| # | 阶段 | 起点→终点 | 耗时 | 证据 |
|---|---|---|---|---|
| ① | 主窗点击→VideoWindow 构造/XAML 装载（含 SwapChainPanel） | 13.978→14.032 | **未打点**（≤54 ms，需新增计时） | `dm_debug.log:85635-85648` |
| ② | `ApplyDefaultWindowSize` 改窗尺寸（**异步生效**） | 14.032 | 0 | `VideoWindow.xaml.cs:266-279` |
| ③ | `DshEngine_Init`：D3D11CreateDevice + SwapChain + `create_gfx`（3 次 `D3DCompile`） | 14.087→14.119 | **32 ms**（p50 47，n=37） | `dsh_engine.log:60440-60444`；`dsh_engine.cpp:1458/1462/1465` |
| ④ | `InitRtxVsrUi`：`CopyRtxDllsToApp`(23 MB) + `SetRtxSdkPath("")` → **NVNGX 重探测** | 14.135→14.556 | **11 + 453 ≈ 464 ms**（p50 609，max 718，n=32） | `dsh_engine.log:60446-60447`；`VideoWindow.xaml.cs:609-611`；`dsh_engine.cpp:2482-2503` |
| ⑤ | `OpenMedia` 编排（OpenMedia→UpdateMetaAsync→ApplySpeed→ApplyExclusive→ApplyRtxVsr→ApplyVolume×2→字幕） | 14.567→14.571 | 4 ms（但 `ApplyRtxVsr` **必失败**，埋 1.2 s 后置事件） | `VideoWindow.xaml.cs:5496-5509`；`dm_debug.log:85653` |
| ⑥ | 引擎 `LoadMedia`：`avformat_open_input`+`find_stream_info`+`avcodec_open2`+音频设备+sws+视频纹理+弹幕层 | 14.567→14.587 | **≈20 ms** | `dsh_engine.log:60527-60537`；`dsh_engine.cpp:2037-2084` |
| ⑦ | 弹幕层**第二次**重建（2288×1408→2787×1812）+ FT 重初始化 + 4096² 图集重建 | 14.587→14.62 | **31 ms**（同开总计 62 ms） | `dsh_engine.log:60540-60543`；`dsh_engine.cpp:744/752-755` |
| ⑧ | Vulkan 首次初始化 + 共享目标导入（仅进程首开） | +141 ms | **141 ms**（p50 140，n=28） | `dsh_engine.log:60478-60480`；`dsh_engine.cpp:427-448` |
| ⑨ | 首帧渲染（Vulkan 上传+渲染均 8.62 ms/帧） | 14.806 | — | `dsh_engine.log:60545` |
| — | **合计：点击→首帧可播** | 13.98→14.63 | **≈650 ms（最快档）**，典型 1.0–1.2 s | 前端 37 次打开 `窗口尺寸→已复制` 统计：min 531 / avg 888 / max 2024 ms |
| — | 其后 +1.5 s 的 VSR 冻结 | 15.70→16.15 | **+438 ms 冻屏** | 见 B 节 |

**可优化项排序（占打开耗时）：④ NVNGX 重探测 ~65% > ⑧ Vulkan 首建 ~15%（仅首开）> ⑦ 弹幕层重建 ~5% > ③ Init ~5% > 其它。**

## 2. 根因

### A. 加载慢

**A1（确凿，最大头）每次打开都强制重新探测 NVNGX：453–718 ms**

`VideoPanel_Loaded` 每次必调 `InitRtxVsrUi()`（`VideoWindow.xaml.cs:3126`），后者只要设置里存了 SDK 路径就**无条件**执行 `CopyRtxDllsToApp` + `DshEngine_SetRtxSdkPath("")`（`:604-611`）；而引擎侧该函数会 `FreeLibrary(rtx_vsr_bridge)`、把 `g_vsr.available=-1`、再 `vsr_probe()` 重新 `LoadLibrary`+NGX 特性枚举（`dsh_engine.cpp:2482-2503`、`507-514`）。日志中 `RTX SDK: 已清除自定义 DLL 搜索目录` → `RTX VSR: 探测 可用` 的间隔：**n=32，min 0 / p50 609 / max 718 / avg 504 ms**。
注意：`vsr_probe()` 本身有缓存（`if(g_vsr.available>=0) return`，`:508`），**只是被 `SetRtxSdkPath("")` 主动失效了**。

**A2（确凿）窗口尺寸「先建后改」：每次打开必然多做一次交换链/Vulkan 目标/弹幕层重建**

`VideoPanel_Loaded` 先 `ApplyDefaultWindowSize()` 改窗尺寸，**紧接着**用 `ApplyRenderScale()` 读 `VideoHost.ActualWidth`（`VideoWindow.xaml.cs:3106-3107`），此时布局尚未更新 → `DshEngine_Init(2288,1408)` 用的是**旧尺寸**；布局到位后 `VideoHost.SizeChanged` 再 `DshEngine_Resize(2787,1812)`（`:250-255`、`:2816-2822`）。
日志铁证：同一开内 `FT: layer ok … 层=2288x1408`（`dsh_engine.log:60530`）→ 31 ms 后 `FT: layer ok … 层=2787x1812`（`:60541`）；`LoadMedia 步骤` 里还写着 `交换链=复用（尺寸=2288x1408 vkOn=1）`（`:60538`）——即交换链是旧尺寸。
尺寸抖动还会放大成本：`dsh_engine.log:193-271` 连续 **16 次** `Vulkan: 渲染目标 = 窗口 2787x18xx`，间隔 15.6 ms（t=791078234→791078484），每次都在 UI 线程重建共享纹理+CreateSharedHandle+导入句柄，合计 ~250 ms。

**A3（确凿）弹幕层/字形图集被重复销毁重建**

`create_danmaku_layer` 在尺寸变化时先 `dmGpu.shutdown()` 再 `dmGpu.init(e.dev, 4096)`（`dsh_engine.cpp:744`）——4096² BGRA 图集约 64 MB，且 `FT_Done_FreeType`+`FT_Init_FreeType`+`FT_New_Face(simhei.ttf)` 全部重来（`:750-755`）。一次打开至少跑两遍（`:60530`、`:60541`），另加 `Present`/`DrawDanmaku` 路径的 `create_danmaku_layer(g, w, h)`（`:978`）与 `SetDanmaku`（`:1809-1810`）潜在第三遍。每遍观测 15–31 ms。

**A4（确凿，证伪）「每次打开都复制 nvngx DLL」不是主因**

`CopyRtxDllsToApp` 确实每次都 `File.Copy(..., overwrite:true)`（`VideoWindow.xaml.cs:495-511`，19.1 MB + 4.0 MB）。但实测复制代价仅 **9.2 ms + 2.2 ms = 11.4 ms**（跨卷 F:→C:，源文件取自 `refs\RTX_Video_SDK_v1.1.0\bin\Windows\x64\rel`）。相对 531–2024 ms 的打开耗时占比 <2%，**不构成"加载慢"**。
另有两个隐患：① DLL 已被进程 `LoadLibrary` 后覆盖会抛 sharing violation，被 `catch { return false; }`（`:510`）静默吞掉，而 `:611` 的日志无论成败都打——日志会"撒谎"；② 真正贵的是 A1 的探测，不是这次复制。

**A5（确凿，结构性）每次打开都整拆整建引擎**

37 次 `窗口尺寸` 对应 37 次 `Init ok`，且每次 `LoadMedia` 前都有 `Shutdown: 进入/退出`（如 `dsh_engine.log:60497-60498` → `:60514`）。根源在 `MainWindow.xaml.cs:113-117`：视频窗 `Closed` 即置 `_video=null`，下次点播**新建 VideoWindow** → `VideoPanel_Loaded` 再 `Init`（`VideoWindow.xaml.cs:3112-3123`）。于是每次打开都要重付：D3D11 设备 32–47 ms + NVNGX 探测 ~600 ms + Vulkan 141 ms（首开）+ 3 次 `D3DCompile` + 64 MB 图集。

### B. 启动后必卡一次

**结论：卡顿发生在「播放开始后约 1.5 s 的 RTX VSR 首次初始化」，执行线程 = UI 线程，耗时 438–672 ms。** 四段证据闭合：

1. **首次 `SetRtxVsr` 必然失败**（确凿）。`DshEngine_OpenMedia` 只是投递请求立即返回（`dsh_engine.cpp:1505-1516`），`OpenMedia` 在同线程下一行就调 `ApplyRtxVsrOnOpen()`（`VideoWindow.xaml.cs:5496→5501`）。此刻 `g.vw/g.vh` 仍为 0 → `vsr_calc_target` 首行 `if(g.vw<=0||g.vh<=0…) return false`（`:520`）→ `vsr_prepare` 直接 `return false`（`:541-542`）→ `SetRtxVsr` 返回 -5（`:2530`）。日志里 `ApplyRtxVsrOnOpen 首次失败 rc=-5` **14 次、全是 -5、零例外**（`dm_debug.log:699/2571/57504/84065/…/85653`）。
2. **重试落在 UI 线程**（确凿）。`_ = RetryRtxVsrOnOpenAsync(1)`（`:681`）内 `await Task.Delay(1200)` 未加 `ConfigureAwait(false)`（`:703`），续体回到 UI 线程后直接调 `DshEngine_SetRtxVsr(1)`（`:706`）。
3. **该调用里做 NGX 实例 + 4K 纹理**（确凿）。`vsr_prepare` → `g_vsr.pInit((void*)g.dev, 3840, 2160)` 并取输出 SRV（`dsh_engine.cpp:545-550`）。日志中 `RTX VSR: 已初始化 1920x1080→3840x2160（质量 4）` **前一行到它的空档：n=20，p50 438 / max 672 / avg 420 ms，期间零日志**（`dsh_engine.log:60545→60551`，t=258987984→258989328）。
4. **UI 线程心跳空洞正好包住它**（确凿）。前端 TICK 是 125 ms 定时器（正常间隔恒为 125 ms），实测：
   - 18:36:15.766 → 16.434 空洞 **668 ms**，内含 `16.165 延迟重试开启成功`（`dm_debug.log:85676`→`85678`，成功行 `85677`）；
   - 18:36:10.015 → 10.600 空洞 **585 ms**，内含 `10.514 延迟重试开启成功`（`85620`→`85623`，成功行 `85622`）。
   两次空洞与引擎侧 438 ms 空档在时间轴上对齐（引擎 `已开启` t=258989343 ↔ 前端 16.165）。

**排除项（附证据，避免误判）：**
- `ensure_vk_target` 首次创建：141 ms，发生在 `LoadMedia ok` 之后仅 **+187 ms**（`dsh_engine.log:60471`→`60480`，t=258982125→258982312），比卡顿时刻早约 1.2 s 且不重合 → **不是它**。
- `create_danmaku_layer` 首次 FT 初始化：15–31 ms，发生在 +0.03 s → 量级不符。
- Top3 的 qCap 320 MB / 帧队列：`DecodeAhead` 每轮有 `if(g_qCount>=qCap()) break` 真背压（`dsh_engine.cpp:2212`）、`qPush` 满即拒绝（`:329-330`），1080p 下 cap≈40 帧；VSR 前后 `[性能]` 无尖峰（见 5.5）。**队列不是这次卡顿的原因**。
- 「首帧后突然连解码」在理论上存在（一次可解满 40 帧），但日志无解码线程耗时打点，**未证实**其对 UI 的影响（列入 5.6）。

## 3. 修复方案（按收益排序）

**① VSR 初始化搬出 UI 线程，并取消「先失败再重试」（消掉 438–672 ms 冻结）**
改法（最小改动版）：`RetryRtxVsrOnOpenAsync` 改为
`await Task.Delay(1200).ConfigureAwait(false); int rc = await Task.Run(() => DshNative.DshEngine_SetRtxVsr(1));`
UI 回填统一 `DispatcherQueue.TryEnqueue(...)`；`ApplyRtxVsrOnOpen` 的首次调用改为「先判 `DshEngine_IsMediaReady()`（新增导出，返回 `g.vw>0 && g.mediaLoaded`），未就绪则直接排重试、不产生 -5」。
更彻底版：引擎加 `DshEngine_SetRtxVsrDeferred(1)`，只置 `g_vsr.pendingEnable`，由解码线程在首次 `have` 出帧后执行 `vsr_prepare()`，UI 线程只读结果。
预期收益：该次冻结 438–672 ms → <50 ms；`RTX VSR: 已初始化` 前后无 >100 ms 空档。工作量 S（最小版）/ M（彻底版）。

**② NVNGX 探测从「每次打开」降到「每次进程」（省 453–718 ms，占打开 60–80%）**
改法：`InitRtxVsrUi` 的 `:604-611` 加守卫——用 **实例级 `static`**（跨 VideoWindow 共享）记住 `_rtxSdkAppliedDir` 与目标文件 `(Length, LastWriteTimeUtc)`；未变化则**跳过** `CopyRtxDllsToApp` 与 `SetRtxSdkPath("")`，只调 `DshEngine_RtxVsrAvailable()`（走 `available` 缓存，O(1)）。
配套：`DshEngine_SetRtxSdkPath` 在 `pathUtf8` 为空且 `SetDllDirectoryW` 已为 null 时**提前 return 现有 available**，不做 `FreeLibrary`+重探（`dsh_engine.cpp:2481-2503`）。
预期收益：打开耗时 −500 ms 左右（650→150 ms 量级）。工作量 S。

**③ 用正确尺寸 Init + 尺寸变化去抖（省 ~30–280 ms，并消除重建风暴）**
改法 a：`VideoPanel_Loaded` 里改为 `ApplyDefaultWindowSize()` → 等一次布局（`await DispatcherQueue.EnqueueAsync(...)` 或用 `LayoutUpdated` 单次回调）→ 再 `ApplyRenderScale()` + `DshEngine_Init`；应急版：Init 后用 `ApplyRenderScale()` 的返回值立刻补一次 `DshEngine_Resize`，避免 SizeChanged 事件跨帧到达。
改法 b：`VideoHost.SizeChanged`（`VideoWindow.xaml.cs:250-255`）加 150 ms 去抖 + 尺寸相等则 return；引擎侧 `ensure_vk_target`/`create_danmaku_layer` 加 100 ms 静默期。
改法 c：`create_danmaku_layer` 在「仅尺寸变、字号未变」时不做 `dmGpu.shutdown()+init(4096)`（`dsh_engine.cpp:744`），图集按需增长（2048² 起）。预期再省 15–30 ms + 64 MB 反复分配。工作量 S。

**④ shader 预编译为 .cso（省 10–30 ms/次打开 + 去掉运行时 fxc 依赖）**
`compile_shader` 在 `create_gfx` 里编 3 个、`create_danmaku_layer:807` 编 1 个、`Present` 内还有内联 PS（`dsh_engine.cpp:346-352`）。构建期 `fxc /T vs_5_0 /Fo x.cso`，运行时 `D3DReadFileToBlob`。工作量 S。

**⑤ DLL 复制加「已存在且未变则跳过」+ 如实记录结果（省 11 ms，主要是修正确性）**
`CopyRtxDllsToApp`（`VideoWindow.xaml.cs:495-511`）：目标存在且 `Length`+`LastWriteTimeUtc` 与源一致 → `continue`；把返回值传到 `:611` 的日志里（"复制 n 个 / 跳过 m 个 / 失败"）。工作量 XS。**注：这不是加载慢的主因，优先级低于 ①②③。**

**⑥（结构性，工作量 L）复用 VideoWindow 或引擎实例，不每次 Shutdown/Init**
`MainWindow.xaml.cs:113-117` 改常驻隐藏窗口（或 `--video` 独立进程复用），让 `DshEngine_Init` 只在进程首开付一次（省掉 NVNGX 探测 + Vulkan 141 ms + D3DCompile + 图集的首开成本，并让 ② 的守卫天然生效）。预期：二次及以后打开 ≈150–250 ms。

## 4. 验收与测量方法

**必须新增的计时点（当前日志缺，是本报告唯一"需新增计时"的部分）：**
1. 前端：`MainWindow` 点击 → VideoWindow 构造完成 → `Loaded` → `DshEngine_Init` 返回 → 首次 `Present` 有帧 → 首帧 `Present` 返回，共 5 个 `Environment.TickCount64` 写进 `dm_debug.log`（一行 `打开计时: click..firstFrame=xxx ms`）。
2. 引擎：`DshEngine_OpenMedia` 进入时刻；`LoadMedia` 内 `open_input` / `find_stream_info` / `avcodec_open2` / `g_audio.openFile` / `sws` / `ensure_video_tex` / `create_danmaku_layer` 各一条带 tick 的日志；`vsr_probe` 起止、`vsr_prepare` 起止、`ensure_vk_target` 起止。

**验收门槛（用现有日志即可复算）：**
- 打开：`窗口尺寸` → `RTX SDK: 已复制` 的 p50 从 888 ms 降到 <250 ms（当前统计口径：n=32，min 531 / avg 888 / max 2024）。
- 卡顿：TICK 空洞（125 ms 心跳）p99 < 50 ms；`RTX VSR: 已初始化` 前后空档从 p50 438 ms 降到 <100 ms；`ApplyRtxVsrOnOpen 首次失败 rc=-5` 出现次数 = 0。
- 重建：单次打开内 `FT: layer ok` 出现次数从 2 降到 1；`Vulkan: 渲染目标` 在拖动窗口时的条数从 16/次 降到 ≤2/次。

**对照实验（同一二进制 + 环境变量即可量化贡献）：** `DSH_GRAPHICS=d3d11`（去 Vulkan 141 ms）、关 VSR 开关（去 438 ms 冻结 + 8.57 ms/帧 CPU 回读）、`DSH_FRAME_QUEUE_MB=192`（验队列无关）、`DSH_DANMAKU_CPU=1`（区分图集开销）。

## 5. 未证实项

1. **点击 → VideoWindow 构造**（XAML 装载 + SwapChainPanel 创建）无任何打点，只能从 `窗口尺寸` 反推，存在 50–400 ms 不确定区间。
2. **NVNGX 探测 453–718 ms 的内部拆分**（19 MB `LoadLibrary` vs 驱动特性枚举 vs D3D11 设备侧 NGX 绑定）未测，需加分段计时或 ETW。
3. **冷盘/冷页缓存**：日志中 `解码器:`→`LoadMedia ok` 仅 0–16 ms（`dsh_engine.cpp:2056-2117`），说明 FFmpeg DLL 与媒体文件均在系统缓存中；冷启动代价未测，"解封装慢"在本数据集里**未被验证**。
4. **VSR 在 UI 线程执行**的最强证据是 TICK 空洞包住该调用 + 无 `ConfigureAwait(false)`，未抓调用栈；建议用 `dotnet-trace`/PerfView 复核一次。
5. **`[性能]` 计数器跨 Shutdown 不复位**（`dsh_engine.cpp:219` 的 `g_msN/g_msVk/g_msD3d` 是文件级 static，`DshEngine_Shutdown:2840-2878` 未清零），所以打开后 219 ms 就冒出的 `[性能] … 8.62 ms（120 帧）` 混入了上一个会话的帧；2627 个窗口里 max 55.53 ms、p50 11.44 ms 也**不能**直接归因于首帧。需先修计数器复位再重测首帧窗口。
6. **qCap 320 MB 是否让首帧后"突然连续解码"**：`qCount>=qCap()` 有真背压、`[性能]` VSR 前后无尖峰，但解码线程耗时无打点，对 UI 的次生影响未证实。
7. `crash.log` 内 81 KB 全是后台 `SocketException(995) 已中止 I/O`（换片取消请求），与本问题无关，无播放相关崩溃。
