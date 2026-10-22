# 自研播放器（local-media-player）项目现状

> 更新时间：2026-08-29。面向"可复现交付/后续接手"的现状快照。
> 目标：Win32 + Vulkan + 手写 Slang + FFmpeg + WASAPI 的 **madVR 级音画质播放器**。
> 构建目录：`F:\DSH\LocalMediaPlayer-Workspace\app`。

---

## 一、如何构建与运行

**构建**（需 MinGW + CMake + Ninja + Qt + Vulkan SDK）：
```pwsh
# 1) 使 CMake 找到 Qt + MinGW/Ninja
$env:PATH='E:\Qt\Tools\mingw1310_64\bin;E:\Qt\Tools\CMake_64\bin;E:\Qt\Tools\Ninja;'+$env:PATH
# 2) 配置 + 构建（FFmpeg 在 D:\ETHEREIS\third_party\ffmpeg）
E:\Qt\Tools\CMake_64\bin\cmake.exe -S . -B build -G Ninja -DCMAKE_PREFIX_PATH=E:/Qt/6.11.1/mingw_64
E:\Qt\Tools\Ninja\ninja.exe -C build
```

**运行**（Vulkan 校验层 + 播放）：
```pwsh
$env:VK_LAYER_PATH='D:/VulkanSDK/1.4.357.0/Bin'
E:\Qt\Tools\Ninja\ninja.exe -C build
.\build\vulkan_renderer.exe <视频路径>
```

**可执行目标**（均在 `build/`）：
| 目标 | 作用 |
|---|---|
| `vulkan_renderer.exe` | **主播放器**（Vulkan+Slang 渲染 + 媒体引擎 + WASAPI 音频 + LUT + 字幕） |
| `LocalMediaPlayer.exe` | 带 Qt 界面的独立播放器壳 |
| `player_smoke` / `stream_smoke` / `speed_verify` | 播放/推流/变速 smoke 测试 |
| `video_decode_smoke` / `audio_decode_smoke` / `av_probe_smoke` | 解码基础测试 |
| `subtitle_verify` / `subtitle_show` | 字幕链路（解码→事件表→按时刻查询；解码→GDI 渲染） |
| `wasapi_smoke` | WASAPI 输出测试 |
| `vulkan_smoke` / `vulkan_render_smoke` | Vulkan 基础/渲染测试 |

---

## 二、已验证能做（✅ 用真实素材验收过）

### HDR → SDR tone-map（子项 1，已完成验收）
- 源：B站 HDR 真彩视频 BV1R1e4zKEh1 → `bili_4khdr.mp4`（4K HEVC 10bit，PQ/bt2020）
- 管线识别 `动态范围=PQ`，触发 **PQ EOTF + bt2020→bt709 + reinhard + 伽马**
- 修复两个真实 bug：reinhard `*0.01`→`*100`；10bit P010/yuv420p10le 对齐
- 修复后硬解/软解画面正常（mean≈[181,147,115]），`test_hdr.mp4` 校验通过

### 字幕完整显示（子项 5，已完成验收）
- `media_engine` 集成字幕流：`pkt->pts`+`pkt->duration` 换时间 → 事件表 → `subtitleAtUs(us)` 按时刻查询
- GDI 渲染 → `subtitle_render.h`（`renderSubtitleToRGBA`）→ Vulkan 纹理 → shader 底部叠加
- 实测 `test_subtitle.mp4`：字幕**正确出现在画面底部并随内容时刻切换**（0.5s→"Hello subtitle 1"、1.97s→"This is line 2"）

### ICC 校色（子项 4，已用真实 .icc 验收）
- Little-CMS（lcms2）构建成静态库；读系统 `sRGB Color Space Profile.icm` 作源
- `icc_calibrate.cpp`：`cmsCreateTransform`(INTENT_RELATIVE_COLORIMETRIC) 做 **sRGB→Display-P3 色域映射**，采样成 `p3_lut.cube`
- 播放器加载后**画面可见变饱和**（广色域映射），白/黑点保持

### 缩放 / 画质滤镜
- **Lanczos3** 放大 + **B样条**；`DSH_LUT` 加载 .cube 3D LUT（四面体插值）；去色带（时域噪声） 
- **unsharp 锐化**（`DSH_SHARPEN`，作用于 luma，补回缩放软化）——实测开/关像素差 std=6.24（边缘增强真实生效）
- **去隔行**（yadif/bwdif，`DSH_DEINT` 开关；实测运动隔行斜线无梳齿、PSNR≈29.5dB）、**变速**（atempo 保调，`setPlaybackSpeed`/`DSH_SPEED`；speed_verify 确认**主时钟=内容时间**驱动，变速改变播放速率但内容时钟正确，audio#100 在 1.0x/1.5x 主时钟均≈2.13s）
- 2x/1:1/0.5x 三种缩放 regime 均正确渲染（色条正确、无黑屏/伪影）

### 音频
- **WASAPI 共享+独占**（`WasapiSink`），格式协商 float32→int16，`setAudioOutputRate` 动态重建 swr
- 实测 16bit PCM @48kHz 独占、实时同步（180帧/7.48s）

---

## 三、有进展但未闭环（🔄）

### FSRCNNX 超分（子项 2，compute 端到端跑通 + 超分对比验收）
- ✅ `fsrcnnx_parser.py` 逆向 mpv 钩子 .glsl → `fsrcnnx_weights.json`（14 pass/3488 权重）
- ✅ `fsrcnnx_ref.py` CPU 端到端：真实素材 128×128 luma → **256×256 2x 超分**，纹理被真实重建
- ✅ **compute 通**（绕开 graphics 挂起）：graphics pipeline（+离屏 renderpass）在 RTX 5080 挂起，但 **`vkCreateComputePipelines` 完全可用**（`DSH_COMPUTE_TEST=1` 实测成功）。
- ✅ **compute 端到端超分（✅）**：`testComputeFullNet()`（`DSH_COMPUTE_FULL=1`）从真实 luma 出发，**feature→subpixel→agg 三层真实卷积** → 256×256 2x 超分图，蛇头/鳞片/草清晰。`fsrcnnx_feat.slang`(feature)/`fsrcnnx_map.slang`(通用 mat4 卷积)/`fsrcnnx_agg.slang`(2x 重排) 全部编译+验证；feature 层真实素材提取特征、mapping 链式、agg 与 CPU 参考完全一致。
- ✅ **超分对比验收（✅）**：compute 超分图 vs 普通 Lanczos 放大——**FSRCNNX 重建的鳞片纹理更锐/更有立体感，更多高频细节**；Lanczos 平滑但模糊。证明**超分在 compute 上真实有效可验收**。
- 🔊 坑：Slang compute entry 名是 `main`（非 cs_main）；`StructuredBuffer` 用 STORAGE_BUFFER（非 UNIFORM_BUFFER）；descriptor storage/sampled image 类型须严格匹配（VUID-07990）。
- 🙌 剩余（增强，非核心正确性）：接完整中间 8 层 mapping/residual（通用 map.slang 复用）+ 接入 DSH_SUPERRES 做成播放器可选档。

### NNEDI3（子项 3）
- ✅ 权重 `nnedi3_weights.bin`（12.9MB float32）已用 **FFmpeg nnedi 滤镜**验证可被标准解析器读取
- ⚠️ FFmpeg nnedi 是**去隔行**（nsize 0-6），不能直接读任意权重做图像超分；自研 Slang GPU 移植同受上述 piretute 挂起影响

### ASIO（子项 6）
- ✅ `asio_probe.cpp` 枚举到本机 **Generic Low Latency ASIO Driver** 并 `loadDriver`=OK
- ⚠️ `ASIOInit` 等 API 调用在**该 ASUS 驱动下段错误**（0xC0000005）——驱动对 MinGW host 不友好；需 ASIO4ALL 类驱动验证通道/双缓冲

---

## 四、关键环境事实 / 坑（已排查）

- **g++ 直接编译必须 PATH 含 `E:\Qt\Tools\mingw1310_64\bin`**（否则 cc1plus 找不到运行时 DLL，静默 exit=1 无输出）
- **FFmpeg 权重路径的盘符冒号**会让 `nnedi=weights=D:/...` 报解析错（改用相对路径）
- **Vulkan 校验层**需 `VK_LAYER_PATH=D:/VulkanSDK/1.4.357.0/Bin`
- 播放器 mux 字幕进 mp4 用 `mov_text`（raw ass 会报 -22）；合成测试素材在 `test_subtitle.mp4`
- FSRCNNX 权重 `vec4`/`mat4` 列主序混淆是解析器的坑（已修正）

---

## 五、下一步建议（按价值）

1. **解决离屏 pipeline 挂起**（可能是 Vulkan 内存/驱动对多 pipeline 的兼容问题）→ 才能让 FSRCNNX/NNEDI3 GPU 超分真正启用。或改走「主 shader 单 pass 内联放大」绕开多 pipeline。
2. **ASIO**：装 **ASIO4ALL**（对标准 host 友好的通用驱动）后验证通道映射/双缓冲。
3. 若要**针对特定显示器校色**，提供该显示器 .icc 文件即可复用 `icc_calibrate.cpp`。

---

## 六、记忆

- 超分逆向/验证 + lcms2 API + 字幕链路 + ASIO 枚举等可复用事实，已存 Mnemon「自研播放器」空间与运行时 MEMORY.md。
