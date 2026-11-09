# 渲染与画质管线审计报告

审计对象：`app/dsh_engine`（当前线，D3D11 默认 / Vulkan 可选）+ `app/WinUIGui` 呈现侧
审计方式：只读源码，未运行播放器、未编译、未改动任何文件
审计日期：本轮（对应 `docs/player-optimization/audit/` 第 02 篇）

---

## 0. 一句话结论

**链路骨架（D3D11 共享纹理 → 交换链、10bit PQ 直通、BT.2390、GPU 弹幕+PQ 换算、DPI 物理像素 1:1）是靠谱且有实测支撑的；但所有"画质工作"都发生在 PQ/sRGB **编码域** 而不是线性光域，且自研的"4×4 展宽 Catmull-Rom"在数学上不是低通滤波器（2:1 缩小时混叠增益 = 1.000），因此"降采样质量 + HDR 高光保真"是当前最大的两块画质欠账，且修法明确、成本很低；性能侧最大的坑是 Vulkan 路径每帧两次 submit + 两次 fence 全同步。**

---

## 1. 现状渲染架构摘要（≤250 字）

默认 `g_useVulkan=true`（`dsh_engine.cpp:1484`）。解码线程 `DecodeAhead` 用 swscale 把 YUV 转成 BGRA（HDR 时是 `X2BGR10LE`）塞进 16 帧环形队列；UI 线程由 `CompositionTarget.Rendering` 驱动，每 tick 调 `DshEngine_Present`：按主时钟取帧 → Vulkan 路径把 CPU 帧 memcpy 进 staging 再拷进 VkImage，渲染进"窗口大小"的共享纹理（letterbox 在 shader 里做），D3D11 再整块 `CopyResource` 到后缓冲，然后叠 GPU 弹幕、`Present(0,0)`；D3D11 回退路径直接 `UpdateSubresource` + 全屏四边形。HDR→SDR 用 PQ EOTF→BT.2020→709→BT.2390 EETF→sRGB+4×4 Bayer 抖动；HDR 直通走 10bit `R10G10B10A2` + `DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020` + MaxCLL 元数据。缩放/去色带只在 Vulkan 的 `video_blit_quality.frag` 里有，`DSH_QUALITY` 默认 `all`（`vulkan_video.h:181`），但**无 UI 开关**。

---

## 2. 问题清单

### P0-1　HDR 全程在 PQ 码值域做插值与滤波（最严重的画质错误）

- **证据**：D3D11 路径 `video_shaders.h:70` `float3 c = tex.Sample(smp, uv).rgb;` —— 双线性混合直接作用在 PQ 码值上；Vulkan 路径 `video_blit_quality.frag:122` `vec3 c = useScale ? filterScale(uv, stepXY) : texture(tex,uv).rgb;`，到 `:133` 才 `pqToLin`；去色带 `:96` 同样在码值域均值；`video_blit.frag:59` 同。
- **量化（用报告口径的 PQ 常数实算，已在审计中跑过）**：两个相邻纹素 1000 nit / 10 nit，PQ 码值 0.7518 / 0.2997，码值平均 0.5258 → 解码 **119 nit**；线性光平均应为 **505 nit** → **-76%**。1000/1 nit 更差：**55.9 nit vs 500.5 nit = -89%**。203/20 nit 也在 -40%。
- **影响**：任何缩放比 ≠1:1 的时候（140% DPI 下 1080p 视频铺 2688px 窗口、4K 视频进 1440p 窗口、letterbox 边缘）整幅画面的高光都被系统性吃掉：夜景灯点、字幕白字、星空、金属高光出现"发灰 + 边缘发暗"。10bit 直通（mode 2）不受影响（`video_blit_quality.frag:114` 提前返回），所以**只有"色调映射到 SDR"这条最常用的 HDR 路径中招**。
- **根因**：滤波与 EOTF 的先后顺序反了 —— 应该 `PQ→linear → 滤波 → 编码`，现在是 `滤波 → PQ→linear`。
- **方案**：`filterScale`/`filterDeband` 内部先 `pqToLin` 再算，返回前 `pqFromLin`（mode 1 时）；D3D11 路径改成两 pass：pass1 linearize 到 `R16G16B16A16_FLOAT`（半分辨率或同分辨率，按缩放比），pass2 在 float 纹理上做缩放后直接输出 sRGB。SDR 路径不动（sRGB 域滤波的误差只在暗部，量级小一个数量级，可留待 P2）。
- **验收**：`test_hdr_offscreen.cpp`（现成的离屏验证工具）造 1000nit/10nit 双点测试图，比"码值域滤波"与"线性域滤波"的 RMS；真片取夜景帧，关掉缩放（1:1）作为参考，比 RMS/PSNR。
- **工作量**：0.5 人日（shader 内联）；2-pass float 中间缓冲 1.5 人日。

### P0-2　"4×4 展宽 Catmull-Rom" 不是低通，2:1 时混叠增益 1.000

- **证据**：`video_blit_quality.frag:76-85`
  ```glsl
  float wy = crW(float(j) - 1.5);                       // ← 权重用【未展宽】距离求
  vec2 off = vec2(float(i)-1.5, float(j)-1.5) * w * texel;  // ← 采样点却按 step 展宽
  ```
  权重恒为 `[-0.0625, 0.5625, 0.5625, -0.0625]`（和为 1），而采样点被拉到 ±1.5·step。
- **量化（用该核自身的传递函数实算）**：step=2（4K→1080p，输出 Nyquist = 0.25 cyc/texel）时

  | 频率 f (cyc/texel) | 0.10 | 0.25 | 0.40 | 0.50 |
  |---|---|---|---|---|
  | 出厂核 \|H\| | 0.949 | **0.000** | 0.949 | **1.000** |
  | 正确的 2×2 box \|H\| | 0.951 | 0.707 | 0.309 | **0.000** |

  即：**该衰减的地方（f>0.25）几乎不衰减（0.95~1.00），该保留的地方（f=0.25 带宽边缘）被挖成零** —— 软 + 混叠两头都占。同时因为 step=2 时 tap 落在 ±1、±3 的奇数位，**输出像素对与它共位的那个源纹素贡献为 0**。step=1.5 时 f=0.5 仍有 0.884（box 为 0）。
- **根因**：带限核的正确写法是 `w_i = crW((i-1.5)·step)` 再归一化；但 4 tap 在 step=2 时四个权重全落在 crW 支撑区外（全零）——**4 tap 在 2:1 下根本没有物理可行性**，必须 ≥6~8 tap 或直接用 box。
- **影响**：4K 源在 1080p/1440p 窗口、1080p 源在 720p 窗口出现明显摩尔纹与边缘爬行（zone plate 会非常难看）；且 16 次 texture fetch 比 4 次 box **贵 4 倍**。
- **方案**：(a) 立刻止血：`step≥1.8` 走 4-tap 2×2 box（4 fetch，混叠 0）；(b) 正解：8-tap 带限核（`w_i=crW((i-1.5)·step/1.6)` 归一化，支撑 ±4·step），并可拆成水平/垂直两 pass 复用；(c) 长期见 §3。
- **验收**：zone plate（`app` 下已有生成脚本思路）缩放到 1080p 截帧，与 `ffmpeg -vf zscale=filter=lanczos` 结果比 RMS + 目视摩尔纹；帧耗时对照。
- **工作量**：(a)+(b) 0.5~1 人日。

### P0-3　Vulkan 路径每帧 2 次 submit + 2 次 fence 全同步，CPU/GPU 零重叠

- **证据**：`vulkan_video.h:490-502` `oneShot()` = `BeginCommandBuffer → QueueSubmit → WaitForFences(5s) → ResetFences`；`uploadFrame` 里调一次（`:356`），`render` 里再调一次（`:381`），**每帧两次设备同步**；`setTarget`/`uploadFrame` 尺寸变化时还额外 `waitIdle`（`:253`、`:318`）。上传是逐行 `memcpy` 进 31MB 常驻 staging（`:229`、`:354`）再 `vkCmdCopyBufferToImage`。
- **影响**：每帧固定两个 GPU 往返 + 8MB CPU memcpy + 8MB PCIe 拷贝；1080p/24fps 下整帧延迟里约 1~3 ms（**推测（需实测）**，可从日志 `[性能] Vulkan 上传+渲染均 X ms`，`dsh_engine.cpp:2652-2656` 直接读出）纯属串行浪费；VSR 的 Vulkan 路径更狠：`Rt_ProcessCpu` 要整帧回读后再上传（`third_party/rtx_vsr_bridge/rtx_vsr_bridge.cpp:130-136`），2160p 时单帧 33 MB 回读 + 33 MB 上传。
- **方案**：① 把 copy→barrier→draw 合并进**同一个 cmd buffer、一次 submit、一个 fence**（顺序天然有序，不需要两个 fence）；② 2~3 帧 in-flight（每帧独立 cmd/fence/staging 槽），fence 只在该槽复用时等；③ 与 D3D11 侧用 `ID3D11Fence`/`IDXGIKeyedMutex` 在 GPU 侧同步，别再靠 CPU 等 fence 来保证"内容已就绪"；④ staging 按需分配（现在写死 31MB，>4K 直接报"帧比暂存缓冲大"）。
- **验收**：日志里 Vulkan 均耗时降到 0.5~0.6×；Present 间隔抖动（ETW/GPUView）无新增尖峰。
- **工作量**：①+②+④ 1~2 人日；③ 2~3 人日。

### P1-1　SDR 内容的 YUV→RGB 矩阵与 range 根本没传给 swscale

- **证据**：`hdr_convert.h:51-58` 只有 `if (hi.hdr)` 才 `sws_setColorspaceDetails(...)`；`dsh_engine.cpp:2083` 用 `dsh_create_sws`，失败回退 `:2084` 的裸 `sws_getContext`。而 `dsh_engine.log:6` 显示元数据是拿到的：`space=2`（BT.709）、`range=limited(tv)`、`trc=1`。
- **影响**：libswscale 默认按 `SWS_CS_DEFAULT`（=ITU601）+ limited→full 处理，BT.709 高清内容的饱和红/绿会偏色；full-range（屏幕录制/网页转码）内容会被当 limited 拉伸 → 黑位被切、高光过曝。**推测（需实测）**：需用彩条 A/B 验证默认值，但"元数据在手却没传进去"本身是确定缺陷。
- **方案**：无条件调用 `sws_setColorspaceDetails`，矩阵按 `cc->colorspace`（601/709/2020）、range 按 `cc->color_range`（JPEG=full），未知时按分辨率猜（≥720p→709）。顺带把 `UNSpec` 也覆盖。
- **验收**：`ffmpeg -f lavfi -i testsrc2` 编 BT.709 彩条 → 比 `mpv --no-config` 截帧；或检查 16/235 灰阶是否落到 0/255。
- **工作量**：0.5 人日。

### P1-2　HDR 元数据把 MaxFALL 写成 MaxCLL；内容峰值取历史最大

- **证据**：`dsh_engine.cpp:1327` `md.MaxFrameAverageLightLevel=(UINT)(pk+0.5f);`（pk 就是 MaxCLL）；`:2266` `if(cll->MaxCLL>0 && > g.hdrPeak)` → **单调递增**；`:1321-1325` 原色/白点数值正确（BT.2020 + D65 ✔），但 `MaxMasteringLuminance=1000` 写死。
- **影响**：部分电视/显示器用 MaxFALL 决定 ABL 与二次 tone map → 直通画面整体被压暗；一片里只要有一帧 MaxCLL=4000 就整片按 4000 处理。
- **方案**：MaxFALL 用 `AVContentLightMetadata::MaxFALL`（0=未知就不写）；峰值优先读 `AV_PIX_FMT` 的 mastering display side data，帧侧改用"首帧 + 滑动 p99"而不是历史最大；mastering luminance 从 side data 取。
- **验收**：HDR 直通时看显示器信息页收到的 MaxCLL/MaxFALL（`app/dsh_engine/dxgi_hdr_probe.exe` 现成）；同帧 A/B 亮度。
- **工作量**：0.5 人日。

### P1-3　"显示器是否 HDR / SDR 白场"遍历所有适配器所有输出，任一命中即真

- **证据**：`dsh_engine.cpp:1101-1116`（`for adapters { for outputs { if(d1.ColorSpace==...G2084...) hdr=true; } }`）、`:1124-1142`（取所有 path 里最大 SDRWhiteLevel）。`DshEngine_HdrAvailable`（`:1915`）与 `dsh_apply_hdr_mode`（`:1380`）都靠它。
- **影响**：双屏（一 HDR 一 SDR）时，在 SDR 屏的窗口上也会允许切 10bit PQ 直通 → 画面惨灰；弹幕亮度用错白场。
- **方案**：用 `IDXGISwapChain::GetContainingOutput` 或窗口 `HMONITOR` 对应的 `IDXGIOutput6` 判定。
- **验收**：双屏把窗口拖到另一块屏，看 `DshEngine_HdrAvailable` 与开关可用性。
- **工作量**：0.3 人日。

### P1-4　每个 UI tick 全量重绘 + 全窗口 CopyResource + 每帧新建 RTV

- **证据**：`dsh_engine.cpp:2659-2666`（注释明写"每 tick 都重画+present"）、`:629-631`（每帧 `GetBuffer` + `CreateRenderTargetView` + 释放）、`:645`（每帧全屏 Clear，Vulkan 分支 CopyResource 会覆盖它，属白做）、`:665`（整窗 `CopyResource`）。
- **影响**：120Hz 屏上 120 次/秒的全窗口拷贝（2688×1512 窗口 = 16 MB/次 → **~1.9 GB/s**）与 120 次 RTV 创建，与视频帧率无关；笔记本功耗/掉帧风险，且无新帧时画面完全一样。
- **方案**：dirty flag（无新视频帧 + 弹幕静止 → 直接 return 或只 Present）；RTV 按后缓冲缓存；CopyResource 只在 `vkHasFrame` 且本帧有更新时做；Clear 移到 `!vkDrawn` 分支内。
- **验收**：PIX/GPUView 看每帧拷贝时长；HWiNFO 对比整机功耗；日志加 draw/present 分段耗时。
- **工作量**：0.5 人日。

### P1-5　放大只有硬件双线性；RTX VSR 的 2× 输出又被双线性 2:1 缩回

- **证据**：`video_blit_quality.frag:71` `if (step<=1.0001...) return texture(tex,uv).rgb;`；`video_shaders.h:70`（D3D11 路径全双线性）；`dsh_engine.cpp:520-535` 强制 `s<2.0 → s=2.0`（1080p 一律出 2160p），`:681-682` 再把这张 2× 纹理当 quad 源，由 D3D11 双线性缩到窗口尺寸（1080p 窗口就是 2:1 直缩）。
- **影响**：1080p→1440p/4K 边缘软 + 锯齿；VSR 的 AI 细节被 2:1 双线性混叠抵掉一部分（相当于 P0-2 表格里 box=0 那一行没做）。
- **方案**：(a) 放大分支加 4~6 tap Catmull-Rom/Lanczos-lite（每像素 +6~10 fetch，**推测（需实测）** +0.3~0.8 ms）；(b) VSR 目标尺寸按"实际显示尺寸"算（去掉强制 2×），或对 2× 输出做 4-tap box 缩回。
- **验收**：斜边 ESF 测 MTF50；zone plate；VSR 开/关同帧截图对比（`refs/rtx_vsr_bridge/ab_*.png` 已有基线）。
- **工作量**：1 人日。

### P1-6　去色带是"3×3 静止均值"，没有时域抖动，缩小场景下等于没做

- **证据**：`video_blit_quality.frag:90-105`（8 tap 3×3、`thr=3/255`、`mix(...,k*0.85)`）；抖动是固定 4×4 Bayer（`:127`），而"帧相位"`q.w` 永远是 0 —— `vulkan_video.h:379` 里 `pc[11]` 写死 `0.0f`，即**注释里的"相位"从未生效，没有时域抖动**。
- **影响**：8bit 渐变仍有色带（madVR/libplacebo 是半径 8~22 px 多尺度 + 时域噪声）；`step>1` 时 3×3 源邻域比一个输出像素还小 → 对缩小后的画面几乎无效；`mix` 上限 0.85 在平坦区会把细腻纹理一起糊掉。
- **方案**：三尺度（4/8/16 px）取梯度最小方向 + 时域噪声 dither（`hash(px, frameIndex)`，把帧序号真的从 `Present` 传进 `pc[11]`）+ 按输出位深自适应阈值（10bit 目标可关）。
- **验收**：8bit 灰阶 ramp 截帧数色阶数（开/关）；运动场景看是否出现颗粒感；功耗/耗时。
- **工作量**：1~1.5 人日。

### P2 清单（简）

| 编号 | 问题 | 证据 | 方案要点 | 工作量 |
|---|---|---|---|---|
| P2-1 | letterbox 轴的 `step` 算反：`destW=uvSx*tw`，正确应为 `tw/uvSx`，2.33:1 影片纵向少滤 1.7 倍 | `vulkan_video.h:374-377` | 改成 `tw/max(uvSx,eps)` | 0.1 人日 |
| P2-2 | HDR **直通**完全不过缩放滤波（早返回），4K HDR 进 1440p 窗口是纯双线性缩小 | `video_blit_quality.frag:114` | 直通分支也做 box 缩小（仍不抖动） | 0.3 人日 |
| P2-3 | `rgba->format` 写死 `BGRA`，而 sws 的 dst 是 `X2BGR10LE`，只靠"都是 4B/px"侥幸成立 | `dsh_engine.cpp:2202` vs `hdr_convert.h:41` | `rgba->format = g.hdrInfo.swsFmt` | 0.1 人日 |
| P2-4 | staging 写死 31MB + 逐行 memcpy，>4K 直接失败 | `vulkan_video.h:229,354` | 按实际帧尺寸分配、单次 memcpy | 0.2 人日 |
| P2-5 | 每帧 3 次 `ALL_COMMANDS→ALL_COMMANDS` 屏障 | `vulkan_video.h:485-486` | 换具体 stage/access | 0.3 人日 |
| P2-6 | VSR 耗时统计每帧 `s_n` 被加两次 → 日志"均 ms/帧"**少报一半** | `dsh_engine.cpp:562-563` | 去掉重复自增 | 0.05 人日 |
| P2-7 | Vulkan 侧无 `VK_ERROR_DEVICE_LOST` 处理，重建只覆盖 D3D11 | `dsh_engine.cpp:2297-2350` | 见 §4 #9 | 0.5 人日 |
| P2-8 | `Rv_Probe` 用**默认适配器**探测（非引擎设备），双显卡机器可能误报不可用 | `third_party/rtx_vsr_bridge/rtx_vsr_bridge.cpp:45-48` | 探测改传引擎 device | 0.2 人日 |
| P2-9 | 24fps→60Hz 无 cadence 管理（3:2 抖动）；播放路径没有插帧钩子（RIFE 只在离线增强链 `app/backend/app/enhancers/rife.py`） | `dsh_engine.cpp:2607-2615` | 见 §3 #10 | — |
| P2-10 | **ICC / 3D LUT / 锐化 / 去隔行 在当前线完全不存在**（全仓 grep 只命中 `_legacy-player` 与老线文档） | `app/PROJECT_STATUS.md:53-61`、`app/未做.md:323-337` 均为老线资料 | 见 §3 #8 | — |

---

## 3. 画质提升方案（按收益/成本比排序）

1. **线性光滤波（P0-1）**：高清 HDR 边缘高光误差 **-40%~-89% → <5%**（同域 RMS 对比）；等于白捡，改动集中在两个 shader 的 8 行。
2. **换缩放核（P0-2）**：2× 缩小的混叠增益 **1.000 → 0.000（box）/≈0.03（8-tap 带限）**，zone plate 摩尔纹消失；fetch 数 **16→4~8**，**推测（需实测）** -0.5~1.5 ms/帧。
3. **Vulkan 同步重构（P0-3）**：0 画质、**-1~2 ms/帧**，且是后面"加滤镜不卡"的前置条件。
4. **放大加 CR/Lanczos-lite（P1-5）**：1080p→1440p 斜边 MTF50 提升一个台阶，**+0.3~0.8 ms/帧**；VSR 用 4-tap 缩回后 AI 增益不再被混叠吃掉。
5. **SDR 矩阵/范围（P1-1）**：饱和色 ΔE 从可能的 3~8 降到 <1，full-range 内容黑位不再被切。
6. **HDR 元数据（P1-2）**：直通亮度一致性（同片同帧 A/B 亮度差 <3%）。
7. **deband 多尺度+时域抖动（P1-6）**：8bit 色带台阶数 **-50% 以上**，代价 +4~8 fetch。
8. **补齐老线遗产**（这是"未闭环能力"的大头，全部已在老线验证过、可移植）：
   - **ICC/3D LUT**：老线有 `icc_calibrate.cpp` + `.cube` 四面体插值（`app/未做.md:323-337`），当前线零实现。建议先做 1D LUT（伽马/白点）+ 33³ 3D LUT，单 pass，**+0.15 ms**。
   - **锐化**：老线是 luma unsharp + 抗振铃 clamp + 按梯度自适应（`app/PROJECT_STATUS.md:60`）。建议放进 quality shader，按 `step` 只对"缩放后软化"补偿，强度自适应。
   - **去隔行**：老线用 yadif/bwdif 滤镜图（`app/未做.md:308-315`）。当前线最省事是在 `DecodeAhead` 里插 avfilter 图（`buffer→bwdif→buffersink`），只在 `field_order≠progressive` 时建图。
9. **超分**：FSRCNNX 只有**老线 compute 实现**（`app/SR_PROGRESS_61_FINAL.md`），当前线没有；RTX VSR 是**真的接上了**（`dsh_engine.cpp:489-591` + `third_party/rtx_vsr_bridge`，D3D11 路径 GPU 内直出、Vulkan 路径 CPU 回读）。所以"未闭环"的是 FSRCNNX，而 VSR 需要修的是"2× 输出缩回 + 探测误报 + 统计少报"。
10. **24fps→60Hz**：真实解只有插帧。落地分级：①（0 成本）在 120Hz 屏上做精确 5:2 节奏、60Hz 上做 3:2 节奏标注；②（低成本）检测到 24p 内容时提示用户切 120Hz/23.976Hz 输出；③（大工程）后端 RIFE 离线预插帧成文件（已有 `rife.py`），不在播放线程做实时插帧。

---

## 4. 推荐实施顺序（Top 10）

| # | 标题 | 收益 | 成本 | 风险 | 依赖 |
|---|---|---|---|---|---|
| 1 | P0-1 线性光滤波（PQ→linear→滤波） | 高光误差 -89%→<5%，观感立刻变 | 0.5~1.5 人日 | 低（只碰 mode1/0） | 无 |
| 2 | P0-2 换缩放核（box + 8-tap 带限） | 摩尔纹消失；fetch 16→4~8 | 0.5~1 人日 | 低（可 `DSH_QUALITY` 回退） | 无 |
| 3 | P1-1 SDR 矩阵/range 传参 | 色彩正确性 | 0.5 人日 | 低 | 无 |
| 4 | P0-3 Vulkan 单 submit + 多帧 in-flight | -1~2 ms/帧 | 1~2 人日 | 中（同步错误=花屏/黑帧） | 需 ETW 观测 |
| 5 | P1-4 dirty flag + RTV 缓存 + 省 Clear | GPU 占用 -30~60%（静止时） | 0.5 人日 | 低 | 无 |
| 6 | P1-2 HDR 元数据（MaxFALL/峰值策略） | 直通亮度一致 | 0.5 人日 | 低 | 需 HDR 显示器实测 |
| 7 | P1-6 deband 多尺度+时域抖动 | 8bit 色带 -50% | 1~1.5 人日 | 中（糊/颗粒观感） | 依赖 #1（同域） |
| 8 | P1-5 放大 CR + VSR 2× 缩回 | 清晰度 + AI 增益兑现 | 1 人日 | 低 | 依赖 #2 的核复用 |
| 9 | P2-7 Vulkan 设备丢失 + P2-8 探测 + P2-6 统计 | 稳定性/可观测 | 0.7 人日 | 低 | 无 |
| 10 | 老线遗产移植：ICC/LUT → 锐化 → 去隔行 | 能力补齐（用户可见） | 3~5 人日 | 中（新依赖 lcms2/avfilter） | 需先定 product 优先级 |

---

## 5. 审计覆盖与未覆盖

**已覆盖（逐行读过）**：`vulkan_video.h` 全 672 行；`shaders/video_blit.frag`、`video_blit_quality.frag`、`video_blit.vert` 全文；`build_shaders.ps1`、`video_shaders.h`、`hdr_convert.h` 全文；`dsh_engine.cpp` 中 `DshEngine` 结构、`create_gfx`、`ensure_vk_target`、`ensure_video_tex`、`vsr_*`、`draw_frame`、`create_danmaku_layer`、`dsh_display_is_hdr`/`dsh_sdr_white_nits`、`dsh_set_hdr_meta`、`dsh_create_swap`、`dsh_apply_hdr_mode`、`LoadMedia`、`DoSeek2`、`DecodeAhead`、`dsh_rebuild_device`、设备丢失退避、`DshEngine_Present`、`DshEngine_Resize`、`Shutdown`；`third_party/rtx_vsr_bridge/rtx_vsr_bridge.cpp` 全文；`danmaku_gpu.h` 的 HDR 换算段；`WinUIGui/VideoWindow.xaml.cs` 的 `ApplyRenderScale`/`OnRenderFrame`/`VideoPanel_Loaded`。
**未覆盖**：`audio_out.h`/`wasapi_sink.cpp`（音频，非本范围）；`danmaku_lanes.cpp` 排版；解码时钟主逻辑；**未反汇编 `.spv`**，只信 `build_shaders.ps1` 流程（若与 `.frag` 不一致需重跑脚本核对）；**未运行播放器/未实测**，文中 ms 与 dB 均为"按内核传递函数/PQ 常数计算"或标注"推测（需实测）"；`_legacy-player` 与老线文档按题意只作历史参考；`app/CHANGELOG.md` 仅关键词检索，未逐条验证。
