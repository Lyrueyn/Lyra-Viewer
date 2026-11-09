# DeepSeek R6 实施报告

任务书：`GPT验收结论-R5暨DeepSeek返工任务书-R6.md`，SHA-256 `5498220F4B78B457C23FD8B3E90C447E38B7FAB474DA5E196628FDC31EE4F75C`。
只写新 R6 目录/候选；R5/R4/R3 原件未改；未发布、未安装用户环境。

## 1. 身份

- 分支/提交：`D:\Temp\Lyra-Extension-Epub`，`r6` / `99bd102f4c9b4c1a92668d04e2e12260e523ad94`，工作树干净
- 候选包：`docs/epub-optimization/candidates-r6/lyra-ext-epub-0.10.0-candidate.zip`
  SHA-256 `F2B2741A166749E7108E010151A46246AC2DF40BF1F0861050E8E12D612C535C`，98,840 bytes，0.10.0
- 最终宿主：`D:\Temp\lyra_epub_host_r6` / 运行 `D:\Temp\lyra_r6_candidate`
  `WinUIGui.dll=3F30CCC9915993F058DAD62879F1619AC1EBD5B735CE1741D09FAEE86B28719C`
  `WinUIGui.exe=D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
- 运行模块：`reader.js=5A81D526B8126B590738BC50652DB829D701FDB9E3319240766FB3D07C2FC837`（扩展 0.10.0）；`reader-state.js` 以包内文件为准
- 状态设计：`r6-evidence/状态设计-R6.md`

## 2. R6 P1 修复与实测

### R6-01 view 提交/回滚
- `paginator` 增加 committed view 与失败恢复：任何 prepare/资源/layout 后取消或异常都不把候选 DOM/索引发布为 view；失败时恢复旧 view（DOM/页/锚点）后才返回失败。
- `reader` 的 ready/page/stable 只在 commit 后发布，取消后 `restoreNavView` 对齐旧 view；flush 在 navPending 时只用旧稳定位置。
- 真实 Edge E01_INPUT/E01_CLOSE：资源阶段释放后 `state.ready=true, spine=0, preparedIndex=0, paginatorReady=true, paginatorSpine=0`，正文仍是 c0；下一页正常保存；defect=false。
- E02：资源阶段 finalize 保存旧稳定 locator textOffset2506/page4，不写候选空锚点；defect=false。
- 结果：`r6-evidence/new-stage/results.json` E01/E02 defect=false。

### R6-02 关闭操作账本/动画/迟到任务
- `paginator.freezeForClose` 先 settle 动画目标、再 cancel rAF/定时器；settle 使用目标页 fraction 锚点，避免 pageIndex=target 但 reopen 回 0。
- `flushOnHide`（pagehide 对照）也先 settle 再保存，不冒 finalizing。
- `finishAndAck` 汇总 `failedSaves` 未解决项（含其它书），B 成功不再清掉 A 的失败账本；未解决返回 ok=false + closeDiagnostics。
- 迟到 open 在 file.arrayBuffer/parse/navigation 各阶段检查 finalizing/intent，finalizing 后不再改变 ready/error。
- 实测：E06_ANIMATION_FINALIZE 与 E06_ANIMATION_PAGEHIDE 均保存 textOffset659/page1，真实 reload 回 page1，defect=false；E07 迟到 File bytes finalized 后 error 空、ready 不变，defect=false。
- **未闭合**：A 保存失败→B 成功→不重开 A 直接关闭的**原客户端→原 C#**完整闭环本轮未取得有效证据；客户端账本代码已改，host 侧未重跑 GPT E03。不得记 PASS。

### R6-03 完成凭据/压力
- `PreferenceCompletion` 保存 grant 全字段 + 每字段 applied/idempotent/superseded + values；完成 grant 被回收后重试重放完整结果；后续更晚 order 让 applied 字段仍返回 superseded。
- 无完成项可回收时，容量背压只安全回收“每字段都已被更晚 order 取代”的未完成 grant，最后意图可申请。
- 宿主 headless 审计：`R6-K01-COMPLETION-SUPERSEDED` PASS（A theme/bg/fg，B 更晚 theme/bg；A 完成重试 fg applied、theme/bg superseded，无 FIELD_NOT_GRANTED）；`R6-C02-PRESSURE-LAST-INTENT` PASS（4096 同字段授权后 lineHeight2.2 落盘）。
- R5 单字段 C01/容量/scope 回归仍 PASS；host-audit 8/8。

## 3. R6 P2

### R6-04 位置操作身份
- 新增 `positionOperation` 在待保存意图产生时冻结 book/openIntent/tokenPromise/sequence/operationId/opVersion/pos/baseRevision；同一失败操作跨 flush、换书前、重开、timeout 重试复用；新用户交互才新包。
- 动画首帧未落地时不抓 DOM，等 settle/动画完成后再冻结，修复“目标页 pageIndex1 但 textOffset 旧页”的混合操作。
- 未完成：原 JS→原 C# E01 的完整 `savePosition` 请求包逐笔对照本轮未跑；只以 E01/E06 状态和代码链证明，报告记 PARTIAL。

### R6-05 本地未知版本保护
- 位置/书签/设置分别校验 version+结构；未知版本不回空表、不写回、保留原字节；合法 version1/旧数组恢复后清除 corrupt 并可添加。
- 隔离测试 `r6-evidence/local-format/unknown-version-results.json`：
  bookmark unknown warn=true、add `LOCAL_CORRUPT`、rawUnchanged=true；合法恢复 1→2、version1、futureData 保留；position/prefs unknown warn=true、saveCode `LOCAL_CORRUPT`、rawUnchanged=true。

### R6-06 可见模型/SVG 高亮/长查询
- CSS `display/visibility` 按“最后有效声明 + !important 优先”解析；HTML/SVG 模型统一。
- SVG 文字高亮改用 SVG `tspan` 命名空间，不再塞 HTML mark；E03 高亮前后可见文字相同、长度/bbox 稳定，defect=false。
- E04 4097 查询显示明确“超过 4096 UTF-16 单位，请缩短后重新查找”，defect=false；E05 重复 visibility 声明目标找到1、隐藏未命中，defect=false。

### R6-07 版本降级生命周期
- 宿主对 v2 只承接当前受信 session/source/loadId 的 app.ready/book.begin/book.ready/book.error；hello 回 INCOMPATIBLE_PROTOCOL 让旧客户端 local；不接受旧持久化命令。
- 最终宿主 + 完整 R4 0.8/v2 包原生：`LEGACY_V2_HELLO_LOCAL`；book.ready 后加载层解除；正文可见、local footer“此主程序版本不支持跨窗口保存”、翻页可交互。证据 `r6-evidence/v2-interop/`。
- 真 c12/R3/旧宿主矩阵本轮未重跑（R5 正例保留），A21 仍 PARTIAL。

## 4. A11/A23

- A11：**UNTESTED**。未构造 ready 后迟到资源/字体重排的真实目标阶段证据；不引用 coverage:false 当 PASS。
- A23：最终候选浏览器 201 章含图书，**200/200 次真实成功切章**（每步断言 spine 目标且 ready）；随后同页 `#file` 真实入口连续换 10 本书（非 page.goto）；DOM iframe 节点 2、blob 资源 1→0、taskSets 1、静置 heap 回落；耗时 2,378ms。证据 `r6-evidence/a23/results.json`。**仍缺原生 WinUI/WebView2 进程 RSS/句柄趋势，A23 PARTIAL。**

## 5. 测试汇总

- host-audit：8/8 PASS（R5 6 + K01/C02）
- 真实 Edge new-stage E01–E07：全部 defect=false（含 E06 finalize/pagehide、E07 late file）
- local format：3 类未知版本保护 + 合法恢复全部通过
- v2 原生互配：最终宿主 + R4 完整包，local 可见/可交互/加载层消失
- A23：200/200 成功切章 + 10 同上下文换书
- 包 smoke：真实入口 15 页正常（`r6-evidence/package-smoke.png`）
- 未改 R5/R4/R3；候选/报告/证据新目录。

## 6. A 表（R6）

| # | 状态 | 依据/缺口 |
|---|---|---|
| A01 | PARTIAL | R5 正文正例；辅助导航/片段未补 |
| A02 | PARTIAL | NCX/无目录正例；深层嵌套未补 |
| A03 | PARTIAL | 片段/编码正例；跨章脚注未补 |
| A04 | PARTIAL | 旧入口去重未全跑 |
| A05 | PASS | E01 资源阶段最后意图与全状态一致 |
| A06 | PARTIAL | E01 回滚；失败重试/拖入未全 |
| A07 | PASS | R5 Range/刷新正例保留 |
| A08 | PASS（正常稳定路径） | R5 补充有效图片/文字/设置/实际书签/重开/重启；异常关闭位置未新增 |
| A09 | PARTIAL | 改名共享正例；同大小不同内容未补 |
| A10 | PARTIAL | 锚点正例；组合设置未全 |
| A11 | UNTESTED | 未完成 ready 后迟到资源目标阶段 |
| A12 | PARTIAL | 快速/反向翻页未全 |
| A13 | PARTIAL | 刷新率矩阵未补 |
| A14 | PARTIAL | 选区/编辑互斥未全 |
| A15 | PARTIAL | 缩放矩阵未补 |
| A16 | PARTIAL | 嵌套框架/冲突 ID 未补 |
| A17 | PASS | E03/E04/E05 关闭 SVG 消失、重复 visibility、长查询 |
| A18 | PARTIAL | 书签组合未新增 |
| A19 | PARTIAL | R5 双书签/旧资格拒绝保留；R6 固定时序未新增 |
| A20 | PARTIAL | E07 迟到 file 关闭；A/B 失败账本原链未闭合 |
| A21 | PARTIAL | v2→最终宿主已修复；c12/R3/旧宿主矩阵未 R6 重跑 |
| A22 | PASS | R5 373/255 章正例保留，未上传原书 |
| A23 | PARTIAL | 200/200 成功切章+10 同上下文换书；无原生进程趋势 |
| A24 | PARTIAL | E02/E06/E07 资源/动画/迟到关闭；A/B 原链未闭合 |
| A25 | PASS | 0.10.0 包 11 文件真实入口 smoke |

## 7. 明确未完成（不冒充完成）

1. R6-02 A 失败→B 成功→不重开 A 直接关闭的原客户端→原 C# 闭环未取得有效证据；代码账本已改，A20/A24 仍 PARTIAL。
2. R6-04 跨失败/换书请求的 operation 包逐笔原链对照未跑；PARTIAL。
3. A11 ready 后迟到资源/字体/横竖图目标场景未完成；UNTESTED。
4. A23 原生进程 RSS/句柄趋势未测；仅浏览器 renderer/DOM/URL/task/heap。
5. A21 c12/R3/旧宿主 R6 矩阵、A01-A19 剩余子项未全部重跑。
6. 本轮完成的是确定 P1 的资源后段/动画/迟到 File、SVG/可见性/长查询、v2 生命周期、完成凭据与本地保护；未达到 GPT“所有 A 子项全 PASS”门槛。
