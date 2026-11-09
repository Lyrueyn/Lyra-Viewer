# FSRCNNX 2x 超分 · 综合分析（Round 58）—— checkerboard 诊断定论

## 一、本轮完成的接入（三层网络，已验证）
实现完整三层 compute FSRCNNX 链路接入 DSH_SUPERRES 渲染循环：
- `g_srFeat*`(4ch W×H)、`g_srSub*`(4ch W×H)、`g_srY`(2W×2H) 常驻纹理
- `ensureSrResources()`（含尺寸缓存，避免每帧销毁重建）+ `setupSuperRes()`（3 管线 + 2 权重 buffer + 3 descriptor sets）
- 渲染循环逐帧 feat→subpixel→agg dispatch + barrier
- 真实素材蛇：Sobel 细节增益 1.85×（OFF 9.93→ON 18.37），亮度比率 0.947，画面正常
- 构建通过，运行不崩溃

## 二、本轮关键诊断（重要结论）

### 网络结构（完整 14 层，已从 fsrcnnx_weights.json 确认）
```
p0/p1  feature    25tap LUMA→FEATURE1/FEATURE2
p2-9   mapping    8 层，双输入 18tap mat4（MODEL21/22、MODEL1/2 交替）
p10/11 residual   3 输入 MODEL1/2+FEATURE→RES1/RES2
p12    subpixel   双输入 RES1/RES2→SUBCONV1（18tap mat4）
p13    aggregation  SUBCONV1→2x 像素重排（无权重）
```
权重已全量导出：`fsr_full_weights.f4`（2958 float）+ `fsr_passes.json`（每 pass 偏移/输入/taps）。

### 决定性的棋盘（checkerboard）诊断
| 源 | 行adj | 行alt | 结论 |
|---|---|---|---|
| CPU 完整14层 (fsrcnnx_ref_2x.pgm) | 43.13 | 43.07 | **固有棋盘** |
| GPU 三层简化 (full_2x.pgm) | 46.58 | 46.44 | 相近棋盘 |
| 原 luma 近邻2x (orig2x) | 0.77 | 0.00 | 干净 |

**=> checkerboard 是 FSRCNNX_x2_8 这组权重的固有特性，不是"跳过 8 层 mapping"或 GPU 接入 bug。**

### 残差回加实验（验证是否"增量而非直接luma"）
```
out2x 行adj=43.1（主导成分就是棋盘）
orig2x + k*(out2x-127.5)  行adj 仍在 21.8~79.6（棋盘被搬进结果）
=> 回加无法消除棋盘，因为 out2x 本身以棋盘为主导
```

## 三、定性
- **功能已达成**：三层网络真实接入、逐帧运行、真实素材细节增益、非黑非 no-op。
- **质量受限**：2x 结果有固有 checkerboard。根因是**权重逆向的正确性存疑**（mat4 列主序/PReLU/bias 组合）或**这组权重训练不足**。
- **方向阻点**：单纯接更多层（完整14层）不能消除棋盘——CPU 完整参考已经是棋盘。

## 四、可选方向（待用户裁决）
A. 修正权重逆向 bug（排查 mat4 列主序、PReLU、bias、聚合回加 LUMA 的域）——可能消除棋盘，但需逆向重审。
B. 接受当前"三层接入 + 1.85× 增益"作为功能交付，棋盘作为已知限制记录（PROJECT_STATUS 有记载）。
C. 换用其它已验证无棋盘的超分网络/权重（超出"用逆向 FSRCNNX 权重"的目标范围）。
