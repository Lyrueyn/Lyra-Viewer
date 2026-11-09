# 弹幕模块审计报告

> 审计对象：`app/dsh_engine/danmaku_gpu.h`(526)、`danmaku_lanes.{h,cpp}`(180/384)、`dsh_engine.cpp` 弹幕段、`app/WinUIGui/VideoWindow.xaml.cs` 弹幕段、`app/backend/app/{bilibili,services}.py` 弹幕链。
> 方式：只读静态审计（未启动播放器/后端，未改任何文件）。文中"推测（需实测）"= 未跑通真机验证的量化估计。

## 0. 一句话结论
GPU 字形图集这条路是对的（0.04ms/帧的着色器+上传确实已不是瓶颈），但**每帧的 CPU 侧调度（出生无上限、行分配 O(行×在飞)、每字重建四边形、按需栅格化字形）与一次"伪装成 seek 的弹幕重置"才是当前真正的卡顿源**；再叠加"窗口一改尺寸就重建 32MB 图集 + 重编着色器 + 重开 FreeType"和"喂弹幕在 UI 线程逐字测宽"，四个点合起来能解释绝大多数弹幕相关的冻结与掉帧。可改、且都不需要动渲染管线骨架。

## 1. 现状架构摘要（≤250 字）
C# 在 `CompositionTarget.Rendering`（vsync，60/120Hz）里先 `UpdateDanmaku()` 后 `Present()`（VideoWindow.xaml.cs:3096-3102）。`UpdateDanmaku` 只做两件事：必要时 `SetDanmaku` 喂全量，然后每帧 `GetDanmakuLayout(null×4,0)`（:4958-4961）。全部布局/出生/轨道都在 DLL：`Tick()` 用 A/V 主时钟（`dsh_clock_ms_double`）出生 + `pickRow/pickTopRow/pickBottomRow` 选行，再把在飞弹幕交给 `dm_gpu_build()` 逐字查图集、拼 6 顶点/字，`flush()` 一次 Draw 叠在视频上（dsh_engine.cpp:1948-1988、924-942、697-703）。后端一次性返回全量弹幕，去重由 DB 唯一索引兜底。HDR 直通配色公式已核对正确。

## 2. 问题清单

### P0-1 改屏蔽词/刷新弹幕会触发一次**真正的视频 seek**
- 证据：`if (posMs > 15000) DshNative.DshEngine_SeekJump(posMs);`（VideoWindow.xaml.cs:4996）；`_dmFeed = false` 由 `DmBlockBox_TextChanged` 设置（:4766）；下一帧 `!_dmFeed` 即重新喂（:4958-4959）。
- 影响：在屏蔽词框里**每敲一个字符**就 = 一次全量 seek（清帧队列 `g_qCount=0`、改 `lastPtsUs`、重解码 GOP，dsh_engine.cpp:2015-2021）→ 画面闪一下、音频断一下、进度条抖。刷新弹幕同理多一次。
- 根因：没有"只重置弹幕游标"的轻量导出，借用了 `SeekJump`。
- 方案：新增 `DshEngine_DanmakuResetTo(int64 ms){ g_dm.ResetTo((long)ms); }`，C# 改调它（语义与 `ResetTo` 完全一致，见 danmaku_lanes.cpp:273-286）。
- 验收：`dsh_engine.log` 里改屏蔽词前后 "跳转 seek" 行数不增加；`DM: TICK` 后仍在飞正常。
- 工作量：0.3 人日。

### P0-2 喂弹幕时在 UI 线程逐字 FreeType 测宽
- 证据：`for (...) { d.width = texts && texts[i] ? dm_measure_w(texts[i]) : 0; ... }`（dsh_engine.cpp:1821）；`dm_measure_w` 内部每字符 `FT_Get_Char_Index` + `FT_Load_Glyph`（:1794-1801）；调用点在渲染帧内（VideoWindow.xaml.cs:4993）。
- 影响：1 万条×12 字 = 12 万次 FT 调用，10 万条 = 百万次；**单帧数百 ms~数秒 UI 冻结**（换视频、刷新、改屏蔽词全命中）。推测（需实测）单次 `FT_Load_Glyph`(DEFAULT) ≈1~3µs。
- 根因：宽度本可"出生时才需要"，却在入库时全量同步预计算。
- 方案：① `DmInput.width` 允许 0，`Tick` 出生该条时才测；② 加 `unordered_map<uint32_t,int> advCache` 复用同字 advance；③ 或把预计算挪到工作线程（`SetDanmaku` 只存裸数据）。
- 验收：`SetDanmaku` 前后加计时日志，10 万条 < 20ms；改屏蔽词时 UI 无冻结（日志帧间隔不超 50ms）。
- 工作量：1 人日。

### P0-3 出生调度无上限：渲染停摆恢复后单帧灌入成百上千条
- 证据：`while (_next < _all.size() && _all[_next].time <= clockL) {`（danmaku_lanes.cpp:294）——循环内无任何条数上限/落后快进；`CompositionTarget.Rendering` 在遮挡/最小化时会停且不一定恢复（VideoWindow.xaml.cs:3104-3106 自述）；`if (pw <= 1 || ph <= 1) return;`（:4955）也会整帧跳过 Tick。
- 影响：恢复瞬间时钟已跳 N 秒 → 单帧出生 N 百条 → 每条都要 `pickRow`（O(行×在飞)）再加 `erase` → 几十~几百 ms 冻结 + 屏幕瞬间糊满（与 P1-4 是同一放大器）。
- 根因：`Tick` 假设时钟单调小步推进。
- 方案：① `Tick` 内加 `int bornThisFrame`，超过 `kMaxBirthPerFrame=32` 即 `ResetTo(clockL)` 快进并打日志；② 若 `clockL - 上次时钟 > 1000ms`（停摆恢复）直接 `ResetTo`。
- 验收：最小化 10s 后恢复，`DM: TICK active` 回落到 ≤ 行上限×4，无 >50ms 帧。
- 工作量：0.5 人日。

### P0-4 窗口尺寸一变就重建 32MB 图集 + 重编两个着色器 + 重开 FreeType
- 证据：`create_danmaku_layer` 的就绪判定含 `e.dmW==w && e.dmH==h`（dsh_engine.cpp:744），不满足则 `if(e.dmGpuOn){ e.dmGpu.shutdown(); e.dmGpu.init(e.dev, 4096); }`（:745）+ `FT_Done_Face/FT_Init_FreeType/FT_New_Face`（:751-756）+ `createShaders()` 里两次 `D3DCompile`（danmaku_gpu.h:479-480）；而 `VideoHost.SizeChanged` 直接 `DshEngine_Resize`（VideoWindow.xaml.cs:242-247）无节流。
- 影响：拖窗口边缘时每个 size 事件都做一次 2048 字形表清空 + 4096×4096 R8G8（**32MB**）纹理重分配 + 两次运行时 HLSL 编译 + 读 simhei.ttf → 明显拖影卡顿。也是引擎里"图集被无限创建"历史告警的来源（:1165 注释）。
- 根因：字形图集与"层尺寸"耦合；着色器/图集没有独立的生命周期。
- 方案：① 尺寸变化只更新 `dmW/dmH`（字形 UV 是图集归一化坐标，与窗口尺寸无关）；② 只在 `uiScale`/`dmFontPct` 变时重建；③ 着色器只编一次（`dmGpu.shutdown()` 里别放 `vs/ps`，或加 `keepShaders` 参数）；④ C# 侧 `SizeChanged` 加 50ms 去抖。
- 验收：拖动窗口时日志不再出现 "FT: layer ok / GPU 路径已启用"；拖拽帧率不低于静止时 80%。
- 工作量：0.5 人日。

### P1-1 无字体回退：emoji / 生僻字变方框，且测宽与实绘不一致
- 证据：只加载一个字体 `FT_New_Face(e.ft,"C:\\Windows\\Fonts\\simhei.ttf",0,&e.ftFace)`（dsh_engine.cpp:756）；`dm_gpu_glyph` **没有 gi==0 检查**：`FT_UInt gi=FT_Get_Char_Index(...); if(FT_Load_Glyph(g.ftFace,gi,...)!=0) return nullptr;`（:895-896）→ gi=0 照常加载 `.notdef` 画出空心方框；而测宽处**有**检查并用 `fallback` 替代（:1798）。
- 影响：emoji、CJK Ext-B 生僻字 → 成排方框；且测宽（整字宽占位）与实绘（.notdef）不一致 → 该条 W≠实际像素宽 → `step=(pw+W)/dur` 与车道占用算错。
- 方案：建 2~3 级字体回退链（simhei → msyh.ttc → seguisym.ttf/segoeuiemoji），`FT_Get_Char_Index` 逐级尝试；仍缺字则**跳过该字形**（不画方块）但 advance 按 fallback 推进；测宽复用同一函数。
- 验收：含 😂/𠮷 的弹幕不再出现方框；`dm_measure_w` 与实绘宽度差 ≤1px（可用现有 `test_dll_host.exe` 出图对比）。
- 工作量：1 人日。

### P1-2 图集容量与显存记账都错了；4K + 200% 字号会反复淘汰重栅格化
- 证据：代码传 4096（dsh_engine.cpp:738/745），`R8G8_UNORM`（danmaku_gpu.h:436）= **2 字节/像素 → 4096²=32MB**，但注释写"单张 4096² RG8 = 16MB"（danmaku_gpu.h:274）、CHANGELOG 写"2048²/8MB"（CHANGELOG.md:1318/1331）；字体 `fontPx=27*uiScale*dmFontPct`（:759），4K@200% → 81px，单字形含 2px 描边 ≈85²，容量仅 ~2300 字；项目自己已记录"构建耗时最大 50.2 → 25.0ms"（CHANGELOG.md:2819）。
- 影响：生僻字多/字号大的场景反复 LRU 淘汰→重新 FreeType 栅格化 + **每字形一次 `UpdateSubresource`**（danmaku_gpu.h:180，非批量）→ 25ms 级帧尖峰，60Hz 必掉帧。
- 方案（按性价比）：① 改 **R8**（描边由 PS 做 4-tap max 膨胀）→ 显存 32→16MB、sprite 不外扩 → 容量 +20~30%，并省掉膨胀描边这圈 CPU；② 中期换 **SDF 图集**（48px 单元 → ~7000 字，换字号**不必重建图集**，顺带解决 P0-4 的 ②）；③ 短期把"新字形上传"攒到帧末一次（staging + `CopySubresourceRegion`），别一字一次 UpdateSubresource。
- 验收：4K+200% 连播 30 分钟，日志 `atlasEvictCount` 增量 < 500、`atlasSkipCount` 保持 0；弹幕构建耗时 P99 < 2ms。
- 工作量：① 1 人日；② 3 人日。

### P1-3 每帧每字重新查表拼四边形（无"字形串"缓存）
- 证据：`dm_gpu_build` 对每条弹幕每个字符调 `dm_gpu_glyph(cp)`（内部 `g.dmGpu.find(key)`，danmaku_gpu.h:133-139）后 `addGlyph` 每次 `verts.insert(verts.end(), v, v+6)`（:207）；`GetDanmakuLayout` 每帧新建 4 个 `std::vector` 并 `reserve(n)`（dsh_engine.cpp:1968-1971）。
- 影响：8000 字/帧 ≈ 8000 次哈希查找 + 8000 次 6 元素插入 + 4 次堆分配；推测（需实测）0.5~1.0ms/帧，120Hz 屏翻倍，且全在 UI 线程。
- 方案：① 弹幕**出生时**生成一次 `GlyphRun{vector<{uv,dx}>}` 存进 `Active`，每帧只做 `x0=penX+dx` 写实例；② `verts` 用 `resize(n*6)`+指针直写替代 insert；③ 4 个 scratch vector 提成成员；④ 再上 instanced（共用四边形 + `SV_VertexID`，每字仅 24B，现 192B）→ 上传 0.2→0.05MB/帧。
- 验收：引擎加 `QueryPerformanceCounter` 的 build 计时日志，300 条/3000 字场景 P99 从 ~0.9ms 降到 <0.25ms。
- 工作量：1.5 人日（含实例化 2.5 人日）。

### P1-4 行分配 O(行×在飞)，且左→右弹幕（mode6）不计行占用
- 证据：`pickRow` 每行都遍历全部 actives 判追尾（danmaku_lanes.cpp:125-137，内层 `willHitSameDir` 一次）；占用统计只算 mode1：`busyCount(actives, TYPE_SCROLL_RL, rowY, rowH)`（:127）；L2R 与 R2L 的 `pickRow` 调用是**完全相同的三元两支**（:327-328、356-357，死代码）。
- 影响：每次出生 2×行×在飞 次迭代（35 行×500 在飞 = 3.5 万），叠加 P0-3 的批量出生就是 O(N²)；mode6 弹幕在"mode1 最少"的行里互相重叠（`willHitSameDir` 对不同 mode 直接返回 false）。
- 方案：维护 `行号 → 在飞下标数组` 桶，pickRow 只扫目标行（O(行+在飞)）；busy 统计并入 mode 6/7；删掉重复三元。
- 验收：现有 `test_danmaku_lanes.cpp` 对拍输出不变（同 y 分布/提速占比），新增"60 条/秒"压测：单帧 Tick 耗时 <0.3ms。
- 工作量：1 人日。

### P1-5 长视频全量加载，无时间窗/分段
- 证据：`SELECT time, mode, size, color, text FROM danmaku WHERE video_id=? ORDER BY time`（services.py:2097-2103，无 LIMIT/无 from-to）；C# 一次拉全量并整体喂入（VideoWindow.xaml.cs:5031、5062）；C++ `_all` 全量常驻（danmaku_lanes.h:138）。
- 影响：10 万条视频 ≈ JSON 8MB + C# 字符串 ~15MB + `_all`(48B/条+wstring) ~15MB ≈ 40MB 常驻；且 P0-2 的测宽成本随条数线性爆炸。
- 方案：后端加 `?from=&to=`（或以 360s 为段）；C# 维护 3 段窗口（当前 + 前段缓存 + 后段预取），跨界用"增量 push"（新导出 `DshEngine_DanmakuAppend`，按 time 归并、不动 `_next`）。注：`consumed` 语义要求窗口替换小心，可保留全量但改存紧凑数组 `(time,mode,color,w,H,offset)` + 文本池。
- 验收：10 万条视频首帧喂入 < 20ms，常驻内存增量 < 8MB。
- 工作量：2 人日（后端 0.5 + 前端 1.5）。

### P2（汇总，均为小改）
1. **入场随机延迟破坏时间轴**：`entryDelayMs()` 0~180ms（danmaku_lanes.cpp:66，用于 :326/341/349/355）→ 每条最多晚 180ms；建议仅对高频刷屏启用或降到 ±40ms。
2. **弹幕画满整窗口含黑边**：布局用 `ph=g.h`（:1950），letterbox 区也有弹幕；B 站只在视频矩形内。建议加 `videoRect` 选项。
3. **`size` 字段被丢弃**：DB 有 `size`（services.py:2100），C# 只读 time/mode/color/text（VideoWindow.xaml.cs:5039）→ 18/25/36 三档字号全按同尺寸渲染。
4. **mode7/8 当滚动处理**：`else` 分支一律滚动（danmaku_lanes.cpp:353-357），mode8 脚本弹幕会把代码原文滚出来；建议 mode≥7 过滤。
5. **速度随窗口宽度变**：`_duration` 用物理 `pw`（danmaku_lanes.h:172-176）→ `pw=3051` 夹到 9000ms、`pw=1000` 仅 5.6s；改窗口即改观感。
6. **除零隐患**：`cand[rndU32()%(unsigned)nc]`（:143）在 `nc==0` 时崩（行数>512 才可达），建议加 `if(nc<=0) return topPad;`。
7. **屏蔽只做子串匹配**（VideoWindow.xaml.cs:4985-4986），无正则/按用户屏蔽；亦无弹幕发送（全仓无发送接口，确认不支持）。
8. **文档全面漂移**：`弹幕层技术文档.md:64`（msyh.ttc/18px）、`dmReadTex/CopyResource/每帧重建 SRV`（:78-79）与现码（simhei/27px/无副本）全不符；`danmaku_gpu.h:274` 的 16MB、`CHANGELOG.md:1318/1331` 的 2048²/8MB 也错。
9. （已核实**不是**问题：HDR 直通配色 `ps:60-68` 的反预乘/白场/PQ 与预乘混合正确；`db.py:165-166` 的内容唯一索引已堵住重复入库。）

## 3. 优化方案（按收益排序）
1. **去伪 seek + 宽度惰性测量**（P0-1/P0-2）：零架构改动，消掉"改屏蔽词卡一下""换视频冻一下"两大可感问题。
2. **出生上限 + 停摆快进**（P0-3）：约 20 行改动，消灭最凶的一类帧尖峰。
3. **图集/着色器生命周期解耦**（P0-4）：拖窗口不再卡，顺带省掉最贵的重复初始化。
4. **字形 run 缓存 + 去分配**（P1-3）：把"每帧每字查表"变成"每帧每字一次加法"。
5. **字体回退链**（P1-1）：观感最显眼（方框），顺手修好测宽=实绘。
6. **R8/SDF 图集 + 批量上传**（P1-2）：解决高字号/生僻字下的 25ms 尖峰与 32MB 显存。
7. **行桶化 + mode6 修正**（P1-4）：布局 O(行×在飞)→O(行+在飞)，高密度下限保障。
8. **分段/时间窗加载**（P1-5）：长视频内存与首帧延迟的结构性解法。
9. **P2 批处理**：size 字段、mode7/8 过滤、视频矩形、时长基准、文档重写。

## 4. 推荐实施顺序（Top 10）
| # | 事项 | 对应 | 预期收益 | 工作量 |
|---|---|---|---|---|
| 1 | 新增 `DanmakuResetTo` 导出，C# 不再 SeekJump | P0-1 | 改屏蔽词/刷新：0 次多余 seek | 0.3 人日 |
| 2 | 宽度改惰性 + advance 缓存 | P0-2 | 10 万条喂入 秒级 → <20ms | 1 人日 |
| 3 | `Tick` 出生上限 + 时钟跳变快进 | P0-3 | 恢复渲染不再单帧灌 N 百条 | 0.5 人日 |
| 4 | 尺寸与图集/着色器解耦（+C# 去抖） | P0-4 | 拖窗口掉帧消失；省 N×32MB 重分配 | 0.5 人日 |
| 5 | 字形 run 缓存 + verts 直写 + scratch 复用 | P1-3 | UI 线程 build ~0.9 → <0.25ms/帧 | 1.5 人日 |
| 6 | 字体回退链（simhei→msyh→seguisym） | P1-1 | 方框消除；测宽=实绘 | 1 人日 |
| 7 | 图集 R8 + PS 描边 + 批量上传 | P1-2 | 显存 32→16MB；容量 +25%；去 25ms 尖峰 | 1 人日 |
| 8 | 行桶化 + mode6/7 占用统计 | P1-4 | 高密度 Tick <0.3ms | 1 人日 |
| 9 | 后端时间窗 + 分段预取 | P1-5 | 常驻内存 −30MB；首帧 <20ms | 2 人日 |
| 10 | instanced 渲染（可选） | P1-3④ | 顶点上传 −75%，CPU −30% | 2.5 人日 |

验收通用口径：`dsh_engine.log` 的 `DM: TICK`/`弹幕图集`/`ATLAS` 三类日志 + 引擎内 `QueryPerformanceCounter` 打点（Tick / build / flush 三段分别计时），60Hz 与 120Hz 各跑一段 5 分钟密集弹幕，记录 P50/P99。

## 5. 审计覆盖与未覆盖
- **已完整读**：`danmaku_gpu.h`(1-526)、`danmaku_lanes.h`(1-180)、`danmaku_lanes.cpp`(1-384)；**已精读**：`dsh_engine.cpp` 80-161/660-1080/1460-1490/1620-1710/1780-1990/2000-2030/2800-2850；**已读**：`VideoWindow.xaml.cs` 228-267、3025-3130、4738-4810、4945-5075；`VideoWindow.xaml` 弹幕控件默认值；`services.py` 205-225、1156-1185、2090-2105、2233-2345；`bilibili.py` 178-250、251-347、348-410；`db.py` 弹幕表/索引；`CHANGELOG.md` 1310-1340、2805-2825；三份设计与进度文档。
- **未覆盖**：① 未跑真机，ms/MB 均为静态推算或引用项目既有实测（CHANGELOG:2819、1331）；② Vulkan 路径的弹幕合成只看了调用点，未读 `vulkan_video.h`；③ 设备丢失/重建（:2296-2346）未做时序推演；④ `audio_out.h` 的 T1 主时钟发布细节未读；⑤ 未重跑 `test_danmaku_lanes.exe`/`test_merge.exe` 核对现有结论。
