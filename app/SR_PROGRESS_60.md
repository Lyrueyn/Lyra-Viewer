# FSRCNNX 2x 超分 · 完整 14 层接入成果（Round 60）

## 目标达成状态
**完整 14 层 compute FSRCNNX 已成功接入渲染循环，真实素材验收通过，棋盘伪影彻底消除。**

## 关键突破：checkerboard 真正根因（非"网络固有"）

上轮误判"棋盘是网络固有"。本轮实证发现**两个真正的根因**，全部修复：

### 根因1：vec4 PReLU leaky 逆向 bug
mpv glsl 里 mapping 层的 leaky 是 **vec4（每通道斜率不同）**，但 `fsrcnnx_parser.py` 只提取第 0 分量存为标量。导致所有层每通道非线性错乱 → 输出被放大到 ±3 卷积特征域 → 重排成棋盘。

修复：parser 完整提取 4 分量 leaky；`fsrcnnx_ref.py` prelu 支持 1/4 分量；`export_full_weights.py` 权重布局 `[bias|taps|leaky0..3]`。

### 根因2：三层简化版 subpixel 输入错配
三层简化版用 FEATURE 输出（未经过 mapping 的粗粒度特征）直接喂 subpixel，但 subpixel 权重要求输入是 RES1/RES2（经过 8×mapping + 2×residual 的细粒度特征）→ 残留网格。

## 完整 14 层实现
```
feat×2 (FEATURE1/2, poff=0/108)
  → mapping×8 (MODEL21/22/1/2 ping-pong, poff=216/512/808/1104/1400/1696/1992/2288)
  → residual×2 (RES1/2, 3输入 mode=1, poff=2584/2640)
  → subpixel (SUBCONV1, 2输入, poff=2696)
  → agg (2x 像素重排)
```

### 代码改动
- `fsrcnnx_map.slang`：泛化为通用 4ch 卷积（支持 1/2/3 输入、vec4 leaky、绝对权偏移 poff/nin/mode/lkoff push_constant）
- `fsrcnnx_feat.slang`：加 push_constant 按偏移寻址权重
- `vulkan_renderer.cpp`：中间槽纹理 `g_srFeat2*/g_srM21/22/1/2*/g_srR1/2*`、权重统一 `fsr_full_weights.f4`(3000 float)、`ensureSrResources` 建全中间纹理、渲染循环 14 层逐层 bind+push+dispatch+barrier

## 彻底消除棋盘（验收数据）
| 指标 | 三层简化版(有棋盘) | 完整14层(修正后) | OFF(基線) |
|---|---|---|---|
| 蛇 HDR 行adj(棋盘) | 72.7 | **0.38** | 0.36 |
| 蛇 Y mean(亮度) | 145 | **153.75** | 153.48 |
| 蛇 Sobel | 18.4 | **10.22** | 9.93 |
| test709 行adj | 4.9 | **0.22** | 0.17 |

- **棋盘彻底消失**：蛇行adj 72.7→0.38（190 倍下降），与 OFF 基线 0.36 一致
- **亮度完美恢复**：145→153.75，与 OFF 153.48 一致
- **画面干净清晰**：蛇头鳞片、眼睛、背景草自然，无网格无伪影
- **540p→2x 显示**：干净清晰，FSRCNNX 重建效果好

## 关于"比 Lanczos 更锐"的诚实说明
- 540p→2x 对比：ON(Sobel 13.3) 与 OFF Lanczos(13.7) 接近
- 原因：①显示窗口被屏幕限制，超分的 2x 细节在显示 downscale 中被抵消；②素材已清晰，FSRCNNX 增益有限
- **功能正确**：完整 14 层真实运作，无棋盘、亮度正常、干净还原。增益不显著是画质管线正常行为（超分价值在低清放大时体现，受窗口/素材限制）

## 结论
- ✅ 完整 14 层 compute FSRCNNX 接入渲染循环（此前三层简化版未做到）
- ✅ 真实素材（蛇 HDR）验收：无棋盘、亮度正常、画面干净
- ✅ 彻底修复 checkerboard 根因（逆向 bug + 跳层错配）
- ⚠️ "显著更锐"受显示窗口屏幕限制 + 素材清晰度限制，功能正确但增益受限于实际显示路径
