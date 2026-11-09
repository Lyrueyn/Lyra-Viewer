# DeepSeek 实施回报 · R7

任务书：`GPT验收结论-R6暨实施返工任务书-R7.md`
SHA-256：`835A9B0F27BDED0735779FE8834F21912A66BBB08DAF81CFC360F32AB98F7A22`

## 1. 交付物与身份

- 扩展仓库分支：`r7`，HEAD `a2bd44de2b53d0a3f9b4db70a4c597853ac7a315`，工作树干净；R6 commit `99bd102f…` 与 `candidates-r6/`、`gpt-review-r6/` 原件未改。
- 唯一最终候选：`candidates-r7/lyra-ext-epub-0.10.0-candidate.zip`
  - 101,736 bytes
  - SHA-256 `459766C816A42E9539EE555212F085525DDE8645F93CB5772B5AA0DA1650BCAA`
  - 成员：`index.html`、`README.md`、`LICENSE`、`extension.json`、`reader.js`、`reader.css`、`epub-book.js`、`paginator.js`、`reader-state.js`、`search.js`、`lib/jszip.min.js`
- 宿主构建：`D:\Temp\lyra_epub_host_r7`
  - `WinUIGui.dll` 631,808 bytes SHA-256 `3E4FF44734A471C4607A57CD98453D7CDCF73AD527DB81B7654B49A4DCD7F238`
  - `WinUIGui.exe` 162,816 bytes SHA-256 `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
  - `WinUIGui.pri` SHA-256 `45032201F325B7D1E44970284EA0336C9A5DDE6CBF1306BE94AFA1C5A3CF14FE`
- 隔离运行根：`D:\Temp\lyra_r7_candidate`（39,968 文件；已换入上述 EXE/DLL/PRI、`extensions\epub` 指向本候选文件）。未发布、未安装、未改用户数据。
- 状态设计（改代码前交付）：`r7-evidence/状态设计-R7.md`。

## 2. R7-01 / R7-02 结果（真实 Edge + 原 JS→原 C#）

真实 Edge（原始模块，仅门控资源/字体/iframe 完成）：
- F01–F04：5/5 `defect=false`（取消保留最新页、旧 prepare 不覆盖新章节、restore 期间新导航/关闭、资源等待异常恢复）。
- R6 E01–E07 新阶段回归：9/9 `defect=false`（E01 资源取消、E02 document 后 layout 前 finalize、E03 SVG 高亮、E04 4096、E05 CSS 顺序、E06 动画 finalize/pagehide、E07 迟到文件字节）。

原始 JS→原 C# 宿主审计（不替换宿主、不假造状态文件）：
- `extended.cjs`：11/11 PASS，含 E01 重试身份、E02 元数据失败重开、E03 他书失败关闭、E04 prepare/resources 关闭、E05 动画关闭、E06 能力全量、E07 本地书签恢复、E08 未知 schema、E09 legacy revision。
- `close-boundary.cjs`：3/3 PASS，含 L02 设置回执卡住时位置仍落盘。
- `boundary.cjs`：5/5 PASS，含 B02 两进程小编号碰撞被消除、B01 重启迁移、B03 4096、B04 幂等、B05 过期恢复。

模型层（冻结原 JS 原文 + jsdom/几何 double，仅作交错补充）：
- R6 交错：55 条中 **52 PASS / 0 FAIL / 3 已知 PROBE_ERROR**（`R6-CANCEL-fonts-nav-PROBE`、`R6-CANCEL-fonts-open-PROBE`、`R6-RESTORE-resources-open-PROBE`，与 R6 冻结探针错误同类，未伪造成 PASS）。
- R4 `audit-r4-reader-stages`：16/16 PASS（N-resources-* 全过）。
- R5 `audit-r5-reader-consistency`：27/27 PASS（Y-*、YT-*、YFAIL-* 全过）。
- R5 `audit-r5-search-visibility`：15 PASS / 0 FAIL / 2 OBSERVATION。
- R5 `audit-r5-late-cancel-adapted`：3/3 PASS（Z-LATE-search-close 已过）。
- R4 boundaries：8 PASS / 0 FAIL / 3 OBSERVATION；anchor-cancel：3 PASS / 0 FAIL / 1 OBSERVATION。

## 3. R7-03 / R7-04 / R7-05

- R7-03 位置身份：E01 两次请求 `sequence`、`operationId`、`savedAt` 相同；有新的用户输入才重建操作包。
- R7-04 跨进程 order：宿主 `AllocatePreferenceOrderLocked()` 用独立锁文件读盘上最大值 +1 并立即落盘；B02 两进程交错 PASS，B01 重启继续递增。
- R7-05 可见性：`sanitizeVisibilityStyles` 成为 `prepareSection` 与 `getSectionPlainText` 唯一清洗入口；F05 计数 1/0/0，F06 SVG 命中，SVG 高亮同时带 `.lyra-search-mark`；R5 可见性 15/15 PASS（含 `Z-VIS-important-first-stripped`、正反重复声明、SVG 等）。

## 4. A01–A25 逐项状态（本报告不夸大）

| ID | R7 状态 | 依据 / 未闭合项 |
|---|---|---|
| A01 | PARTIAL | 目录/正文/辅助导航旧证据保留；未补目录遗漏正文与同章多片段全覆盖 |
| A02 | PARTIAL | 未补 EPUB3 深层嵌套/前置正文专项 |
| A03 | PARTIAL | 跨章脚注、完整 SVG 资源未本轮补 |
| A04 | PARTIAL | 三旧传书入口解码前去重未本轮补 |
| A05 | PASS | F01/F02 真实 Edge defect=false；liveView 恢复最新页 |
| A06 | PASS | F04/R6-FAULT 真实 Edge + R6 交错 PASS；失败不留候选正文、恢复失败清 busy |
| A07 | PASS | 保持 |
| A08 | PARTIAL | R6 正常稳定路径 PASS；R7 未重跑原生异常结束进程 |
| A09 | PARTIAL | 同名同大小不同内容未本轮补 |
| A10 | PARTIAL | 字号/行距/宽度/侧栏/窗口组合未本轮全矩阵 |
| A11 | PARTIAL | 真实 Edge ready 后图片 geometry PASS（`a11-results.json` defect=false）；字体晚加载/横竖图未补原生 |
| A12 | PARTIAL | 20 次快速/反向/边界输入未本轮独立补 |
| A13 | PARTIAL | 60/144/240Hz 受控时间源未本轮补 |
| A14 | PARTIAL | 拖选/复制/右键/链接/方向键互斥未全矩阵 |
| A15 | PARTIAL | 520/760/1400px × 100/150/200% 未全矩阵 |
| A16 | PARTIAL | 事件/嵌套框架/冲突 ID/宿主接口隔离未全矩阵 |
| A17 | PASS | F05/F06 defect=false；R5 可见性 15 PASS；D05 命中/渲染一致性列入 R5 套件 |
| A18 | PARTIAL | 书签去重/删/重排/跳转/重启组合未全矩阵 |
| A19 | PASS | B02 跨进程顺序号 PASS |
| A20 | PARTIAL | 本地未知版本保护 PASS；异常退出/损坏/拒写原字节保护未全 |
| A21 | PARTIAL | R6 已有身份/截图未本轮重跑；无书入口、R3 关闭回执、PDF 多窗未补 |
| A22 | PASS | R7 最终候选在 E01–E09/L01–L03/B01–B05 抽检全 PASS |
| A23 | PARTIAL | 真实 Edge 200/200 切章 + 10 次换书 PASS（`a23/results.json`）；原生 200 次与进程 WorkingSet/句柄趋势未做 |
| A24 | PASS | F03_ROLLBACK_WAIT_CLOSE defect=false；L02 PASS；E01 身份稳定 |
| A25 | PARTIAL | 最终候选包与隔离运行根已生成；原生空目录完整包复验未做 |

R7-01/02/03/04/05 已按任务书闭合；R7-06 原生部分（A08 异常关闭、A21 无书/R3 关闭回执/PDF 多窗、A23 原生 200 与进程趋势、A11 字体/横竖图）仍为 PARTIAL/UNTESTED，不伪造 PASS。其余 A 项沿用原状态，未用本轮证据覆盖。

## 5. 修复要点

- `liveView` 从每次翻页/定位更新，恢复一律取 liveView；`viewEpoch` 关联 navGeneration/openIntent/finalizing，过期导航只释放 owner。
- 取消恢复不再走候选 DOM 提交流：`_restoreViewDirect` 直接写回旧文档并带 epoch/finalize 守卫；恢复失败清 busy/loading，保留旧稳定位置。
- finalize 前先恢复 pending liveView，再 `freezeForClose`；关闭后迟到布局/文件/图片/恢复直接返回；位置与设置、元数据并行且有界。
- 位置失败快照在新用户输入前复用原 sequence/operationId/savedAt；宿主偏好 order 跨进程落盘单调。
- 搜索清洗器与文字模型共用 `sanitizeVisibilityStyles`；SVG tspan 同时带 `.lyra-search-mark`。

## 6. 证据与可复跑

- 真实 Edge 探针：`r7-evidence/tools/review-navigation.cjs`、`review-visibility.cjs`、`review-new-stages.cjs`；结果在 `r7-evidence/navigation|visibility|new-stage/`。
- 模型交错：`r7-evidence/model-audit/`（复制 gpt-review-r6 原件，仅改 candidate 路径），结果 `*-results.json`。
- 宿主审计：`r7-evidence/host-audit/`（复制原件，Program.cs 仅把 r6 作用域改 r7-evidence），结果 `runs/r7-final4-*/results.json`。
- 原生/Edge 补充：`r7-evidence/a11/a11-results.json`、`r7-evidence/a23/results.json`、`r7-evidence/local-format/unknown-version-results.json`。
- 清单：`r7-evidence/r7-manifest.json`、`r7-evidence/SHA256SUMS`；候选 ZIP 成员散列 `r7-evidence/candidates-r7-files.json`。

## 7. 结论

R7 的 P1（取消页丢失、旧导航覆盖、关闭恢复、候选残留、关闭被设置阻塞）与 P2（身份不变、跨进程顺序、清洗后可见性）均已由真实 Edge、原 JS→原 C# 宿主和模型交错闭合，R6 已知探针错误之外无失败。原生 A 表剩余项未在本轮全部补齐，按 PARTIAL/UNTESTED 如实交付，未发布、未安装、未改用户数据。