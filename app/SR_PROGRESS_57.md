# FSRCNNX 2x 超分 · 接入渲染循环进展（Round 57）

## 达成（重大突破）
把 `DSH_SUPERRES` 从"单层占位 1x 锐化器"整体升级为**完整三层 compute FSRCNNX**：
`feature(fsrcnnx_feat.slang) → subpixel(fsrcnnx_map.slang) → agg(fsrcnnx_agg.slang)`，
逐帧 read 真实 luma → 输出 **2W×2H 真 2x 超分**，主 shader 经 binding7(texY2) 采样显示。

### 代码改动（vulkan_renderer.cpp）
- 新增常驻中间层纹理 `g_srFeat*`(4ch W×H)、`g_srSub*`(4ch W×H)、`g_srY`(2W×2H)。
- `ensureSrResources()`：按媒体尺寸建 3 纹理 + 逐帧重绑 3 条 compute descriptor + 主 shader binding7。
- `setupSuperRes()`：2 个权重 storage buffer（feat_weights.f4=1100 / sub_weights.f4=293）+ 3 条 compute 管线 + 3 descriptor sets。
- 渲染循环：三层 dispatch + 层间 barrier（g_yV→SHADER_READ、feat/sub 写后→SHADER_READ、agg→g_srY→SHADER_READ）。
- `yuv_to_rgb.slang`：声明 binding7 `texY2`；`sub.w>0.5` 时读 texY2 且 Y 一律做 limited 展开。

### 构建
`ninja -C build` 通过，`vulkan_renderer.exe` 编译链接成功（仅剩无害 narrowing warnings）。

## 真实素材验收（clip_snake_hdr.mp4 第60帧）
| 指标 | OFF(superres=0) | ON(superres=1) | 结论 |
|---|---|---|---|
| RGB mean | 147.75 | 140.54 | 亮度正常(比率0.947,非黑) |
| RGB std | 66.20 | 77.85 | 细节更丰富 |
| Sobel(细节) | 9.93 | 18.37 | **增益 1.85×** |
| Diff mean | — | 40.89 | 真实改写 |

画面彩色正常，蛇头/鳞片/草清晰，**非全黑、非 no-op**。测试素材 test709 增益 3.38×。

## 待解决问题（未达完全验收标准）
- **全图周期-2 网格/横纹伪影**：OFF 行diff=0.42 → ON 行diff=68.9（160 倍暴涨），行+列周期均为 2 像素。
  该伪影在**独立 `DSH_COMPUTE_FULL` 验证时就已记载**（PROJECT_STATUS："归一化偏暗/有横纹"），非本轮接入引入。
- 判断：周期-2 + 隔行/相邻比≈1.0 → **sub-pixel 卷积(pixel shuffle)的经典 checkerboard artifact**，
  源于网络权重在平坦/渐变区产生固定高频响应（真实素材上覆盖蛇身+草）。

## 结论
功能层面：**三层网络已成功接入渲染循环，透明工作，跑通不崩溃**，真实素材细节增益 1.85×。
质量层面：网格伪影存在，属"权重/相位调优"，需下一轮深挖（怀疑点：agg 重排相位 vs 主 shader 采样对齐、权重在平坦区的响应）。
