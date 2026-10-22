# B 站弹幕引擎改进 — 进度文档

> 项目：`F:\DSH\LocalMediaPlayer-Workspace\app`（自研本地播放器）
> 目标：把弹幕布局改成**和 B 站一模一样**（方案 A：完整移植 B 站 `DanmakusRetainer` 算法，并搬进 C++ DLL 层）
> 状态跟踪：2026-09-02 起

---

## 一、背景与问题

当前 WinUI 3 GUI（`WinUIGui/VideoWindow.xaml.cs`）+ `dsh_engine.dll`（C++：FreeType 光栅化 + D3D 上传）的弹幕层，用户反馈**"滚动弹幕全部堆在第一行"**。此前多轮在 C# 侧给"近似复刻版 `AlignTopRetainer`"打补丁（`PickLane` → `PickY` → 每行占位集合 `_dmLaneScroll/Top/Bottom`），均未解决，用户判断"系统问题很大，要大改"。

**裁决**：改用**方案 A** —— 把 B 站弹幕轨道算法**完整移植**（逐行对照 DFM `DanmakusRetainer` + `DanmakuUtils`），并**搬进 `dsh_engine.dll`（C++）**，C# 只负责喂数据 + 读坐标上屏。理由：
- 渲染管线本就在 C++（FreeType/D3D），算法放 C++ 更贴近 B 站、且能用**命令行对拍**可验证。
- 文字宽度用 **FreeType advance 累加**测量，和光栅化同源，根治"WinUI 测宽 vs FreeType 光栅化宽不匹配"的老坑。
- 摆脱"每帧更新 + WinUI 锁"的纠缠。

---

## 二、已完成 ✅

### 1. 独立可复用的 C++ 弹幕轨道算法模块
新增（纯逻辑、无 D3D/FFmpeg 依赖，可命令行对拍）：
- `dsh_engine/danmaku_lanes.h`：类型常量（SCROLL_RL=1/FIX_BOTTOM=4/FIX_TOP=5/SCROLL_LR=6/SPECIAL=7）、`Dm` 弹幕布局结构、`posX`/`posX_L2R` 位置公式、`willHitInDuration`/`hitAt` 碰撞检测（逐行对照 `DanmakuUtils`）。
- `dsh_engine/danmaku_lanes.cpp`：`alignTopFix`（滚动弹幕，逐行对照 `AlignTopRetainer.fix` 完整决策树）、`fixTopFix`（顶部固定，`FTDanmakusRetainer`）、`fixBottomFix`（底部固定，`AlignBottomRetainer`）。

### 2. 命令行对拍测试 `dsh_engine/test_danmaku_lanes.cpp`
- 模拟真实播放：统一弹幕时钟逐 tick 推进 + 到点出生 + 占位集合同 y 顶替。
- **验证结论：核心滚动算法正确**。`marginTop=0` 时弹幕正确铺开为多行（`y=0/37/74/111/148`，每行 1~4 条，错落分布），**不再堆一行**。

### 3. ★ 真正根因（关键）
之前"堆第一行"的根因是 `AlignTopRetainer.isOutVerticalEdge` 里的条件 `(firstItem.getTop() > 0)`：
- B 站 `marginTop` 默认 **0**，弹幕 y 从 0 起算 → 第一行弹幕 `top=0`，不触发该分支。
- 此前 C# 实现行 y 非 0（或 margin 起算），导致几乎所有新弹幕被判"越界"→ 强制回顶行 → 堆一行。
- **结论：接入时 `marginTop` 必须为 0。**

### 4. 编译验证
用于 MinGW（`E:\Qt\Tools\mingw1310_64\bin\g++.exe`），`g++ -std=c++17 test_danmaku_lanes.cpp danmaku_lanes.cpp -o test_danmaku_lanes.exe` 编译运行通过。

---

## 三、已完成 ✅（接 DLL + 改 C# 完成，全部编译通过）

### 5. 接入 `dsh_engine.dll`（已实现并链接成功）
新增导出接口：
- `DshEngine_SetDanmaku(texts, modes, colors, times, count)`：C# 加载后一次喂入全量弹幕；DLL 内部用 **FreeType advance 累加测宽**（`dm_measure_w`），按 time 排序。
- `DshEngine_GetDanmakuLayout(xs, ys, cols, cap)`：C# 每帧调用，DLL 读 `GetClockPosMs` → `DanmakuEngine.Tick` 出生调度 + 轨道分配 → 把在飞弹幕**光栅化进 dmTex**（内部复用 `DshEngine_DrawDanmaku` 光栅化）→ 返回条数。
- C# 不再调 `DrawDanmaku` 传坐标（光栅化在 DLL 内完成）；`DshEngine_DrawDanmaku` 导出保留。
- DLL 构建命令（MinGW）：`g++ -shared dsh_engine.cpp danmaku_lanes.cpp zstub.cpp -I<ffmpeg include> -I<QtFreetype> -L<ffmpeg lib> -lavformat -lavcodec -lavutil -lswscale -lswresample -L<Qt lib> -lQt6BundledFreetype -lQt6BundledLibpng -ld3d11 -ldxgi -ld3dcompiler_47 -lgdi32 -luser32 -lole32 -lws2_32 -static ...`。FFmpeg 用 `D:\DSH\VOCALIVE\ffmpeg`；Qt bundled freetype 用 `E:\Qt\6.11.1\mingw_64`。

### 6. 改 C# `VideoWindow.xaml.cs`（已精简，编译通过）
- 删掉全部旧轨道逻辑：`PickY`/`PickY_FixTop`/`PickY_FixBottom`/`WillHitInDuration`/`HitAt`/`RegisterLane`/`UnregisterLane`/`CenterX`/`BinSearch`/`MakeItem`/`TextWidth` 及字段 `_danmaku`/`_dmActive`/`_dmMerge`/`_dmFree`/`_dmLaneScroll/Top/Bottom`/`_dmMerged`/`MergeWindowMs`/`DmMargin`/`DmActive`。
- `UpdateDanmaku` 精简为：首次 `DshEngine_SetDanmaku`（喂全量）+ 每帧 `DshEngine_GetDanmakuLayout`（DLL 内 Tick+光栅化）。
- 新增字段 `_dmTexts/_dmModes/_dmColors/_dmTimes/_dmFeed`；加载弹幕后构造数组喂给 DLL。

### 7. 已验证
- `test_danmaku_lanes.exe`（命令行对拍）：滚动弹幕 `marginTop=0` 时正确铺开为多行（`y=0/37/74/111/148`，错落，不堆一行）。
- `dsh_engine.dll` 重建成功（5.1MB），`objdump` 确认导出 `DshEngine_SetDanmaku`/`DshEngine_GetDanmakuLayout`。
- C# `WinUIGui.csproj` 构建成功（0 错误）。
- 已把新 DLL 复制到运行目录 `WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64`。

### 8. ★ 2026-09-03 弃用 B 站 AlignTopRetainer，改回旧版 QML pickRow 负载均衡（用户裁决）
- **原因**：B 站"同 lane 首尾相接"会把同文本刷屏全堆进最上面一行 → 顶部重叠。用户明确判定"不是合并问题，而是你的 lane 分配就有问题"。**弃用 B 站模型**，改回旧版 `DanmakuLayer.qml` 的 `pickRow`（选当前占用最少行，负载均衡铺满各行）。
- `danmaku_lanes.h/.cpp`：删掉 `willHitInDuration`/`hitAt`/`alignTopFix` 决策树，实现 `pickRow`/`pickTopRow`/`pickBottomRow`（统计每行在飞同类型数，选最少占用行）。
- 离线 `test_danmaku_lanes.cpp` 验证：active=150，y 覆盖 [0..1260]（约 95% 高度，36 行），最上面一行仅 5 条（3%）——铺满、顶部不堆。

### 9. ★ 2026-09-03 同文本**动态合并 ×N**（用户要求"不是所有相同全合一起，可多条，阈值别太低"）
- **参照**：旧版 QML `mergeDanmaku`（同文本 age<3s → count++ → 显示 "文本 xN"）。但 QML 把所有相同文本都合进**一条**（可无限大 x999）。
- **用户新要求**：不要全合成一条，要能分成**多条** xN；且合并窗口**别设太低**。
- **实现（`DanmakuEngine`）**：
  - `Active` 加 `origText`（原始文本，永不带 xN，用于同文本匹配）/`mergeCount`/`mergeTime`（`born` 用于算合并寿命）。
  - **★ 最终合并策略（用户裁决，默认不合并、挤爆才合并）**：
    - **平时**：每条弹幕正常出生、各自占行（负载均衡铺满各行），**不合并**（消除此前"一直合并→只剩几条→大片空白+阶梯"的问题）。
    - **挤爆判断**：`ScrollActiveCount() >= RowCap(ph/rowH)`（在飞滚动弹幕数 ≥ 行数上限）才判定塞不下。
    - **挤爆时前瞻合并**：把 `_all` 队列里"**将要出现的同文字弹幕**"（`_next` 起、同 mode+同 text、距当前出生 ≤ `_mergeWindowMs`(3000ms)）合并成一条 `text xN`，`_next` 直接跳过这些条（不再单独出生）→ 省下行位，正好让塞不下的弹幕（含这条合并线）填进空位。
    - **★★ 随机合并条数（用户最终裁决 + 2026-09-10 修复"漏合导致后面被迫加速"）**：用户现象="**有的弹幕不合并，导致后面的弹幕加速了**"。定位并修掉两个"漏合"硬伤：
      ① **先摇号后放弃**：旧逻辑先随机目标 N∈[4,12] 再数实际条数，`实际 < N` 就整个不合 → 一波 5 条刷屏有 6/9 概率被漏掉、白白占行。**改为先数实际可合条数、再在 [4, min(实际,12)] 里随机**（能合就一定合，数字仍有随机落差，且不出现 x2/x3）。
      ② **只找紧挨着的**：旧逻辑 `c.text != it.text` 时 `break` → 真实弹幕流是交错的（`生日快乐/生日快乐/天依生日快乐/生日快乐`），中间一插就整段漏合。**改为跳过不同文字继续找**（`continue`；B 站本就是按"窗口内同文本"合并）。
      - 配套：`Item` 加 `consumed` 标记（候选不再连续，不能靠 `_next +=` 跳过，改标记"已并入、不再单独出生"）；`ResetTo` 里清除全部 consumed（seek 后重新参与合并）。
      - **效果**：同文洪峰提速占比 **66.67% → 23.09%**（"该合的合上了，后面就不用加速"）；覆盖行数 16/17 → **17/17 满格**；严格交错场景合并线样本 32631（≈连续同文的 33445，证明交错也能合上）。
    - **★★ 2026-09-10b 修复"太规整"（用户反馈"有点太规整了，不是很好"）**：上一版"能合就一定合"把合并从"偶发救场"变成"只要挤爆就必然合并"→ 屏幕稍满就几乎每条同文字都被吞进 xN 线，只剩几条整齐的 `生日快乐 x11`，**看不到独立弹幕、观感机械**。
      - **改法：给"是否合并"本身加随机、且随拥挤程度自适应**（`ShouldMergeNow`）：刚挤爆（crowdRatio≈1.0）只 **25%** 概率合并（多数弹幕保持独立 → 画面有疏有密、自然）；越挤概率越高，≥2.0 倍时 **95%**（该合就合，防塞不下被迫提速）；中间线性插值。`_mergeProbBase=25` / `_mergeProbMax=95`。
      - **★ 同时修掉一个真 bug（提速连环放大的根因）**：弹幕移除条件原先死等 `elapsed >= dur`，但**提速后的弹幕飞得更快、早就飞出屏幕却还赖在 `_active` 里白占行位** → 新弹幕找不到安全行 → 也被提速，连环放大。改为**真正飞出屏幕即移除**（R2L：`x + W <= 0`；L2R：`x >= pw`；固定弹幕仍按 `elapsed>=dur`）。峰值提速率 **90.57% → 62.25%**。
      - **效果（真实密度，50% 重复率）**：5条/秒 独立92.5%/提速**0%**；15条/秒 独立89.8%/提速**0%**；30条/秒 独立88.1%/提速30.9%；60条/秒 独立86.9%/提速77.2%；60条/秒+85%刷屏 独立59.3%/合并40.7%/提速42.1%。→ **日常到偏重密度完全不提速、九成是独立弹幕**（不再满屏整齐 xN）。
    - **★★ 2026-09-10c 修复"工整的一行行对齐排列"（用户反馈）**：弹幕 y 全是 `topPad + 行号*35` 的**整数倍网格**，且选行时"占用相同就取最上面那行"（`busy < best` 严格小于、平局取先遍历到的）→ 弹幕像排队一样一行行码放，呈表格状。
      - **改法①：行内纵向抖动**（`rowJitter`，±3px）——同一行的弹幕不再排成一条笔直横线；配 `clampY` 夹进 `[0, ph-H]` 防止抖出屏幕被裁。
      - **改法②：并列候选里随机选行**（`pickRow` / `pickTopRow` / `pickBottomRow`）——在"安全且占用最少"的**并列行**中随机挑一行，不再永远从最上面那行开始码放。
      - 安全性：抖动仅 ±3px，而行高 35、弹幕高 27（间隙 8px）→ 相邻行最坏仍留 2px 间隙，不会转成重叠；`busyCount` 的行归属判定阈值 ±17.5px 也不受影响。
      - **效果**：不同 y 值数量 98 个，其中只有 **17.3%** 落在 35px 网格上（修复前 100%）；y 样本如 `1 / 107 / 176 / 208 / 247 / 418 / 561`，不再是 `0/35/70/105…` 的整齐网格。
    - **★★ 2026-09-10d 修复"隔了很多行 + 大量提速"（用真实运行数据定位）**：从运行日志拿到用户真实参数——窗口 **1525×939**、**在飞 118~127 条**、**26 行**（939/35）→ 每行平均 4.6 条，其实**很密**，并非"弹幕少所以有空隙"。
      - **真因**：`willHitSameDir` 的**进入间隔判据 gapGap 太苛刻（30px）**——一行只要有人还在进入点附近，整行就被判"不可用"；高密度下几乎每行都如此 → 大量行被废掉 → 弹幕挤进少数行（"隔了很多行"），同时大量走兜底提速（"弹幕变快"）。
      - **改法**：① **gapGap 30px → 12px**（行可用性大幅提升）；② 选行回到"**安全行里选占用最少、并列随机**"（铺满全部行，避免"只在上部"）；③ 撤销上一版"取最上面安全轨道"（实测只用到 y≤350，即上半屏 37%，是回归）。
      - **效果（真实参数复现）**：26 行**全部占满**（0:8 1:3 2:4 … 25:4，空行 0/26）、**提速占比 0.0%**（原先高峰 60%+）。
    - **★★ 2026-09-10e 修复"同文本弹幕总是很工整地排列"（用户反馈："天依生日快乐那几个字的弹幕总是很工整的排列"）**：根因有两条，都只作用于**高频重复的同一句话**（不同文字因宽度/速度不同会互相错开，所以不显）：
      ① **同文字 → 宽度相同 → 速度 step=(pw+W)/dur 完全相同** → 同一行里的同文本弹幕**永远保持恒定间距**并排飞，像复制粘贴。
         → **改法：每条弹幕加 0~180ms 随机入场延迟**（`entryDelayMs()`），入场点不再重合、行内间距被随机化。
      ② **合并被"挤爆才触发"卡死**：挤爆（在飞≥行数上限）才合并，可一合并数量就落到阈值以下 → 停止合并 → 副本又开始各自出生（自我抵消）。
         实测纯刷屏流下在飞 27 条里 **24 条都是同文本独立副本**。B 站是把刷屏的同文本一律收敛成 `文本 xN`。
         → **改法：真·刷屏（3 秒窗口内同一句话 ≥10 次）→ 无条件合并**（不看挤不挤爆）；其余仍按"挤爆 + 概率自适应"。阈值取 10 是因为取 8 会误伤正常重复（回归测试 A 曾因此失败）。
      - **效果**：纯刷屏流(5条/秒) 屏幕上同文本独立副本 **24 → 12 条**（74% 已并入 xN 线）；非刷屏的独特文本**零误合**（合并条数 0）；回归测试 A/B 全 PASS。
    - `EstWidth` 按 CJK/半角近似重算合并后宽度。
  - **★ 防同行重叠（用户最终裁决 2026-09-05："提速只是备选，默认尽量不追尾"）**：
    - `Active` 加 `speedUp`（默认 1.0）。`pickRow` 两阶段：**①默认换行为主**——遍历所有行，用 `willHitSameDir` 判断放进某行是否追尾，命中则该行不安全跳过；在**安全行**里选**占用最少**的。**②仅当所有行都追尾**（无安全行）才退到占用最少行 + 随机 `speedUp 1.3~1.8` 提速救场。
    - **★ `willHitSameDir` 判据修正（2026-09-05，关键）**：之前用"任意时刻区间相交"判追尾，把"**快弹幕追上慢弹幕**"也判成追尾 → 大量弹幕找不到安全行 → 几乎全兜底提速（"大量变成加快弹幕"）。修正为**只对"会持续贴脸"判追尾**：
      - **速度差 ≥ 15%**（一快一慢）→ 快者会**超过**慢者、自然错开（只是瞬间穿过）→ **安全**，不判追尾。
      - **几乎同速**（速度差 < 15%）→ 会相对静止贴飞；此时若进入间距 < gapGap（贴上）→ 追尾（返回 true）。
    - 位置更新 `step *= speedUp`。
  - 导出接口 `DshEngine_SetDanmakuMerge(windowMs)`（单参：前瞻窗口 ms，默认 3000），C# 首次喂弹幕前调用 `SetDanmakuMerge(3000)`。
  - **★ seek 重置弹幕（2026-09-05 修复"弹幕一次性/往前没弹幕"）**：此前弹幕引擎的 `_next` 只在播放入口喂一次后一直往前推进，seek 后时钟跳回之前位置、但 `_next` 已越过那些弹幕 → 之前位置的弹幕不再出生。新增 `DanmakuEngine::ResetTo(clockMs)`：二分找到 `_all` 里第一条 `time >= clockMs` 设为 `_next`、并清空 `_active`，让下帧 `Tick(新时钟)` 让该位置起的弹幕重新出生。`DshEngine_SeekJump` 内调用 `g_dm.ResetTo(ms)`（`SeekPreview` 预览不重置，避免拖动预览弹幕乱跳）。
- **离线验证**：
  - `test_merge.cpp`（PASS）：A 不挤爆（8 条重复文本、在飞 8<17 行）→ **0 合并**；B 挤爆（400 条刷屏、在飞达行上限）→ 触发合并，计数 6 种不同值（x4/x6/x8/x9/x10/x11）**有随机落差、不再 x2/x3**，覆盖 **17/17 行满格**。
  - **不规整度自检（2026-09-10c）**：150 条弹幕累计出现 **98 个不同 y 值**，落在 35px 网格上的仅 **17.3%**（修复前 100%）；滚动/顶固/底固三者都加了抖动+并列随机。
  - **漏合修复对照（2026-09-10）**：同文洪峰提速占比 **66.67% → 23.09%**；严格交错场景合并线样本 32631（旧逻辑"遇不同文字就停"会几乎全漏合）。
  - 防重叠自检（修正判据后）：正常/中等/洪峰/极端长差 四档**持续重叠(≥240ms)全为 0**（只留"瞬间穿过"可接受），**提速占比 0%**（都找到安全行），同行最大并存 3~5 条**错开分布**。
  - seek 重置自检：播到 8s（在飞 19）→ 往前 seek 到 4s 再播 0.6s（在飞 2，恢复 4s 起弹幕）→ 往后 seek 到 15s 再播 0.6s（在飞 2）→ **PASS**，seek 起有对应弹幕、非"一次性/没弹幕"。
- `dsh_engine.dll`（5.27MB，FreeType 测宽 + DshEngine_SetDanmakuMerge 单参导出 + 修正 speedUp 防重叠 + seek ResetTo）已重建并复制到运行目录。

---

## 六、待办/决策 ⏳（下一步）

- [ ] **运行验证**：重启 WinUIGui 播放视频，看滚动弹幕平时是否**铺满各行、不空白**；只在**挤爆**时才把"将要出现的同文字"合并成 xN 救场（不阶梯、不空白）。
- [ ] mode7 高级弹幕：目前当滚动处理（mode!=4/5 都走滚动）。
- [ ] 位置诊断：读 `dm_debug.log` 的 `TICK pos=.. active=..` 看数量是否合理。

---

## 关键文件
- `dsh_engine/danmaku_lanes.h` / `.cpp`（B 站轨道算法 + DanmakuEngine）
- `dsh_engine/test_danmaku_lanes.cpp`（命令行对拍）
- `dsh_engine/dsh_engine.cpp`（SetDanmaku/GetDanmakuLayout 导出）
- `WinUIGui/tv_...`（无）
- `WinUIGui/VideoWindow.xaml.cs`（精简为喂数据/读坐标）
- 参照权威算法：`DanmakuFlameMaster-解释文档.md` §5.2 + DFM `DanmakusRetainer.java`/`DanmakuUtils.java`
