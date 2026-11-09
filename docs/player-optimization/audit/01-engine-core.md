# 引擎核心与播放管线审计报告

审计对象：`app/dsh_engine/dsh_engine.cpp`（2874 行，全文逐段读完）+ `app/WinUIGui/VideoWindow.xaml.cs` 的 P/Invoke 面与调用点。
审计方式：只读静态审查。未启动播放器、未跑性能采样；所有毫秒/带宽数字为按代码路径推算，需实测复核。

## 0. 一句话结论

架构方向是对的（解码/呈现/音频三线程 + 音频主时钟 + 有界退避的设备丢失阀门），但存在 **3 个 P0**：界面每秒调用的 `DshEngine_GpuDead` 在当前 C++ 源码里**已不存在**（重编即丢导出，故障提示静默失效）；帧队列背压判据与容量函数不一致，导致 **>1080p 时解码线程空转、丢帧、seek 排队**；解封装请求单槽存储，**换片与 seek 并发时 load 请求被吞**（偶发黑屏）。先修这 3 条（1.5 天），再谈性能优化。

## 1. 现状架构摘要（≤250 字）

全局泄漏式单例 `static DshEngine& g = *new DshEngine()`（:165）。**解码线程**（:1489 起 `DecodeLoop`）独占 `g.fmt/g.cc/g.sws`，每 ≤16ms 轮询一次 `g_reqType` 单槽请求，处理 load/preview-seek/jump-seek，然后 `DecodeAhead` 连续解码到 `QMAX=16` 环形队列（每帧 BGRA `std::vector`）。**音频线程**在 `audio_out.h` 内部，自带 demuxer，WASAPI 共享/独占 + 两级 atempo。**UI 线程**由 C# `CompositionTarget.Rendering` 每显示帧驱动：`UpdateDanmaku`→`DshEngine_GetDanmakuLayout`（推进弹幕、建顶点）→`DshEngine_Present`（弹时钟、弹队列、`UpdateSubresource` 上传、Vulkan 或 D3D11 绘制、`Present`）。A/V 同步用"音频 `mediaUs()` 单调锚 + 墙钟插值 × 倍速"（:1634）。设备丢失走"时间窗(60s/3 次)+抖动(≤10s)"双阀，超限判死停机。

## 2. 问题清单（按严重度排序）

### P0-1 当前源码没有 `DshEngine_GpuDead`，界面每秒调它 —— 重编即丢导出
- **严重度**：P0（功能静默失效 + 源码/产物不一致）
- **证据**：`dsh_engine.cpp` 全文无该函数定义，只剩三处注释引用——`:2357`「界面用 DshEngine_GpuDead() 读状态」、`:2369` 日志串、`:2463`「★ 只读导出：显卡是否已被判死（1=已判死）」（注释在，函数被删）。C# 侧 `VideoWindow.xaml.cs:107` `public static extern int DshEngine_GpuDead();`，`:309` 定时器 1 秒一次、`:337` `dead = DshNative.DshEngine_GpuDead() == 1;`。PE 导出表实测：源码同时间戳（2026-10-23 18:10）编出的 `dsh_engine_vsr.dll` 共 **41 个导出，不含 GpuDead**；而 10-18 的旧 `dsh_engine.dll` 有 GpuDead 但缺 6 个 VSR 导出 → GpuDead 是旧版有、当前源码丢了。
- **影响**：`:337` 的 try/catch 吞掉 `EntryPointNotFoundException` 并置 `_gpuDeadProbeBroken` → 显卡判死横幅永不出现，"白屏可读化"整条功能失效；每秒白抛一次异常。
- **根因**：导出面无版本号、无自检，全靠人工对齐；GpuDead 被删时无编译期约束。
- **方案**：在 `:2544` 前补 `extern "C" __declspec(dllexport) int WINAPI DshEngine_GpuDead(){ return g_gpuDead?1:0; }`；同批加 `DshEngine_GetApiVersion()`，C# Init 后校验，不匹配就禁用相关按钮并在 UI 明示"DLL 与界面版本不匹配"。
- **验收**：`dumpbin /exports` 含 42 个符号；`DSH_TEST_DEVICE_LOST=1` 注入后 3 次退避判死，1 秒内横幅出现，换片后消失。
- **工作量**：0.5 天。

### P0-2 背压判据用 `QMAX` 而容量函数是 `qCap()` —— >1080p 解码线程空转丢帧
- **严重度**：P0（性能/正确性）
- **证据**：`:2213` `{ std::lock_guard lk(g_mtx); if(g_qCount>=QMAX) break; }`（`QMAX=16`，`:253`）；而 `:315-324` `qCap()` 按 192MB 预算返回 4~16（2560×1440 只有 13，4K 只有 5）；`:327-332` 队列满时 qPush **覆盖最旧帧且 `g_qCount` 不再增长**。于是只要 `qCap()<16`，`g_qCount` 永远到不了 16，`:2213` 永不 break，`for(int guard=0; guard<300; guard++)` 会解满 300 帧，其中约 295 帧刚解出就被下一次 qPush 覆盖丢弃。4K BGRA 每帧 33MB，一次 `DecodeAhead` 最坏 = 300 次「解码 + 33MB 分配 + 33MB 逐行拷贝」，耗时可达秒级；期间解码线程无法回到 `:2172` 取 seek 请求。
- **影响**：2K/4K 播放 CPU 打满；呈现侧 `:2609` 只 pop 到"未来帧"→ 5 帧丢 4 帧，画面一顿一顿；seek 延迟从毫秒级退化到秒级。这是"4K 卡"最可能的直接原因。
- **根因**：两处对"队列满"的定义不一致；qPush 用覆盖代替背压，把"满"隐藏了。
- **方案**：`:2213` 改 `if(g_qCount>=qCap()) break;`；`qPush` 改返回 `bool`，满时 `return false` 由调用方 break（不要覆盖）；`qCap()` 结果按媒体缓存，避免每帧算。
- **验收**：同一 4K HEVC 文件对比 `[性能]`(:2652) 与 `seek->show`(:2798) 日志；新增"本 tick 解码 N 帧 / 入队 M 帧"，要求 N≈M。
- **工作量**：0.5 天。

### P0-3 解封装请求只有单槽 `g_reqType` —— 换片与 seek 并发时 load 请求被吞
- **严重度**：P0（偶发黑屏/换片失败）
- **证据**：`:1526` OpenMedia `g_reqType.store(1)`；`:2007`/`:2024` SeekPreview/SeekJump `store(2)/store(3)`；解码线程 `:2172` `req=g_reqType.exchange(0)` 单槽取一次。`SeekJump` 只在 `:2012` 检查一次 `g_mediaLoaded`，而 OpenMedia 在 `:1511` 已把它置 false 并于 `:1525` 把 `g_mediaGen` 加一；若 SeekJump 的检查先通过、store 落在 OpenMedia 的 `store(1)` 之后，则 1 被 3 覆盖。
- **影响**：换片同时拖进度条 → 新视频永不加载（画面停上一部最后一帧、进度条静止），必须再点一次换片。与"换片有时候白屏"吻合。
- **根因**：单槽 + 无优先级；load 是"不可丢"的请求，却被 seek 当同级覆盖。
- **方案**：`SeekPreview/SeekJump` 改用 `compare_exchange` 只允许 `0→2/3`，读到 1 就丢弃本次 seek；或把请求做成 4 槽小环，每条带 `gen`（`:2176` 的陈旧校验可直接复用）。
- **验收**：脚本化"换片同时连发 5 次 SeekJump"×100 次，无一次停在 0 帧。
- **工作量**：0.5 天。

### P1-4 `LoadMedia`/`DoSeek2` 全程持 `g_mtx`，UI 线程的 Present 被阻塞 → 换片/seek 整窗假死
- **严重度**：P1
- **证据**：`:2034` `std::lock_guard<std::mutex> lk(g_mtx);` 一直持到 `:2136` 函数结束，中间含 `avformat_open_input`(:2038)、`avformat_find_stream_info`(:2039)、`avcodec_open2`(:2056)、`g_audio.openFile`(:2072)；`DoSeek2` 同样 `:2140` 持锁跨 `avformat_seek_file`(:2146) 与 `g_audio.flush`(:2155)。UI 线程的 `Present` 在 `:2587` 与 `:2607` 两次拿 `g_mtx`，`dsh_clock_ms_double` 在 `:1647` 也拿。
- **影响**：换片/大 seek 期间 XAML UI 线程被冻结数十至数百毫秒（具体值需实测），按钮不响应、动画卡顿，直接对应"换片要顿一下"。
- **根因**：把慢 I/O 放进了保护共享状态的互斥锁。
- **方案**：三段式——① 锁外把媒体开到临时局部变量（open/stream_info/avcodec_open2/openFile）；② 持锁只做指针交换 + 队列/时钟/flags 复位（目标 <1ms）；③ 失败清理回到锁外。`DoSeek2` 同理：锁内取指针 + 复位状态，`avformat_seek_file`/`flush` 放锁外（二者与 LoadMedia 本来就在同一线程串行，安全）。
- **验收**：`:2034`/`:2140` 前后加 `QueryPerformanceCounter` 打印持锁时长，目标 <2ms；手动快速换片时窗口不失去响应。
- **工作量**：1 天。

### P1-5 每帧 3 次全帧 CPU 拷贝 + 每帧 8MB 堆分配
- **严重度**：P1（性能）
- **证据**：`sws_scale` 写进 `rgba`(:2272) → 逐行 `memcpy` 到**新建** `std::vector<uint8_t> buf((size_t)w*h*4)`(:2273-2274) → `UpdateSubresource` 再整块上传(:2647)；`qPopOldest` 里 `out=std::move(g_q[idx].bg)` 之后又 `bg.clear()`(:340-341)，缓冲区容量丢失，下一帧必然重新 new 8MB；`g.lastFrame = std::move(cur)`(:2657) 又把上一帧释放。
- **影响（1080p，推算/需实测）**：每帧 CPU 触碰约 3×8.3MB≈25MB，60fps → ~1.5GB/s 内存带宽；每秒 60 次 8MB 分配+释放 ≈ 500MB/s 分配器压力。
- **方案**：建 `qCap()` 大小的 slab 池（一次分配 cap×w*h*4），队列元素改存 slab 索引，`sws_scale` 直接以 slab 行为 `dst`（`dst[0]=slab.data(); linesize=w*4`）→ 删掉 :2274 的 memcpy；pop 后归还 slab，不 free。
- **验收**：现有 `:2652` 的 `[性能]` 行对比改造前后；D3D11 上传均耗预期降 1~3ms/帧。
- **工作量**：2 天（含 P1-6 之外的所有权梳理）。

### P1-6 拖拽预览每帧解一整 GOP（`skipsToClock=false`）
- **严重度**：P1
- **证据**：`:2152` `g.eof=false; g.skipsToClock.store(!preview);` —— preview 时为 false，于是 `:2224` 的 skip 分支永不成立，解码线程把落点关键帧之后的**每一帧**都入队；`:2186-2187` 只做 80ms 节流，注释自认"每次 seek+flush+关键帧解码约 100~150ms"。
- **影响**：拖拽每 80ms 触发一次"seek + flush + 解一个 GOP"；4K 长 GOP 可达 60~125 帧 → 单次预览 100ms~1s，进度条跟手差、音频每次 flush 都断续。
- **方案**：给 preview 加"只出一帧"语义：`DoSeek2(preview=true)` 置 `g.previewOneFrame`，`DecodeAhead` 在 preview 下取到首帧入队后立即 break 并停止；或临时 `g.cc->skip_frame=AVDISCARD_NONREF` 到出帧为止。
- **验收**：4K 长 GOP 文件拖拽的单次预览耗时 ≤1 帧解码时间；对比 `seek prev`(:2161) 与 `seek->show`(:2798)。
- **工作量**：1 天。

### P1-7 D3D 资源跨线程竞态：`ensure_video_tex` 在解码线程，`dsh_rebuild_device` 在 UI 线程
- **严重度**：P1（低概率崩溃）
- **证据**：`ensure_video_tex` 由 `LoadMedia` 在 `:2085`（解码线程、持 `g_mtx`）调用，内部 `:467` `videoTex->Release()`、`:473` `CreateTexture2D(&td,nullptr,&e.videoTex)`；`dsh_rebuild_device` 由 UI 线程经 `:2399`/`:2454`/`:2685` 调用，在 `:2319-2323` 释放 `videoTex/videoSrv/swap/ctx/dev`，`:2347` 又调 `ensure_video_tex` —— 这一段**完全没有加锁**。同类：`g.vw/g.vh` 在 `:2060` 锁内写、`:2616` 锁外读；`g.texW/texH` 同理。
- **影响**：换片与设备丢失重合时可能 use-after-free（崩溃落在驱动层，日志只剩间接线索）。
- **方案**：D3D 资源生命周期收敛到单线程——解码线程只写 `std::atomic<int> g_pendingW/H` 并置位 `g_needTexRebuild`，纹理创建/释放在 Present 里做；`dsh_rebuild_device` 与 `LoadMedia` 的纹理段共用一把 `g_gfxMtx`。
- **验收**：`DSH_TEST_DEVICE_LOST=flap` + 连续换片 100 次不崩；加 `assert(g_gfxOwnerThread==GetCurrentThreadId())` 兜底。
- **工作量**：1 天。

### P1-8 `sws` 为空 / `av_frame_get_buffer` 失败未检查，`sws_scale` 空指针崩溃
- **严重度**：P1（可崩溃）
- **证据**：`:2083-2084` 两次尝试建 sws（`dsh_create_sws` 失败后 `sws_getContext`），**失败也不返回**，继续 `:2086 g.mediaOpen=true`；`:2272` `sws_scale(g.sws,...)` 直接解引用。`:2200-2202` `rgba=av_frame_alloc(); … av_frame_get_buffer(rgba,1);` 返回值未检查，失败则 `rgba->data[0]` 为空、`:2274` memcpy 崩。
- **方案**：两处都改成失败即记录错误（见 P2-9）+ `g.mediaOpen=false; g.mediaLoaded.store(false); return;`。
- **验收**：喂一个 sws 不支持的 pix_fmt，界面提示"无法解码该视频"而不崩溃。
- **工作量**：0.5 天。

### P2-9 错误不可上报：LoadMedia 全是 void，界面拿不到任何原因
- **严重度**：P2（但直接决定用户可见反馈）
- **证据**：`LoadMedia`(:2033) 返回 void；`:2038/2039/2044/2056` 四个失败分支都只 `g.mediaOpen=false; g_mediaLoaded.store(false); return;`，无 `av_strerror` 文本；`DshEngine_OpenMedia`(:1506) 恒返回 0（只有 `!g.init`→-1、空路径→-10）；引擎无 `GetLastError` 类导出（C# 也未声明）。
- **方案**：加 `int DshEngine_GetLastError(char* buf,int n)`（内容 = 阶段名 + `av_strerror` 文本）与 `DshEngine_GetState(DshEngineState*)`（init/mediaOpen/eof/gpuDead/vw/vh/audioOk 一次取回，替掉 C# 现在零散的 6 个调用）。
- **验收**：损坏 mp4 → UI 显示具体原因（如 "moov atom not found"）。
- **工作量**：0.5 天。

### P2-10 每帧重建后缓冲 RTV
- **证据**：`:629-631` `e.swap->GetBuffer(0,…)` → `CreateRenderTargetView` → `OMSetRenderTargets`，`:724` 每帧 `rtv->Release(); bb->Release();`。
- **方案**：缓存 2 个后缓冲的 RTV，用 `IDXGISwapChain3::GetCurrentBackBufferIndex()` 选择，仅在 Resize/重建时失效。
- **预期**：省 0.2~1ms/帧（需实测），120Hz 下收益翻倍。**工作量**：0.5 天。

### P2-11 `Shutdown` 漏清 Vulkan；重复 Init 覆盖设备不释放
- **证据**：`DshEngine_Shutdown`(:2828-2866) 从头到尾未碰 `vkTargetTex / vkHandle / g.vk.shutdown() / vkOn / needReupload / lastFrame`，而 `dsh_rebuild_device` 在 `:2310-2317` 是认真清的 → 属遗漏。重复 Init 路径 `:1459` 直接把 `g.dev/g.ctx` 覆盖成新设备，旧设备从未 Release；`:1466 create_gfx` 同样覆盖 `vs/ps/il/samp/vb`（而 `:1449` 的注释还写着"保留 D3D 设备"，自相矛盾）。
- **影响**：单窗口单进程模型下被进程退出兜住，属潜在问题；C# 目前有 `_engineReady`(:3054) 挡住二次 Init，所以暂时不发作。
- **方案**：抽 `dsh_gfx_teardown()` 供 Shutdown / 重复 Init / 重建三处共用。
- **工作量**：0.5 天。

### P2-12 `g_speedReq` 非原子；换片时倍速下发晚于开音频
- **证据**：`:303 static float g_speedReq`；写 `:2818`（UI 线程）、读 `:1559`（UI + 解码线程）、`:2068`（解码线程）→ 数据竞争，应改 `std::atomic<float>`。另外 C# 换片流程 `:5430` 先 `OpenMedia`、`:5433` 才 `ApplySpeed()`，而解码线程可能在 `:2068` 之前就读到旧值，打破 `:2065-2068` 注释要求的"先设速率再开音频"。
- **方案**：改原子 + 把倍速随 load 请求一起快照传入（`g_reqSpeed`），LoadMedia 用它而非全局。
- **工作量**：0.5 天。

### P2-13 日志每条 fopen/fclose、无轮转；shader 错误文本被丢弃
- **证据**：`dsh_log`(:67-74) 每行 `GetModuleFileNameA` + `fopen("a")` + `fprintf` + `fclose`，无轮转；`compile_shader`(:349-350) 拿到 `D3DCompile` 的错误 blob 后 `const char*m=…; (void)m;` 直接丢弃。
- **影响**：长期播放日志无限增长；shader 编译失败只剩一句 "shader compile fail" —— 这正是"黑屏但没线索"的典型来源。
- **方案**：常驻 `FILE*` + 行缓冲 + 8MB 轮转（`.1`）；`:350` 把 err 文本写进日志。
- **工作量**：0.5 天。

### P2-14 17 个环境变量当功能开关，`PLAY_*.bat` 是唯一入口
- **证据**：`:1469-1488` 及散落各处：`DSH_GRAPHICS`、`DSH_GPU`、`DSH_HDR_PASSTHROUGH`、`DSH_SEEK_FAST`、`DSH_DEC_THREADS`、`DSH_TONE_BOOST/HDR_DITHER`、`DSH_DANMAKU_CPU` 等 17 个（含 4 个 `DSH_TEST_*`）。而 `PLAY_*.bat` 只设 `DSH_GRAPHICS` 与 `DSH_HDR_PASSTHROUGH` —— 用户想换渲染后端或 HDR 只能自己改 bat。
- **方案**：加 `DshEngine_ApplyConfig(const DshEngineConfig*)`（首字段 `structSize` 做前向兼容），env 降级为"首次默认值"；渲染后端/HDR 直通/解码线程数/日志级别搬进设置页；`DSH_TEST_*` 收进 `#ifdef DSH_DEBUG`。
- **工作量**：1 天。

### P2-15 导出面靠 1780 行 `extern "C"` 大块兜着，且无版本号
- **证据**：41 个导出依赖 `:1085` 起的 `extern "C" {`（到 `:2867` 结束）；`:2468-2543` 的 6 个 VSR 函数又各自单独写 `extern "C"`；`:878` 注释记录了"不加 extern \"C\" 就 GetProcAddress 找不到（踩过）"。界面 38 条 DllImport 中，引擎有 4 个导出从未被使用（`DumpFrameBmp / GetAudioInfo / GetDanmakuOptions / HasAudio`）。
- **方案**：全部导出集中到一个 `dsh_engine_api.h`（含版本宏、错误码 enum、结构体），`.cpp` 只 include 它，C# 从同一份 IDL 生成 —— P0-1 这类问题从机制上消失。
- **工作量**：1 天。

## 3. 优化方案（非 bug 提升项，按收益排序）

1. **硬解直出 GPU 纹理**（D3D11VA，`AV_PIX_FMT_D3D11` 直接当 SRV）：现在是软解 + sws + 3 次拷贝，4K 全靠 CPU（`:2049-2055` 自述 4K HEVC 16 线程 142fps）。预期 4K CPU 从接近打满降到 20~30%。风险最高：MinGW 下 `d3d11va.h` 可用但需对齐现有 `videoTex` 路径，且必须保留 `DSH_HWACCEL=off` 回退。3 天。
2. **帧队列 slab 池 + 呈现/解码解耦**（P1-5）：消除每帧 8MB 分配与 2 次全帧拷贝。预期 1080p60 CPU 降 5~10%，4K 降 15%+。2 天。
3. **呈现从 UI 线程剥离**：现在 `VideoWindow.xaml.cs:3096-3101` 的 `CompositionTarget.Rendering` 里串行执行 `UpdateDanmaku` + `Present`，任何 UI 卡顿直接变成视频丢帧（P1-4 的锁阻塞更是直接冻结 XAML）。改成引擎自有 present 线程 + C# 只喂弹幕数据。3 天。
4. **shader 预编译**：`create_gfx`(:355-384) 每次重建跑 4 次 `D3DCompile`（VS/PS/HDR-PS/弹幕-PS），设备丢失恢复因此多几十至上百 ms。改成构建期编 `.cso` 用 `#include` 进二进制（项目已有 `build_shaders.ps1`）。预期设备恢复快 50~150ms。1 天。
5. **弹幕顶点增量更新**：`dm_gpu_build`(:924-974) 每帧对所有在飞弹幕逐字符重算顶点（`wcslen` + `dm_gpu_glyph` 哈希），120Hz 下 100 条×20 字 ≈ 24 万次/秒。改成仅"新出生 + 位置变化"增量更新，顶部/底部固定弹幕完全不重建。预期省 0.5~2ms/帧。1.5 天。
6. **HDR 路径去掉格式巧合**：`dsh_create_sws` 输出 4Bpp，但 `rgba` 按 `AV_PIX_FMT_BGRA` 分配(:2202)、`videoTex` 却是 `R10G10B10A2`(:464)，靠"都是 4 字节/像素"工作。改成显式 10bit 格式，避免将来改 format 时静默错色。1 天。
7. **A/V 同步显式策略 + 统计**：丢帧目前是 `:2609-2613` while 循环的副作用，既无计数也无"超前等待/落后丢帧"判断。加 `g_stats{dropped, duplicated, avDiffMs}` 并导出，供调试面板显示。0.5 天。
8. **`DshEngine_SetDanmaku` 同步测宽**：`:1813-1825` 对每条弹幕的每个字符调 `dm_measure_w`→`FT_Load_Glyph`，5000 条≈5 万次，全在 UI 线程；改成后台算宽或延迟到首次光栅化。0.5 天。

## 4. 推荐实施顺序（Top 10）

| 编号 | 标题 | 收益 | 成本 | 风险 | 依赖 |
|---|---|---|---|---|---|
| 1 | P0-1 补 `DshEngine_GpuDead` + 版本协商 | 恢复显卡故障提示；杜绝重编丢接口 | 0.5 天 | 低 | 无 |
| 2 | P0-2 背压判据改 `qCap()` | 4K/2K 从 CPU 打满→正常；seek 从秒级→毫秒级 | 0.5 天 | 低 | 无 |
| 3 | P0-3 请求单槽改小环 / load 优先级 | 消除换片偶发黑屏 | 0.5 天 | 中 | 无 |
| 4 | P1-4 缩短 `g_mtx` 持有 | 换片/seek 不再冻结 UI | 1 天 | 中 | 无 |
| 5 | P1-8 + P2-9 错误上报 | 崩溃/打不开→可读提示 | 1 天 | 低 | 无 |
| 6 | P1-5 slab 池去拷贝 | 1080p60 省 ~1GB/s 带宽 + 全部分配开销 | 2 天 | 中 | 无 |
| 7 | P1-7 D3D 资源单线程化 | 消除"换片 + 设备丢失"崩溃 | 1 天 | 中 | 4 |
| 8 | P1-6 preview 只出一帧 | 拖拽跟手 | 1 天 | 低 | 2 |
| 9 | P2-10 缓存后缓冲 RTV | 省 0.2~1ms/帧 | 0.5 天 | 低 | 无 |
| 10 | 硬解直出 GPU 纹理 | 4K CPU 降 60%+ | 3 天 | 高 | 6 |

## 5. 审计覆盖与未覆盖

**已读**：`dsh_engine.cpp` 全部 2874 行（分段逐段读完，含 `extern "C"` 大块内部全部导出定义）；`VideoWindow.xaml.cs` 的 38 条 `DshEngine_*` DllImport 全表（:22-109）与 Init/Present/UpdateDanmaku/Seek/换片/GpuDead 轮询几处关键调用点（约 800 行）；`audio_out.h` 仅看线程创建(:152)、`mediaUs()`(:446)、`pendingFrames()`(:473)、`tempoNow()`(:482)、暂停(:918)、独占协商(:790-885)等**接口层**；两份 DLL 的 PE 导出表（自写解析，已核对 36/41 个符号）；`PLAY_*.bat`；`Backend.cs`（确认无引擎引用，设置不在其中）。

**未读**：`audio_out.h` 的 atempo 图实现与 WASAPI 细节（1047 行，音频内部推断属其他子报告范围）；`wasapi_sink.h/.cpp`、`danmaku_gpu.h`(28KB)、`vulkan_video.h`(44KB)、`hdr_convert.h`、`video_shaders.h`；`WinUIGui` 其余 15 个 `.cs`；Python 后端；`CHANGELOG.md` 与 `PROJECT_STATUS.md`（按要求不采信旧文档，只以代码为准）。

**未做**：未启动播放器/后端，未采集性能样本。因此 P1-5 的带宽、P2-10 的毫秒、P1-4 的冻结时长、`g_audio.openFile` 的耗时均为**推算**，标注"需实测"。HDR 直通、Vulkan 互操作、RTX VSR 三条支路只做静态审查，未 A/B。建议 Lead 把 P0-2 的验证排第一位——它有现成日志（`[性能]`/`seek->show`）可直接证伪。
