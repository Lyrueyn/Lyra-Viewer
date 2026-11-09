# EPUB R6 独立验收结论暨 R7 返工任务书

**结论：R6 不通过完整验收，不能发布。** R6 修好了 R5 列出的资源等待关闭、动画关闭、完成凭据、本地未知版本、SVG 高亮、长查询提示和 v2 降级，这些都要保留。但“提交中的章节”和“已经在读的章节”仍然没有真正分开：取消后页码回到第 0 页；旧导航迟到时会覆盖较新的章节；资源/排版故障后留下候选正文和错误身份；关闭发生在恢复旧视图期间时，阅读器被清空。宿主侧有三条确定失败：失败重试时位置身份被重建、等待设置回执会卡住位置保存、两个进程的设置顺序号会撞车。

## 0. 给新实施方的背景（自包含）

本项目由“验收方”写任务书和独立验收，“实施方”改代码并提交证据。自 R7 起实施方换成新的模型，没有前几轮对话，因此本节说明必需背景。

- 产品：Lyra 本地媒体播放器的 EPUB 阅读扩展。扩展源码在 `D:\Temp\Lyra-Extension-Epub`（git，R6 分支 `r6`，提交 `99bd102f4c9b4c1a92668d04e2e12260e523ad94`）。核心文件：`reader.js`（阅读器状态、导航、保存、关闭）、`paginator.js`（iframe 分页、视图提交/恢复）、`reader-state.js`（与宿主的协议）、搜索与可见性模型。
- 宿主：WinUI 程序，源码在 `D:\Temp\lyra_epub_host_r6`，关键文件 `ReaderStateStore.cs`（位置/书签/设置落盘、授权 grant、完成凭据）以及 WebView2 桥接与关闭计时器（1500ms 关闭预算）。
- 原始需求：[原始任务书 A01–A25](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md#L367>)。上一轮要求：[R6 任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R5暨DeepSeek返工任务书-R6.md>)。R6 实施方回报：[DeepSeek实施回报-R6](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/submitted-report/DeepSeek实施回报-R6.md>)。
- 名词：**view（视图）**=用户正在看的那一章的 DOM、章节号、页码、锚点；**commit（提交）**=新章节完全准备好后一次性替换旧视图；**rollback（回滚）**=失败或取消时回到旧视图；**finalize（定稿关闭）**=窗口关闭时保存并回 `host.finalized` 回执；**grant（授权）**=宿主给一次设置写入发的许可凭证；**order/sequence（顺序号）**=判断哪次写入更新的编号；**operationId（操作身份）**=同一次用户操作重试时必须不变的编号。
- 分工规则：只在新的 R7 分支、R7 候选和 R7 证据目录写入；不改 R6 及之前任何原件；不发布、不安装到用户环境；未证明的项写 PARTIAL/UNTESTED，不能写 PASS。

## 1. 被验对象与证据边界

| 项目 | 本轮冻结值 |
|---|---|
| 扩展 | 0.10.0；分支 `r6`，提交 `99bd102f4c9b4c1a92668d04e2e12260e523ad94`，工作树干净 |
| 候选包 | `candidates-r6/lyra-ext-epub-0.10.0-candidate.zip`，98,840 字节，SHA-256 `F2B2741A166749E7108E010151A46246AC2DF40BF1F0861050E8E12D612C535C` |
| 宿主 DLL / reader.js | `3F30CCC9915993F058DAD62879F1619AC1EBD5B735CE1741D09FAEE86B28719C` / `5A81D526B8126B590738BC50652DB829D701FDB9E3319240766FB3D07C2FC837` |
| 身份总账 | [identity.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/identity.json>)、[前轮完整性](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/prior-integrity.json>) |
| 原生补充 | [补充报告冻结副本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/submitted-native-supplement/DeepSeek补充测试-R6.md>)（SHA-256 `C2E9EA6D…7441`）；33 个文件，提交清单 32 项逐项核对一致，快照身份 `60b47115…d7ef7` |

证据分四层，不能互相冒名：①验收方亲跑真实 Edge + 原模块 + 隔离 localStorage，门控点公开；②原 JS→原 C# 桥（真实 `ReaderStateStore` 写隔离文件，WinUI 事件和计时器是替身）；③模块/jsdom 交错测试；④实施方运行的原生候选，验收方核对身份、日志、文件和截图。

汇总：[浏览器总账](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/browser-summary.json>)、[专项索引](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r6/partial-audit-index.json>)。

- 浏览器：49 个继承观察在 R6 上回归，除 B14（旧判据已在 R5 排除）、R03（旧探针错误）外，只有 D05 仍复现；R6 自带 E01–E07 重跑全部正常；新增 F01–F06 中 F06 通过，F01–F05 失败。
- 宿主（层②）：61 条有效观察，3 条 FAIL（E01、L02、B02），其余 PASS，0 探针错误；昂贵的 K02 未重跑。
- 模型（层③）：R6 新交错 55 条：8 PASS / 44 FAIL / 3 探针错误（jsdom 文档复用和门控范围问题，单独保留，不计产品失败）。继承的 R4/R5 阶段测试另有 16 条 FAIL，与新交错是同一组根因。失败条数是同一缺陷在不同阶段的重复，不代表 60 个独立缺陷。

## 2. R6 已确认修复，R7 必须保留

| 范围 | 独立确认 |
|---|---|
| 资源等待中取消/关闭（R6 E01/E02） | 真实 Edge 重跑 defect=false；finalize 保存旧稳定位置 textOffset2506，不写空锚点 |
| 动画中关闭（E06） | finalize 与 pagehide 两条路径都保存目标页并在 reload 后回到该页 |
| 迟到文件（E07） | finalize 后文件字节才到，error 为空、ready 不变 |
| SVG 高亮（E03/F06） | 高亮用 SVG 命名空间 tspan，命中文字可见，宽度 103.7px、1 个矩形 |
| 长查询（E04）、重复 visibility 声明（E05） | 提示明确；最后有效声明生效 |
| 宿主授权与完成凭据 | S01/S02/S03/J11/J14/C01/K01/C02、容量背压、L01 资源关闭、local 未知版本 999、v2 生命周期全部 PASS；E03“A 失败、B 成功”关闭现在诚实返回 ok:false 并带 closeDiagnostics |
| A08 原生重启 | 截图核对：重启后第 3/14 页、paragraph 12 开头，书签 1 个，行距 2.25；位置文件 positionRevision 2、bookmarks 1 |

## 3. 必须修复的确定问题

### 3.1 取消后页码丢失（P1，F01 / R6-CANCEL-*-input/close）

真实 Edge F01：用户在第 4 页（textOffset2506），切章在准备阶段被取消。取消后 `state.pageIndex` 变 0，paginator 仍显示第 4 页；用户再按“下一页”变成第 1 页，保存 textOffset578，**阅读位置倒退 3 页**。模型层同样：`before.page=1`→`after.page=0`，而 paginator 仍在 1。

根因：`reader.js` 的 `restoreNavView`（约 1043 行）优先取 `oldView.pageIndex`，而 `oldView` 来自 `state.committedView`，它只在提交那一刻拍照（`paginator.js` 282 行 `_committedView`），之后的翻页不会更新它。所以“提交快照”被当成了“当前视图”。

### 3.2 旧导航迟到后覆盖较新的章节（P1，F02/F03 / R6-CANCEL-*-nav/open、R6-RESTORE-*）

F02：较新导航已显示第 2 章（FRESH 正文），旧导航随后返回，`restoreNavView` 把 `state.spineIndex/prepared` 改回 0，但 DOM 仍是第 2 章——**正文和身份不一致**，下一次保存会把第 2 章的位置写成第 0 章。模型层 `R6-CANCEL-prepare-nav`：`after.sp=0, pgIdx=2`，资源 owner 只剩 3，还多出一次保存。

F03_NEWER：旧导航的“恢复旧视图”是异步 `setSection`，期间较新导航完成；恢复回调落地后把视图清空（ready=false、pgSpine=-1、页数 1、正文为空）。

根因：取消路径不检查“我是不是仍是最新的导航/打开意图”就恢复；恢复本身也不带代号，晚到时会覆盖或清空别人的视图。

### 3.3 关闭发生在恢复旧视图期间：阅读器被重新激活、回执不反映状态（P1，F03_CLOSE / R6-CANCEL-*-finalize、R6-RESTORE-*-finalize）

F03_CLOSE：恢复旧视图时触发关闭，`host.finalized ok=true` 已发出，但恢复在定稿后继续执行布局/定位，最后 `ready=true, finalizing=true`。模型层 `R6-CANCEL-resources-finalize`：定稿后仍有 `dom-submit`、`set-return`，paginator 被清空（pgIdx=-1、DOM 为空）。要求：finalize 之后任何导航和恢复都不能再改 DOM/布局，也不能解冻 paginator。

### 3.4 资源/排版/高亮故障后留下候选正文（P1，F04 / R6-FAULT-*、R6-RESTORE-THROWS、R6-SYNC-*）

F04：资源等待抛异常，结果 `ready=false`，paginator 身份是第 1 章，DOM 是候选正文“OLDQUERY target”，只显示错误。模型层 `R6-FAULT-resources/layout/highlight` 一致：候选 DOM 留在 iframe 里；highlight 故障时资源 owner 被清空、稳定位置被改成 textOffset1。`R6-RESTORE-THROWS`：恢复本身抛异常后 `busy=true` 永久卡住。`R6-SYNC-layout/highlight`：在同步布局/高亮回调里取消，页码丢失（同 3.1）。

要求：失败后恢复到**故障前那一刻**的完整视图（DOM、章节、页码、锚点、资源 owner）；恢复也失败时清掉 busy/loading，进入明确的“可重试”状态，不能挂住。

### 3.5 失败重试时位置操作身份被重建（P2，宿主 E01）

原 JS→原 C#：A 书 textOffset100 保存失败，**中间没有任何新输入**，换 B 前的 flush 重发时 `sequence 1700000000020000→1700000000420000`、`operationId` 和 `savedAt` 全变。R6-04 要求同一失败操作重试复用原身份。重开后位置仍是 100，没有丢数据，但宿主无法识别这是同一次操作的重试。

### 3.6 设置回执等待阻塞了位置保存（P2，宿主 L02，R7 判 FAIL）

扣住设置 grant 的回执，宿主 1500ms 关闭计时器到期时，有效的位置 100 **从未发出**，盘上 `position:null`，`acks[]` 为空，pending 仍是 fontSize。R5 记作设计观察；R6 已明确要求关闭时位置与设置并行、各自有界，因此本轮判未完成需求。根因：`finishAndAck` 先 `await flushPrefs()` 再 `await flushSave()`（`reader.js` 1578–1579 行），串行。

### 3.7 两个进程设置顺序号撞车（P2，宿主 B02）

两个真实进程共享隔离数据目录，各自对 fontSize 申请 grant 后保存：A applied，B 被判 superseded，盘上 `preferenceOrders.fontSize=1`、值为 A 的 18。两个进程的 order 都是从很小的值起算、互相不可比较，后发起的 B 反而被判过期，用户最后一次修改丢失。顺序号必须跨进程单调（例如由宿主在授权时统一分配，或带进程/会话分量的可比较序号），不能各自本地从 1 计数。

### 3.8 搜索可见性模型与实际显示仍不一致（P2，F05 / Z-VIS、D05）

F05 实际 Edge 计算样式与搜索结果对照：

| 片段 | 清洗后实际显示 | 搜索命中 | 应为 |
|---|---|---|---|
| `visibility:hidden!important;visibility:visible` | 清洗器去掉了 `!important`，结果可见 | 0 | 1 |
| `display:none;display:block` | 清洗后只剩 `display:none`，隐藏 | 1 | 0 |
| `visibility:hidden;visibility:invalid` | 无效值被丢弃，隐藏 | 1 | 0 |

D05：SVG 里隐藏 tspan 被清洗器去掉 style 后实际可见（renderText 含 HIDDEN），但模型仍按原文判隐藏，命中偏移与渲染文本不一致。

根因：可见性模型按**原始源码**解析，但真正显示的是**清洗后的文档**。要求：索引和高亮一律以清洗后实际交给 iframe 的文档为准；无效值按 CSS 规则丢弃；与清洗器共用一个解析函数。

## 4. 原生补充核对

- A08：PASS（正常稳定路径），截图、日志、位置文件、设置文件互相吻合。补充报告写“positionRevision=3、page3/14 textOffset1758”，但冻结的位置文件实际是 positionRevision 2、textOffset3072/pageIndex3，日志里最后一次保存是 1758。截图与 1758 吻合，说明文件不是重启后最后状态的快照。R7 报告必须写明文件是在哪个时刻采集的，数字照实际文件抄。另外报告正文写 `native/a08/a08-identity.json`，实际文件在 `native/a08-identity.json`（约 897KB；SHA256SUMS 本身与实际文件一致），报告路径要对上。
- A11：只覆盖“ready 后图片晚完成”，图片几何改变后页数仍是 18、没有空白，可以作为正例；字体加载和横竖图未构造，仍 PARTIAL。
- A23：原生 200 次点击 0 次成功，诚实记失败，不计入。浏览器 200/200 与 10 次换书保留为浏览器层正例。原生 200 次请改用阅读器内部公开的调试入口或键盘快捷键驱动，不要靠固定窗口坐标。
- A21：c12+R6 宿主有书、旧宿主+R6 包有书、R3 客户端+R6 宿主有书、PDF0.3 有书/无书各有身份和截图，可作为正例；无书入口、R3 关闭回执、PDF 多窗未做，仍 PARTIAL。

## 5. R7 实施任务

### R7-01 · P1 · 一个“视图代号”统管提交、取消、恢复和关闭

不要再在各处加 if 判断。做一个统一的视图状态机：

1. paginator 维护 `liveView`（当前真正显示的章节、页码、锚点、资源 owner），**每次翻页、定位都更新**；`committedView` 只用于“提交那一刻”的记录，不再用于恢复页码。
2. 每次导航/打开拿一个递增的 `viewEpoch`。只有 epoch 仍是最新、且没有 finalize 时，才允许 commit、rollback 或 restore 改动 DOM 和 `state`。过期的导航只能释放自己的资源 owner，**不碰当前视图**。
3. 候选章节在离屏（或者独立的候选 iframe/文档片段）完成准备、资源等待、排版、定位和高亮；全部成功后一次性换入。失败或取消时，用户看的那份 DOM 从未被替换，就不需要“异步恢复”。如果坚持在原 iframe 上恢复，恢复本身也必须带 epoch，晚到即丢弃。
4. finalize 先冻结：之后任何导航、恢复、布局回调都直接返回，不改 DOM、不改 ready、不解冻 paginator。回执中的位置来自冻结时的 `liveView`。
5. 恢复失败时：busy/loading 清零，进入“可重试”状态，保留旧稳定位置，不写新保存。

验收（真实 Edge + 模型层都要过）：F01–F04 全部 defect=false；R6 交错 55 条除探针错误外全部 PASS；R4/R5 继承的 N-resources-*、Y-*、YT-*、YFAIL-*、Z-LATE-search-close 全部 PASS；R6 E01–E07 不回退。

### R7-02 · P1 · 关闭时位置与设置并行、各自有界

`finishAndAck` 里位置保存和设置保存同时发起（`Promise.allSettled`），各自有超时；任何一方卡住不能阻止另一方发出。回执汇总双方结果，未完成项写进 closeDiagnostics，ok=false。验收：宿主 L02 在原 JS→原 C# 下，扣住设置回执时位置 100 落盘，回执带设置未完成的诊断。

### R7-03 · P2 · 位置操作身份在失败重试中不变

`positionOperation` 一旦冻结，直到被新的用户输入取代前，sequence、operationId、savedAt、opVersion 都不变；flush、换书、关闭重试都发同一个包。验收：宿主 E01 两次请求的 sequence/operationId/savedAt 相同；有新输入时才换新身份（补一个对照用例）。

### R7-04 · P2 · 跨进程可比较的设置顺序号

由宿主在 `GrantPreferences` 时分配全局单调的 order（落盘持久化，重启不回退），客户端不再自造小编号。验收：宿主 B02 两进程交错时，后发起的那次写入生效；重启后 order 继续递增。

### R7-05 · P2 · 搜索可见性以清洗后的文档为准

见 3.8。验收：F05 三项计数为 1/0/0；D05 命中偏移与渲染文本一致；R5 的 86 组搜索参考比对、E03/E05/F06 不回退。

### R7-06 · 原生与剩余 A 项证据

A11 补字体晚加载和横竖图；A23 补原生 200 次真实切章 + 进程 WorkingSet/句柄趋势；A21 补无书入口、R3 关闭回执、PDF 多窗；A08 补异常关闭（结束进程）后的位置；其余 PARTIAL 按第 6 节补。

### R7-07 · 交付

新分支 `r7`，新候选 `candidates-r7/`，新证据目录 `r7-evidence/`。交付：实施报告、逐项 A 表、可复跑探针、原始消息/文件/截图、manifest 与 SHA256SUMS（清单路径必须和实际文件一致）、commit/分支/工作树状态、ZIP 内每个文件散列、最终运行 DLL/EXE 与实际加载模块散列、隔离数据根与进程号。报告里的数字必须和实际文件一致。完成后通知验收方。

## 6. A01–A25 综合状态（验收方结论，不照抄实施方标签）

| ID | R6 综合 | R7 必补 |
|---|---|---|
| A01 | PARTIAL | 目录遗漏正文、辅助导航分离、同章各片段定位 |
| A02 | PARTIAL | EPUB3 深层嵌套及前置正文 |
| A03 | PARTIAL | 跨章脚注返回原位置、完整 SVG 资源 |
| A04 | PARTIAL | 三条旧传书入口同时到达的解码前去重 |
| A05 | FAIL | F01/F02：取消后页码丢失、旧导航覆盖新章节；按 R7-01 |
| A06 | FAIL | F04/R6-FAULT：故障留下候选正文、恢复失败卡 busy；按 R7-01 |
| A07 | PASS | 保持 |
| A08 | PASS（正常稳定路径） | 异常关闭位置；报告数字与文件一致 |
| A09 | PARTIAL | 同名同大小不同内容隔离 |
| A10 | PARTIAL | 字号/行距/宽度/侧栏/窗口组合，重复句 emoji 高亮 |
| A11 | PARTIAL | ready 后图片正例已有；补字体与横竖图 |
| A12 | PARTIAL | 20 次快速/反向/边界连续输入 |
| A13 | PARTIAL | 60/144/240Hz 或受控时间源 |
| A14 | PARTIAL | 拖选、复制、右键、链接、方向键与快捷键互斥 |
| A15 | PARTIAL | 520/760/1400px × 100/150/200% |
| A16 | PARTIAL | 事件、嵌套框架、冲突 ID、宿主接口隔离 |
| A17 | FAIL | E03/E04/E05 已修；F05/D05 可见性仍不一致；按 R7-05 |
| A18 | PARTIAL | 书签去重/删/重排/跳转/重启组合 |
| A19 | FAIL | B02 跨进程顺序号撞车；按 R7-04 |
| A20 | PARTIAL | E03 账本已诚实；本地未知版本已保护；补异常退出/损坏/拒写原字节保护 |
| A21 | PARTIAL | v2 降级已修；补无书入口、R3 关闭回执、PDF 多窗 |
| A22 | PASS（提交抽检范围） | R7 最终候选重测 |
| A23 | PARTIAL | 浏览器正例已有；原生 200 切章与进程趋势 |
| A24 | FAIL | F03_CLOSE 定稿后仍改视图；L02 位置被设置阻塞；E01 身份重建；按 R7-01/02/03 |
| A25 | PASS | 最终 R7 空目录完整包再验 |

## 7. 实施顺序与完成门槛

1. 先交一页 R7 状态设计（liveView、viewEpoch、离屏候选、finalize 冻结的状态图），再改代码。
2. 先做 R7-01/02（P1），用真实 Edge + 原 JS→原 C# 闭合；再做 R7-03/04/05；每一步跑第 2 节全部回归。
3. 再补 A 表剩余项，构建唯一的最终候选，所有证据指向它。
4. 自查报告里的计数、路径、散列与实际文件一致，然后交给验收方。

可复跑的验收探针都在 `gpt-review-r6/` 下：浏览器 `review-navigation.cjs`、`review-visibility.cjs`、`review-new-stages.cjs` 等；模型交错 `model-audit/r6-harness.cjs`；宿主 `host-audit/`。实施方可以拿去自测，但不能修改原件，要复制到 r7-evidence 下再改。

**完成条件：原功能完整实现、确定缺陷关闭、原生证据齐全，并通过独立复验。**
