# Lead 独立复核记录（播放器优化方案素材）

> 审计时间：2026-10-30（Asia/Shanghai）
> 审计基线：`F:\DSH\LocalMediaPlayer-Workspace` 工作区源码
> - 引擎 `app\dsh_engine\dsh_engine.cpp`（2874 行，2026-10-23 18:10 修改）、`vulkan_video.h`（672 行）、`audio_out.h`（1047 行）、`danmaku_gpu.h`、`danmaku_lanes.*`、`hdr_convert.h`
> - 产物：`dsh_engine_vsr.dll`（10-23 18:10，5.24MB）；`dsh_engine.dll` 为 10-18 旧产物
> - 前端 `app\WinUIGui\VideoWindow.xaml.cs`（6018 行）
> - 后端 `app\backend\app\stdlib_server.py` + `services.py`（3631 行）
> 本文件是 Lead 亲自逐行复核的**一级证据**，供汇总《播放器优化方案》使用；与 7 路并行子审计（01~07）互为交叉验证。

---

## A. 引擎核心 / 线程 / 生命周期

**A-1【P1】`LoadMedia()` 全程持有 `g_mtx`，会阻塞 UI 线程的 `Present()`**
- 证据：`dsh_engine.cpp:2033-2034`（`LoadMedia` 进入即 `std::lock_guard<std::mutex> lk(g_mtx)`）；`2042 avcodec_find_decoder`、`2043 avformat_open_input`、`2055 avcodec_open2`、`2063-2074 g_audio.openFile()`（音频侧再开一次 demuxer + 探测流 + 等设备初始化最多 4 秒，见 `audio_out.h:155` 的 `400×10ms` 轮询）都在锁内。
- 同锁竞争方：`DshEngine_Present()` 每渲染帧在 `dsh_engine.cpp:2587`、`2607` 取 `g_mtx`；`DoSeek2()`（`:2140`）也全程持锁做 `avformat_seek_file` + `audio.flush`。
- 影响：打开大文件/网络盘/损坏文件时 UI 线程直接卡住（WinUI 窗口"未响应"），至少丢掉若干帧；seek 时同理。
- 方案：把"打开/探测"移出 `g_mtx`（本地临时对象 hold 资源，成功后一次性 swap 进全局，用 generation 号丢弃过期请求）；`Present` 用 `std::try_lock` 或把帧队列换成无锁 SPSC 队列；`DoSeek2` 的 avformat 调用改为在解码线程执行（本来就是解码线程在执行！只是锁粒度太大，应缩小到"交换资源指针"那几行）。

**A-2【P1】打开媒体失败无任何用户可见反馈**
- 证据：`DshEngine_OpenMedia()` 永远 `return 0`（`dsh_engine.cpp:1506-1529`，只发请求给解码线程）；`LoadMedia()` 每个失败分支只 `g.mediaOpen=false; g_mediaLoaded.store(false); return;`（`:2038/:2039/:2041/:2044/:2056`），**不写日志、不记错误码、不通知 UI**。
- 影响：文件损坏/无视频流/解码器缺失 → 用户只看到黑屏 + 进度条 0，日志也查不到原因（历史多次靠猜）。
- 方案：增加导出 `DshEngine_GetLastError(char*,int)`（结构化错误码 + 中文原因 + 文件路径），`LoadMedia` 每个失败点写 `dsh_log` 并 `setError(...)`；前端在打开后 1.5s 内轮询一次，失败弹提示。

**A-3【P1】重复 `Init` 时"故意不释放"D3D 设备与交换链 → 每次开/关视频窗泄漏**
- 证据：`dsh_engine.cpp:1442-1456` 注释明确"设备/交换链不销毁，留给系统回收"；实际只 `Release` 了 `g.panel`。
- 影响：同一进程反复打开视频窗（多视频窗口模式、主界面预览等）会累积 D3D11 设备/交换链/纹理（数十 MB 级）；也解释了"Init 42 次"事故为何会压垮驱动。
- 方案：把引擎对象改成**一次 Init 全程复用**（当前已接近），但在 `VideoWindow.Closed` 里改成"只 Shutdown + 记住句柄"；根治方案是给 `ISwapChainPanelNative` 绑定的新面板做引用计数配对（新面板 SetSwapChain 后再释放旧交换链）。至少：加泄漏计数日志（设备/交换链创建次数 vs 释放次数）。

**A-4【P1】帧队列内存/分配策略仍偏"大方"**
- 证据：`QMAX=16`（`:253`）、`qCap()` 预算 192MB（`:315-324`）——4K BGRA 每帧 33MB → 只留 5 帧（≈83ms 缓冲，4K 60fps 下抖动余量很小）；1080p 留 16 帧（133MB）。
- 每帧分配：`DecodeAhead()` 里 `std::vector<uint8_t> buf((size_t)w*h*4)`（`:2273`）**每帧重新分配**（1080p 8.3MB / 4K 33MB），随后 `qPopOldest()` 把 vector move 出去、Present 里再 `g.lastFrame = std::move(cur)` → 每帧至少 1 次 33MB 级分配 + 释放；4K60 = 2GB/s 级分配器压力。
- 方案：①框缓冲池（回收 popped 的 vector 重复用，`buf` 复用同一池）；②4K 下把 QMAX 与 `qCap` 改成"按时间预算"（如 ≥200ms 缓冲，但按 CPU 预算封顶），并给出 `DSH_FRAME_QUEUE_MB` 可调。

**A-5【P1】每帧 CPU 侧 3~4 次全画幅拷贝 / 无零拷贝路径**
- 证据：`DecodeAhead()`：`sws_scale()` 写入 `rgba`（1 次写）→ 逐行 `memcpy` 到 `buf`（1 次读+1 次写，`:2273-2274`）→ 入队；`Present()` Vulkan 路径 `g.vk.uploadFrame()` 内部再 `memcpy` 到 staging（`vulkan_video.h:354`，1 次读+1 次写）。4K 每帧约 33MB×4 次内存流量 ≈ 8GB/s@60fps，纯 CPU 拷贝。
- 方案（收益最大）：①`sws_scale` 直接写进预分配/持久映射的目标（跳过 rgba→buf）；②Vulkan 路径直接在 `stagePtr` 上做 sws（`sws_scale` 支持任意 dst 指针 + stride），彻底省掉 2 次拷贝；③长期：YUV 直接上传 + shader 做 chroma upsampling/矩阵（见 D 段）。

**A-6【P2】日志无上限、无级别、每行 `fopen/fclose`**
- 证据：`dsh_log()` 每次调用 `fopen(path,"a")…fclose`（`:67-74`）；安装版 `dsh_engine.log` 已 4.3MB、前端 `dm_debug.log` 6.3MB，无轮转。
- 影响：长期使用日志无限增长；每条日志一次文件打开，热点路径（seek/欠载/看门狗）有真实开销。
- 方案：常驻文件句柄 + 分级（INFO/WARN/ERR）+ 单文件 8MB 轮转保留 3 份 + `DSH_LOG_LEVEL`；详细轨迹日志默认关。

**A-7【P2】无 `GetAudioInfo`/`GpuDead` 之外的运行状态查询，诊断全靠日志**
- 证据：导出表（见附录）里没有"当前帧耗时/FPS/丢帧数/队深/欠载/解码线程数/是否硬解"等结构化查询，`DshEngine_DumpFrameBmp` 是唯一自检口。
- 方案：加 `DshEngine_GetStats(DshStats*)`（fps、呈现帧耗时 p50/p99、丢帧数、队深、欠载数、A/V 偏差、解码 fps、GPU 路径），前端设置页显示"播放统计"面板——这会直接替代现在"看日志猜"的方式。

---

## B. 视频呈现 / GPU 管线

**B-1【P0/P1】Vulkan 路径每帧 2 次"提交 + 等 fence"全同步（历史 A2-2 仍未做）**
- 证据：`vulkan_video.h:490-502` `oneShot()` 每次 `vkQueueSubmit` + `vkWaitForFences(…,5s)`；`uploadFrame()` 调一次（`:356`）、`render()` 再调一次（`:381`）→ **每帧 2 次 CPU-GPU 全同步**，命令缓冲/栅栏全局复用（无 in-flight 帧）。
- 现状数据：1080p 上传+渲染均 0.75ms、开画质滤镜 +0.3~0.4ms（历史实测）；RTX VSR 开时 4.8~7.2ms/帧。
- 影响：CPU 等待浪费、GPU 队列空转、4K/高刷下帧时间抖动放大；与"Present 在 UI 线程"叠加后直接变成掉帧。
- 方案：①把 upload+render 合成**一个** command buffer（同一次提交）；②3~4 帧 in-flight（每帧自己的 cb/fence/staging 环），Present 只等"上一帧"的 fence；③跨 API 同步改用 `VK_KHR_external_semaphore_win32` + D3D11 `ID3D11Fence` 共享句柄（历史 A2-2 方案，预期 0.75ms → ~0.3ms）；④必须保留回退开关 `DSH_VK_SYNC=legacy`。

**B-2【P1】Vulkan 渲染结果要整帧 `CopyResource` 回 D3D11，再叠弹幕、再 Present**
- 证据：`draw_frame()` `:661-666`：`e.ctx->CopyResource(bb, e.vkTargetTex)`（窗口大小整帧 GPU 拷贝，1440p≈14.7MB、4K≈33MB/帧）；之后 `:697-703` 再用 D3D11 画弹幕。
- 影响：4K60 下约 2GB/s 额外显存带宽 + 一次全屏合成；Vulkan 与 D3D11 双渲染器状态都要维护（bug 面大）。
- 方案：①短期把弹幕也交给 Vulkan 合成（弹幕图集/字形四边形已是独立模块，纹理可共享），D3D11 只做 `Present`；②中期评估"Vulkan 直接渲染进 D3D11 交换链后缓冲"（DXGI flip 后缓冲共享 + `VK_KHR_external_memory_win32`，可省整帧拷贝）；③或把画质 shader 移植到 D3D11（HLSL），默认回 D3D11 单 API，Vulkan 作为可选（需先证明画质 shader 在 HLSL 下逐字节一致）。

**B-3【P2】letterbox 区域依赖"未定义内容"**
- 证据：`vulkan_video.h:386` `loadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE` + 只画视频四边形；`renderArea` 是全窗口，黑边区域**从不被写**；D3D11 侧 `CopyResource` 把整帧（含黑边）覆盖到后缓冲（`dsh_engine.cpp:665`）。
- 现在"看起来正常"因为显存初值 0；窗口缩放/HDR 切换/设备重建后是否仍为 0 无保证。
- 方案：`loadOp=CLEAR` + `clearValue={0,0,0,1}`（代价≈0），或渲染前 clear 一次；并由 D3D11 侧不要再"先 clear 再被 CopyResource 全盖"。

**B-4【P2】呈现无显式帧节奏/延迟控制**
- 证据：`dsh_create_swap()`（`:1335-1373`）用 `BufferCount=2`、`DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL`、未设 `SetMaximumFrameLatency`、未创建 `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`；`Present(0,0)`（`:2666`）无 vsync 参数；呈现节奏完全依赖 WinUI `CompositionTarget.Rendering`（`VideoWindow.xaml.cs:3076`）。
- 影响：延迟与抖动不可控；窗口被遮挡时回调会停（前端有 2s 看门狗只记日志，不恢复，`:3104-3117`）。
- 方案：①建链加 `FRAME_LATENCY_WAITABLE_OBJECT` + `SetMaximumFrameLatency(2)`，Present 前 `WaitForSingleObjectEx`（仅 D3D11 路径，需实测）；②CompositionTarget.Rendering 停摆时**自动降级**为 `DispatcherQueueTimer(16ms)` 驱动 Present（当前只告警）；③加 `DSH_TEST_PRESENT_STALL` 自测钩子。

**B-5【P1】RTX VSR 在 Vulkan 路径是"GPU→CPU 回读→再上传"**
- 证据：`dsh_engine.cpp:2627-2632`（`vsr_process_cpu` 把 VSR 输出回读到 CPU，再 `g.vk.uploadFrame`）；VSR 自身是 D3D11 纹理（`vsr_process_cpu` `:570-593`）。历史实测 4.8~7.2ms/帧。
- 影响：4K 下回读+上传 ≈ 66MB/帧带宽，是 VSR 路径的主要开销；也限制 VSR 与 HDR/画质滤镜组合。
- 方案：VSR 输出纹理用 `D3D11_RESOURCE_MISC_SHARED_NTHANDLE` 建共享，Vulkan 直接 import 成 sampled image（和现在 vkTargetTex 同一套 interop 代码），省掉回读；预期 7.2ms → 2~3ms。

**B-6【P2】Vulkan 诊断走 `printf`/stdout，WinUI 应用里等于丢失**
- 证据：`vulkan_video.h:191/195/276/309/514` 等 `printf` + `fflush(stdout)`；WinUI 无控制台。
- 方案：改成回调进 `dsh_log`（`DSH_VK_LOG=1` 才详细），`[vk]` 前缀，便于 `diag.ps1` 收集。

**B-7【P2】staging 缓冲固定 3840×2160×4**
- 证据：`vulkan_video.h:228-240/606`；`uploadFrame` 超尺寸直接失败（`:353`）。
- 影响：8K/竖屏超宽（如 3840×2400）会走 D3D11 回退（丢画质滤镜），且固定映射 33MB 常驻。
- 方案：按实际 `w*h*4` 动态分配（上限 + 日志），或按"最大已见分辨率"增长。

---

## C. 解码 / 格式 / HDR 检测

**C-1【P1】当前线完全没有硬解路径（软解 16 线程 + sws）**
- 证据：`LoadMedia()` 只 `avcodec_find_decoder`（软解，`:2043`），显式 `thread_count=min(hw,16)`（`:2049-2054`）；无 `AVHWDeviceType`/`get_format` 回调。
- 现状数据：4K HEVC 软解 ~140fps（够 60fps，但 8K/高码率/低端 CPU 不够）；历史"硬解更慢"结论来自 **D3D11VA 解码后 CPU 回读**（那当然慢），不代表"硬解+零拷贝"。
- 方案：新增可选硬解路径（`DSH_HWACCEL=d3d11va|none`）：D3D11VA 输出 NV12/P010 纹理 → 共享句柄 → Vulkan 导入 → shader 做 NV12/P010 转换；SDR/HDR 均可。失败/不支持时保持软解（默认软解，避免历史坑）。预期 4K CPU 占用下降 50%+。

**C-2【P1】HLG HDR 完全未处理**
- 证据：`hdr_convert.h:22-23` 注释明确"HLG(arib-std-b67) 暂不按 HDR 处理"；`dsh_detect_hdr()` 只认 `AVCOL_TRC_SMPTE2084`（`:37`）。
- 影响：iPhone/相机/部分流媒体 HLG 视频 → 当 SDR 显示（发灰、过曝、BT.2020 色域未转换）。
- 方案：HLG 走 OOTF（`HLG EOTF+OOTF`，近似：`linear = pow(HLG_inv(x), 1.2)`，BT.2100 标准公式），再进入现有 BT.2390 链路；`hdrInfo` 增加 `curve=PQ|HLG`；`test_hdr_offscreen` 加 HLG 测试向量（可用 ffmpeg 生成 `-color_trc arib-std-b67` 合成片）。

**C-3【P2】DoVi（杜比视界）只按基础层 HDR10 处理，profile 5 会明显偏色**
- 证据：无 DoVi 解析代码（grep 无 `dovi/rpu`）；PQ 检测即当 HDR10。工作区有 `dv_*.mp4` 测试片。
- 影响：Profile 8.1（B 站"杜比视界"档常见）基础层 = HDR10，**显示基本正确**；Profile 5（IPTPQc2）→ 颜色错（青/紫）。
- 方案：①检测 `dovi` side data/`dvcC/dvvC` box → 日志明确"DV profile N"，profile 5 时给出提示并可用"按 SDR/按 HDR10"手动切；②长期接 libdovi（Rust）或 FFmpeg dovi_rpu 解 RPU 做动态映射（成本高，排在后面）。

**C-4【P2】SDR 10bit 内容被 sws 降到 8bit，无抖动的 10→8 转换**
- 证据：`hdr_convert.h:24-44`：只有 PQ 才用 `X2BGR10LE`（10bit）；其它一律 `AV_PIX_FMT_BGRA`（8bit），`dsh_create_sws()` 用 `SWS_BILINEAR`（`:49`）。
- 影响：10bit SDR（动漫/电视剧 10bit HEVC）在暗部渐变换档处出现色带；sws 转换时无 dither。
- 方案：非 PQ 的 >8bit 源也走 10bit 纹理（`X2BGR10LE`），shader 输出端再做 8bit 抖动（现有 quality shader 的 Bayer 抖动可复用）；或至少给 sws 加 dither 选项。

**C-5【P2】`sws_scale` 用 `SWS_BILINEAR` 做 YUV→RGB 与 chroma 上采样**
- 证据：`hdr_convert.h:49`；`LoadMedia` 兜底再建一次（`:2084`）。
- 影响：色度上采样质量一般（细红线/彩边）；缩放在 shader 做、色度不参与。
- 方案：短期 `SWS_BILINEAR→SWS_ACCURATE_RND|SWS_FULL_CHR_H_INT`（若可用）；中期 YUV 直传 shader（chroma 用 Catmull-Rom/Jinc，luma 用现有缩放核），这是画质提升最明显的一步。

---

## D. 画质管线（着色器逐行复核）

**D-1【P1】缩放只有 4×4 "展宽 Catmull-Rom"，大倍率缩小会欠采样**
- 证据：`shaders/video_blit_quality.frag:69-87`：4 taps × 4 taps、`off = (i-1.5)*max(step,1)*texel` 把核展宽，权重仍用固定 CR 权重；`step>1` 时才启用，`step<=1.0001` 直接硬件线性。
- 问题：①4K→1080p 时 step=2，4 个 tap 间隔 2 texel，等效只采 4 个源点/轴 → 高频混叠（zoneplate 可测）；②CR 负瓣在展宽后未做核积分/归一（有 `wsum` 归一但核形不对）→ 振铃/边缘过冲；③放大路径完全不用滤镜（硬件 bilinear）——低清源放大画质弱。
- 方案：①缩小：8~16 tap 可分离 Lanczos/Box 积分核（每个输出像素按 footprint 积分，等价 madVR/mpv 的 scale filter），或两 pass；②放大：加一档 Catmull-Rom/FSR-EASU 风格放大（可选），或至少 `VK_EXT_filter_cubic`（若驱动支持）；③加 zoneplate/多频测试与量化指标（工作区已有 `zoneplate_1080p.mp4`、`measure_banding.py`，应做成回归脚本）。
- 验收：zoneplate 频谱混叠能量下降 ≥6dB，4K→1080p 与 ffmpeg lanczos 参考 RMS 下降；每帧成本 ≤ +1ms（4K）。

**D-2【P1】deband 是"3×3 均值平滑"，不是真正的去色带；抖动是静态 Bayer**
- 证据：`video_blit_quality.frag:90-105`（局部 range<3/255 时向 3×3 均值混 85%）；`:126-128` Bayer 4×4 静态抖动（`q.w` 相位"保留"未用）。
- 问题：①均值平滑不增加位深，只是把台阶变模糊（细节区也会被糊：range 判据用 RGB 最大通道差，细纹理<3/255 的会被平滑）；②静态 Bayer 是固定图案（不随时间变化），去 banding 能力弱于时域抖动，且可能产生"十字纹"观感；③先缩放后 deband，banding 已被缩放放大。
- 方案：①改"多尺度范围滤波 + 小幅（≤1/255）时域抖动/蓝噪声"（libplacebo deband 思路：`diff=|y-mid1|+|y-mid2|-|mid1-mid2|` 判据，历史老线已验证有效）；②抖动改成"Bayer + 帧相位偏移"或蓝噪声纹理（`q.w` 已有位置）；③顺序：deband 在**缩放前**的源分辨率做（成本低、判据更准）。

**D-3【P2】色彩管理缺位：无 3D LUT/ICC、无显示色彩空间感知**
- 证据：当前线 grep 无 `LUT/lcms/ICC`；输出固定假定 sRGB（`srgbEnc`），BT.2020→709 后直接输出；交换链色彩空间固定 `RGB_FULL_G22_NONE_P709`（`dsh_engine.cpp:1352`）。
- 影响：广色域显示器（P3/AdobeRGB）上颜色偏饱和/不匹配；无法做显示器校色；HDR 直通正常但 SDR 无色彩管理。
- 方案：①quality shader 增加 3D LUT（33³，四面体插值）binding，C++ 侧加载 `.cube`；②lcms2 工具复用老线 `icc_to_lut.cpp`/`icc_calibrate.cpp`（`_legacy-player` 里有，已验证过）；③设置页加"显示器 ICC 校色"入口 + 自动读取系统 `GetICMProfile` 的显示器 profile；④输出色彩空间按显示器能力选（P3 显示器用 `RGB_FULL_G22_NONE_P3` 或 10bit sRGB）。

**D-4【P2】HDR→SDR 简化点**
- 证据：`video_blit.frag:53-85`：白场=MaxCLL（或 1000），无 mastering display 亮度、无 HLG、无 HDR10+ 动态元数据；`minNits=0`；boost 在归一化之后乘（`:78`）。
- 方案：①读 mastering display（`AV_MASTERING_DISPLAY_METADATA`）的 max_luminance 作为白场候选；②boost 改成在 tone-map 前对亮度做（或提供 `tone-mapping-max-boost` 语义）；③加"HDR→SDR 参考帧 A/B"回归（现有 `test_hdr_offscreen`）。

**D-5【P2】两条渲染路径的画质能力不对等**
- 证据：D3D11 路径只有 HDR 着色器（`video_shaders.h`）+ 普通采样；缩放/deband/抖动只在 Vulkan quality shader。Vulkan 初始化失败/尺寸不匹配/8K staging 超限时自动回退 D3D11（`dsh_engine.cpp:2638-2651`）→ 画质静默降级。
- 方案：①把 quality 逻辑做成 HLSL 版给 D3D11（至少 deband+dither+缩放）；②回退时在 UI 状态栏提示"画质滤镜不可用（已回退）"；③RTX VSR 也是 D3D11 路径（`draw_frame` 里），两条路的能力矩阵应统一。

---

## E. 音频链路（`audio_out.h` 逐段复核）

**E-1【P1】5.1/多声道源会丢中置/环绕（naive 通道映射）**
- 证据：`audio_out.h:709-714`：`if (ch != outCh) fix[i*outCh+c] = inter[i*ch + (c<ch?c:ch-1)]` → 5.1(6ch)→2ch 只取 FL/FR 两个声道，**中置人声/环绕全丢**。
- 且 `buildResampler()` 用 `av_channel_layout_default(&lay,outCh)` 作为**输入**布局（`:661-664`），忽略了源真实布局 → swr 只做采样率转换，不做下混。
- 影响：播放 5.1 电影/演唱会时人声变小甚至消失（很典型）。
- 方案：用源 `cc->ch_layout` 作为 in layout、目标 `outCh` 作为 out layout 调 `swr_alloc_set_opts2`，让 swr 做标准下混（可指定 LFE 剔除/环绕衰减，支持 `rematrix_maxval`/custom matrix）；`outCh==srcCh` 时保持直通。

**E-2【P1】非立体声时变速无效，音画会脱节**
- 证据：`audio_out.h:511` `if (outCh != 2 || srcRate <= 0) return false;`（atempo 只对立体声建图）；但 `DshEngine_SetSpeed` 无条件把视频主时钟乘倍速（`dsh_engine.cpp:2812-2827`）→ 5.1 内容切 2x 时视频 2x、音频 1x。
- 方案：①支持多声道 atempo（`graphEnsure` 的 `channel_layout=stereo` 改成按 outCh 生成对应 layout 字符串；atempo 本身与布局无关，只是 abuffer 参数问题）；②失败时 UI 禁用倍速并提示，或把视频也锁 1x（保持一致）。

**E-3【P1】音频渲染循环用 `sleep_for(2ms)` 轮询，实际节拍 ~15.6ms**
- 证据：`audio_out.h:927` `std::this_thread::sleep_for(std::chrono::milliseconds(2))`；同文件 `:369-372` 注释已明确"Windows 上 sleep(2ms) 实际约 15.6ms"。共享模式缓冲 ≈10ms（`:879-881` 注释）→ 供给节拍 > 缓冲时长，靠 1.6s 环形缓冲兜（`:893-894`）；但任何一次解码/持锁抖动都会造成欠载。
- 方案：①用事件驱动 + `WaitForSingleObject(ev, 2)`（独占模式已建事件句柄 `:821`，共享模式可用 `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`）；②或 `CreateWaitableTimerEx(HIGH_RESOLUTION)` / `timeBeginPeriod(1)`；③把"解码"从渲染线程拆出去（历史上失败过一次，但那是另一个 bug：见 `:153` 注释；现在有 1.6s 环，可安全重试）。

**E-4【P2】音频 EOS 后持续"欠载计数 + 喂静音"**
- 证据：`audio_out.h:392` `decodeOnePacket` 在 `r<0` 时置 `eof=true`；`:969-981` 每轮 `got < freeN` 就 `underruns++` 并补零；循环没有"音频已结束"分支。
- 影响：片尾音频结束后欠载计数暴涨（日志误导），CPU 继续空转；对本已在排查欠载的人很致命。
- 方案：加 `audioEnded` 状态，EOF 后只补静音但**不计欠载**、不写日志，并通知引擎"音频已结束"（A/V 时钟切纯墙钟）。

**E-5【P2】每包 malloc/AVFrame alloc；环形缓冲逐样本拷贝**
- 证据：`audio_out.h:342-343` 每个包 `av_packet_alloc()+av_frame_alloc()`；`:271-275` `ringPush` 逐样本循环；`:306-309` `ringPop` 逐样本循环。
- 方案：复用 `AVPacket/AVFrame`（按流复用一个）；`ringPush/ringPop` 在"连续区间不跨环尾"时 `memcpy`，跨尾分两段。

**E-6【P2】没有音频设备变化/拔插监听**
- 证据：全文无 `IMMNotificationClient`；`renderMain()` 一次初始化后一直用同一个 `IAudioClient`（`:744-1046`）。
- 影响：切换默认设备（蓝牙/HDMI 插拔/独占被抢）后音频静默或继续写死设备，UI 无提示。
- 方案：注册 `IMMNotificationClient`（OnDefaultDeviceChanged/OnDeviceRemoved/OnDeviceStateChanged）→ 置"重开设备"标志，渲染线程重建 client 并保持 playUs 连续（重新锚定）。

**E-7【P2】共享模式未尝试 IAudioClient3 低延迟/RAW 模式**
- 证据：共享只 `IAudioClient::Initialize(SHARED,0,0,0,mix)`（`:881`）。
- 方案：优先 `IAudioClient3::InitializeSharedAudioStream`（Win10+，可申请最小周期），失败回退 `IAudioClient`；设置里给"低延迟/兼容"两档。

**E-8【已做对，不要改】** 独占协商（32f→32i→24i→16i + IsFormatSupported + 事件句柄 + 失败不再静默，`:805-877`）；swr 带限重采样（`:653-684`）；音量/静音斜坡与欠载淡出（`:969-998`）；媒体时钟 FIFO 记账（`:267-337`，解决切倍速累积误差）。这些是硬功夫，优化时不要动语义。

---

## F. 前端（`VideoWindow.xaml.cs`，只复核与播放直接相关部分）

**F-1【P1】每渲染帧都做 C# 侧弹幕布局 + 数组分配 + 2 次 P/Invoke**
- 证据：`OnRenderFrame()`（`:3096-3102`）每帧 `UpdateDanmaku()` → `GetDanmakuLayout(null,null,null,0)`（`:4961`）、构造 `texts/xs/ys/colors` 数组（`:4993`）→ `DshEngine_SetDanmaku`/`DrawDanmaku`。120Hz 屏 = 每秒 120 次；数组/字符串 marshal 产生 GC 压力。
- 方案：复用固定大小数组 + `GCHandle`/`Span` 固定；或把弹幕布局完全下沉到 C++（现在其实已经是 C++ 算轨道，C# 只转数据），改成"引擎内部 tick 一次拿 GPU 四边形，C# 只调 `Present`"。

**F-2【P1】`CompositionTarget.Rendering` 停摆只告警不恢复**
- 证据：`:3104-3117` 看门狗只 `DmLog`；没有切换到定时器驱动。
- 方案：见 B-4，加自动恢复（Rendering 停 >2s → 启动 16ms `DispatcherQueueTimer` 驱动 Present；恢复后切回）。

**F-3【P2】`UpdateSeekUi()` 50ms 定时器 + 每 tick 读 4 个引擎查询**
- 证据：`:3079-3082`、`:2922` 附近；`UpdateSeekUi` 里 `GetPosMs/GetDurationMs` 等。
- 方案：把"进度/时间/状态"做成一次 `DshEngine_GetPlaybackState()` 调用，减少 P/Invoke 次数；UI 文案只在变化时更新。

**F-4【P1】打开媒体是"异步请求 + 无结果回执"**
- 证据：`:5430` `DshEngine_OpenMedia(path)` 后没有等待/失败分支；靠后续 tick 发现"没在播"。
- 方案：配合 A-2 的错误 API + 前端 1.5s 超时检查；打开期间显示"正在打开…"遮罩（现在只有拖拽/加载态？）。

**F-5【P2】`VideoWindow.xaml.cs` 6018 行单文件**
- 方案：按"播放控制/弹幕/字幕/增强链/设置/网络"拆 partial 类文件，先机械拆分（不改逻辑），降低后续改动风险。

---

## G. 后端（播放相关）

**G-1【P1】无"在线直链播放"能力：所有网络视频必须先整段下载**
- 证据：`stdlib_server.py` 路由表无 `/api/stream`/Range 服务；播放路径只有本地文件（`VideoWindow` 打开 `LocalPath`）；下载完成才入库。
- 影响：B 站/YouTube 想"边下边播/先看后存"做不到；短视频也要等下载完。
- 方案（用户价值高）：①后端加 `/api/stream/{id}` 支持 HTTP Range + ffmpeg 转封装（B 站 DASH 需要音视频分离，可先 ffmpeg 合成到本地缓存文件再播）；②前端"在线播放"走"边下边缓存"（下载任务 + 播放器读缓存文件 + Range 支持）；③或直接集成 libmpv 的网络能力（但用户已明确不用 mpv 渲染，可只用其 demux 能力？风险大）。

**G-2【P2】播放期所有数据都靠前端 HTTP 轮询**
- 证据：`Backend.cs` + `VideoWindow` 的 2s/800ms 轮询（`:865-962` AI 字幕、`:3079-3082` 进度）。
- 方案：加 SSE/长轮询通道（stdlib `http.server` 可做 `text/event-stream`），播放状态/任务进度推送，减少轮询。

**G-3【P2】本地 HTTP 服务的路径校验/安全边界未系统化**
- 证据：`services.media_info(path)`、`product_info(path)`、`import_local_video(path)` 等直接收前端传的绝对路径；`_safe_resolve_media_path`（`services.py:3067`）只覆盖增强产物。
- 方案：统一 `_resolve_local(path)`：只允许"库内/下载目录/数据目录"下的路径（`Path.resolve().is_relative_to`），否则拒绝并记日志；顺便防"误删/误播"。

**G-4【P2】单 `ThreadingHTTPServer` 承载下载/增强/Whisper/AI 安装等长任务**
- 证据：`stdlib_server.py` 只起一个 `ThreadingHTTPServer`；长任务用 `threading.Thread` 后台跑（下载/增强已有），但没有统一队列/并发上限。
- 方案：统一任务队列（优先级/并发上限/取消语义），与前端状态机对齐；重任务（AI 装依赖/Whisper）单独限流。

---

## H. 工程质量 / 构建部署

**H-1【P1】"当前跑的是哪一版引擎"仍然靠人肉纪律**
- 证据：`docs/工程纪律.md` 整节都在讲这个坑（`-t:Rebuild` 会清掉 bin 里的引擎、`Copy-Item` 保留时间戳、必须比 MD5）；`dsh_engine\dsh_engine.dll` 是 10-18 旧产物，与 `dsh_engine.cpp`(10-23) 不同步。
- 方案：①引擎源码里编译进 `__DATE__ __TIME__` + git describe，导出 `DshEngine_GetBuildInfo()`，前端"关于"页显示；②`deploy.ps1` 已在，但应在启动日志打印 build info，`diag.ps1` 一并收集；③CI 式一键验收脚本（本机 cron 不必）。

**H-2【P2】没有自动化回归**
- 证据：`dsh_engine` 目录里全是手工探针（vk_probe/test_dll_host/test_hdr_offscreen…），`testdata` 目录有素材；但没有一条"命令跑完出 PASS/FAIL"的回归。
- 方案：把已有探针整理成 3 组必跑：①画质（HDR→SDR PSNR≥90、HDR 直通逐字节、quality off/on 差异）②音频（THD+N、欠载 0/60s、44.1k→48k 位一致）③播放（open/seek/EOF/device-lost 注入/多声道下混）。每次改动引擎后 `deploy.ps1 -Test` 跑。

---

## I. 问题严重度初判（Lead 视角 Top 12）

| # | 问题 | 严重度 | 依据 |
|---|---|---|---|
| 1 | 5.1/多声道下混错（丢中置/环绕） | P1（音质事故） | E-1 |
| 2 | 打开媒体阻塞 UI + 失败无提示 | P1 | A-1/A-2 |
| 3 | Vulkan 每帧 2 次全同步 + 整帧 CopyResource | P1（性能/抖动） | B-1/B-2 |
| 4 | 大倍率缩小缩放核欠采样 + deband 非真 deband | P1（画质） | D-1/D-2 |
| 5 | 无色彩管理（LUT/ICC/广色域） | P1（画质能力缺口） | D-3 |
| 6 | HLG 当 SDR、DV profile5 偏色 | P1（兼容性） | C-2/C-3 |
| 7 | 无硬解路径（4K 软解吃 CPU） | P1（性能） | C-1 |
| 8 | 非立体声变速音画脱节 | P1 | E-2 |
| 9 | 音频线程 15.6ms 轮询节拍 | P1（稳定性） | E-3 |
| 10 | RTX VSR CPU 回读 | P2（性能，但用户在用） | B-5 |
| 11 | 打开失败静默 / 日志无界 / 无结构化统计 | P2（可观测性） | A-2/A-6/A-7 |
| 12 | 无在线直链播放 | P2（产品能力） | G-1 |

> 注：以上为 Lead 亲核项；子审计（01~07）会补充弹幕/后端/日志取证/渲染细节与优先级，汇总时合并去重。
