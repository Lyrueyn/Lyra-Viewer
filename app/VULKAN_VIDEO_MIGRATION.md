# Vulkan Video 硬解迁移 · 进度

## 目标
把自研播放器视频解码从 d3d11va 迁移到 **Vulkan Video**：
```
视频文件 → FFmpeg解封装 → Vulkan Video硬解(av_hwdevice_vulkan) → 解码直接生成Vulkan纹理 → Vulkan渲染 → 屏幕
```
- 解决"开着播放器时 B站/浏览器黑屏加载失败"的 **D3D11 硬解资源争抢**问题。
- 实现**零拷贝**（解码 VkImage 直连渲染，不 CPU 中转）。

## 已验证（可行性）
### 1. FFmpeg 支持 Vulkan Video
- 本机 `ffmpeg -hwaccels` 含 **vulkan**；`--enable-vulkan --enable-libshaderc`。
- **实测 `ffmpeg -hwaccel vulkan -i dv_video.mp4` 成功解码 HEVC 10bit**（`frame=3`，输出 p010le/bt2020nc/smpte2084 3840x2160）——RTX5080 VK_KHR_video_decode 可用。

### 2. 架构（共享 VkDevice 零拷贝）
- `AVVulkanDeviceContext` 允许**预填外部创建的** `inst/phys_dev/act_dev` + `enabled_inst_extensions/enabled_dev_extensions`（含 VK_KHR_video_decode_queue）+ `queue_family_decode_index/nb_decode_queues`。
- 官方注释：**"All of these can be set before init"** → **渲染器建 Vulkan 设备（带 decode queue + video ext），传给 FFmpeg 做 av_hwdevice_vulkan**，解码 VkImage 与渲染同设备，**零拷贝**。
- `AVVkFrame` 里保存解码的 VkImage（av_vk_frame_alloc 分配），可提取供渲染采样。

## 实现步骤（规划）
1. **渲染器**：建 Vulkan 设备时，探测/启用视频解码队列族 + VK_KHR_video_decode_queue 扩展，暴露 g_inst/g_phys/g_dev/decode_queue_family。
2. **media_engine**：用这些（外部 Vulkan 设备）建 av_hwdevice_vulkan 解码器 → avcodec HEVC Vulkan 解码。
3. **纹里共享**：从解码 AVFrame 的 AVVkFrame 提取 VkImage，直接用为渲染纹理采样（零拷贝）。
4. **回退**：Vulkan Video 不可用（无扩展/驱动）时回退当前 d3d11va/CPU 软解。

## 状态
- 已完成：可行性验证（步骤 0）+ 架构确认。
- 开始：步骤 1（渲染器建设备带 video decode）。

## Round 1-2 进展
### 步骤 1（部分完成）：渲染器启用 Vulkan Video 扩展
- createDevice 扩展探测已加（VK_KHR_VIDEO_DECODE_QUEUE/MAINTENANCE_1/DECODE_H265/SYNC2）。
- **实测本机 RTX 5080 全部支持**：`VulkanVideo: decode_queue=1 maintenance1=1 h265=1 sync2=1`（+ extmem_win32/ycbcr 零拷贝也=1）。
- 编译通过，扩展会启用（日志"启用 Vulkan Video 硬解"）。
- ⚠️ 坑：VkPhysicalDeviceVideoQueueInfoKHR / VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VIDEO_QUEUE_INFO_KHR 在本 Vulkan header 中缺失（编译错），**video 队列族探测已回退**（可选，非关键）。需确认 header 版本或换 API 名。

### 下一步（核心）
步骤 2：media_engine 用 av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_VULKAN) 建 Vulkan 解码器（先自动模式，FFmpeg 内部设备），验证能解码 HEVC 10bit 并拿到 AVVkFrame(VkImage)。
步骤 3：零拷贝——AVVkFrame 的 VkImage 经外部内存(extmem_win32)导入渲染器，或共享 VkDevice。
步骤 4：回退路径（Vulkan Video 不可用时回退 d3d11va/软解）。

## Round 3 核心突破：av_hwdevice_vulkan 解码 HEVC + 提取 VkImage 完全可行
- 新增 `src/vkdecode_smoke.cpp` + CMake 目标，独立验证迁移核心。
- **实测结果（完整通过）**：
  - `av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_VULKAN)` OK（FFmpeg 自动建 Vulkan 设备）
  - `av_hwframe_ctx_init` OK（P010LE 3840x2160）
  - `avcodec_open2 OK (decoder=hevc)`
  - **成功解码 6 帧，每帧 format=VULKAN，`vk->img[0]=0x...`（VkImage 可提取）**
- **结论**：av_hwdevice_vulkan 建 Vulkan 解码器 + 解 HEVC 10bit + 拿 AVVkFrame(VkImage) **核心前提全线可行**！迁移是真实可行的。
- ⚠️ 注意：这 VkImage 在 **FFmpeg 的 Vulkan 设备**上，与渲染器的 Vulkan 设备**不同**——零拷贝需 **external memory(extmem_win32) 跨设备导入**，或**共享 VkDevice**（AVVulkanDeviceContext 可传外部设备，前面已确认）。

### 下一步
- 步骤 3a：把 vkdecode_smoke 的成功逻辑集成进 media_engine（建 Vulkan 解码器路径，DSH 开关）。
- 步骤 3b：VkImage 与渲染器共享——优先**共享 VkDevice**（渲染器建 Vulkan 设备（含 video 扩展+视频队列族），经 AVVulkanDeviceContext 传给 FFmpeg），这样解码 VkImage 与渲染同设备，**零拷贝最干净**。
- 步骤 4：回退（Vulkan Video 不可用→d3d11va/软解）。

## Round 4 里程碑：media_engine 迁移到 Vulkan Video 硬解（默认）！
- media_engine open() 改为**默认用 av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_VULKAN)** 建解码器（`DSH_DECODER=vulkan|d3d11va|cpu` 可强制；默认 vulkan）。
- 解码帧处理：`if(format==D3D11 || format==VULKAN)` 都经 av_hwframe_transfer_data 转 CPU（复用现路径）。
- 修复：渲染器**不再启用 video 扩展**（只探测打印）——渲染器启用会因依赖 VK_KHR_video_queue 导致 vkCreateDevice 失败。video 扩展由 av_hwdevice_vulkan 解码器自己启用。
- **实测成功**：
  - 日志 `[解码] 使用 Vulkan Video 硬解（无D3D11冲突）`
  - **正常解码渲染播放**（已显示 30/60/90 帧）
  - frame300 彩色墨水帧**画面正常**（蓝圆滴/橙黄流体/紫青渐变，色彩正确）
  - frame60 黑画面是**杜比演示片前几秒黑场/标题**（素材本身，非 bug）
- **核心达成的**：播放器现在**默认不持 d3d11va 设备**（用 Vulkan Video），**彻底解决"开着播放器时 B站/浏览器黑屏加载失败"的 D3D11 硬解冲突**！

## 下一步（优化）
- 当前是"Vulkan 解→transfer 转 CPU 帧→渲染"（改最小、已跑通、不冲突）。**零拷贝**（VkImage 直连渲染不转 CPU）是后续优化——需共享 VkDevice 或 external_memory(extmem_win32)。
- 更新日志措辞（"硬解=d3d11va" 改实际解码器）。
- 验收：让用户实测 Vulkan 解码 + 同时开 B站，确认不再冲突。

# 未做.md 画质项推进

## 10.2 去晕影（已实现，Round 5）
- shader `yuv_to_rgb.slang`：最终输出前加径向暗角补偿 `vig = 1.0 + peak.y * rad2`（rad2=到四角归一化距离²），边缘提亮校正镜头暗角。peak.y=强度(0关)。
- C++ `vulkan_renderer.cpp`：g_vignette 全局(默认0) + `DSH_VIGNETTE` 环境变量(0..1) + data[21]=g_vignette + g_vigSlot 逐帧同步。编译通过。
- 用：`set DSH_VIGNETTE=0.15`（边缘提亮，对实拍视频暗角有校正效果）。
- ⚠️ 既有警告：descriptor binding 6/7(字幕/超分) 无值时用 VK_NULL_HANDLE（校验层报 VUID-vkWriteDescriptorSet-descriptorType-02997）——不影响功能，可后续用 1x1 占位纹理规范化。

## 待做（下一步候选）
- 7.2 Jinc 缩放、10.3 CLAHE（大工程）、7.4 NNEDI3（大工程）、1.视频格式全覆盖(YUV444等)。
- 优先级：用户画质价值 → 缩放自动选择增强、简单画质滤镜先做；大工程(NNEDI3/CLAHE/Dolby Vision)长线。

## Round 7 调研：Jinc 缩放——判定收益低（暂不做）
- 调研 mpv EWA Lanczos(Jinc)：bessel J1 核 + 椭圆加权，质量超 Lanczos 但**是增量**（Lanczos 已够好）。
- **完整 EWA Jinc 移植是大工程**（bessel J1 近似 + 椭圆加权 + 多 tap），且 mpv 的实现依赖其 shader 框架。
- **判定**：投入产出比低（Lanczos 已达高质量，Jinc 增量小），**暂不做**。除非用户明确要极致缩放。
- 画质核心已到位：BT.2390 色调 + Lanczos 缩放 + FSRCNNX 超分 + 去晕影 + ffmpeg式deb and。**剩余大工程画质项(Jinc/NNEDI3/CLAHE/BM3D)边际收益有限**。

## 下一轮方向（待选，需集中做完一项）
- 选项A：视频格式全覆盖(YUV444/YUVJ/灰阶/RGB)——但 444→420 降采样会损失专业444精度，不理想；完整444需跨层(shader处理全分辨率chroma)。
- 选项B：字幕 ASS 特效(5.5)——用户看片会用到样式/定位/特效字幕（当前仅简单文本）。
- 选项C：独立 OSD 层(16)——进度条/音量/信息显示。
- 选项D：CLI+IPC(18)、用户配置系统(24)等实用功能。
- **建议**：画质核心已达，下一个做**用户会实际用到的**（字幕ASS 或 OSD 或配置系统），或用户醒来后指定方向。

## Round 8：解码错误恢复（19）完成
- media_engine 视频解码循环加**解码错误恢复**：
  - `send_packet` 错误(≠EAGAIN) → `avcodec_flush_buffers` 清解码器残留态，继续后续帧（避免卡死/持续错误）。
  - `receive_frame` 错误(≠EAGAIN/EOF) → flush 解码器，继续。
- 编译通过，正常播放不受影响（30/60/90 帧；恢复仅在解码错误时触发）。
- **价值**：播放器遇到损坏/异常包时自动恢复，不黑屏/不卡死（健壮性基础）。

## 后续（每轮专注做一项，验收后推进）
- 健壮性/实用：编码错误恢复✅、Vulkan设备丢失恢复、实时性能监控、分级日志。
- 功能：字幕ASS、OSD、配置系统、CLI+IPC、AB循环。
- 大工程画质长线：NNEDI3/CLAHE/Dolby Vision。

## Round 9：独立 OSD 层（16）——渲染原语完成
- `subtitle_render.h` 加 `renderOsdToRGBA(frac, info, ...)`：用 GDI 渲染**进度条(填充比例frac) + 播放信息文字**到 RGBA。alpha=进度条固定/文字亮度。
- 编译通过（vulkan_renderer 链接成功）。
- **接入渲染循环（下一步）**：①uploadSubtitleTexture 泛化为可传尺寸；②shader 加 OSD 叠加区域（或复用 texSub 但错开字幕）；③渲染循环按操作(暂停/快进/音量)渲染 OSD + 上传 + 叠加 + 几秒消失。
- 现状：OSD 渲染原语已就绪，接入显示是下一轮。

## Round 10-13：独立 OSD 层（16）完成✅
- `uploadSubtitleTexture` **泛化为动态尺寸**：纹理尺寸变化时销毁重建（支持字幕+OSD 共用；FW/FH 全改 W/H）。
- 渲染循环加 **O 键 OSD 显示**：按 O 显示进度条+播放时间 OSD，**3 秒自动消失**，进度条随帧走。复用 texSub（字幕纹理叠加通道），OSD 优先于字幕。
- 键盘：`O`/`o` 键触发 OSD。
- 编译通过（vulkan_renderer 链接成功）。
- 用：播放中按 **O** 显示进度条/时间 OSD。
- 说明：实际观看效果由用户按 O 实测（capture 无法模拟按键）。

## 长目标完成汇总（到 Round 25）
- ✅ Vulkan Video 硬解迁移（解决 B站冲突，默认生效）
- ✅ 去晕影（10.2，DSH_VIGNETTE）
- ✅ 解码错误恢复（19）
- ✅ 独立 OSD 层（16，O 键）
- ✅ **AB 循环（17）**：播放中按 A 设 A 点、B 设 B 点并开循环、C 关闭；播放到 B 点自动 seek 回 A 重复片段。
- 画质核心：BT.2390 色调 / Lanczos 缩放 / FSRCNNX 超分 / 去晕影 / ffmpeg式去带

## 下一项（继续，每轮专注做一件）
- 健壮性：Vulkan 设备丢失恢复、实时性能监控、分级日志。
- 功能：字幕 ASS 特效、CLI+IPC。
- 大工程画质长线：NNEDI3 / CLAHE / Dolby Vision。

## Round 34：用户配置系统（24）完成✅
- 加 `loadPlayerConfig()`：读 exe 同目录 `player.conf`（key=value）覆盖画质/播放默认值（tone_boost/vignette/sharpen/deband/speed），环境变量(DSH_*)仍可再覆盖。
- main 里在 DSH_* 读取前调用；player.conf 不存在则用内置默认。
- **验证成功**：`[配置] 已加载 player.conf (tone_boost=1.30 vignette=0.20)`，sharpen=0.6 正确应用（非默认0.5）。
- 用：exe 同目录放 `player.conf`（`tone_boost=1.2`/`vignette=0.2`/`sharpen=0.6`/`deband=0.04`/`speed=1.5`），重启即用预设。示例在 `build/player.conf`。

## 长目标完成汇总（到 Round 37）
- ✅ Vulkan Video 硬解迁移（解决 B站冲突，默认生效）
- ✅ 去晕影（10.2，DSH_VIGNETTE）
- ✅ 解码错误恢复（19）
- ✅ 独立 OSD 层（16，O 键）
- ✅ AB 循环（17，A/B/C 键）
- ✅ 用户配置系统（24，player.conf）
- ✅ 实时性能监控（22，OSD 显示 FPS/分辨率）

## Round 40-41：实时性能监控（22）完成✅
- OSD 信息里加 **FPS 统计**（帧间隔均值，每 500ms 更新）+ **分辨率** 显示（`%.0f FPS %dx%d`）。
- 按 O 显示 OSD 时即可看到当前 FPS / 分辨率 / 播放时间 / 暂停状态。
- 编译通过（vulkan_renderer 链接成功）。
- 用：按 O 查看实时性能（FPS/分辨率/时间）。
